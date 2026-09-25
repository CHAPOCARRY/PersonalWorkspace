using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class OrganizationView : UserControl
{
    private bool confirming;
    public OrganizationView() => InitializeComponent();
    private OrganizationViewModel ViewModel => (OrganizationViewModel)DataContext;
    private void OnEdit(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: OrganizationRow row }) ViewModel.EditCommand.Execute(row);
    }
    private void OnArchive(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: OrganizationRow row }) ViewModel.ArchiveCommand.Execute(row);
    }
    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        if (confirming || !ViewModel.IsIdle || sender is not FrameworkElement { Tag: OrganizationRow row }) return;
        confirming = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Light, Title = "Delete tag?",
                Content = $"Delete #{row.Name} and remove its {row.AssignmentCount} assignment(s)? Tasks will be kept.",
                PrimaryButtonText = "Delete tag", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.DeleteTagConfirmedCommand.ExecuteAsync(row);
        }
        finally { confirming = false; }
    }
}
