using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.System;

namespace PersonalWorkspace.App.Views;

public sealed partial class ListWorkspaceView : UserControl
{
    public ListWorkspaceView() => InitializeComponent();
    private ListWorkspaceViewModel? Model => DataContext as ListWorkspaceViewModel;
    private void OnOpen(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: ListRow row }) Model?.OpenCommand.Execute(row); }
    private void OnEdit(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: ListItemRow row }) Model?.EditItemCommand.Execute(row); }
    private void OnAssign(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: OrganizationChoice choice }) Model?.AssignCommand.Execute(choice); }
    private async void OnCheck(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: ListItemRow row } && Model is { } model) await model.CheckAsync(row); }
    private async void OnQuickKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || Model is not { CanEdit: true } model) return;
        args.Handled = true; await model.QuickAddCommand.ExecuteAsync(null); QuickInput.Focus(FocusState.Programmatic);
    }
    private async void OnClearChecked(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanEdit: true, DetailRow: { } row } model) return;
        if (await TrackerMenus.Confirm(XamlRoot, "Clear checked items?", $"Permanently remove all checked items from “{row.Title}”? Unchecked items and the List will remain.", "Clear checked")) await model.ClearCheckedAsync(row.Reference);
    }
    private void OnItemActions(object sender, object args)
    {
        if (Model is not { CanEdit: true } model || sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: ListItemRow row }) return;
        menu.Items.Clear();
        void Add(string title, Func<Task> action) { var button = new MenuFlyoutItem { Text = title }; button.Click += async (_, _) => await action(); menu.Items.Add(button); }
        Add("Move item up", () => model.ReorderAsync(row, -1)); Add("Move item down", () => model.ReorderAsync(row, 1));
        Add("Create Task from item", () => model.CreateTaskAsync(row)); Add("Delete item", () => model.DeleteItemAsync(row));
    }
    private void OnListActions(object sender, object args)
    {
        if (Model is not { } model || sender is not MenuFlyout menu || menu.Target is not FrameworkElement { Tag: ListRow row }) return;
        menu.Items.Clear(); var item = row.Definition.Item;
        void Add(string title, Func<Task> action) { var button = new MenuFlyoutItem { Text = title }; button.Click += async (_, _) => await action(); menu.Items.Add(button); }
        if (item.DeletedAtUtc is not null)
        {
            Add("Restore List", () => model.ApplyAsync(row, ListAction.RestoreTrash));
            Add("Permanently delete List", async () => { if (await TrackerMenus.Confirm(XamlRoot, "Delete List permanently?", $"Permanently delete “{item.Title}” and all its items? Tasks created from these items will survive. This cannot be undone.", "Delete List")) await model.DeleteAsync(row); });
        }
        else
        {
            Add(item.ArchivedAtUtc is null ? "Archive List" : "Restore List", () => model.ApplyAsync(row, item.ArchivedAtUtc is null ? ListAction.Archive : ListAction.RestoreArchive));
            Add("Move List to Trash", () => model.ApplyAsync(row, ListAction.Trash)); Add("Duplicate List", () => model.DuplicateAsync(row));
        }
    }
}
