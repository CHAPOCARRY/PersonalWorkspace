using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class JournalListView : UserControl
{
    public JournalListView() => InitializeComponent();
    private void OnOpen(object sender, RoutedEventArgs args) { if (DataContext is JournalWorkspaceViewModel model && sender is FrameworkElement { Tag: JournalSummaryRow row }) model.OpenCommand.Execute(row); }
    private void OnActionsOpening(object sender, object args) { if (DataContext is JournalWorkspaceViewModel model && sender is MenuFlyout menu && menu.Target is FrameworkElement { Tag: JournalSummaryRow row }) JournalMenus.Fill(menu, model, row, XamlRoot); }
}
internal static class JournalMenus
{
    public static void Fill(MenuFlyout menu, JournalWorkspaceViewModel model, JournalSummaryRow row, XamlRoot root)
    {
        menu.Items.Clear(); var item = row.Journal.Item;
        void Add(string label, Func<Task> action) { var button = new MenuFlyoutItem { Text = label }; button.Click += async (_, _) => await action(); menu.Items.Add(button); }
        if (item.DeletedAtUtc is not null)
        {
            Add("Restore Journal", () => model.ApplyAsync(row, JournalAction.RestoreTrash));
            Add("Permanently delete Journal", async () => { if (await TrackerMenus.Confirm(root, "Delete Journal permanently?", $"Permanently delete “{item.Title}”, its fields and all daily entries? This cannot be undone.", "Delete Journal")) await model.DeleteAsync(row); });
        }
        else
        {
            Add(item.ArchivedAtUtc is null ? "Archive Journal" : "Restore Journal", () => model.ApplyAsync(row, item.ArchivedAtUtc is null ? JournalAction.Archive : JournalAction.RestoreArchive));
            Add("Move Journal to Trash", () => model.ApplyAsync(row, JournalAction.Trash));
            Add("Duplicate Journal", () => model.DuplicateAsync(row));
        }
    }
}
