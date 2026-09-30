using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class TrackerWorkspaceViewModel : ObservableObject
{
    private readonly ITrackerService service;
    private readonly IOrganizationService organization;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly TimeProvider clock;
    private readonly ILogger<TrackerWorkspaceViewModel> logger;
    private Guid? observedProfile, editorProfile;
    private WorkspaceItemReference? reference;
    private TrackerItem? detail;
    private TrackerEntry? editing;
    private int revision;
    private bool busy, loading;
    private string? error;
    private string entryValue = "", entryNote = "";
    private DateTimeOffset? entryDate;
    private DateTimeOffset? historyDate;
    private DateOnly loadedDate;
    private DateOnly? historyPeriod;
    private TrackerEditor editor = new();
    public TrackerWorkspaceViewModel(ITrackerService service, IOrganizationService organization, ICurrentProfile current,
        INavigationService navigation, TimeProvider clock, ILogger<TrackerWorkspaceViewModel> logger, TrackerAnalyticsViewModel? analytics = null)
    {
        this.service = service; this.organization = organization; this.current = current; this.navigation = navigation; this.clock = clock; this.logger = logger;
        Analytics = analytics;
        if (Analytics is not null) Analytics.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(TrackerAnalyticsViewModel.IsBusy)) Notify(); };
        observedProfile = current.Current?.Id;
        navigation.Changed += (_, _) => { Notify(); _ = ReloadAsync(); };
        current.Changed += (_, _) =>
        {
            if (observedProfile == current.Current?.Id) return;
            observedProfile = current.Current?.Id; ++revision; Clear(); Notify();
            if (IsEditor) navigation.Navigate(new("Trackers")); else _ = ReloadAsync();
        };
    }
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    public TrackerAnalyticsViewModel? Analytics { get; }
    public bool IsArea => navigation.Current.Destination is "Trackers" or "Tracker";
    public bool IsLibrary => navigation.Current.Destination == "Trackers";
    public bool IsSection => navigation.Current.Destination is "Today" or "Archived" or "Trash";
    public bool IsEditor => navigation.Current.Destination == "Tracker";
    public bool IsDetail => IsEditor && detail is not null;
    public bool IsNew => IsEditor && navigation.Current.EntityId == "new";
    public bool IsBusy => busy || loading || Analytics?.IsBusy == true;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool CanEdit => IsIdle && editorProfile is not null && detail?.Item.DeletedAtUtc is null;
    public bool CanRecord => CanEdit && detail is { IsActive: true };
    public bool IsBoolean => detail?.Settings.Type == TrackerValueType.Boolean;
    public bool IsNumeric => !IsBoolean;
    public bool CanChooseEntryDate => editing is null;
    public bool HasRows => Rows.Count > 0;
    public string EntryHeading => editing is null ? "Record a value" : "Correct entry";
    public string EntryHint => detail?.Settings.Type == TrackerValueType.Duration ? "minutes:seconds or hours:minutes:seconds" : detail?.Settings.Unit ?? detail?.Settings.CurrencyCode ?? "Value";
    public string EntryButton => editing is not null || detail?.EntryMode == TrackerEntryMode.Single ? "Save value" : "Add value";
    public string Heading => detail?.Item.Title ?? "New Tracker";
    public string DefinitionSummary => detail is null ? "" : $"{detail.Settings.Type} · {TrackerPresentation.Frequency(detail.Schedule)} · {detail.EntryMode} · {detail.Aggregation}"
        + (detail.Schedule.EndDate is { } end ? $" · Ends {end:d}" : "");
    public string CurrentSummary { get; private set; } = "";
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public TrackerEditor Editor { get => editor; private set => SetProperty(ref editor, value); }
    public string EntryValue { get => entryValue; set { if (SetProperty(ref entryValue, value)) OnPropertyChanged(nameof(BooleanEntryValue)); } }
    public string? BooleanEntryValue { get => EntryValue is "true" or "false" ? EntryValue : null; set => EntryValue = value ?? ""; }
    public string EntryNote { get => entryNote; set => SetProperty(ref entryNote, value); }
    public DateTimeOffset? EntryDate { get => entryDate; set => SetProperty(ref entryDate, value); }
    public DateTimeOffset? HistoryDate { get => historyDate; set => SetProperty(ref historyDate, value); }
    public IReadOnlyList<string> BooleanValues { get; } = new[] { "true", "false" };
    public IReadOnlyList<TrackerRow> Rows { get; private set; } = [];
    public IReadOnlyList<TrackerEntryRow> Entries { get; private set; } = [];
    public IReadOnlyList<OrganizationChoice> Assignments { get; private set; } = [];
    public TrackerRow? DetailRow => detail is { } item && reference is { } r ? new(r.ProfileId, new(item, null, null, 0, false)) : null;

    public async Task ReloadAsync(bool preserveDraft = false)
    {
        var request = ++revision; var profile = current.Current?.Id; var route = navigation.Current;
        loading = true; Error = null; Notify();
        if (!preserveDraft)
        {
            if (reference?.ItemId.ToString("D") != route.EntityId) Analytics?.Clear();
            detail = null; reference = null; editorProfile = null; Entries = []; Assignments = []; CurrentSummary = ""; historyPeriod = null; HistoryDate = null; ResetEntry();
        }
        try
        {
            if (profile is null) { Clear(); return; }
            if (!IsEditor || IsNew) Analytics?.Clear();
            if (IsLibrary || IsSection)
            {
                var collection = route.Destination switch { "Archived" => TrackerCollection.Archived, "Trash" => TrackerCollection.Trash, _ => TrackerCollection.Active };
                var rows = await service.GetAsync(profile.Value, collection, route.Destination == "Today");
                if (request != revision) return;
                Rows = rows.Select(p => new TrackerRow(profile.Value, p)).ToArray(); loadedDate = Today;
            }
            else if (IsEditor)
            {
                if (route.EntityId == "new")
                {
                    Editor = new() { StartDate = TrackerEditor.Picker(Today) }; editorProfile = profile;
                }
                else if (Guid.TryParse(route.EntityId, out var id))
                {
                    var r = new WorkspaceItemReference(profile.Value, id);
                    var item = await service.FindAsync(r) ?? throw new TrackerValidationException("This Tracker is no longer available.");
                    var recent = await service.GetEntriesAsync(r, historyPeriod is { } history ? new(Period: history) : new(Recent: 50));
                    var period = item.Schedule.PeriodOn(Today);
                    var summary = period is { } date ? await service.GetPeriodAsync(r, date) : null;
                    var catalog = await organization.GetAsync(profile.Value);
                    if (request != revision) return;
                    detail = item; reference = r; editorProfile = profile;
                    if (!preserveDraft) { Editor = new(); Editor.Load(item.Draft, item.HasEntries); }
                    else Editor.Locked = item.HasEntries;
                    Entries = recent.Select(e => new TrackerEntryRow(r, e, item.Settings)).ToArray();
                    CurrentSummary = summary is null ? "No current expected period" : new TrackerRow(profile.Value, summary).Summary;
                    Assignments = catalog.Tags.Select(t => new OrganizationChoice(r, t.Id, OrganizationKind.Tag, t.Name, t.Color, catalog.ItemTags.Contains(new(id, t.Id))))
                        .Concat(catalog.Spaces.Where(s => s.ArchivedAtUtc is null || catalog.ItemSpaces.Contains(new(id, s.Id))).Select(s =>
                            new OrganizationChoice(r, s.Id, OrganizationKind.Space, s.Name, s.Color, catalog.ItemSpaces.Contains(new(id, s.Id)), s.ArchivedAtUtc is not null))).ToArray();
                    if (!preserveDraft) ResetEntry();
                    if (Analytics is not null) await Analytics.SetTrackerAsync(r);
                }
            }
        }
        catch (Exception exception) { if (request == revision) Report(exception); }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public async Task RefreshClockAsync()
    {
        if (!IsBusy && (IsLibrary || navigation.Current.Destination == "Today") && loadedDate != Today) await ReloadAsync();
    }
    [RelayCommand] private void NewTracker() => navigation.Navigate(new("Tracker", "new"));
    [RelayCommand] private void Back() => navigation.Navigate(new("Trackers"));
    [RelayCommand] private void Open(TrackerRow? row)
    {
        if (row is not null && row.ProfileId == current.Current?.Id) navigation.Navigate(new("Tracker", row.Reference.ItemId.ToString("D")));
    }
    [RelayCommand] private Task SaveDefinitionAsync() => Run(async () =>
    {
        if (editorProfile is not { } profile) return;
        var draft = Editor.Build(); var saved = reference is { } r ? await service.UpdateAsync(r, draft) : await service.CreateAsync(profile, draft);
        if (current.Current?.Id == profile) navigation.Navigate(new("Tracker", saved.Item.Id.ToString("D")));
    });
    [RelayCommand] private Task SaveEntryAsync() => Run(async () =>
    {
        if (reference is not { } r || detail is null) return;
        var date = TrackerEditor.LocalDate(EntryDate) ?? throw new TrackerValidationException("Choose an entry date.");
        await service.SaveEntryAsync(r, date, editing?.LocalTime ?? TimeOnly.FromDateTime(clock.GetLocalNow().DateTime),
            TrackerPresentation.Parse(EntryValue, detail.Settings.Type), EntryNote, editing?.Id);
        if (reference == r) ResetEntry();
    }, true);
    [RelayCommand] private void EditEntry(TrackerEntryRow? row)
    {
        if (row is null || row.Reference != reference) return;
        editing = row.Entry; EntryDate = TrackerEditor.Picker(editing.LocalDate);
        EntryValue = TrackerPresentation.Input(editing.Value, row.Settings.Type); EntryNote = editing.Note ?? ""; Notify();
    }
    [RelayCommand] private void CancelEntry() { ResetEntry(); Notify(); }
    [RelayCommand] private Task LoadPeriodAsync() => Run(async () =>
    {
        if (reference is not { } r || detail is null) return;
        var date = TrackerEditor.LocalDate(HistoryDate) ?? throw new TrackerValidationException("Choose a history date.");
        var period = detail.Schedule.PeriodOn(date) ?? throw new TrackerValidationException("There is no Tracker period on this date.");
        var entries = await service.GetEntriesAsync(r, new(Period: period));
        if (reference == r) { historyPeriod = period; Entries = entries.Select(e => new TrackerEntryRow(r, e, detail.Settings)).ToArray(); }
    }, true, false);
    [RelayCommand] private Task RecentEntriesAsync() { historyPeriod = null; return ReloadAsync(true); }
    public Task DeleteEntryAsync(TrackerEntryRow row) => Run(async () => { await service.DeleteEntryAsync(row.Reference, row.Entry.Id); if (reference == row.Reference) ResetEntry(); }, true);
    public Task ApplyAsync(TrackerRow row, TrackerAction action) => Run(async () => { await service.ApplyAsync(row.Reference, action); });
    public Task DuplicateAsync(TrackerRow row) => Run(async () =>
    {
        var copy = await service.DuplicateAsync(row.Reference);
        if (row.ProfileId == current.Current?.Id) navigation.Navigate(new("Tracker", copy.Item.Id.ToString("D")));
    });
    public Task PermanentlyDeleteAsync(TrackerRow row) => Run(async () =>
    {
        await service.PermanentlyDeleteAsync(row.Reference);
        if (reference == row.Reference) navigation.Navigate(new("Trackers"));
    });
    [RelayCommand] private Task AssignAsync(OrganizationChoice? choice) => choice is null ? Task.CompletedTask : Run(async () =>
        await organization.AssignAsync(choice.Item, choice.Kind, choice.Id, !choice.IsAssigned), true);
    private async Task Run(Func<Task> action, bool preserveDraft = false, bool reload = true)
    {
        if (!IsIdle) return;
        var profile = current.Current?.Id; var route = navigation.Current;
        busy = true; Error = null; Notify();
        try { await action(); if (reload && profile == current.Current?.Id && route == navigation.Current) await ReloadAsync(preserveDraft); }
        catch (Exception exception) { if (profile == current.Current?.Id && route == navigation.Current) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void ResetEntry() { editing = null; EntryValue = ""; EntryNote = ""; EntryDate = TrackerEditor.Picker(Today); }
    private void Clear() { Rows = []; Entries = []; Assignments = []; detail = null; reference = null; editorProfile = null; Editor = new(); ResetEntry(); historyPeriod = null; HistoryDate = null; CurrentSummary = ""; Error = null; }
    private void Report(Exception exception)
    {
        if (exception is TrackerValidationException or TrackerOperationException or WorkspaceChangedException or OrganizationValidationException or OrganizationOperationException) Error = exception.Message;
        else { logger.LogError(exception, "Tracker presentation operation failed"); Error = "The Tracker could not be loaded or saved. Please try again."; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsArea), nameof(IsLibrary), nameof(IsSection), nameof(IsEditor), nameof(IsDetail), nameof(IsNew), nameof(IsBusy), nameof(IsIdle),
            nameof(CanEdit), nameof(CanRecord), nameof(IsBoolean), nameof(IsNumeric), nameof(CanChooseEntryDate), nameof(HasRows), nameof(EntryHeading), nameof(EntryHint),
            nameof(EntryButton), nameof(Heading), nameof(DefinitionSummary), nameof(CurrentSummary), nameof(Rows), nameof(Entries), nameof(Assignments), nameof(DetailRow) }) OnPropertyChanged(name);
    }
}
