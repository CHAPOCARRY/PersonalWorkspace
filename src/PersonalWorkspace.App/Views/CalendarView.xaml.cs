using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using PersonalWorkspace.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace PersonalWorkspace.App.Views;

public sealed partial class CalendarView : UserControl
{
    private CalendarViewModel? model;
    private bool synchronizing;
    private CalendarEntry? dragging;
    private Windows.Foundation.Point? dragOrigin;
    private bool startingDrag;
    public CalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= OnChanged; model = null; dragging = null; };
    }
    private void Attach()
    {
        if (model is not null) model.PropertyChanged -= OnChanged;
        model = DataContext as CalendarViewModel;
        if (model is not null) model.PropertyChanged += OnChanged;
        SynchronizePickers(); RenderDays();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CalendarViewModel.Days)) RenderDays();
        if (args.PropertyName is nameof(CalendarViewModel.SelectedDate) or nameof(CalendarViewModel.StartDate) or nameof(CalendarViewModel.EndDate)
            or nameof(CalendarViewModel.StartTime) or nameof(CalendarViewModel.EndTime)) SynchronizePickers();
    }
    private void SynchronizePickers()
    {
        synchronizing = true;
        try
        {
            SelectedDayPicker.Date = model?.SelectedDate; StartDatePicker.Date = model?.StartDate; EndDatePicker.Date = model?.EndDate;
            StartTimePicker.SelectedTime = model?.StartTime; EndTimePicker.SelectedTime = model?.EndTime;
        }
        finally { synchronizing = false; }
    }
    private void OnSelectedDayChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null && args.NewDate is { } date) model.SelectedDate = date; }
    private void OnStartDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.StartDate = args.NewDate; }
    private void OnEndDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!synchronizing && model is not null) model.EndDate = args.NewDate; }
    private void OnStartTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) { if (!synchronizing && model is not null) model.StartTime = args.NewTime; }
    private void OnEndTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) { if (!synchronizing && model is not null) model.EndTime = args.NewTime; }

    // Native layout only: dates, grouping, entries, and commands come from the view model.
    private void RenderDays()
    {
        if (dragging is not null) return;
        DaysGrid.Children.Clear(); DaysGrid.RowDefinitions.Clear(); DaysGrid.ColumnDefinitions.Clear();
        if (model is null || !model.IsCalendar) return;
        var columns = model.Mode == CalendarMode.Day ? 1 : 7;
        SetCalendarWidth(columns);
        for (var col = 0; col < columns; col++) DaysGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (model.Days.Count + columns - 1) / columns; row++) DaysGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var index = 0; index < model.Days.Count; index++)
        {
            var day = model.Days[index];
            var panel = new StackPanel { Spacing = Number("Space4") };
            var date = new Button { Content = new TextBlock { Text = day.Label, TextTrimming = TextTrimming.CharacterEllipsis }, Command = model.OpenDayCommand, CommandParameter = day,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(date, day.Date.ToString("D"));
            AutomationProperties.SetAutomationId(date, "CalendarDay_" + day.Date.ToString("yyyy-MM-dd"));
            panel.Children.Add(date);
            if (model.Mode == CalendarMode.Month)
            {
                foreach (var entry in day.Entries.Take(3)) panel.Children.Add(EntryButton(entry));
                if (day.Entries.Count > 3) panel.Children.Add(new Button { Content = $"+{day.Entries.Count - 3} more", Command = model.OpenDayCommand, CommandParameter = day });
            }
            else
            {
                AddSection(panel, "Tasks", day.Tasks); AddSection(panel, "All day", day.AllDayEvents); AddSection(panel, "Events", day.TimedEvents);
            }
            var cell = new Border { Child = panel, Padding = new Thickness(Number("Space8")), BorderThickness = new Thickness(1),
                BorderBrush = Brush(day.IsToday ? "Accent" : "Border"), Background = Brush(day.InMonth || model.Mode != CalendarMode.Month ? "Surface" : "SurfaceSecondary"),
                MinWidth = Number("CalendarDayWidth"), MinHeight = Number("CalendarDayHeight"), AllowDrop = true, Tag = day };
            cell.DragOver += OnDragOver; cell.Drop += OnDrop;
            Grid.SetColumn(cell, index % columns); Grid.SetRow(cell, index / columns); DaysGrid.Children.Add(cell);
        }
    }
    private void AddSection(StackPanel panel, string heading, IEnumerable<CalendarEntry> entries)
    {
        panel.Children.Add(new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["BodyStyle"] });
        foreach (var entry in entries) panel.Children.Add(EntryButton(entry));
    }
    private FrameworkElement EntryButton(CalendarEntry entry)
    {
        var button = new Button { Content = new TextBlock { Text = entry.Label, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                FontSize = Number("Small"), TextTrimming = TextTrimming.CharacterEllipsis }, Tag = entry,
            Style = (Style)Application.Current.Resources["TaskTitleButtonStyle"], IsEnabled = model?.IsIdle == true };
        AutomationProperties.SetName(button, entry.Label); ToolTipService.SetToolTip(button, entry.IsTask ? entry.Label : entry.Label + "\n" + entry.EventSummary);
        button.Click += OnOpen;
        // A separate handle avoids the Button's pointer capture competing with the native drag gesture.
        var handle = new TextBlock { Text = "⠿", Tag = entry, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(Number("Space4")) };
        AutomationProperties.SetName(handle, entry.DragLabel); ToolTipService.SetToolTip(handle, "Drag onto another day");
        handle.PointerPressed += OnHandlePressed; handle.PointerMoved += OnHandleMoved; handle.PointerReleased += OnHandleReleased;
        handle.DragStarting += OnDragStarting; handle.DropCompleted += OnDropCompleted;
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(button); Grid.SetColumn(handle, 1); row.Children.Add(handle);
        return row;
    }
    private static double Number(string key) => (double)Application.Current.Resources[key];
    private void OnCalendarSizeChanged(object sender, SizeChangedEventArgs args) => SetCalendarWidth(model?.Mode == CalendarMode.Day ? 1 : 7);
    private void SetCalendarWidth(int columns) => DaysGrid.Width = Math.Max(CalendarScroll.ActualWidth - Number("Space16"), columns * Number("CalendarDayWidth") + (columns - 1) * Number("Space4"));
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private void OnOpen(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: CalendarEntry entry }) model?.OpenCommand.Execute(entry); }
    private void OnSchedule(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: CalendarEntry entry }) model?.ScheduleCommand.Execute(entry); }
    private void OnHandlePressed(object sender, PointerRoutedEventArgs args)
    {
        if (model?.IsIdle != true || sender is not UIElement element) return;
        var point = args.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed) return;
        dragOrigin = point.Position; element.CapturePointer(args.Pointer); args.Handled = true;
    }
    private void OnHandleReleased(object sender, PointerRoutedEventArgs args)
    {
        dragOrigin = null;
        if (sender is UIElement element) element.ReleasePointerCapture(args.Pointer);
    }
    private async void OnHandleMoved(object sender, PointerRoutedEventArgs args)
    {
        if (startingDrag || dragOrigin is not { } origin || sender is not UIElement element) return;
        var point = args.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed || Math.Abs(point.Position.X - origin.X) + Math.Abs(point.Position.Y - origin.Y) < Number("Space8")) return;
        startingDrag = true; dragOrigin = null;
        try { await element.StartDragAsync(point); }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        { model?.ReportDragFailure(exception); }
        finally { startingDrag = false; element.ReleasePointerCaptures(); dragging = null; RenderDays(); }
    }
    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (model?.IsIdle != true || sender is not FrameworkElement { Tag: CalendarEntry entry }) { args.Cancel = true; return; }
        dragging = entry; args.Data.SetText(entry.Id.ToString("D")); args.Data.RequestedOperation = DataPackageOperation.Move;
    }
    private void OnDropCompleted(UIElement sender, DropCompletedEventArgs args) { dragging = null; RenderDays(); }
    private void OnDragOver(object sender, DragEventArgs args) { if (dragging is not null && model?.IsIdle == true) args.AcceptedOperation = DataPackageOperation.Move; }
    private async void OnDrop(object sender, DragEventArgs args)
    {
        var entry = dragging; dragging = null;
        if (entry is not null && model is not null && sender is FrameworkElement { Tag: CalendarDay day }) await model.MoveAsync(entry, day.Date);
    }
}
