using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class TrackerEditorView : UserControl
{
    private TrackerEditor? model;
    private bool synchronizing;
    public TrackerEditorView()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = null; };
    }
    private void Attach()
    {
        if (model is not null) model.PropertyChanged -= Changed;
        model = DataContext as TrackerEditor;
        if (model is not null) model.PropertyChanged += Changed;
        Sync();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(TrackerEditor.StartDate) or nameof(TrackerEditor.EndDate)) Sync(); }
    private void Sync() { synchronizing = true; try { StartPicker.Date = model?.StartDate; EndPicker.Date = model?.EndDate; } finally { synchronizing = false; } }
    private void OnStartChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.StartDate = args.NewDate; }
    private void OnEndChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.EndDate = args.NewDate; }
    private void OnClearEnd(object sender, RoutedEventArgs args) { if (model is not null) model.EndDate = null; }
    private void OnClearStart(object sender, RoutedEventArgs args) { if (model is not null) model.StartDate = null; }
}
