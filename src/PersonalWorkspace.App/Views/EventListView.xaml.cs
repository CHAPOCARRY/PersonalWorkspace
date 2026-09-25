using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class EventListView : UserControl
{
    private bool confirming;
    public EventListView() => InitializeComponent();
    private CalendarViewModel Model => (CalendarViewModel)DataContext;
    private static void Execute(object sender, ICommand command) { if (sender is FrameworkElement { Tag: CalendarEntry entry }) command.Execute(entry); }
    private void OnOpen(object sender, RoutedEventArgs args) => Execute(sender, Model.OpenCommand);
    private void OnActionsOpening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        if (menu.Target is not FrameworkElement { Tag: CalendarEntry entry }) return;
        void Add(string text, ICommand command) => menu.Items.Add(new MenuFlyoutItem { Text = text, Command = command, CommandParameter = entry });
        if (entry.CanOpen) Add("Open", Model.OpenCommand);
        if (entry.CanArchive) Add("Archive", Model.ArchiveCommand);
        if (entry.CanOpen) Add("Move to Trash", Model.TrashCommand);
        if (entry.CanRestore) Add("Restore", Model.RestoreCommand);
        if (entry.IsDeleted)
        {
            var delete = new MenuFlyoutItem { Text = "Permanently delete", Tag = entry };
            delete.Click += OnDelete; menu.Items.Add(delete);
        }
    }
    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        if (confirming || !Model.IsIdle || sender is not FrameworkElement { Tag: CalendarEntry entry }) return;
        confirming = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Light, Title = "Permanently delete event?",
                Content = $"Permanently delete “{entry.Title}”? This cannot be undone.", PrimaryButtonText = "Permanently delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.PermanentlyDeleteConfirmedCommand.ExecuteAsync(entry);
        }
        finally { confirming = false; }
    }
}
