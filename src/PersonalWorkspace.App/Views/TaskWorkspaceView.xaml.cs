using System.Windows.Input;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class TaskWorkspaceView : UserControl
{
    private bool confirmationOpen;
    private bool synchronizingDate;
    private TaskWorkspaceViewModel? dateModel;
    public TaskWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachDateModel();
        Loaded += (_, _) => AttachDateModel();
        Unloaded += (_, _) =>
        {
            if (dateModel is not null) dateModel.PropertyChanged -= OnDateModelChanged;
            dateModel = null;
        };
    }
    private TaskWorkspaceViewModel ViewModel => (TaskWorkspaceViewModel)DataContext;

    // WinUI's reflection binding coerces a null DateTimeOffset to the calendar minimum.
    // Bridge this nullable native control property explicitly so an unscheduled task stays visibly empty.
    private void AttachDateModel()
    {
        if (dateModel is not null) dateModel.PropertyChanged -= OnDateModelChanged;
        dateModel = DataContext as TaskWorkspaceViewModel;
        if (dateModel is not null) dateModel.PropertyChanged += OnDateModelChanged;
        SynchronizeDate();
    }
    private void OnDateModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TaskWorkspaceViewModel.EditorScheduledDate)) SynchronizeDate();
    }
    private void SynchronizeDate()
    {
        synchronizingDate = true;
        try { ScheduledDatePicker.Date = dateModel?.EditorScheduledDate; }
        finally { synchronizingDate = false; }
    }
    private void OnScheduledDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!synchronizingDate && dateModel is not null) dateModel.EditorScheduledDate = args.NewDate;
    }
    private static void Execute(object sender, ICommand command)
    {
        if (sender is FrameworkElement { Tag: TaskRowViewModel row }) command.Execute(row);
    }
    private void OnOpen(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.OpenCommand);
    private void OnToggleDone(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.ToggleDoneCommand);
    private void OnDuplicate(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.DuplicateCommand);
    private void OnArchive(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.ArchiveCommand);
    private void OnRestore(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.RestoreCommand);
    private void OnTrash(object sender, RoutedEventArgs args) => Execute(sender, ViewModel.TrashCommand);
    private void OnAssignment(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: OrganizationChoice choice }) ViewModel.ToggleAssignmentCommand.Execute(choice);
    }

    private async void OnPermanentDelete(object sender, RoutedEventArgs args)
    {
        if (confirmationOpen || !ViewModel.IsIdle || sender is not FrameworkElement { Tag: TaskRowViewModel row }) return;
        confirmationOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ElementTheme.Light,
                Title = "Permanently delete task?",
                Content = $"Permanently delete “{row.Title}”? This cannot be undone.",
                PrimaryButtonText = "Permanently delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.PermanentlyDeleteConfirmedCommand.ExecuteAsync(row);
        }
        finally { confirmationOpen = false; }
    }
}
