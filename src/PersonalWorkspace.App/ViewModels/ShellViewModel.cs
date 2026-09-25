using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISettingsService settings;
    private readonly INavigationService navigation;
    private readonly ILogger<ShellViewModel> logger;

    private bool isSidebarExpanded = true;
    private string title = "Today";
    private string description = "Your daily overview will be implemented in a future phase.";
    private string? errorMessage;

    public bool IsSidebarExpanded { get => isSidebarExpanded; private set => SetProperty(ref isSidebarExpanded, value); }
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string Description { get => description; private set => SetProperty(ref description, value); }
    public string? ErrorMessage { get => errorMessage; private set => SetProperty(ref errorMessage, value); }

    public ProfilesViewModel Profiles { get; }
    public TaskWorkspaceViewModel Tasks { get; }
    public OrganizationViewModel Organization { get; }
    public Guid? ActiveSpaceId => navigation.Current.Destination == "Space" && Guid.TryParse(navigation.Current.EntityId, out var id) ? id : null;
    public bool ShowOrganization => Profiles.ShowPlaceholder && navigation.Current.Destination == "Organization";
    public bool ShowPlaceholder => Profiles.ShowPlaceholder && !Tasks.IsTaskArea && !ShowOrganization;
    public bool ShowTasks => Profiles.ShowPlaceholder && Tasks.IsTaskArea;

    public ShellViewModel(ISettingsService settings, INavigationService navigation, ILogger<ShellViewModel> logger, ProfilesViewModel profiles, TaskWorkspaceViewModel tasks, OrganizationViewModel organization)
    {
        this.settings = settings;
        this.navigation = navigation;
        this.logger = logger;
        Profiles = profiles;
        Tasks = tasks;
        Organization = organization;
        Profiles.PropertyChanged += (_, _) => NotifyContent();
        Tasks.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Tasks.IsTaskArea)) NotifyContent(); };
        navigation.Changed += (_, _) => UpdateDestination();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsSidebarExpanded = !await settings.GetAsync(SettingKeys.SidebarCollapsed, false, cancellationToken);
        UpdateDestination();
        await Tasks.ReloadAsync();
        await Organization.ReloadAsync();
    }

    public void Navigate(string destination)
    {
        Profiles.CloseManagementCommand.Execute(null);
        navigation.Navigate(new NavigationRoute(destination));
    }

    public void OpenSpace(OrganizationRow space)
    {
        if (space.ProfileId != Profiles.CurrentId) return;
        Profiles.CloseManagementCommand.Execute(null);
        navigation.Navigate(new("Space", space.Id.ToString("D")));
    }

    public void NewSpace()
    {
        Profiles.CloseManagementCommand.Execute(null);
        Organization.NewSpace();
    }

    [RelayCommand]
    private async Task ToggleSidebarAsync()
    {
        var expanded = !IsSidebarExpanded;
        try
        {
            await settings.SetAsync(SettingKeys.SidebarCollapsed, !expanded);
            IsSidebarExpanded = expanded;
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Sidebar preference could not be saved");
            ErrorMessage = "The sidebar preference could not be saved. Please try again.";
        }
    }

    [RelayCommand]
    private void OpenSettings() => Navigate("Settings");

    [RelayCommand]
    private void AddTask()
    {
        Profiles.CloseManagementCommand.Execute(null);
        Tasks.NewTaskCommand.Execute(null);
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowTasks));
        OnPropertyChanged(nameof(ShowOrganization));
    }

    public void ReportError(string message) => ErrorMessage = message;

    private void UpdateDestination()
    {
        NotifyContent();
        OnPropertyChanged(nameof(ActiveSpaceId));
        Title = navigation.Current.Destination == "Task" ? "Tasks" : navigation.Current.Destination;
        Description = Title switch
        {
            "Today" => "Your daily overview will be implemented in a future phase.",
            "Tasks" => "Task management will be implemented in a future phase.",
            "Calendar" => "Calendar and events will be implemented in a future phase.",
            "Trackers" => "Trackers and progress will be implemented in a future phase.",
            "Journal" => "Journaling will be implemented in a future phase.",
            "Pages" => "Pages and the configurable canvas will be implemented in a future phase.",
            "Lists" => "Lists will be implemented in a future phase.",
            "Archived" => "Archived items will be available in a future phase.",
            "Trash" => "Trash and item recovery will be implemented in a future phase.",
            "Settings" => "Application settings will be expanded in a future phase. Shell preferences are saved automatically on this device.",
            _ => "This destination will be implemented in a future phase."
        };
    }
}
