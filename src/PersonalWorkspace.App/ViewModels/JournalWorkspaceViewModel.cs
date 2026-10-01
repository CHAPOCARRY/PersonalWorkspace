using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class JournalWorkspaceViewModel : ObservableObject
{
    private readonly IJournalService service;
    private readonly IOrganizationService organization;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly TimeProvider clock;
    private readonly ILogger<JournalWorkspaceViewModel> logger;
    private WorkspaceItemReference? reference;
    private Guid? observedProfile;
    private JournalDefinition? definition;
    private int revision;
    private bool busy, loading, configuring;
    private string title = "", description = "";
    private string? error, message;
    private DateOnly date, loadedDate;
    private JournalFieldEditor fieldEditor = new();
    public JournalWorkspaceViewModel(IJournalService service, IOrganizationService organization, ICurrentProfile current, INavigationService navigation, TimeProvider clock, ILogger<JournalWorkspaceViewModel> logger)
    {
        this.service = service; this.organization = organization; this.current = current; this.navigation = navigation; this.clock = clock; this.logger = logger;
        observedProfile = current.Current?.Id; date = Today;
        navigation.Changed += (_, _) => { Notify(); _ = ReloadAsync(); };
        current.Changed += (_, _) =>
        {
            if (observedProfile == current.Current?.Id) return;
            observedProfile = current.Current?.Id; Clear();
            if (IsArea && navigation.Current.EntityId is not null) navigation.Navigate(new("Journal")); else _ = ReloadAsync();
        };
    }
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    public bool IsArea => navigation.Current.Destination == "Journal";
    public bool IsNew => IsArea && navigation.Current.EntityId == "new";
    public bool IsSection => navigation.Current.Destination is "Today" or "Archived" or "Trash";
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool HasDetail => IsArea && definition is not null;
    public bool CanConfigure => IsIdle && definition?.Item.DeletedAtUtc is null;
    public bool CanSaveEntry => IsIdle && definition?.IsActive == true;
    public bool ShowDefinition => IsNew || HasDetail && Configuring;
    public bool ShowEntry => HasDetail && !Configuring;
    public bool HasNoJournals => Rows.Count == 0;
    public bool Configuring { get => configuring; set { if (SetProperty(ref configuring, value)) Notify(); } }
    public string Title { get => title; set => SetProperty(ref title, value); }
    public string Description { get => description; set => SetProperty(ref description, value); }
    public string Heading => definition?.Item.Title ?? (IsNew ? "New Journal" : "Journal");
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string? Message { get => message; private set => SetProperty(ref message, value); }
    public string EntryState { get; private set; } = "No entry yet";
    public DateTimeOffset SelectedDate { get => JournalPresentation.Picker(date); set { var next = DateOnly.FromDateTime(value.DateTime); if (next != date && reference is { } r) Navigate(r, next); } }
    public IReadOnlyList<JournalSummaryRow> Rows { get; private set; } = [];
    public JournalSummaryRow? SelectedJournal { get => Rows.FirstOrDefault(r => r.Journal.Item.Id == reference?.ItemId); set { if (value is not null && value.Journal.Item.Id != reference?.ItemId) Open(value with { Date = date }); } }
    public IReadOnlyList<JournalFieldInput> Inputs { get; private set; } = [];
    public IReadOnlyList<JournalField> Fields => definition?.Fields ?? [];
    public IReadOnlyList<OrganizationChoice> Assignments { get; private set; } = [];
    public JournalFieldEditor FieldEditor { get => fieldEditor; private set => SetProperty(ref fieldEditor, value); }
    public JournalSummaryRow? DetailRow => definition is not null && reference is { } r ? new(r.ProfileId, definition, date, "", Inputs.Count > 0) : null;
    public void Clear()
    {
        ++revision; reference = null; definition = null; Rows = []; Inputs = []; Assignments = []; date = Today; loading = false;
        Title = Description = ""; Error = Message = null; configuring = false; FieldEditor = new(); Notify();
    }
    public async Task ReloadAsync()
    {
        var request = ++revision; var profile = current.Current?.Id; var route = navigation.Current;
        loading = true; Error = null; Message = null; Notify();
        try
        {
            if (profile is null) { Clear(); return; }
            if (!IsArea && !IsSection) return;
            var collection = route.Destination switch { "Archived" => JournalCollection.Archived, "Trash" => JournalCollection.Trash, _ => JournalCollection.Active };
            if (IsSection) date = Today;
            Guid? selected = reference?.ProfileId == profile ? reference.ItemId : null;
            if (IsArea && route.EntityId is { } entity && entity != "new")
            {
                var parts = entity.Split('|');
                if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var day)) throw new JournalValidationException("This Journal link is invalid.");
                selected = id; date = day;
            }
            var snapshot = await service.GetAsync(profile.Value, collection, IsNew ? null : date);
            if (request != revision) return;
            Rows = JournalPresentation.Rows(profile.Value, date, snapshot); loadedDate = Today;
            if (IsSection) { definition = null; reference = null; Inputs = []; Assignments = []; return; }
            if (IsNew) { definition = null; reference = null; Title = Description = ""; Inputs = []; Assignments = []; return; }
            selected ??= Rows.FirstOrDefault()?.Journal.Item.Id;
            if (selected is null) { definition = null; reference = null; Inputs = []; Assignments = []; return; }
            if (!snapshot.Journals.Any(j => j.Item.Id == selected)) snapshot = await service.GetRangeAsync(new(profile.Value, selected.Value), date, date);
            if (request != revision) return;
            var item = snapshot.Journals.Single(j => j.Item.Id == selected);
            var entry = snapshot.Entries.SingleOrDefault(e => e.JournalId == selected && e.Date == date);
            var catalog = await organization.GetAsync(profile.Value); if (request != revision) return;
            var r = new WorkspaceItemReference(profile.Value, selected.Value);
            if (reference != r) { configuring = false; FieldEditor = new(); }
            definition = item; reference = r; Title = item.Item.Title; Description = item.Description;
            if (!Rows.Any(row => row.Journal.Item.Id == selected)) Rows = Rows.Concat(JournalPresentation.Rows(profile.Value, date, snapshot)).ToArray();
            Inputs = item.Fields.Select(f => new JournalFieldInput(f, entry?.Values.GetValueOrDefault(f.Id), snapshot.References)).ToArray();
            EntryState = entry?.Values.Count > 0 ? "Saved entry" : "No entry yet — fill any fields and save";
            Assignments = catalog.Tags.Select(t => new OrganizationChoice(r, t.Id, OrganizationKind.Tag, t.Name, t.Color, catalog.ItemTags.Contains(new(r.ItemId, t.Id))))
                .Concat(catalog.Spaces.Where(s => s.ArchivedAtUtc is null || catalog.ItemSpaces.Contains(new(r.ItemId, s.Id))).Select(s => new OrganizationChoice(r, s.Id, OrganizationKind.Space, s.Name, s.Color, catalog.ItemSpaces.Contains(new(r.ItemId, s.Id)), s.ArchivedAtUtc is not null))).ToArray();
        }
        catch (Exception exception) { if (request == revision) { definition = null; reference = null; Inputs = []; Assignments = []; Report(exception); } }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public Task RefreshClockAsync() => !IsBusy && navigation.Current.Destination == "Today" && loadedDate != Today ? ReloadAsync() : Task.CompletedTask;
    private void Navigate(WorkspaceItemReference r, DateOnly day) { if (r.ProfileId == current.Current?.Id) navigation.Navigate(new("Journal", $"{r.ItemId:D}|{day:yyyy-MM-dd}")); }
    [RelayCommand] private void Open(JournalSummaryRow? row) { if (row is not null) Navigate(row.Reference, row.Date); }
    [RelayCommand] private void NewJournal() { Configuring = false; navigation.Navigate(new("Journal", "new")); }
    [RelayCommand] private void Previous() => Shift(-1);
    [RelayCommand] private void Next() => Shift(1);
    private void Shift(int days) { if (!IsIdle || reference is not { } r) return; try { Navigate(r, date.AddDays(days)); } catch (ArgumentOutOfRangeException) { Error = "There are no more dates in this direction."; } }
    [RelayCommand] private void GoToday() { if (reference is { } r) Navigate(r, Today); }
    [RelayCommand] private void Configure() => Configuring = !Configuring;
    [RelayCommand] private void NewField() => FieldEditor = new();
    [RelayCommand] private void EditField(JournalField? field) { if (field is not null) { var editor = new JournalFieldEditor(); editor.Load(field); FieldEditor = editor; } }
    [RelayCommand] private void AddOption() => FieldEditor.Options.Add(new(Guid.NewGuid(), ""));
    [RelayCommand] private void RemoveOption(JournalOptionEditor? option) { if (option is not null) FieldEditor.Options.Remove(option); }
    [RelayCommand] private Task SaveDefinitionAsync() => Run(async () =>
    {
        if (current.Current is not { } profile) return;
        var saved = reference is { } r ? await service.UpdateAsync(r, Title, Description) : await service.CreateAsync(profile.Id, Title, Description);
        Navigate(new(profile.Id, saved.Item.Id), date);
    });
    [RelayCommand] private Task SaveFieldAsync() => Run(async () => { if (reference is { } r) { await service.SaveFieldAsync(r, FieldEditor.Id, FieldEditor.Build()); FieldEditor = new(); } });
    [RelayCommand] private Task SaveEntryAsync() => Run(async () => { if (reference is { } r) await service.SaveEntryAsync(r, date, Inputs.ToDictionary(i => i.Field.Id, i => i.Build())); }, "Entry saved");
    public Task MoveFieldAsync(JournalField field, int direction) => Run(async () => { if (reference is { } r) await service.MoveFieldAsync(r, field.Id, direction); });
    public Task DeleteFieldAsync(JournalField field, bool confirmed) => Run(async () => { if (reference is { } r) { await service.DeleteFieldAsync(r, field.Id, confirmed); FieldEditor = new(); } });
    public Task ApplyAsync(JournalSummaryRow row, JournalAction action) => Run(async () =>
    {
        await service.ApplyAsync(row.Reference, action);
        if (IsArea && row.ProfileId == current.Current?.Id) { reference = null; definition = null; Inputs = []; configuring = false; navigation.Navigate(new("Journal")); }
    });
    public Task DuplicateAsync(JournalSummaryRow row) => Run(async () => { var copy = await service.DuplicateAsync(row.Reference); Navigate(new(row.ProfileId, copy.Item.Id), Today); });
    public Task DeleteAsync(JournalSummaryRow row) => Run(async () =>
    {
        await service.PermanentlyDeleteAsync(row.Reference);
        if (IsArea && reference == row.Reference)
        {
            reference = null; definition = null; Inputs = []; configuring = false;
            navigation.Navigate(new("Journal"));
        }
    });
    [RelayCommand] private Task AssignAsync(OrganizationChoice? choice) => Run(async () => { if (choice is not null) await organization.AssignAsync(choice.Item, choice.Kind, choice.Id, !choice.IsAssigned); });
    private async Task Run(Func<Task> action, string? success = null)
    {
        if (!IsIdle) return; var profile = current.Current?.Id; busy = true; Error = Message = null; Notify();
        try { await action(); if (profile == current.Current?.Id) { await ReloadAsync(); Message = success; } }
        catch (Exception exception) { if (profile == current.Current?.Id) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void Report(Exception exception)
    {
        if (exception is JournalValidationException or JournalOperationException or WorkspaceChangedException or OrganizationOperationException or OrganizationValidationException) Error = exception.Message;
        else { logger.LogError(exception, "Journal presentation failed"); Error = "The Journal could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsArea), nameof(IsNew), nameof(IsSection), nameof(IsBusy), nameof(IsIdle), nameof(HasDetail), nameof(CanConfigure), nameof(CanSaveEntry), nameof(ShowDefinition), nameof(ShowEntry), nameof(HasNoJournals), nameof(Heading), nameof(SelectedDate), nameof(Rows), nameof(SelectedJournal), nameof(Inputs), nameof(Fields), nameof(Assignments), nameof(DetailRow), nameof(EntryState), nameof(Configuring) }) OnPropertyChanged(name);
    }
}
