using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileService service;
    private readonly ICurrentProfile current;
    private readonly ILogger<ProfilesViewModel> logger;
    private bool isBusy;
    private bool isManaging;
    private bool editorOpen;
    private string name = "";
    private string? error;
    private Profile? editing;

    public ObservableCollection<Profile> Profiles { get; } = [];
    public IEnumerable<ProfileRowViewModel> ProfileRows => Profiles.Select(profile => new ProfileRowViewModel(profile, profile.Id == CurrentId));
    public bool HasCurrent => current.Current is not null;
    public bool HasProfiles => Profiles.Count > 0;
    public string CurrentName => current.Current?.Name ?? "Profiles";
    public Guid? CurrentId => current.Current?.Id;
    public bool ShowPlaceholder => HasCurrent && !IsManaging;
    public bool IsManaging { get => isManaging; private set { SetProperty(ref isManaging, value); OnPropertyChanged(nameof(ShowPlaceholder)); } }
    public bool IsBusy { get => isBusy; private set { SetProperty(ref isBusy, value); OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool EditorOpen { get => editorOpen; private set => SetProperty(ref editorOpen, value); }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string PageHeading => HasProfiles ? "Manage profiles" : "Create your first profile";
    public string EditorHeading => editing is not null ? "Rename profile" : "New profile";
    public string SaveLabel => editing is not null ? "Save name" : "Create Profile";

    public ProfilesViewModel(IProfileService service, ICurrentProfile current, ILogger<ProfilesViewModel> logger)
    {
        this.service = service;
        this.current = current;
        this.logger = logger;
        current.Changed += (_, _) => NotifyCurrent();
    }

    public Task InitializeAsync() => PerformAsync(() => service.RestoreAsync());

    [RelayCommand]
    private void Manage()
    {
        if (IsBusy) return;
        Error = null;
        IsManaging = true;
        EditorOpen = false;
    }

    [RelayCommand]
    private void CloseManagement() { if (!IsBusy && HasCurrent) IsManaging = false; }

    [RelayCommand]
    private void NewProfile() { if (!IsBusy) StartEditor(null); }

    [RelayCommand]
    private void BeginRename(Profile profile) { if (!IsBusy) StartEditor(profile); }

    private void StartEditor(Profile? profile)
    {
        editing = profile;
        Name = profile?.Name ?? "";
        Error = null;
        IsManaging = true;
        EditorOpen = true;
        OnPropertyChanged(nameof(EditorHeading));
        OnPropertyChanged(nameof(SaveLabel));
    }

    [RelayCommand]
    private Task SaveAsync() => PerformAsync(async () =>
    {
        if (editing is { } profile) await service.RenameAsync(profile.Id, Name);
        else await service.CreateAsync(Name);
        EditorOpen = false;
        IsManaging = false;
    });

    [RelayCommand]
    private Task SwitchAsync(Guid id) => PerformAsync(async () =>
    {
        await service.SwitchAsync(id);
        IsManaging = false;
    });

    [RelayCommand]
    private Task DeleteConfirmedAsync(Profile profile) => PerformAsync(() => service.DeleteAsync(profile.Id));

    private async Task PerformAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try { await operation(); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Profile presentation operation failed");
            Error = exception is ProfileValidationException or ProfileOperationException
                ? exception.Message : "Profiles could not be updated. Please try again or review the local logs.";
            IsManaging = true;
        }
        finally
        {
            try
            {
                var profiles = await service.GetAllAsync();
                Profiles.Clear();
                foreach (var profile in profiles) Profiles.Add(profile);
                OnPropertyChanged(nameof(HasProfiles));
                OnPropertyChanged(nameof(PageHeading));
                if (!HasProfiles && (!EditorOpen || editing is not null))
                {
                    var previousError = Error;
                    StartEditor(null);
                    Error = previousError;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Profile list refresh failed");
                Error = "Profiles could not be loaded. Check the local application folder and restart.";
            }
            NotifyCurrent();
            IsBusy = false;
        }
    }

    private void NotifyCurrent()
    {
        OnPropertyChanged(nameof(ProfileRows));
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(CurrentId));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }
}
