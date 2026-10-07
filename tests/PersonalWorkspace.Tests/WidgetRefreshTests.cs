using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Services;
using Xunit;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    // Counts calls at service boundaries, keeping all canonical implementations in these tests.
    public class WidgetSpy<T> : DispatchProxy where T : class
    {
        public T Target { get; set; } = null!;
        public Dictionary<string, int> Calls { get; } = [];
        public string? Fail { get; set; }
        public static (T Service, WidgetSpy<T> Spy) Wrap(T target)
        { var service = Create<T, WidgetSpy<T>>(); var spy = (WidgetSpy<T>)(object)service; spy.Target = target; return (service, spy); }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls[method!.Name] = Calls.GetValueOrDefault(method.Name) + 1;
            if (Fail == method.Name) throw new InvalidOperationException("test failure");
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
    }
    [Fact]
    public async Task WidgetResolutionBatchesSourceTypesAndCachesDuplicateBoundedCharts()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Task")); var tracker = await Create(); var journal = await Journal();
        for (var i = 0; i < 4; i++)
        {
            await WAdd(page, task.Item); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.CurrentValue);
            await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart); await WAdd(page, journal.Item, WidgetType.Journal, WidgetMode.TodaySummary);
        }
        var tasks = WidgetSpy<ITaskService>.Wrap(JournalTasks()); var trackers = WidgetSpy<ITrackerService>.Wrap(service); var charts = WidgetSpy<ITrackerAnalyticsService>.Wrap(Analytics()); var journals = WidgetSpy<IJournalService>.Wrap(Journals());
        var vm = new WidgetWorkspaceViewModel(Widgets(), tasks.Service, WidgetRecurrence(), trackers.Service, charts.Service, WidgetEvents(), journals.Service, Pages(), current, new NavigationService(), clock, NullLogger<WidgetWorkspaceViewModel>.Instance);
        await vm.OpenAsync(PRef(page)); Assert.Equal(16, vm.Rows.Count); Assert.All(vm.Rows, r => Assert.Null(r.Error));
        Assert.Equal(1, Assert.Single(tasks.Spy.Calls).Value); Assert.Equal("GetGraphAsync", Assert.Single(tasks.Spy.Calls).Key);
        Assert.Equal(1, Assert.Single(trackers.Spy.Calls).Value); Assert.Equal("GetPeriodsAsync", Assert.Single(trackers.Spy.Calls).Key);
        Assert.Equal(1, Assert.Single(charts.Spy.Calls).Value); Assert.Equal(1, Assert.Single(journals.Spy.Calls).Value);
        Assert.All(vm.Rows.Where(r => r.Analytics is not null), r => Assert.Equal(30, r.Analytics!.Range.Through.DayNumber - r.Analytics.Range.From.DayNumber + 1));
    }
    [Fact]
    public async Task WidgetTrackerBatchLoadsOnlyCurrentAndLatestPeriodsAcrossUniqueSources()
    {
        var tracker = await Create(Historical()); for (var i = 0; i < 25; i++) await Save(tracker, i, Day.AddDays(-i));
        var second = await Create(Historical()); await Save(second, 10, Day.AddDays(-2));
        var result = await new SqliteTrackerRepository().ReadPeriodsAsync(new(profile, paths.WorkspaceDatabase(profile)), [tracker.Item.Id, second.Item.Id, tracker.Item.Id], Day, default);
        Assert.Equal(2, result.Items.Count); Assert.Equal(2, result.Entries.Count); Assert.Single(result.Entries, e => e.TrackerId == tracker.Item.Id);
        var summaries = await service.GetPeriodsAsync(profile, [tracker.Item.Id, second.Item.Id], Day);
        Assert.Equal(await service.GetPeriodAsync(Ref(tracker), Day), summaries.Single(p => p.Current.Tracker.Item.Id == tracker.Item.Id).Current);
        Assert.Null(summaries.Single(p => p.Current.Tracker.Item.Id == second.Item.Id).Current.Value);
    }
    [Fact]
    public async Task WidgetSourceFailureStaysLocalAndCanRefreshSuccessfully()
    {
        var page = await CanvasPage(); var tracker = await Create(); var journal = await Journal(); await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart); await WAdd(page, journal.Item, WidgetType.Journal, WidgetMode.TodaySummary);
        var charts = WidgetSpy<ITrackerAnalyticsService>.Wrap(Analytics()); charts.Spy.Fail = "GetAsync"; var vm = WidgetModel(charts: charts.Service); await vm.OpenAsync(PRef(page));
        Assert.Null(vm.Error); Assert.NotNull(vm.Rows.Single(r => r.Widget.Type == WidgetType.TrackerChart).Error); Assert.Null(vm.Rows.Single(r => r.Widget.Type == WidgetType.Journal).Error);
        charts.Spy.Fail = null; await vm.RefreshAsync(); Assert.All(vm.Rows, r => Assert.Null(r.Error));
    }
    [Fact]
    public async Task WidgetRecurringValueDisplaysCanonicalCarryEffectiveTargetWithoutRecalculation()
    {
        var task = await JournalTasks().CreateAsync(profile, new("Push-ups", Value: new(TaskValueType.Number, 20))); var recurrence = WidgetRecurrence();
        await recurrence.SetRuleAsync(new(profile, task.Item.Id), new(RecurrencePattern.Daily, Day.AddDays(-1))); await recurrence.SetCarrySettingsAsync(new(profile, task.Item.Id), true, false);
        var occurrences = await recurrence.GetRangeAsync(profile, Day.AddDays(-1), Day); await recurrence.UpdateAsync(new(profile, occurrences[0].Occurrence.Id), Day.AddDays(-1), TaskStatus.Doing, 18, false);
        var canonical = await recurrence.FindAsync(new(profile, occurrences[1].Occurrence.Id)); Assert.Equal(22m, canonical.Value!.Target);
        var page = await CanvasPage(); await WAdd(page, task.Item, mode: WidgetMode.Progress); var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page));
        Assert.Contains(TaskValuePresentation.Progress(canonical.Value), vm.Rows[0].Summary); Assert.False(vm.Rows[0].CanCompleteToday); Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
    }
    [Fact]
    public async Task WidgetChartConfigurationsAreIndependentAndInvalidChangesRollBack()
    {
        var page = await CanvasPage(); var tracker = await Create(); var a = await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart); var b = await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart);
        await WEdit(page, new ConfigureWidget(a.Id, WidgetMode.Chart, new(WidgetChartKind.Bar, AnalyticsPreset.SevenDays)));
        Assert.Equal(b, (await Widgets().GetAsync(PRef(page))).Widgets.Single(w => w.Id == b.Id));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WEdit(page, new ConfigureWidget(a.Id, WidgetMode.Chart, new(WidgetChartKind.Line, AnalyticsPreset.Custom))));
        await Sql("CREATE TRIGGER FailSettings BEFORE UPDATE ON WidgetChartSettings BEGIN SELECT RAISE(ABORT,'test'); END;");
        var other = await Create(); var before = (await Widgets().GetAsync(PRef(page))).Widgets.Single(w => w.Id == a.Id);
        await Assert.ThrowsAsync<WidgetOperationException>(() => WEdit(page, new ConfigureWidget(a.Id, WidgetMode.Chart, new(WidgetChartKind.Area, AnalyticsPreset.NinetyDays), other.Item.Id)));
        Assert.Equal(before, (await Widgets().GetAsync(PRef(page))).Widgets.Single(w => w.Id == a.Id));
    }
    private sealed class DelayedWidgets(IWidgetService inner) : IWidgetService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Delay { get; set; } = true;
        public async Task<WidgetPage> GetAsync(WorkspaceItemReference page, CancellationToken token = default)
        { var result = await inner.GetAsync(page, token); if (Delay) { Started.TrySetResult(); await Release.Task; } return result; }
        public Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceItemReference page, WidgetType type, CancellationToken token = default) => inner.CandidatesAsync(page, type, token);
        public Task<WidgetPage> EditAsync(WorkspaceItemReference page, WidgetEdit edit, CancellationToken token = default) => inner.EditAsync(page, edit, token);
    }
    [Fact]
    public async Task WidgetLateLoadCannotRestorePriorPageOrProfileState()
    {
        var page = await CanvasPage(); var other = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Old task")); await WAdd(page, task.Item);
        var delayed = new DelayedWidgets(Widgets()); var vm = WidgetModel(source: delayed); var first = vm.OpenAsync(PRef(page)); await delayed.Started.Task; delayed.Delay = false;
        await vm.OpenAsync(PRef(other)); delayed.Release.SetResult(); await first; Assert.Empty(vm.Rows); Assert.Equal(PRef(other), vm.Reference);
        var delayedProfile = new DelayedWidgets(Widgets()); vm = WidgetModel(source: delayedProfile); first = vm.OpenAsync(PRef(page)); await delayedProfile.Started.Task; await profiles.CreateAsync("Other profile"); delayedProfile.Release.SetResult(); await first;
        Assert.Empty(vm.Rows); Assert.Null(vm.Reference); Assert.Null(vm.Page); Assert.Null(vm.Error);
    }
}
