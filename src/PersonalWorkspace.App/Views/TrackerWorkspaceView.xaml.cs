using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class TrackerWorkspaceView : UserControl
{
    private TrackerWorkspaceViewModel? model;
    private bool synchronizing;
    public TrackerWorkspaceView()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = null; };
    }
    private void Attach()
    {
        if (model is not null) model.PropertyChanged -= Changed;
        model = DataContext as TrackerWorkspaceViewModel;
        if (model is not null) model.PropertyChanged += Changed;
        Sync();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(TrackerWorkspaceViewModel.EntryDate) or nameof(TrackerWorkspaceViewModel.HistoryDate)) Sync(); }
    private void Sync() { synchronizing = true; try { EntryPicker.Date = model?.EntryDate; HistoryPicker.Date = model?.HistoryDate; } finally { synchronizing = false; } }
    private void OnEntryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.EntryDate = args.NewDate; }
    private void OnHistoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.HistoryDate = args.NewDate; }
    private void OnEditEntry(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: TrackerEntryRow row }) model?.EditEntryCommand.Execute(row); }
    private async void OnDeleteEntry(object sender, RoutedEventArgs args)
    {
        if (model is { } vm && sender is FrameworkElement { Tag: TrackerEntryRow row } && await TrackerMenus.Confirm(XamlRoot, "Delete entry?", $"Delete {row.Label}? The Tracker will remain.", "Delete entry")) await vm.DeleteEntryAsync(row);
    }
    private void OnAssign(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: OrganizationChoice choice }) model?.AssignCommand.Execute(choice); }
    private void OnActionsOpening(object sender, object args)
    {
        if (model is { } vm && sender is MenuFlyout menu && menu.Target is FrameworkElement { Tag: TrackerRow row }) TrackerMenus.Fill(menu, vm, row, XamlRoot);
    }
}
