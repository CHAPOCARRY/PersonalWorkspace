using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class ActivityHistoryView : UserControl
{
    private ActivityHistoryViewModel? attached;
    private bool synchronizing;
    public ActivityHistoryView()
    {
        InitializeComponent(); Loaded += (_, _) => Attach(); DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => { if (attached is not null) attached.PropertyChanged -= Changed; attached = null; };
    }
    private void Attach()
    {
        if (attached is not null) attached.PropertyChanged -= Changed;
        attached = DataContext as ActivityHistoryViewModel;
        if (attached is not null) attached.PropertyChanged += Changed;
        SyncDates();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(ActivityHistoryViewModel.From) or nameof(ActivityHistoryViewModel.Through)) SyncDates(); }
    private void SyncDates() { synchronizing = true; try { FromPicker.Date = attached?.From; ThroughPicker.Date = attached?.Through; } finally { synchronizing = false; } }
    private void FromChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e) { if (!synchronizing && attached is not null) attached.From = e.NewDate; }
    private void ThroughChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e) { if (!synchronizing && attached is not null) attached.Through = e.NewDate; }
    private void ClearFrom(object sender, RoutedEventArgs e) => Model.From = null;
    private void ClearThrough(object sender, RoutedEventArgs e) => Model.Through = null;
    private ActivityHistoryViewModel Model => (ActivityHistoryViewModel)DataContext;
    private async void Refresh(object sender, RoutedEventArgs e) => await Model.RefreshAsync();
    private async void More(object sender, RoutedEventArgs e) => await Model.RefreshAsync(true);
    private async void MoreChanges(object sender, RoutedEventArgs e) => await Model.SelectAsync(true);
    private async void Open(object sender, RoutedEventArgs e) => await Model.OpenAsync();
    private void Reset(object sender, RoutedEventArgs e) => Model.ResetFilters();
    private void FilterTarget(object sender, RoutedEventArgs e) => Model.FilterSelected();
    private async void ClearHistory(object sender, RoutedEventArgs e)
    {
        if (Model.ClearRequest is not { } request) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Clear this Profile’s activity history?", Content = "This permanently removes recorded activity. Your Tasks, Pages, entries, Templates and files remain unchanged. This cannot be undone.", PrimaryButtonText = "Clear history", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.ClearConfirmedAsync(request.Profile, request.Epoch, request.Revision);
    }
}
