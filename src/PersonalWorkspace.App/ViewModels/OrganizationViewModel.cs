using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record OrganizationRow(Guid ProfileId, Guid Id, OrganizationKind Kind, string Name, OrganizationColor Color,
    string Description = "", string Icon = "", bool IsArchived = false, int AssignmentCount = 0)
{
    public string Label => Kind == OrganizationKind.Tag ? "#" + Name : Name;
    public string ArchiveLabel => IsArchived ? "Restore" : "Archive";
    public string State => IsArchived ? "Archived" : "";
    public bool IsTag => Kind == OrganizationKind.Tag;
    public bool IsSpace => !IsTag;
}

public sealed partial class OrganizationViewModel : ObservableObject
{
    private readonly IOrganizationService service;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly ILogger<OrganizationViewModel> logger;
    private Guid? observedProfile;
    private int revision;
    private bool busy, loading;
    private OrganizationSnapshot snapshot = OrganizationSnapshot.Empty;
    private OrganizationRow? editing;
    private OrganizationKind kind = OrganizationKind.Space;
    private string name = "", description = "", icon = "";
    private OrganizationColor color;
    private string? error;

    public OrganizationViewModel(IOrganizationService service, ICurrentProfile current, INavigationService navigation, ILogger<OrganizationViewModel> logger)
    {
        this.service = service; this.current = current; this.navigation = navigation; this.logger = logger;
        observedProfile = current.Current?.Id;
        current.Changed += (_, _) =>
        {
            if (observedProfile == current.Current?.Id) return;
            observedProfile = current.Current?.Id;
            ++revision;
            snapshot = OrganizationSnapshot.Empty;
            ClearEditor();
            Error = null;
            Notify();
            _ = ReloadAsync();
        };
        navigation.Changed += (_, _) => { if (navigation.Current.Destination == "Organization") _ = ReloadAsync(); };
    }
    public IEnumerable<OrganizationRow> ActiveSpaces => SpaceRows.Where(row => !row.IsArchived);
    private IEnumerable<OrganizationRow> SpaceRows => snapshot.Spaces.Select(space => new OrganizationRow(observedProfile ?? Guid.Empty,
        space.Id, OrganizationKind.Space, space.Name, space.Color, space.Description, space.Icon, space.ArchivedAtUtc is not null,
        snapshot.ItemSpaces.Count(link => link.EntityId == space.Id)));
    public IEnumerable<OrganizationRow> Rows => IsSpace ? SpaceRows : snapshot.Tags.Select(tag => new OrganizationRow(observedProfile ?? Guid.Empty,
        tag.Id, OrganizationKind.Tag, tag.Name, tag.Color, AssignmentCount: snapshot.ItemTags.Count(link => link.EntityId == tag.Id)));
    public bool IsSpace => kind == OrganizationKind.Space;
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public string Heading => IsSpace ? "Manage spaces" : "Manage tags";
    public string EditorHeading => editing is null ? (IsSpace ? "New space" : "New tag") : "Edit " + (IsSpace ? "space" : "tag");
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Description { get => description; set => SetProperty(ref description, value); }
    public string Icon { get => icon; set => SetProperty(ref icon, value); }
    public OrganizationColor Color { get => color; set => SetProperty(ref color, value); }
    public IReadOnlyList<OrganizationColor> Colors { get; } = Enum.GetValues<OrganizationColor>();
    public string? Error { get => error; private set => SetProperty(ref error, value); }

    public async Task ReloadAsync()
    {
        var request = ++revision;
        var profile = current.Current?.Id;
        loading = true; Notify();
        try
        {
            var loaded = profile is { } id ? await service.GetAsync(id) : OrganizationSnapshot.Empty;
            if (request == revision) snapshot = loaded;
        }
        catch (Exception exception) { if (request == revision) { snapshot = OrganizationSnapshot.Empty; Report(exception); } }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    [RelayCommand] private void ShowSpaces() { kind = OrganizationKind.Space; ClearEditor(); Notify(); }
    [RelayCommand] private void ShowTags() { kind = OrganizationKind.Tag; ClearEditor(); Notify(); }
    [RelayCommand] private void New() { ClearEditor(); Notify(); }
    public void NewSpace() { kind = OrganizationKind.Space; ClearEditor(); Notify(); navigation.Navigate(new("Organization")); }
    [RelayCommand] private void Edit(OrganizationRow row)
    {
        if (!IsIdle || row.ProfileId != current.Current?.Id) return;
        editing = row; kind = row.Kind; Name = row.Name; Description = row.Description; Icon = row.Icon; Color = row.Color; Notify();
    }
    [RelayCommand] private Task SaveAsync() => MutateAsync(async profile =>
    {
        await service.SaveAsync(profile, kind, editing?.Id, new(Name, Color, Description, Icon));
        if (profile == current.Current?.Id) ClearEditor();
    });
    [RelayCommand] private Task ArchiveAsync(OrganizationRow row) => MutateAsync(profile =>
        row.ProfileId == profile ? service.ArchiveSpaceAsync(profile, row.Id, !row.IsArchived) : throw new WorkspaceChangedException());
    [RelayCommand] private Task DeleteTagConfirmedAsync(OrganizationRow row) => MutateAsync(async profile =>
    {
        if (row.ProfileId != profile) throw new WorkspaceChangedException();
        await service.DeleteTagAsync(profile, row.Id);
        if (editing?.Id == row.Id) ClearEditor();
    });
    private async Task MutateAsync(Func<Guid, Task> action)
    {
        if (!IsIdle || current.Current is not { } profile) return;
        busy = true; Error = null; Notify();
        try { await action(profile.Id); if (profile.Id == current.Current?.Id) await ReloadAsync(); }
        catch (Exception exception) { if (profile.Id == current.Current?.Id) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void ClearEditor() { editing = null; Name = Description = Icon = ""; Color = OrganizationColor.None; Error = null; }
    private void Report(Exception exception)
    {
        if (exception is OrganizationValidationException or OrganizationOperationException or WorkspaceChangedException) Error = exception.Message;
        else { logger.LogError(exception, "Organization presentation operation failed"); Error = "Tags and spaces could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var property in new[] { nameof(ActiveSpaces), nameof(Rows), nameof(IsSpace), nameof(Heading), nameof(EditorHeading), nameof(IsBusy), nameof(IsIdle) }) OnPropertyChanged(property);
    }
}
