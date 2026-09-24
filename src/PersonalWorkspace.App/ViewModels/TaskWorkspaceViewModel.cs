using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class TaskWorkspaceViewModel : ObservableObject
{
    private readonly ITaskService service;
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
    private string returnDestination = "Tasks";
    private DateOnly displayedToday = DateOnly.FromDateTime(DateTime.Now);

    public TaskWorkspaceViewModel(ITaskService service, ICurrentProfile current, INavigationService navigation, ILogger<TaskWorkspaceViewModel> logger)
    {
        this.service = service;
        this.current = current;
        this.navigation = navigation;
        this.logger = logger;
        observedProfile = current.Current?.Id;
        current.Changed += (_, _) => OnProfileChanged();
        navigation.Changed += (_, _) => { OnPropertyChanged(nameof(IsTaskArea)); _ = ReloadAsync(); };
    }

    public bool IsTaskArea => navigation.Current.Destination is "Tasks" or "Today" or "Archived" or "Trash" or "Task";
    public bool IsEditor => navigation.Current.Destination == "Task" || navigation.Current is { Destination: "Tasks", EntityId: "new" };
    public bool IsDetail => navigation.Current.Destination == "Task";
    public bool IsList => !IsEditor;
    public bool IsIdle => !busy && !loading && current.Current is not null;
    public bool IsBusy => busy || loading;
    public bool CanSave => IsIdle && editorProfile is not null;
    public bool IsEmpty => !loading && !IsEditor && !Rows.Any();
    public bool CanFilter => navigation.Current.Destination == "Tasks" && !IsEditor;
    public bool IsToday => navigation.Current.Destination == "Today";
    public string Heading => IsEditor ? (IsDetail ? "Task detail" : "New task") : navigation.Current.Destination;
    public string EmptyMessage => IsToday ? "No tasks scheduled for today." : CanFilter && filter.Length > 0 ? "No tasks match your title filter." : "No tasks here yet.";
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string Filter { get => filter; set { if (SetProperty(ref filter, value)) NotifyRows(); } }
    public IEnumerable<TaskRowViewModel> Rows => CanFilter
        ? rows.Where(row => row.Title.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase)) : rows;
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
            if (route is { Destination: "Tasks", EntityId: "new" })
            {
                editorProfile = profileId;
                editorReference = null;
                Detail = null;
                EditorTitle = "";
                EditorDescription = "";
                EditorStatus = TaskStatus.ToDo;
                EditorPriority = TaskPriority.None;
                EditorScheduledDate = null;
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
            if (request == revision) { loading = false; NotifyView(); }
        }
    }

    public Task RefreshTodayIfNeededAsync() => IsToday && displayedToday != DateOnly.FromDateTime(DateTime.Now) ? ReloadAsync() : Task.CompletedTask;

    private void OnProfileChanged()
    {
        if (observedProfile == current.Current?.Id) return;
        observedProfile = current.Current?.Id;
        ++revision;
        rows = [];
        editorReference = null;
        editorProfile = null;
        Detail = null;
        EditorTitle = EditorDescription = Filter = "";
        EditorScheduledDate = null;
        Error = null;
        NotifyView();
        if (IsEditor) navigation.Navigate(new NavigationRoute("Tasks"));
        else _ = ReloadAsync();
    }

    [RelayCommand]
    private void NewTask()
    {
        if (!IsIdle) return;
        returnDestination = IsToday ? "Today" : "Tasks";
        navigation.Navigate(new NavigationRoute("Tasks", "new"));
    }

    [RelayCommand]
    private void Browse() => navigation.Navigate(new NavigationRoute("Tasks"));

    [RelayCommand]
    private void Back() => navigation.Navigate(new NavigationRoute(returnDestination));

    [RelayCommand]
    private void Open(TaskRowViewModel row)
    {
        if (!IsIdle || row.ProfileId != current.Current?.Id) return;
        returnDestination = navigation.Current.Destination is "Today" or "Archived" ? navigation.Current.Destination : "Tasks";
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
        if (IsEditor && current.Current?.Id == row.ProfileId) navigation.Navigate(new NavigationRoute(returnDestination));
    });

    [RelayCommand]
    private Task DuplicateAsync(TaskRowViewModel row) => MutateAsync(async () =>
    {
        var copy = await service.DuplicateAsync(row.Reference);
        if (current.Current?.Id == row.ProfileId) navigation.Navigate(new NavigationRoute("Task", copy.Item.Id.ToString("D")));
    });

    [RelayCommand]
    private Task PermanentlyDeleteConfirmedAsync(TaskRowViewModel row) => MutateAsync(() => service.PermanentlyDeleteAsync(row.Reference));

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
        if (exception is TaskValidationException or WorkspaceChangedException or TaskOperationException) Error = exception.Message;
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
            nameof(CanFilter), nameof(IsToday), nameof(Heading), nameof(CreatedText), nameof(ModifiedText) }) OnPropertyChanged(property);
        NotifyRows();
    }
}
