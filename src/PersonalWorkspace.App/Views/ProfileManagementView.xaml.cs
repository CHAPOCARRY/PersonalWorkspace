using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class ProfileManagementView : UserControl
{
    private bool confirmationOpen;
    public ProfileManagementView() => InitializeComponent();
    private ProfilesViewModel ViewModel => (ProfilesViewModel)DataContext;

    private void OnOpen(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: Profile profile }) ViewModel.SwitchCommand.Execute(profile.Id);
    }
    private void OnRename(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: Profile profile }) ViewModel.BeginRenameCommand.Execute(profile);
    }
    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        if (confirmationOpen || ViewModel.IsBusy || sender is not Button { Tag: Profile profile }) return;
        confirmationOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Light,
                Title = "Delete profile?",
                Content = $"Permanently delete “{profile.Name}” and all of its local workspace files and attachments? This cannot be undone.",
                PrimaryButtonText = "Delete profile",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.DeleteConfirmedCommand.ExecuteAsync(profile);
        }
        finally { confirmationOpen = false; }
    }
}
