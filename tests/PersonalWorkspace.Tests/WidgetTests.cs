using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private WidgetService Widgets() => new(new SqliteWidgetRepository(), current, gate, clock, NullLogger<WidgetService>.Instance);
    private RecurrenceService WidgetRecurrence() => new(new SqliteTaskRepository(), current, gate, clock, NullLogger<RecurrenceService>.Instance);
    private EventService WidgetEvents() => new(new SqliteEventRepository(), current, gate, clock, NullLogger<EventService>.Instance);
    private WidgetWorkspaceViewModel WidgetModel(NavigationService? nav = null, IWidgetService? source = null, ITrackerAnalyticsService? charts = null) =>
        new(source ?? Widgets(), JournalTasks(), WidgetRecurrence(), service, charts ?? Analytics(), WidgetEvents(), Journals(), Pages(), current, nav ?? new(), clock, NullLogger<WidgetWorkspaceViewModel>.Instance);
    private Task<WidgetPage> WEdit(PageItem page, WidgetEdit edit) => Widgets().EditAsync(PRef(page), edit);
    private async Task<WidgetInstance> WAdd(PageItem page, WorkspaceItem source, WidgetType type = WidgetType.Task, WidgetMode mode = WidgetMode.Card, Guid? host = null, WidgetChartSettings? chart = null)
    {
        var before = (await Widgets().GetAsync(PRef(page))).Widgets.Select(w => w.Id).ToHashSet();
        return (await WEdit(page, new AddWidget(type, source.Id, mode, host, Chart: chart))).Widgets.Single(w => !before.Contains(w.Id));
    }
    [Fact]
    public async Task WidgetMigrationUpgradesPopulatedPhaseThirteenIdempotently()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(11)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var page = Guid.NewGuid(); var block = Guid.NewGuid();
        await Sql($"INSERT INTO WorkspaceItems VALUES('{page}',5,'Training','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00',NULL,NULL);INSERT INTO Pages VALUES('{page}',NULL,0,NULL);INSERT INTO PageCanvasSettings VALUES('{page}',0,125);INSERT INTO PageCanvasItems VALUES('{block}','{page}',NULL,0,24,24,192,128,'','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00');", legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(12L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy)); Assert.Equal(11L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(block.ToString(), await Sql("SELECT Id FROM PageCanvasItems;", legacy)); Assert.Equal(125L, await Sql("SELECT ZoomPercent FROM PageCanvasSettings;", legacy)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WidgetInstances;", legacy));
    }
    [Fact]
    public async Task WidgetIdentityAllowsRepeatedSourcesWithCollisionFreeAtomicHosts()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("20 push-ups"));
        var a = await WAdd(page, task.Item); var b = await WAdd(page, task.Item); var state = await Widgets().GetAsync(PRef(page));
        Assert.NotEqual(a.Id, a.CanvasItemId); Assert.NotEqual(a.Id, a.SourceItemId); Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.CanvasItemId, b.CanvasItemId);
        Assert.Equal(a.SourceItemId, b.SourceItemId); Assert.Single(state.Sources); Assert.Equal(2, state.Widgets.Count);
        Assert.All(state.Canvas.Items, h => { Assert.Equal(320, h.Rect.Width); Assert.Equal(240, h.Rect.Height); }); CanvasLayout.Validate(new(state.Canvas));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Tasks;")); Assert.Equal(a, state.Widgets.Single(w => w.Id == a.Id));
    }
    [Fact]
    public async Task WidgetStorageFailureRollsBackNewHostAndDuplicate()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Task")); var a = await WAdd(page, task.Item);
        await Sql("CREATE TRIGGER FailWidget BEFORE INSERT ON WidgetInstances BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<WidgetOperationException>(() => WAdd(page, task.Item)); await Assert.ThrowsAsync<WidgetOperationException>(() => WEdit(page, new DuplicateWidget(a.Id)));
        Assert.Single((await Layout(page)).Items); Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets);
    }
    [Fact]
    public async Task WidgetEmptyBlockConversionRemovalAndWholeRemovalPreserveSource()
    {
        var page = await CanvasPage(); var host = await Block(page); var task = await JournalTasks().CreateAsync(profile, new("Task")); var a = await WAdd(page, task.Item, host: host.Id);
        Assert.Equal(host, Assert.Single((await Layout(page)).Items));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WAdd(page, task.Item, host: host.Id));
        await WEdit(page, new RemoveWidget(a.Id)); Assert.Equal(host, Assert.Single((await Layout(page)).Items));
        var b = await WAdd(page, task.Item, host: host.Id); await WEdit(page, new RemoveWidget(b.Id, true)); Assert.Empty((await Layout(page)).Items);
        Assert.Equal(task, await JournalTasks().FindAsync(new(profile, task.Item.Id)));
    }
    [Fact]
    public async Task WidgetContainerAndWrongSourcesAreRejectedWithoutOrphans()
    {
        var page = await CanvasPage(); var container = await Block(page, kind: CanvasKind.Container); var tracker = await Create();
        await Assert.ThrowsAsync<WidgetValidationException>(() => WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.CurrentValue, container.Id));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WAdd(page, tracker.Item));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WAdd(page, page.Item, WidgetType.PageLink));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WEdit(page, new AddWidget(WidgetType.Task, Guid.NewGuid(), WidgetMode.Card)));
        Assert.Empty((await Widgets().GetAsync(PRef(page))).Widgets); Assert.Single((await Layout(page)).Items);
        Assert.DoesNotContain(await Widgets().CandidatesAsync(PRef(page), WidgetType.PageLink), s => s.Id == page.Item.Id);
    }
    [Fact]
    public async Task WidgetDatabaseEnforcesUniqueHostIdentityAndCompatibleSource()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Task")); var a = await WAdd(page, task.Item); var tracker = await Create();
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO WidgetInstances SELECT '{Guid.NewGuid()}',CanvasItemId,WidgetType,SourceItemId,PresentationMode,CreatedAtUtc,UpdatedAtUtc FROM WidgetInstances;"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE WidgetInstances SET Id='{Guid.NewGuid()}' WHERE Id='{a.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE WidgetInstances SET SourceItemId='{tracker.Item.Id}' WHERE Id='{a.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE WidgetInstances SET PresentationMode=6 WHERE Id='{a.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE PageCanvasItems SET Kind=1,Title='Container' WHERE Id='{a.CanvasItemId}';"));
    }
    [Fact]
    public async Task WidgetSourceLifecycleMissingReplacementPreservesStableIdentities()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Disposable")); var a = await WAdd(page, task.Item); var host = Assert.Single((await Layout(page)).Items); var vm = WidgetModel();
        foreach (var action in new[] { TaskAction.Archive, TaskAction.RestoreArchive, TaskAction.Trash, TaskAction.RestoreTrash })
        {
            await JournalTasks().ApplyAsync(new(profile, task.Item.Id), action); await vm.OpenAsync(PRef(page));
            Assert.Equal(action is TaskAction.RestoreArchive or TaskAction.RestoreTrash, Assert.Single(vm.Rows).SourceActive); Assert.Equal(a, Assert.Single(vm.Page!.Widgets));
        }
        await JournalTasks().ApplyAsync(new(profile, task.Item.Id), TaskAction.Trash); await JournalTasks().PermanentlyDeleteAsync(new(profile, task.Item.Id)); await vm.RefreshAsync();
        Assert.Null(Assert.Single(vm.Rows).Widget.SourceItemId); Assert.Equal("Missing reference", vm.Rows[0].Lifecycle); Assert.Null(vm.Rows[0].Route); Assert.Equal(host, Assert.Single((await Layout(page)).Items));
        var replacement = await JournalTasks().CreateAsync(profile, new("Replacement")); await WEdit(page, new ReplaceWidgetSource(a.Id, replacement.Item.Id));
        var restored = Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets); Assert.Equal(a.Id, restored.Id); Assert.Equal(a.CanvasItemId, restored.CanvasItemId); Assert.Equal(a.CreatedAtUtc, restored.CreatedAtUtc); Assert.Equal(replacement.Item.Id, restored.SourceItemId); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task WidgetTimestampsSeparateLayoutConfigurationAndSource()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Task")); var a = await WAdd(page, task.Item); var host = Assert.Single((await Layout(page)).Items);
        await WEdit(page, new ConfigureWidget(a.Id, WidgetMode.Progress)); var b = Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets);
        Assert.True(b.UpdatedAtUtc > a.UpdatedAtUtc); Assert.Equal(host, Assert.Single((await Layout(page)).Items));
        await Edit(page, new MoveCanvasItems([host.Id], 8, 8)); Assert.Equal(b, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets)); Assert.Equal(task, await JournalTasks().FindAsync(new(profile, task.Item.Id)));
        var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); await vm.RefreshAsync(); Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
    }
    [Fact]
    public async Task WidgetDuplicateCopiesConfigurationButNotSourceAndPageDuplicateStaysEmpty()
    {
        var page = await CanvasPage(); var tracker = await Create(); var a = await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart, chart: new(WidgetChartKind.Area, AnalyticsPreset.SevenDays));
        var b = (await WEdit(page, new DuplicateWidget(a.Id))).Widgets.Single(w => w.Id != a.Id); Assert.Equal(a.Chart, b.Chart); Assert.Equal(a.SourceItemId, b.SourceItemId); Assert.NotEqual(a.CanvasItemId, b.CanvasItemId);
        var duplicate = await Pages().DuplicateAsync(PRef(page)); Assert.Empty((await Widgets().GetAsync(PRef(duplicate))).Widgets); Assert.Empty((await Layout(duplicate)).Items); Assert.Single(await service.GetAsync(profile));
    }
    [Fact]
    public async Task WidgetPageLifecyclePreservesThenCascadesOnlyLayout()
    {
        var page = await CanvasPage(); var tracker = await Create(); var a = await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.CurrentValue);
        foreach (var action in new[] { PageAction.Archive, PageAction.RestoreArchive, PageAction.Trash, PageAction.RestoreTrash })
        {
            await Pages().ApplyAsync(PRef(page), action); Assert.Equal(a, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets));
        }
        await Pages().ApplyAsync(PRef(page), PageAction.Trash); await Pages().DeleteAsync(PRef(page));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WidgetInstances;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasItems;")); Assert.NotNull(await service.FindAsync(Ref(tracker)));
    }
    [Fact]
    public async Task WidgetContainerMovementAndDeletionKeepContentAndIdentity()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var task = await JournalTasks().CreateAsync(profile, new("Task"));
        var a = Assert.Single((await WEdit(page, new AddWidget(WidgetType.Task, task.Item.Id, WidgetMode.Card, Parent: container.Id, X: 8, Y: 8))).Widgets);
        var before = (await Layout(page)).Items.Single(h => h.Id == a.CanvasItemId); await Edit(page, new MoveCanvasItems([container.Id], 80, 40));
        Assert.Equal(before, (await Layout(page)).Items.Single(h => h.Id == before.Id)); Assert.Equal(a, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets));
        await Edit(page, new DeleteCanvasItems([container.Id])); Assert.Equal(a, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets)); Assert.Null(Assert.Single((await Layout(page)).Items).ParentContainerId);
    }
    [Fact]
    public async Task WidgetLockedTaskActionsUpdateAllReferencesAndKeepCompletionGuards()
    {
        var page = await CanvasPage(); var tasks = JournalTasks(); var task = await tasks.CreateAsync(profile, new("Task")); await WAdd(page, task.Item, mode: WidgetMode.Checkbox); await WAdd(page, task.Item, mode: WidgetMode.Checkbox);
        await Edit(page, new LockCanvas(true)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); Assert.True(vm.CanInteract); Assert.False(vm.CanConfigure);
        await vm.CompleteAsync(vm.Rows[0]); Assert.All(vm.Rows, r => Assert.True(r.Done)); Assert.Equal(TaskStatus.Done, (await tasks.FindAsync(new(profile, task.Item.Id)))!.Status);
        await vm.CompleteAsync(vm.Rows[1]); Assert.All(vm.Rows, r => Assert.False(r.Done));
        await tasks.CreateSubtaskAsync(new(profile, task.Item.Id), "Child"); await vm.RefreshAsync(); await vm.CompleteAsync(vm.Rows[0]); Assert.NotNull(vm.Rows[0].Error); Assert.All(vm.Rows, r => Assert.False(r.Done));
        Assert.False(await vm.EditAsync(new RemoveWidget(vm.Rows[0].Widget.Id))); await Assert.ThrowsAsync<WidgetValidationException>(() => WEdit(page, new RemoveWidget(vm.Rows[0].Widget.Id)));
    }
    [Fact]
    public async Task WidgetValueTaskUsesCanonicalProgressAndDoesNotOfferGlobalCheckbox()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("20 push-ups", Value: new(TaskValueType.Number, 20, 18))); await WAdd(page, task.Item, mode: WidgetMode.Progress);
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var row = Assert.Single(vm.Rows); Assert.Contains(TaskValuePresentation.Progress(task.Value), row.Summary); Assert.False(row.CanComplete); Assert.False(row.CanCompleteToday);
    }
    [Fact]
    public async Task WidgetRecurringRenderDoesNotMaterializeAndActionOnlyChangesToday()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Recurring")); var recurrence = WidgetRecurrence(); await recurrence.SetRuleAsync(new(profile, task.Item.Id), new(RecurrencePattern.Daily, Day));
        await WAdd(page, task.Item, mode: WidgetMode.Checkbox); var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page));
        Assert.False(vm.Rows[0].CanComplete); Assert.False(vm.Rows[0].CanCompleteToday); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TaskOccurrences;")); Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
        var occurrences = await recurrence.GetRangeAsync(profile, Day, Day.AddDays(1)); await vm.RefreshAsync(); Assert.True(vm.Rows[0].CanCompleteToday); await vm.CompleteAsync(vm.Rows[0]);
        Assert.Equal(TaskStatus.ToDo, (await JournalTasks().FindAsync(new(profile, task.Item.Id)))!.Status);
        Assert.Equal(TaskStatus.Done, (await recurrence.FindAsync(new(profile, occurrences[0].Occurrence.Id))).Occurrence.Status); Assert.Equal(TaskStatus.ToDo, (await recurrence.FindAsync(new(profile, occurrences[1].Occurrence.Id))).Occurrence.Status);
    }
    [Theory]
    [InlineData(TrackerEntryMode.Single, "0.7", 1)][InlineData(TrackerEntryMode.Multiple, "1.2", 2)]
    public async Task WidgetQuickEntryDelegatesCanonicalSingleAndMultipleSemantics(TrackerEntryMode mode, string total, int count)
    {
        var tracker = await Create(Draft(TrackerValueType.CustomUnit) with { EntryMode = mode, Aggregation = mode == TrackerEntryMode.Single ? TrackerAggregation.Last : TrackerAggregation.Sum, Target = new(2.5m) });
        var page = await CanvasPage(); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.QuickEntry); await Edit(page, new LockCanvas(true)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page));
        Assert.Null(vm.Rows[0].Periods!.Current.Value); vm.Rows[0].Input = .5m.ToString(); await vm.RecordAsync(vm.Rows[0]); vm.Rows[0].Input = .7m.ToString(); await vm.RecordAsync(vm.Rows[0]);
        Assert.Null(vm.Rows[0].Error); Assert.Equal(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), vm.Rows[0].Periods!.Current.Value!.Number); Assert.Equal(count, (await service.GetEntriesAsync(Ref(tracker), new(Period: Day))).Count);
    }
    [Fact]
    public async Task WidgetTrackerMissingLatestZeroEndedAndValidationRemainCanonical()
    {
        var tracker = await Create(Historical(TrackerValueType.Integer) with { Target = new(10000) }); await Save(tracker, 5, Day.AddDays(-1)); var page = await CanvasPage();
        await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.CurrentValue); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.Progress); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.QuickEntry);
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var progress = vm.Rows.Single(r => r.Widget.Mode == WidgetMode.Progress); Assert.Null(progress.Periods!.Current.Value); Assert.Equal(5m, progress.Periods.Latest.Value!.Number); Assert.Contains("10000", progress.Summary);
        var quick = vm.Rows.Single(r => r.QuickEntry); quick.Input = 1.5m.ToString(); await vm.RecordAsync(quick); Assert.NotNull(quick.Error); Assert.Null(await Total(tracker));
        quick.Input = "0"; await vm.RecordAsync(quick); Assert.All(vm.Rows, r => Assert.Equal(0m, r.Periods!.Current.Value!.Number));
        clock.Now = Day.AddDays(-40); await vm.RefreshClockAsync(); Assert.All(vm.Rows, r => { Assert.False(r.QuickEntry); Assert.Contains("Not started", r.Summary); });
    }
    [Fact]
    public async Task WidgetTrackerEndedDoesNotAllowQuickEntryOrCreateReplacement()
    {
        var tracker = await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-3), Day.AddDays(-1)) }); var page = await CanvasPage(); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.QuickEntry);
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); Assert.False(vm.Rows[0].QuickEntry); Assert.Contains("Ended", vm.Rows[0].Summary); Assert.Single(await service.GetAsync(profile));
    }
    [Theory]
    [InlineData(AnalyticsPreset.SevenDays, WidgetChartKind.Line, 7)][InlineData(AnalyticsPreset.ThirtyDays, WidgetChartKind.Bar, 30)]
    [InlineData(AnalyticsPreset.NinetyDays, WidgetChartKind.Area, 90)][InlineData(AnalyticsPreset.ThisMonth, WidgetChartKind.Line, 29)]
    public async Task WidgetChartPersistsTypedBoundedSettingsAndUsesAnalytics(AnalyticsPreset range, WidgetChartKind kind, int days)
    {
        var tracker = await Create(Historical()); await Save(tracker, 78.2m); var page = await CanvasPage(); var a = await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart, chart: new(kind, range));
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var result = Assert.Single(vm.Rows).Analytics!; Assert.NotNull(result); Assert.Equal(days, result.Range.Through.DayNumber - result.Range.From.DayNumber + 1); Assert.Equal(78.2m, Assert.Single(result.Series).Summary.Latest!.Number);
        Assert.Equal(new(kind, range), Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets).Chart); Assert.Equal(480, Assert.Single((await Layout(page)).Items).Rect.Width);
        var definition = await service.FindAsync(Ref(tracker)); await WEdit(page, new ConfigureWidget(a.Id, WidgetMode.Chart, new(WidgetChartKind.Area, AnalyticsPreset.SevenDays))); Assert.Equal(definition, await service.FindAsync(Ref(tracker)));
    }
    [Fact]
    public async Task WidgetBooleanChartIsExcludedAndRejectedAtomically()
    {
        var tracker = await Create(Draft(TrackerValueType.Boolean)); var page = await CanvasPage(); var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); Assert.Empty(await vm.CandidatesAsync(WidgetType.TrackerChart));
        await Assert.ThrowsAsync<WidgetOperationException>(() => WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart)); Assert.Empty((await Layout(page)).Items);
    }
    [Fact]
    public async Task WidgetJournalLazySummaryRefreshAndPageLinkIdentityNavigation()
    {
        var page = await CanvasPage(); var journal = await Journal(); var field = await Field(journal); var parent = await Page("Personal"); var target = await Page("Goals");
        await WAdd(page, journal.Item, WidgetType.Journal, WidgetMode.TodaySummary); await WAdd(page, target.Item, WidgetType.PageLink); var nav = new NavigationService(); var vm = WidgetModel(nav); await vm.OpenAsync(PRef(page));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;")); Assert.Contains("No entry", vm.Rows.Single(r => r.Widget.Type == WidgetType.Journal).Summary);
        await JSave(journal, field, new(Text: "Steady progress")); await Pages().UpdateAsync(PRef(target), "New goals", PageIcon.Star); await Pages().MoveAsync(PRef(target), parent.Item.Id); await vm.RefreshAsync();
        Assert.Contains("Steady progress", vm.Rows.Single(r => r.Widget.Type == WidgetType.Journal).Summary); var link = vm.Rows.Single(r => r.Widget.Type == WidgetType.PageLink); Assert.Contains("New goals", link.Title); Assert.Equal("Personal", link.Summary); vm.Open(link); Assert.Equal(new("Pages", target.Item.Id.ToString("D")), nav.Current);
    }
    [Theory]
    [InlineData(false, 0)][InlineData(true, 0)][InlineData(true, 3)][InlineData(false, 2)]
    public async Task WidgetEventUsesLocalDateTimeAndRefreshes(bool allDay, int duration)
    {
        var ev = await WidgetEvents().CreateAsync(profile, new("Event", allDay, Day, allDay ? null : new(10, 0), Day.AddDays(duration), allDay ? null : new(11, 0))); var page = await CanvasPage(); await WAdd(page, ev.Item, WidgetType.Event);
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); Assert.Contains(Day.ToString("d"), vm.Rows[0].Summary); Assert.Contains(allDay ? "All day" : new TimeOnly(10, 0).ToString("t"), vm.Rows[0].Summary); if (duration > 0) Assert.Contains(Day.AddDays(duration).ToString("d"), vm.Rows[0].Summary);
        await WidgetEvents().UpdateAsync(new(profile, ev.Item.Id), new("Renamed event", true, Day.AddDays(1), null, Day.AddDays(1), null)); await vm.RefreshAsync(); Assert.Equal("Renamed event", vm.Rows[0].Title);
    }
    [Fact]
    public async Task WidgetPageAndProfileSwitchClearRowsAndRejectStaleActions()
    {
        var page = await CanvasPage(); var other = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Task")); var a = await WAdd(page, task.Item, mode: WidgetMode.Checkbox); var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var stale = vm.Rows[0];
        await vm.OpenAsync(PRef(other)); Assert.Empty(vm.Rows); await vm.CompleteAsync(stale); Assert.Equal(TaskStatus.ToDo, (await JournalTasks().FindAsync(new(profile, task.Item.Id)))!.Status);
        var second = await profiles.CreateAsync("Isolated"); Assert.Empty(vm.Rows); Assert.Null(vm.Reference); await Assert.ThrowsAsync<WorkspaceChangedException>(() => Widgets().GetAsync(PRef(page)));
        var secondPage = await Pages().CreateAsync(second.Id, "Other"); var secondRef = new WorkspaceItemReference(second.Id, secondPage.Item.Id); await Canvas().EditAsync(secondRef, new LockCanvas(false));
        await Assert.ThrowsAsync<WidgetValidationException>(() => Widgets().EditAsync(secondRef, new AddWidget(WidgetType.Task, task.Item.Id, WidgetMode.Card))); await vm.OpenAsync(secondRef); Assert.Empty(vm.Rows);
        await profiles.SwitchAsync(profile); await vm.OpenAsync(PRef(page)); Assert.Equal(a, Assert.Single(vm.Rows).Widget);
    }
}
