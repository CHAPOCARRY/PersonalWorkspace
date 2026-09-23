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

    public ShellViewModel(ISettingsService settings, INavigationService navigation, ILogger<ShellViewModel> logger, ProfilesViewModel profiles)
    {
        this.settings = settings;
        this.navigation = navigation;
        this.logger = logger;
        Profiles = profiles;
        navigation.Changed += (_, _) => UpdateDestination();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsSidebarExpanded = !await settings.GetAsync(SettingKeys.SidebarCollapsed, false, cancellationToken);
        UpdateDestination();
    }

    public void Navigate(string destination)
    {
        Profiles.CloseManagementCommand.Execute(null);
        navigation.Navigate(new NavigationRoute(destination));
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

    public void ReportError(string message) => ErrorMessage = message;

    private void UpdateDestination()
    {
        Title = navigation.Current.Destination;
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
