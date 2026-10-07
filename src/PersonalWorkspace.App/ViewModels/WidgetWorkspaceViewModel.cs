using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed class WidgetContent : ObservableObject
{
    private string? error;
    public required Guid ProfileId { get; init; }
    public required WidgetInstance Widget { get; init; }
    public WorkspaceItem? Source { get; init; }
    public string Title { get; init; } = "Missing reference";
    public string Summary { get; init; } = "";
    public NavigationRoute? Route { get; init; }
    public TaskItem? Task { get; init; }
    public OccurrenceItem? Occurrence { get; init; }
    public TrackerPeriodSummary? Periods { get; init; }
    public TrackerAnalyticsResult? Analytics { get; init; }
    public string Input { get; set; } = "";
    public string? Error { get => error; set => SetProperty(ref error, value); }
    public bool SourceActive => Source is { ArchivedAtUtc: null, DeletedAtUtc: null };
    public string Lifecycle => Source is null ? "Missing reference" : Source.DeletedAtUtc is not null ? "In Trash" : Source.ArchivedAtUtc is not null ? "Archived" : "";
    public bool CanComplete => SourceActive && Task is { IsRecurring: false, ValueType: TaskValueType.Checkbox } && Widget.Mode == WidgetMode.Checkbox;
    public bool CanCompleteToday => SourceActive && Task is { IsRecurring: true, ValueType: TaskValueType.Checkbox } && Occurrence is not null;
    public bool Done => (Occurrence?.Occurrence.Status ?? Task?.Status) == TaskStatus.Done;
    public bool QuickEntry => SourceActive && Widget.Mode == WidgetMode.QuickEntry && Periods?.Current.PeriodDate is not null;
}

public sealed class WidgetWorkspaceViewModel : ObservableObject
{
    private readonly IWidgetService widgets;
    private readonly ITaskService tasks;
    private readonly IRecurrenceService recurrence;
    private readonly ITrackerService trackers;
    private readonly ITrackerAnalyticsService analytics;
    private readonly IEventService events;
    private readonly IJournalService journals;
    private readonly IPageService pages;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly TimeProvider clock;
    private readonly ILogger<WidgetWorkspaceViewModel> logger;
    private int revision;
    private bool loading, saving;
    private DateOnly loadedDate;
    public WidgetWorkspaceViewModel(IWidgetService widgets, ITaskService tasks, IRecurrenceService recurrence, ITrackerService trackers,
        ITrackerAnalyticsService analytics, IEventService events, IJournalService journals, IPageService pages,
        ICurrentProfile current, INavigationService navigation, TimeProvider clock, ILogger<WidgetWorkspaceViewModel> logger)
    {
        this.widgets = widgets; this.tasks = tasks; this.recurrence = recurrence; this.trackers = trackers; this.analytics = analytics;
        this.events = events; this.journals = journals; this.pages = pages; this.current = current; this.navigation = navigation; this.clock = clock; this.logger = logger;
        current.Changed += (_, _) => { if (Reference?.ProfileId != current.Current?.Id) Clear(); };
    }
    public WorkspaceItemReference? Reference { get; private set; }
    public WidgetPage? Page { get; private set; }
    public IReadOnlyList<WidgetContent> Rows { get; private set; } = [];
    public bool IsBusy => loading || saving;
    public bool CanConfigure => !IsBusy && Page?.Canvas is { PageActive: true, Settings.LayoutLocked: false };
    public bool CanInteract => !IsBusy && Page?.Canvas.PageActive == true;
    public string? Error { get; private set; }
    public event Action<CanvasSnapshot>? LayoutChanged;
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    public void Clear() { revision++; Reference = null; Page = null; Rows = []; loading = false; Error = null; Notify(); }
    public void SetCanvas(CanvasSnapshot snapshot)
    {
        if (Page?.Canvas.PageId != snapshot.PageId) return;
        var ids = snapshot.Items.Select(i => i.Id).ToHashSet();
        Page = Page with { Canvas = snapshot, Widgets = Page.Widgets.Where(w => ids.Contains(w.CanvasItemId)).ToArray() };
        Rows = Rows.Where(r => ids.Contains(r.Widget.CanvasItemId)).ToArray(); Notify();
    }
    public async Task OpenAsync(WorkspaceItemReference reference)
    {
        Clear(); if (reference.ProfileId != current.Current?.Id) return;
        Reference = reference; await RefreshAsync();
    }
    public Task RefreshClockAsync() => Reference is not null && loadedDate != Today && !IsBusy ? RefreshAsync() : Task.CompletedTask;
    public async Task RefreshAsync()
    {
        if (IsBusy || Reference is not { } reference) return;
        var request = ++revision; loading = true; Error = null; Notify();
        try
        {
            var page = await widgets.GetAsync(reference); var day = Today;
            var rows = await Resolve(reference.ProfileId, page, day);
            if (request == revision && Reference == reference) { Page = page; Rows = rows; loadedDate = day; }
        }
        catch (Exception e) { if (request == revision) { Rows = []; Error = Message(e); } }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public async Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WidgetType type)
    {
        if (Reference is not { } reference) return [];
        var list = await widgets.CandidatesAsync(reference, type);
        if (type == WidgetType.TrackerChart)
        {
            var numeric = (await analytics.GetCandidatesAsync(reference.ProfileId)).Where(t => t.Settings.Type != TrackerValueType.Boolean).Select(t => t.Item.Id).ToHashSet();
            list = list.Where(s => numeric.Contains(s.Id)).ToArray();
        }
        return Reference == reference ? list : [];
    }
    public async Task<bool> EditAsync(WidgetEdit edit)
    {
        if (!CanConfigure || Reference is not { } reference) return false;
        var request = revision; saving = true; Error = null; Notify(); var success = false;
        try
        {
            var page = await widgets.EditAsync(reference, edit);
            if (request == revision && Reference == reference) { Page = page; LayoutChanged?.Invoke(page.Canvas); success = true; }
        }
        catch (Exception e) { if (request == revision) Error = Message(e); }
        finally { saving = false; Notify(); if (Reference is not null && Reference != reference) await RefreshAsync(); }
        if (success) await RefreshAsync(); return success;
    }
    public void Open(WidgetContent row)
    {
        if (CanInteract && Current(row) && row.Route is { } route) navigation.Navigate(route);
    }
    public Task CompleteAsync(WidgetContent row) => ContentAction(row, async () =>
    {
        if (row.CanComplete) await tasks.ChangeStatusAsync(new(row.ProfileId, row.Task!.Item.Id), row.Done ? TaskStatus.ToDo : TaskStatus.Done);
        else if (row.CanCompleteToday && row.Occurrence is { } occurrence)
        {
            if (occurrence.Occurrence.OccurrenceDate != Today) throw new WidgetValidationException("The local day changed. Refresh before editing today's occurrence.");
            await recurrence.UpdateAsync(new(row.ProfileId, occurrence.Occurrence.Id), occurrence.Occurrence.OccurrenceDate,
                row.Done ? TaskStatus.ToDo : TaskStatus.Done, occurrence.Occurrence.Actual, false);
        }
    });
    public Task RecordAsync(WidgetContent row) => ContentAction(row, async () =>
    {
        if (!row.QuickEntry || row.Periods is not { } periods) return;
        var now = clock.GetLocalNow(); var value = TrackerPresentation.Parse(row.Input, periods.Current.Tracker.Settings.Type);
        await trackers.SaveEntryAsync(new(row.ProfileId, periods.Current.Tracker.Item.Id), DateOnly.FromDateTime(now.DateTime), TimeOnly.FromDateTime(now.DateTime), value);
    });
    private bool Current(WidgetContent row) => Reference?.ProfileId == row.ProfileId && current.Current?.Id == row.ProfileId && Rows.Any(r => ReferenceEquals(r, row));
    private async Task ContentAction(WidgetContent row, Func<Task> action)
    {
        if (!CanInteract || !Current(row) || !row.SourceActive) return;
        var reference = Reference; var request = revision; saving = true; row.Error = null; Notify(); var success = false;
        try { await action(); success = true; }
        catch (Exception e) { if (request == revision) row.Error = Message(e); }
        finally { saving = false; Notify(); if (Reference is not null && Reference != reference) await RefreshAsync(); }
        if (success && Reference == reference && request == revision) await RefreshAsync();
    }
    private async Task<IReadOnlyList<WidgetContent>> Resolve(Guid profile, WidgetPage page, DateOnly today)
    {
        var sourceMap = page.Sources.ToDictionary(s => s.Id);
        bool Active(WidgetInstance w) => w.SourceItemId is { } id && sourceMap.TryGetValue(id, out var s) && s.ArchivedAtUtc is null && s.DeletedAtUtc is null;
        var active = page.Widgets.Where(Active).ToArray(); var errors = new Dictionary<WidgetType, string>();
        TaskGraph? graph = null; IReadOnlyDictionary<Guid, TaskProgress> progress = new Dictionary<Guid, TaskProgress>();
        IReadOnlyList<OccurrenceItem> occurrences = []; IReadOnlyList<TrackerPeriodSummary> periods = [];
        IReadOnlyList<EventItem> eventItems = []; IReadOnlyList<JournalSummaryRow> journalRows = []; PageGraph? pageGraph = null;
        async Task Load(WidgetType type, Func<Task> action)
        {
            if (!active.Any(w => w.Type == type)) return;
            try { await action(); }
            catch (WorkspaceChangedException) { throw; }
            catch (Exception e) { errors[type] = Message(e); }
        }
        await Load(WidgetType.Task, async () =>
        {
            graph = await tasks.GetGraphAsync(profile); progress = graph.Progress();
            if (active.Any(w => w.Type == WidgetType.Task && graph.Tasks.GetValueOrDefault(w.SourceItemId!.Value)?.IsRecurring == true)) occurrences = await recurrence.ReadDayAsync(profile, today);
        });
        await Load(WidgetType.Tracker, async () => periods = await trackers.GetPeriodsAsync(profile, active.Where(w => w.Type == WidgetType.Tracker).Select(w => w.SourceItemId!.Value).Distinct().ToArray(), today));
        await Load(WidgetType.Event, async () => eventItems = await events.GetRangeAsync(profile, DateOnly.MinValue, DateOnly.MaxValue));
        await Load(WidgetType.Journal, async () => journalRows = JournalPresentation.Rows(profile, today, await journals.GetAsync(profile, date: today)));
        await Load(WidgetType.PageLink, async () => pageGraph = await pages.GetAsync(profile));
        var charts = new Dictionary<(Guid, AnalyticsPreset), TrackerAnalyticsResult>(); var chartErrors = new Dictionary<(Guid, AnalyticsPreset), string>();
        foreach (var widget in active.Where(w => w.Type == WidgetType.TrackerChart))
        {
            var key = (widget.SourceItemId!.Value, widget.Chart?.Range ?? AnalyticsPreset.ThirtyDays);
            if (charts.ContainsKey(key) || chartErrors.ContainsKey(key)) continue;
            try { charts[key] = await analytics.GetAsync(profile, [key.Item1], key.Item2); }
            catch (WorkspaceChangedException) { throw; }
            catch (Exception e) { chartErrors[key] = Message(e); }
        }
        return page.Widgets.Select(widget =>
        {
            var source = widget.SourceItemId is { } id ? sourceMap.GetValueOrDefault(id) : null;
            var title = source?.Title ?? "Missing reference"; var summary = ""; NavigationRoute? route = null;
            TaskItem? task = null; OccurrenceItem? occurrence = null; TrackerPeriodSummary? period = null; TrackerAnalyticsResult? chart = null;
            var error = errors.GetValueOrDefault(widget.Type);
            if (source is null) summary = $"Referenced {WidgetRules.SourceType(widget.Type)} no longer exists. Unlock Layout to replace the source or remove this Widget.";
            else if (!Active(widget)) summary = source.DeletedAtUtc is not null ? "Source is in Trash. Restore it in its library." : "Source is archived. Restore it in its library to interact.";
            else switch (widget.Type)
            {
                case WidgetType.Task:
                    task = graph?.Tasks.GetValueOrDefault(source.Id); route = new("Task", source.Id.ToString("D"));
                    if (task is null) break;
                    var todayRows = occurrences.Where(o => o.Definition.Item.Id == source.Id).ToArray(); occurrence = todayRows.Length == 1 ? todayRows[0] : null;
                    summary = task.IsRecurring ? todayRows.Length == 0 ? "Recurring Task · No stored occurrence today. Open the series for its schedule."
                        : "Recurring Task · Today: " + string.Join("; ", todayRows.Select(o => o.Occurrence.Status + " " + TaskValuePresentation.Progress(o.Value)))
                        : task.Status + (widget.Mode == WidgetMode.Card ? $" · Priority: {task.Priority}" + (task.ScheduledDate is { } date ? $" · {date:d}" : "") : "")
                          + (task.Value is null ? "" : "\nValue: " + TaskValuePresentation.Progress(task.Value));
                    if (progress.TryGetValue(source.Id, out var subtask)) summary += "\nSubtasks: " + subtask;
                    if (widget.Mode == WidgetMode.Checkbox && (task.IsRecurring || task.Value is not null)) summary += "\nCompletion uses the occurrence or value rules; open details to record values.";
                    break;
                case WidgetType.Tracker:
                    period = periods.SingleOrDefault(p => p.Current.Tracker.Item.Id == source.Id); route = new("Tracker", source.Id.ToString("D"));
                    if (period is null) break;
                    var selected = widget.Mode == WidgetMode.CurrentValue && period.Current.Value is null ? period.Latest : period.Current;
                    summary = new TrackerRow(profile, selected).Summary;
                    var schedule = period.Current.Tracker.Schedule;
                    summary += today > schedule.EndDate ? "\nEnded" : today < schedule.StartDate ? "\nNot started" : schedule.PeriodOn(today) is null ? "\nNo period today" : "";
                    if (widget.Mode == WidgetMode.Progress && selected.Tracker.Target is null) summary += "\nNo target configured";
                    break;
                case WidgetType.TrackerChart:
                    var key = (source.Id, widget.Chart?.Range ?? AnalyticsPreset.ThirtyDays); chart = charts.GetValueOrDefault(key); error = chartErrors.GetValueOrDefault(key);
                    route = new("Tracker", source.Id.ToString("D")); summary = chart is null ? "" : $"{chart.Range.From:d} – {chart.Range.Through:d} · Complete periods";
                    if (chart?.Series.FirstOrDefault()?.Tracker.Settings.Type == TrackerValueType.Boolean) { chart = null; error = "Boolean Trackers do not have numeric charts. Replace the source with a numeric Tracker."; }
                    break;
                case WidgetType.Event:
                    var ev = eventItems.SingleOrDefault(e => e.Item.Id == source.Id); route = new("Event", source.Id.ToString("D"));
                    if (ev is not null) summary = $"{ev.StartDate:d}" + (ev.EndDate != ev.StartDate ? $" – {ev.EndDate:d}" : "")
                        + (ev.AllDay ? " · All day" : $" · {ev.StartTime:t} – {ev.EndTime:t}") + (ev.IsPast(clock.GetLocalNow().DateTime) ? "\nPast" : "\nUpcoming / ongoing");
                    break;
                case WidgetType.Journal:
                    var journal = journalRows.SingleOrDefault(j => j.Journal.Item.Id == source.Id);
                    summary = journal is null ? "No entry" : $"Today · {journal.State}\n{journal.Summary}"; route = new("Journal", $"{source.Id:D}|{today:yyyy-MM-dd}"); break;
                case WidgetType.PageLink:
                    var target = pageGraph?.Pages.GetValueOrDefault(source.Id);
                    if (target is not null) { title = new PageRow(new(profile, source.Id), target).Label; summary = string.Join(" / ", pageGraph!.Ancestors(source.Id).Select(p => p.Item.Title)); }
                    route = new("Pages", source.Id.ToString("D")); break;
            }
            return new WidgetContent { ProfileId = profile, Widget = widget, Source = source, Title = title, Summary = summary, Route = route,
                Task = task, Occurrence = occurrence, Periods = period, Analytics = chart, Error = error };
        }).ToArray();
    }
    private string Message(Exception e)
    {
        if (e is WidgetValidationException or WidgetOperationException or CanvasValidationException or WorkspaceChangedException or TaskValidationException or TaskOperationException
            or TrackerValidationException or TrackerOperationException or EventValidationException or EventOperationException or JournalValidationException or JournalOperationException or PageValidationException or PageOperationException) return e.Message;
        logger.LogError(e, "Widget presentation failed"); return "This Widget could not be refreshed. Please try again.";
    }
    private void Notify() { foreach (var name in new[] { nameof(Rows), nameof(Page), nameof(IsBusy), nameof(CanConfigure), nameof(CanInteract), nameof(Error) }) OnPropertyChanged(name); }
}
