using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class TaskWorkspaceViewModel : ObservableObject
{
    private readonly ITaskService service;
    private readonly IOrganizationService organization;
    private OrganizationSnapshot catalog = OrganizationSnapshot.Empty;
    private OrganizationFilter? tagFilter, spaceFilter;
    private NavigationRoute returnRoute = new("Tasks");
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly ILogger<TaskWorkspaceViewModel> logger;
    private Guid? observedProfile;
    private int revision;
    private bool busy;
    private bool loading;
    private string? error;
    private string filter = "";
    private IReadOnlyList<TaskRowViewModel> rows = [];
    private TaskReference? editorReference;
    private Guid? editorProfile;
    private TaskRowViewModel? detail;
    private string title = "";
    private string description = "";
    private TaskStatus status;
    private TaskPriority priority;
    private DateTimeOffset? scheduledDate;
    private DateOnly displayedToday = DateOnly.FromDateTime(DateTime.Now);

    public TaskWorkspaceViewModel(ITaskService service, ICurrentProfile current, INavigationService navigation, ILogger<TaskWorkspaceViewModel> logger, IOrganizationService organization)
    {
        this.service = service;
        this.organization = organization;
        this.current = current;
        this.navigation = navigation;
        this.logger = logger;
        observedProfile = current.Current?.Id;
        current.Changed += (_, _) => OnProfileChanged();
        navigation.Changed += (_, _) => { OnPropertyChanged(nameof(IsTaskArea)); _ = ReloadAsync(); };
    }

    public bool IsTaskArea => navigation.Current.Destination is "Tasks" or "Today" or "Archived" or "Trash" or "Task" or "Space";
    public bool IsEditor => navigation.Current.Destination == "Task" || navigation.Current.Destination == "Tasks" &&
        (navigation.Current.EntityId == "new" || navigation.Current.EntityId?.StartsWith("new:", StringComparison.Ordinal) == true);
    public bool IsDetail => navigation.Current.Destination == "Task";
    public bool IsList => !IsEditor;
    public bool IsIdle => !busy && !loading && current.Current is not null;
    public bool IsBusy => busy || loading;
    public bool CanSave => IsIdle && editorProfile is not null;
    public bool IsEmpty => !loading && !IsEditor && !Rows.Any();
    public bool CanFilter => (navigation.Current.Destination is "Tasks" or "Space") && !IsEditor;
    public bool CanChooseSpaceFilter => navigation.Current.Destination == "Tasks" && !IsEditor;
    private Guid? RouteSpaceId => navigation.Current.Destination == "Space" && Guid.TryParse(navigation.Current.EntityId, out var id) ? id : null;
    public bool IsToday => navigation.Current.Destination == "Today";
    public string Heading => IsEditor ? (IsDetail ? "Task detail" : "New task") : navigation.Current.Destination == "Space"
        ? "Space — " + (catalog.Spaces.FirstOrDefault(space => space.Id == RouteSpaceId)?.Name ?? "Unavailable") : navigation.Current.Destination;
    public string EmptyMessage => IsToday ? "No tasks scheduled for today." : CanFilter && (filter.Length > 0 || TagFilter?.Id is not null || SpaceFilter?.Id is not null)
        ? "No tasks match your filters." : "No tasks here yet.";
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string Filter { get => filter; set { if (SetProperty(ref filter, value)) NotifyRows(); } }
    public IEnumerable<TaskRowViewModel> Rows => CanFilter
        ? rows.Where(row => row.Title.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase)
            && catalog.Matches(row.Task.Item.Id, TagFilter?.Id, RouteSpaceId ?? SpaceFilter?.Id)) : rows;
    public IReadOnlyList<OrganizationFilter> TagFilters { get; private set; } = [new(null, "All tags")];
    public IReadOnlyList<OrganizationFilter> SpaceFilters { get; private set; } = [new(null, "All spaces")];
    public OrganizationFilter? TagFilter { get => tagFilter; set { if (SetProperty(ref tagFilter, value)) NotifyRows(); } }
    public OrganizationFilter? SpaceFilter { get => spaceFilter; set { if (SetProperty(ref spaceFilter, value)) NotifyRows(); } }
    public IReadOnlyList<OrganizationChoice> TagChoices { get; private set; } = [];
    public IReadOnlyList<OrganizationChoice> SpaceChoices { get; private set; } = [];
    public TaskRowViewModel? Detail { get => detail; private set => SetProperty(ref detail, value); }
    public string EditorTitle { get => title; set => SetProperty(ref title, value); }
    public string EditorDescription { get => description; set => SetProperty(ref description, value); }
    public TaskStatus EditorStatus { get => status; set => SetProperty(ref status, value); }
    public TaskPriority EditorPriority { get => priority; set => SetProperty(ref priority, value); }
    // CalendarDatePicker requires DateTimeOffset. Only its calendar date crosses the domain boundary.
    public DateTimeOffset? EditorScheduledDate { get => scheduledDate; set => SetProperty(ref scheduledDate, value); }
    public IReadOnlyList<TaskStatus> Statuses { get; } = Enum.GetValues<TaskStatus>();
    public IReadOnlyList<TaskPriority> Priorities { get; } = Enum.GetValues<TaskPriority>();
    public string CreatedText => Detail?.Task.Item.CreatedAtUtc.ToLocalTime().ToString("g") ?? "";
    public string ModifiedText => Detail?.Task.Item.UpdatedAtUtc.ToLocalTime().ToString("g") ?? "";

    public async Task ReloadAsync()
    {
        var request = ++revision;
        var profileId = current.Current?.Id;
        var route = navigation.Current;
        Error = null;
        loading = true;
        NotifyView();
        try
        {
            if (profileId is null || !IsTaskArea) return;
            var loadedCatalog = await organization.GetAsync(profileId.Value);
            if (request != revision) return;
            catalog = loadedCatalog;
            if (route.Destination == "Space" && !catalog.Spaces.Any(space => space.Id == RouteSpaceId && space.ArchivedAtUtc is null))
                throw new OrganizationValidationException("This space is unavailable or archived. Manage spaces to restore it.");
            if (route.Destination == "Tasks" && IsEditor)
            {
                editorProfile = profileId;
                editorReference = null;
                Detail = null;
                EditorTitle = "";
                EditorDescription = "";
                EditorStatus = TaskStatus.ToDo;
                EditorPriority = TaskPriority.None;
                EditorScheduledDate = null;
                if (route.EntityId?.StartsWith("new:", StringComparison.Ordinal) == true)
                {
                    if (!DateOnly.TryParseExact(route.EntityId[4..], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var selected))
                        throw new TaskValidationException("This calendar date is invalid.");
                    EditorScheduledDate = new DateTimeOffset(selected.ToDateTime(TimeOnly.MinValue));
                    returnRoute = new("Calendar");
                }
            }
            else if (route.Destination == "Task")
            {
                if (!Guid.TryParse(route.EntityId, out var id)) throw new TaskValidationException("This task link is invalid.");
                var task = await service.FindAsync(new TaskReference(profileId.Value, id));
                if (request != revision) return;
                if (task is null || task.Item.DeletedAtUtc is not null) throw new TaskValidationException("This task is no longer available here. Check Trash or browse tasks.");
                editorReference = new(profileId.Value, id);
                editorProfile = profileId;
                Detail = new(profileId.Value, task);
                EditorTitle = task.Item.Title;
                EditorDescription = task.Description;
                EditorStatus = task.Status;
                EditorPriority = task.Priority;
                EditorScheduledDate = task.ScheduledDate is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)) : null;
            }
            else
            {
                var collection = route.Destination switch
                {
                    "Today" => TaskCollection.Today,
                    "Archived" => TaskCollection.Archived,
                    "Trash" => TaskCollection.Trash,
                    _ => TaskCollection.Active
                };
                var tasks = await service.GetAsync(profileId.Value, collection);
                if (request != revision) return;
                rows = tasks.Select(task => new TaskRowViewModel(profileId.Value, task)).ToArray();
                displayedToday = DateOnly.FromDateTime(DateTime.Now);
            }
        }
        catch (Exception exception)
        {
            if (request == revision)
            {
                rows = [];
                editorReference = null;
                editorProfile = null;
                Detail = null;
                Report(exception);
            }
        }
        finally
        {
            if (request == revision) { loading = false; NotifyOrganization(); NotifyView(); }
        }
    }

    public Task RefreshTodayIfNeededAsync() => IsToday && displayedToday != DateOnly.FromDateTime(DateTime.Now) ? ReloadAsync() : Task.CompletedTask;

    private void OnProfileChanged()
    {
        if (observedProfile == current.Current?.Id) return;
        observedProfile = current.Current?.Id;
        ++revision;
        rows = [];
        catalog = OrganizationSnapshot.Empty;
        TagFilter = SpaceFilter = null;
        returnRoute = new("Tasks");
        editorReference = null;
        editorProfile = null;
        Detail = null;
        EditorTitle = EditorDescription = Filter = "";
        EditorScheduledDate = null;
        Error = null;
        NotifyOrganization();
        NotifyView();
        if (IsEditor || navigation.Current.Destination == "Space") navigation.Navigate(new NavigationRoute("Tasks"));
        else _ = ReloadAsync();
    }

    [RelayCommand]
    private void NewTask()
    {
        if (!IsIdle) return;
        returnRoute = navigation.Current.Destination is "Today" or "Space" ? navigation.Current : new("Tasks");
        navigation.Navigate(new NavigationRoute("Tasks", "new"));
    }

    [RelayCommand]
    private void Browse() => navigation.Navigate(new NavigationRoute("Tasks"));

    [RelayCommand]
    private void Back() => navigation.Navigate(returnRoute);

    [RelayCommand]
    private void Open(TaskRowViewModel row)
    {
        if (!IsIdle || row.ProfileId != current.Current?.Id) return;
        returnRoute = navigation.Current.Destination is "Today" or "Archived" or "Space" ? navigation.Current : new("Tasks");
        navigation.Navigate(new NavigationRoute("Task", row.Task.Item.Id.ToString("D")));
    }

    [RelayCommand]
    private void Unschedule() => EditorScheduledDate = null;

    [RelayCommand]
    private Task SaveAsync() => MutateAsync(async () =>
    {
        if (editorProfile is not { } profileId) throw new TaskValidationException("Reopen the task before saving.");
        var draft = new TaskDraft(EditorTitle, EditorDescription, EditorStatus, EditorPriority,
            EditorScheduledDate is { } date ? DateOnly.FromDateTime(date.DateTime) : null);
        var saved = editorReference is { } reference ? await service.UpdateAsync(reference, draft) : await service.CreateAsync(profileId, draft);
        if (current.Current?.Id == profileId) navigation.Navigate(new NavigationRoute("Task", saved.Item.Id.ToString("D")));
    });

    [RelayCommand]
    private Task ToggleDoneAsync(TaskRowViewModel row) => MutateAsync(async () =>
        await service.ChangeStatusAsync(row.Reference, row.Task.Status == TaskStatus.Done ? TaskStatus.ToDo : TaskStatus.Done));

    [RelayCommand]
    private Task ArchiveAsync(TaskRowViewModel row) => ApplyAsync(row, TaskAction.Archive);
    [RelayCommand]
    private Task TrashAsync(TaskRowViewModel row) => ApplyAsync(row, TaskAction.Trash);
    [RelayCommand]
    private Task RestoreAsync(TaskRowViewModel row) => ApplyAsync(row, row.IsDeleted ? TaskAction.RestoreTrash : TaskAction.RestoreArchive);

    private Task ApplyAsync(TaskRowViewModel row, TaskAction action) => MutateAsync(async () =>
    {
        await service.ApplyAsync(row.Reference, action);
        if (IsEditor && current.Current?.Id == row.ProfileId) navigation.Navigate(returnRoute);
    });

    [RelayCommand]
    private Task DuplicateAsync(TaskRowViewModel row) => MutateAsync(async () =>
    {
        var copy = await service.DuplicateAsync(row.Reference);
        if (current.Current?.Id == row.ProfileId) navigation.Navigate(new NavigationRoute("Task", copy.Item.Id.ToString("D")));
    });

    [RelayCommand]
    private Task PermanentlyDeleteConfirmedAsync(TaskRowViewModel row) => MutateAsync(() => service.PermanentlyDeleteAsync(row.Reference));

    [RelayCommand]
    private async Task ToggleAssignmentAsync(OrganizationChoice choice)
    {
        if (!IsIdle || editorReference?.ItemId != choice.Item.ItemId || current.Current?.Id != choice.Item.ProfileId) return;
        var request = revision;
        busy = true; Error = null; NotifyView();
        try
        {
            await organization.AssignAsync(choice.Item, choice.Kind, choice.Id, !choice.IsAssigned);
            var loaded = await organization.GetAsync(choice.Item.ProfileId);
            if (request == revision) { catalog = loaded; NotifyOrganization(); }
        }
        catch (Exception exception) { if (request == revision) Report(exception); }
        finally { busy = false; NotifyView(); }
    }

    [RelayCommand]
    private void ManageOrganization() => navigation.Navigate(new("Organization"));

    private void NotifyOrganization()
    {
        var selectedTag = TagFilter?.Id;
        var selectedSpace = SpaceFilter?.Id;
        OrganizationFilter[] tags = [new(null, "All tags"), .. catalog.Tags.Select(tag => new OrganizationFilter(tag.Id, "#" + tag.Name))];
        OrganizationFilter[] spaces = [new(null, "All spaces"), .. catalog.Spaces.Where(space => space.ArchivedAtUtc is null).Select(space => new OrganizationFilter(space.Id, space.Name))];
        // Keep native ComboBox item identities stable across task edits and assignment changes.
        if (!TagFilters.SequenceEqual(tags)) { TagFilters = tags; OnPropertyChanged(nameof(TagFilters)); }
        if (!SpaceFilters.SequenceEqual(spaces)) { SpaceFilters = spaces; OnPropertyChanged(nameof(SpaceFilters)); }
        TagFilter = TagFilters.FirstOrDefault(filter => filter.Id == selectedTag) ?? TagFilters[0];
        SpaceFilter = SpaceFilters.FirstOrDefault(filter => filter.Id == selectedSpace) ?? SpaceFilters[0];
        TagChoices = editorReference is { } reference ? catalog.Tags.Select(tag => new OrganizationChoice(new(reference.ProfileId, reference.ItemId),
            tag.Id, OrganizationKind.Tag, tag.Name, tag.Color, catalog.ItemTags.Contains(new(reference.ItemId, tag.Id)))).ToArray() : [];
        SpaceChoices = editorReference is { } item ? catalog.Spaces.Where(space => space.ArchivedAtUtc is null || catalog.ItemSpaces.Contains(new(item.ItemId, space.Id)))
            .Select(space => new OrganizationChoice(new(item.ProfileId, item.ItemId), space.Id, OrganizationKind.Space, space.Name, space.Color,
                catalog.ItemSpaces.Contains(new(item.ItemId, space.Id)), space.ArchivedAtUtc is not null)).ToArray() : [];
        OnPropertyChanged(nameof(TagChoices)); OnPropertyChanged(nameof(SpaceChoices));
    }

    private async Task MutateAsync(Func<Task> operation)
    {
        if (!IsIdle) return;
        var profileId = current.Current?.Id;
        busy = true;
        Error = null;
        NotifyView();
        try
        {
            await operation();
            if (current.Current?.Id == profileId) await ReloadAsync();
        }
        catch (Exception exception) { if (current.Current?.Id == profileId) Report(exception); }
        finally { busy = false; NotifyView(); }
    }

    private void Report(Exception exception)
    {
        if (exception is TaskValidationException or WorkspaceChangedException or TaskOperationException or OrganizationValidationException or OrganizationOperationException) Error = exception.Message;
        else { logger.LogError(exception, "Task presentation operation failed"); Error = "Tasks could not be updated. Please try again."; }
    }

    private void NotifyRows()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void NotifyView()
    {
        foreach (var property in new[] { nameof(IsIdle), nameof(IsBusy), nameof(CanSave), nameof(IsEditor), nameof(IsDetail), nameof(IsList),
            nameof(CanFilter), nameof(CanChooseSpaceFilter), nameof(IsToday), nameof(Heading), nameof(CreatedText), nameof(ModifiedText) }) OnPropertyChanged(property);
        NotifyRows();
    }
}
