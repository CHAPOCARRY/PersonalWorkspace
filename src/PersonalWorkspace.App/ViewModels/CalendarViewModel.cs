using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly IEventService events;
    private readonly ITaskService tasks;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly TimeProvider clock;
    private readonly ILogger<CalendarViewModel> logger;
    private Guid? observedProfile;
    private int revision;
    private bool loading, busy;
    private string? error;
    private CalendarMode mode;
    private DateOnly selectedDate;
    private string editorTitle = "";
    private bool allDay;
    private DateTimeOffset? startDate, endDate;
    private TimeSpan? startTime, endTime;
    private WorkspaceItemReference? editorReference;
    private Guid? editorProfile;
    private EventItem? detail;
    private NavigationRoute returnRoute = new("Calendar");

    public CalendarViewModel(IEventService events, ITaskService tasks, ICurrentProfile current, INavigationService navigation, TimeProvider clock, ILogger<CalendarViewModel> logger)
    {
        this.events = events; this.tasks = tasks; this.current = current; this.navigation = navigation; this.clock = clock; this.logger = logger;
        selectedDate = Today;
        observedProfile = current.Current?.Id;
        navigation.Changed += (_, _) => { Notify(); _ = ReloadAsync(); };
        current.Changed += (_, _) =>
        {
            if (observedProfile == current.Current?.Id) return;
            observedProfile = current.Current?.Id; ++revision;
            Days = []; Unscheduled = []; EventRows = []; detail = null; editorReference = null; editorProfile = null;
            EditorTitle = ""; StartDate = EndDate = null; StartTime = EndTime = null; Error = null;
            selectedDate = Today; returnRoute = new("Calendar"); Notify();
            if (IsEditor) navigation.Navigate(new("Calendar")); else _ = ReloadAsync();
        };
    }
    private DateTime LocalNow => clock.GetLocalNow().DateTime;
    private DateOnly Today => DateOnly.FromDateTime(LocalNow);
    public bool IsCalendarArea => navigation.Current.Destination is "Calendar" or "Event";
    public bool IsCalendar => navigation.Current.Destination == "Calendar";
    public bool IsEditor => navigation.Current.Destination == "Event";
    public bool IsDetail => IsEditor && editorReference is not null;
    public bool IsEventList => navigation.Current.Destination is "Today" or "Archived" or "Trash";
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool CanSave => IsIdle && editorProfile is not null;
    public bool IsTimed => !AllDay;
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public CalendarMode Mode { get => mode; set { if (SetProperty(ref mode, value)) _ = ReloadAsync(); } }
    public IReadOnlyList<CalendarMode> Modes { get; } = Enum.GetValues<CalendarMode>();
    public DateTimeOffset SelectedDate { get => ToPicker(selectedDate); set { var date = DateOnly.FromDateTime(value.DateTime); if (date != selectedDate) { selectedDate = date; Notify(); _ = ReloadAsync(); } } }
    public string PeriodTitle => Mode == CalendarMode.Month ? selectedDate.ToString("Y", CultureInfo.CurrentCulture)
        : Mode == CalendarMode.Day ? selectedDate.ToString("D", CultureInfo.CurrentCulture)
        : $"{VisibleRange.From:d} – {VisibleRange.Through:d}";
    public (DateOnly From, DateOnly Through) VisibleRange => CalendarPeriods.Range(selectedDate, Mode, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
    public IReadOnlyList<CalendarDay> Days { get; private set; } = [];
    public IReadOnlyList<CalendarEntry> Unscheduled { get; private set; } = [];
    public IReadOnlyList<CalendarEntry> EventRows { get; private set; } = [];
    public bool HasNoEvents => EventRows.Count == 0;
    public string EditorHeading => IsDetail ? "Event detail" : "New event";
    public string EditorTitle { get => editorTitle; set => SetProperty(ref editorTitle, value); }
    public bool AllDay { get => allDay; set { if (SetProperty(ref allDay, value)) OnPropertyChanged(nameof(IsTimed)); } }
    public DateTimeOffset? StartDate { get => startDate; set => SetProperty(ref startDate, value); }
    public DateTimeOffset? EndDate { get => endDate; set => SetProperty(ref endDate, value); }
    public TimeSpan? StartTime { get => startTime; set => SetProperty(ref startTime, value); }
    public TimeSpan? EndTime { get => endTime; set => SetProperty(ref endTime, value); }
    public string CreatedText => detail?.Item.CreatedAtUtc.ToLocalTime().ToString("g") ?? "";
    public string ModifiedText => detail?.Item.UpdatedAtUtc.ToLocalTime().ToString("g") ?? "";
    public CalendarEntry? DetailRow => detail is { } item && editorProfile is { } profile ? Entry(profile, item, item.StartDate) : null;

    public async Task ReloadAsync()
    {
        var request = ++revision; var profile = current.Current?.Id; var route = navigation.Current;
        loading = true; Error = null; Notify();
        try
        {
            if (profile is null) return;
            if (IsCalendar)
            {
                var range = VisibleRange;
                var scheduled = await tasks.GetScheduledAsync(profile.Value, range.From, range.Through);
                if (request != revision) return;
                var calendarEvents = await events.GetRangeAsync(profile.Value, range.From, range.Through);
                if (request != revision) return;
                var unscheduled = await tasks.GetUnscheduledAsync(profile.Value);
                if (request != revision) return;
                var now = LocalNow;
                Days = Enumerable.Range(range.From.DayNumber, range.Through.DayNumber - range.From.DayNumber + 1).Select(number =>
                {
                    var date = DateOnly.FromDayNumber(number);
                    var entries = scheduled.Where(task => task.ScheduledDate == date).Select(task => new CalendarEntry(profile.Value, date, task, null, now))
                        .Concat(calendarEvents.Where(item => item.OccursOn(date)).Select(item => Entry(profile.Value, item, date))).ToArray();
                    return new CalendarDay(date, date == Today, date.Month == selectedDate.Month, entries);
                }).ToArray();
                Unscheduled = unscheduled.Select(task => new CalendarEntry(profile.Value, selectedDate, task, null, now)).ToArray();
            }
            else if (IsEditor)
            {
                if (route.EntityId == "new")
                {
                    editorReference = null; editorProfile = profile; detail = null; EditorTitle = ""; AllDay = false;
                    StartDate = EndDate = ToPicker(selectedDate); StartTime = TimeSpan.FromHours(9); EndTime = TimeSpan.FromHours(10);
                }
                else
                {
                    if (!Guid.TryParse(route.EntityId, out var id)) throw new EventValidationException("This event link is invalid.");
                    var item = await events.FindAsync(new(profile.Value, id));
                    if (request != revision) return;
                    if (item is null || item.Item.DeletedAtUtc is not null) throw new EventValidationException("This event is unavailable here. Check Trash.");
                    detail = item; editorReference = new(profile.Value, id); editorProfile = profile;
                    EditorTitle = item.Item.Title; AllDay = item.AllDay; StartDate = ToPicker(item.StartDate); EndDate = ToPicker(item.EndDate);
                    StartTime = item.StartTime?.ToTimeSpan(); EndTime = item.EndTime?.ToTimeSpan();
                }
            }
            else if (IsEventList)
            {
                var list = route.Destination == "Today" ? await events.GetRangeAsync(profile.Value, Today, Today)
                    : await events.GetCollectionAsync(profile.Value, route.Destination == "Trash" ? EventCollection.Trash : EventCollection.Archived);
                if (request != revision) return;
                EventRows = list.Select(item => Entry(profile.Value, item, route.Destination == "Today" ? Today : item.StartDate)).ToArray();
            }
        }
        catch (Exception exception)
        {
            if (request == revision) { Days = []; Unscheduled = []; EventRows = []; editorProfile = null; editorReference = null; detail = null; Report(exception); }
        }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public Task RefreshClockAsync() => !IsBusy && (IsCalendar || navigation.Current.Destination == "Today") ? ReloadAsync() : Task.CompletedTask;
    public void ReportDragFailure(Exception exception)
    {
        logger.LogWarning(exception, "Native Calendar drag could not start");
        Error = "Dragging is unavailable. Open the item to change its date, or use Schedule for selected date for an unscheduled task.";
    }
    private CalendarEntry Entry(Guid profile, EventItem item, DateOnly date) => new(profile, date, null, item, LocalNow);
    private static DateTimeOffset ToPicker(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue));
    [RelayCommand] private void Previous() => Shift(-1);
    [RelayCommand] private void Next() => Shift(1);
    private void Shift(int direction)
    {
        if (!IsIdle) return;
        try { SelectedDate = ToPicker(Mode == CalendarMode.Month ? selectedDate.AddMonths(direction) : selectedDate.AddDays(direction * (Mode == CalendarMode.Week ? 7 : 1))); }
        catch (ArgumentOutOfRangeException) { Error = "There are no more dates in this direction."; }
    }
    [RelayCommand] private void GoToday() { selectedDate = Today; Notify(); _ = ReloadAsync(); }
    [RelayCommand] private void OpenDay(CalendarDay day) { selectedDate = day.Date; mode = CalendarMode.Day; Notify(); _ = ReloadAsync(); }
    [RelayCommand] private void DayView() { Mode = CalendarMode.Day; }
    [RelayCommand] private void NewEvent()
    {
        if (!IsIdle) return;
        returnRoute = navigation.Current.Destination == "Today" ? navigation.Current : new("Calendar");
        navigation.Navigate(new("Event", "new"));
    }
    [RelayCommand] private void NewTask()
    {
        if (IsIdle) navigation.Navigate(new("Tasks", "new:" + selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }
    [RelayCommand] private void Open(CalendarEntry entry)
    {
        if (!IsIdle || entry.ProfileId != current.Current?.Id || !entry.CanOpen) return;
        returnRoute = navigation.Current;
        navigation.Navigate(new(entry.IsTask ? "Task" : "Event", entry.Id.ToString("D")));
    }
    [RelayCommand] private void Back() => navigation.Navigate(returnRoute);
    [RelayCommand] private Task SaveAsync() => MutateAsync(async () =>
    {
        if (editorProfile is not { } profile || StartDate is null || EndDate is null) throw new EventValidationException("Choose both a start date and an end date.");
        var draft = new EventDraft(EditorTitle, AllDay, DateOnly.FromDateTime(StartDate.Value.DateTime), ToTime(StartTime), DateOnly.FromDateTime(EndDate.Value.DateTime), ToTime(EndTime));
        var item = editorReference is { } reference ? await events.UpdateAsync(reference, draft) : await events.CreateAsync(profile, draft);
        if (current.Current?.Id == profile) navigation.Navigate(new("Event", item.Item.Id.ToString("D")));
    });
    private static TimeOnly? ToTime(TimeSpan? time) => time is { } value && value >= TimeSpan.Zero && value < TimeSpan.FromDays(1) ? TimeOnly.FromTimeSpan(value) : null;
    [RelayCommand] private Task ScheduleAsync(CalendarEntry entry) => MoveAsync(entry, selectedDate);
    public Task MoveAsync(CalendarEntry entry, DateOnly target) => MutateAsync(async () =>
    {
        if (entry.IsTask) await tasks.ScheduleAsync(new(entry.ProfileId, entry.Id), target);
        else
        {
            DateOnly start;
            try { start = entry.Event!.StartDate.AddDays(target.DayNumber - entry.DisplayDate.DayNumber); }
            catch (ArgumentOutOfRangeException) { throw new EventValidationException("The moved event would be outside the supported calendar dates."); }
            await events.MoveAsync(entry.Reference, start);
        }
    });
    [RelayCommand] private Task ArchiveAsync(CalendarEntry entry) => ApplyAsync(entry, EventAction.Archive);
    [RelayCommand] private Task TrashAsync(CalendarEntry entry) => ApplyAsync(entry, EventAction.Trash);
    [RelayCommand] private Task RestoreAsync(CalendarEntry entry) => ApplyAsync(entry, entry.IsDeleted ? EventAction.RestoreTrash : EventAction.RestoreArchive);
    [RelayCommand] private Task PermanentlyDeleteConfirmedAsync(CalendarEntry entry) => MutateAsync(() => events.PermanentlyDeleteAsync(entry.Reference));
    private Task ApplyAsync(CalendarEntry entry, EventAction action) => MutateAsync(async () =>
    {
        await events.ApplyAsync(entry.Reference, action);
        if (IsEditor && current.Current?.Id == entry.ProfileId) navigation.Navigate(returnRoute);
    });
    private async Task MutateAsync(Func<Task> action)
    {
        if (!IsIdle) return;
        var profile = current.Current?.Id; busy = true; Error = null; Notify();
        try { await action(); if (profile == current.Current?.Id) await ReloadAsync(); }
        catch (Exception exception) { if (profile == current.Current?.Id) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void Report(Exception exception)
    {
        if (exception is EventValidationException or EventOperationException or TaskValidationException or TaskOperationException or WorkspaceChangedException) Error = exception.Message;
        else { logger.LogError(exception, "Calendar presentation operation failed"); Error = "Calendar could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var property in new[] { nameof(IsCalendarArea), nameof(IsCalendar), nameof(IsEditor), nameof(IsDetail), nameof(IsEventList), nameof(IsBusy), nameof(IsIdle),
            nameof(CanSave), nameof(SelectedDate), nameof(Mode), nameof(PeriodTitle), nameof(Days), nameof(Unscheduled), nameof(EventRows), nameof(HasNoEvents),
            nameof(EditorHeading), nameof(CreatedText), nameof(ModifiedText), nameof(DetailRow) }) OnPropertyChanged(property);
    }
}
