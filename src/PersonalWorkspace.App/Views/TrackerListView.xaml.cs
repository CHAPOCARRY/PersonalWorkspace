using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class TrackerListView : UserControl
{
    public TrackerListView() => InitializeComponent();
    private void OnOpen(object sender, RoutedEventArgs args) { if (DataContext is TrackerWorkspaceViewModel model && sender is FrameworkElement { Tag: TrackerRow row }) model.OpenCommand.Execute(row); }
    private void OnActionsOpening(object sender, object args)
    {
        if (DataContext is TrackerWorkspaceViewModel model && sender is MenuFlyout menu && menu.Target is FrameworkElement { Tag: TrackerRow row }) TrackerMenus.Fill(menu, model, row, XamlRoot);
    }
}
internal static class TrackerMenus
{
    public static void Fill(MenuFlyout menu, TrackerWorkspaceViewModel model, TrackerRow row, XamlRoot root)
    {
        menu.Items.Clear();
        void Add(string text, Func<Task> action) { var button = new MenuFlyoutItem { Text = text }; button.Click += async (_, _) => await action(); menu.Items.Add(button); }
        var item = row.Period.Tracker.Item;
        if (item.DeletedAtUtc is not null)
        {
            Add("Restore Tracker", () => model.ApplyAsync(row, TrackerAction.RestoreTrash));
            Add("Permanently delete Tracker", async () =>
            {
                if (await Confirm(root, "Delete Tracker permanently?", $"Permanently delete “{item.Title}” and all its entries? This cannot be undone.", "Delete Tracker")) await model.PermanentlyDeleteAsync(row);
            });
        }
        else
        {
            Add(item.ArchivedAtUtc is null ? "Archive Tracker" : "Restore Tracker", () => model.ApplyAsync(row, item.ArchivedAtUtc is null ? TrackerAction.Archive : TrackerAction.RestoreArchive));
            Add("Move Tracker to Trash", () => model.ApplyAsync(row, TrackerAction.Trash));
            Add("Duplicate Tracker", () => model.DuplicateAsync(row));
        }
    }
    public static async Task<bool> Confirm(XamlRoot root, string title, string content, string action) => await new ContentDialog
    {
        XamlRoot = root, Title = title, Content = content, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
    }.ShowAsync() == ContentDialogResult.Primary;
}
