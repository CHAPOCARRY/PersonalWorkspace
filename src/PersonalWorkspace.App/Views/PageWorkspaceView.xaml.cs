using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class PageWorkspaceView : UserControl
{
    public PageWorkspaceView() => InitializeComponent();
    private PageWorkspaceViewModel? Model => DataContext as PageWorkspaceViewModel;
    private void OnRowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Grid { Tag: PageRow row } grid)
        {
            grid.Margin = new Thickness(Math.Min(row.Depth, 12) * (double)Application.Current.Resources["Space12"], 0, 0, 0);
            if (grid.Children[0] is Button toggle) toggle.Content = row.Expanded ? "−" : "+";
            if (grid.Children[1] is Button title) title.FontWeight = row.Selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
    private void OnToggle(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: PageRow row }) Model?.Toggle(row); }
    private void OnOpen(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: PageRow row }) Model?.OpenCommand.Execute(row); }
    private void OnAssign(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: OrganizationChoice choice }) Model?.AssignCommand.Execute(choice); }
    private void OnActionsOpening(object sender, object args)
    {
        if (Model is not { } model || sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: PageRow row }) return;
        menu.Items.Clear(); var item = row.Page.Item;
        void Add(string title, Func<Task> action) { var button = new MenuFlyoutItem { Text = title }; button.Click += async (_, _) => await action(); menu.Items.Add(button); }
        if (item.DeletedAtUtc is not null)
        {
            Add("Restore Page", () => model.ApplyAsync(row, PageAction.RestoreTrash));
            Add("Permanently delete Page", async () => { if (await TrackerMenus.Confirm(XamlRoot, "Delete Page permanently?", $"Permanently delete “{item.Title}”? Its child Pages will survive and become root Pages. This cannot be undone.", "Delete Page")) await model.DeleteAsync(row); });
        }
        else
        {
            Add(item.ArchivedAtUtc is null ? "Archive Page" : "Restore Page", () => model.ApplyAsync(row, item.ArchivedAtUtc is null ? PageAction.Archive : PageAction.RestoreArchive));
            Add("Move Page to Trash", () => model.ApplyAsync(row, PageAction.Trash));
            Add("Duplicate Page", () => model.DuplicateAsync(row));
            Add("Move Page up", () => model.ReorderAsync(row, -1)); Add("Move Page down", () => model.ReorderAsync(row, 1));
        }
    }
}
