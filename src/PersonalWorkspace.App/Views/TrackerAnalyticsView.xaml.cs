using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.Foundation;

namespace PersonalWorkspace.App.Views;

public sealed partial class TrackerAnalyticsView : UserControl
{
    private TrackerAnalyticsViewModel? model;
    private bool syncing;
    private static readonly string[] Colors = ["Accent", "Success", "Warning", "Danger"];
    private double PlotWidth => Math.Max(200, Chart.ActualWidth - 100);
    private const double Left = 75, Top = 20, PlotHeight = 190;
    public TrackerAnalyticsView()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = null; };
    }
    private void Attach()
    {
        if (model is not null) model.PropertyChanged -= Changed;
        model = DataContext as TrackerAnalyticsViewModel;
        if (model is not null) model.PropertyChanged += Changed;
        Sync(); Render();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TrackerAnalyticsViewModel.From) or nameof(TrackerAnalyticsViewModel.Through)) Sync();
        if (args.PropertyName is nameof(TrackerAnalyticsViewModel.Result) or nameof(TrackerAnalyticsViewModel.ChartKind)) Render();
    }
    private void Sync() { syncing = true; try { FromPicker.Date = model?.From; ThroughPicker.Date = model?.Through; } finally { syncing = false; } }
    private void OnFromChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!syncing && model is not null) model.From = args.NewDate; }
    private void OnThroughChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!syncing && model is not null) model.Through = args.NewDate; }
    private void OnChartSizeChanged(object sender, SizeChangedEventArgs args) => RenderChart();
    private Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private TextBlock Label(string text) => new() { Text = text, FontSize = (double)Application.Current.Resources["Small"], Foreground = Brush("TextSecondary") };
    private void Render() { RenderChart(); RenderHeatmaps(); }
    private void RenderChart()
    {
        Chart.Children.Clear(); Legend.Children.Clear(); Inspection.Text = "Point to a value to inspect it. Focus a marker for its value.";
        if (model?.Result is not { } result || !model.IsNumeric || !model.HasData) return;
        var numbers = result.Series.SelectMany(s => s.Points).Where(p => p.Value?.Number is not null).Select(p => (double)p.Value!.Number!.Value).ToArray();
        if (numbers.Length == 0) return;
        var min = numbers.Min(); var max = numbers.Max(); var settings = result.Series[0].Tracker.Settings;
        if (settings.Type == TrackerValueType.Percentage) { min = 0; max = 100; }
        else if (settings.Type == TrackerValueType.Scale) { min = settings.ScaleMin!.Value; max = settings.ScaleMax!.Value; }
        else if (model.ChartKind is TrackerChartKind.Bar or TrackerChartKind.Area) { min = Math.Min(0, min); max = Math.Max(0, max); }
        if (min == max) { var padding = Math.Max(1, Math.Abs(min) * .05); min -= padding; max += padding; }
        double Y(double number) => Top + PlotHeight * (1 - (number - min) / (max - min));
        double X(DateOnly date) => Left + PlotWidth * (date.DayNumber - result.Range.From.DayNumber) / Math.Max(1, result.Range.Through.DayNumber - result.Range.From.DayNumber);
        for (var tick = 0; tick <= 2; tick++)
        {
            var value = min + (max - min) * tick / 2;
            var y = Y(value); var line = new Line { X1 = Left, X2 = Left + PlotWidth, Y1 = y, Y2 = y, Stroke = Brush("Border"), StrokeThickness = 1 }; Chart.Children.Add(line);
            var text = Label(settings.Type == TrackerValueType.Duration ? "" : value.ToString("0.##", CultureInfo.CurrentCulture));
            if (settings.Type == TrackerValueType.Duration && value >= 0 && value <= (double)decimal.MaxValue) text.Text = AnalyticsPresentation.Amount(decimal.Round((decimal)value), settings);
            text.MaxWidth = Left - 4; Canvas.SetTop(text, y - 8); Chart.Children.Add(text);
        }
        var dates = new[] { result.Range.From, DateOnly.FromDayNumber((result.Range.From.DayNumber + result.Range.Through.DayNumber) / 2), result.Range.Through };
        foreach (var date in dates.Distinct()) { var label = Label(date.ToString("d")); Canvas.SetLeft(label, Math.Min(Left + PlotWidth - 80, X(date))); Canvas.SetTop(label, Top + PlotHeight + 12); Chart.Children.Add(label); }
        for (var index = 0; index < result.Series.Count; index++)
        {
            var series = result.Series[index]; var brush = Brush(Colors[index]); var legend = Label($"{index + 1}. {series.Tracker.Item.Title} · {series.Tracker.Settings.Unit ?? series.Tracker.Settings.CurrencyCode ?? series.Tracker.Settings.Type.ToString()}"); legend.Foreground = brush; Legend.Children.Add(legend);
            var segment = new List<Point>();
            void DrawSegment()
            {
                if (segment.Count > 1)
                {
                    if (model.ChartKind == TrackerChartKind.Area)
                    {
                        var polygon = new Polygon { Fill = brush, Opacity = .12 };
                        polygon.Points.Add(new(segment[0].X, Y(Math.Clamp(0, min, max)))); foreach (var point in segment) polygon.Points.Add(point); polygon.Points.Add(new(segment[^1].X, Y(Math.Clamp(0, min, max)))); Chart.Children.Add(polygon);
                    }
                    var line = new Polyline { Stroke = brush, StrokeThickness = 2 };
                    if (index > 0) line.StrokeDashArray = new DoubleCollection { index + 1, 2 };
                    foreach (var point in segment) line.Points.Add(point); Chart.Children.Add(line);
                }
                segment.Clear();
            }
            var sample = Math.Max(1, (int)Math.Ceiling(series.Points.Count / 300d));
            for (var p = 0; p < series.Points.Count; p++)
            {
                var point = series.Points[p]; if (point.Value?.Number is not { } number) { DrawSegment(); continue; }
                var x = X(point.Date); var y = Y((double)number); var description = AnalyticsPresentation.Point(series, point);
                if (model.ChartKind == TrackerChartKind.Bar)
                {
                    var width = Math.Max(1, Math.Min(24, PlotWidth / Math.Max(1, result.Range.Through.DayNumber - result.Range.From.DayNumber + 1) / result.Series.Count));
                    var bar = new Rectangle { Width = width, Height = Math.Max(2, Math.Abs(Y(Math.Clamp(0, min, max)) - y)), Fill = brush };
                    var groupWidth = width * result.Series.Count;
                    var groupLeft = Math.Clamp(x - groupWidth / 2, Left, Left + PlotWidth - groupWidth);
                    Canvas.SetLeft(bar, groupLeft + index * width); Canvas.SetTop(bar, Math.Min(y, Y(Math.Clamp(0, min, max)))); ToolTipService.SetToolTip(bar, description); AutomationProperties.SetName(bar, description); Chart.Children.Add(bar);
                }
                else segment.Add(new(x, y));
                if (p % sample == 0 || p == series.Points.Count - 1)
                {
                    var marker = new Button { Content = (index + 1).ToString(), FontSize = 9, Width = 20, Height = 20, Padding = new(0), MinWidth = 0, MinHeight = 0,
                        Background = Brush("Surface"), Foreground = brush, BorderBrush = brush, BorderThickness = new(1) };
                    Canvas.SetLeft(marker, x - 10); Canvas.SetTop(marker, y - 10); ToolTipService.SetToolTip(marker, description); AutomationProperties.SetName(marker, description);
                    marker.GotFocus += (_, _) => Inspection.Text = description; marker.Click += (_, _) => Inspection.Text = description; Chart.Children.Add(marker);
                }
            }
            DrawSegment();
        }
    }
    private void OnChartPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (model?.Result is not { } result) return;
        var fraction = Math.Clamp((args.GetCurrentPoint(Chart).Position.X - Left) / PlotWidth, 0, 1);
        var day = result.Range.From.DayNumber + (int)Math.Round(fraction * (result.Range.Through.DayNumber - result.Range.From.DayNumber));
        Inspection.Text = string.Join("\n", result.Series.Where(s => s.Points.Count > 0).Select(s => AnalyticsPresentation.Point(s, s.Points.MinBy(p => Math.Abs(p.Date.DayNumber - day))!)));
    }
    private void RenderHeatmaps()
    {
        Heatmaps.Children.Clear(); if (model?.Result is not { } result) return;
        var firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        foreach (var series in result.Series)
        {
            Heatmaps.Children.Add(Label(series.Tracker.Item.Title));
            var cells = TrackerAnalytics.Heatmap(series, result.Range); if (cells.Count == 0) continue;
            var grid = new Grid(); var offset = ((int)cells[0].Date.DayOfWeek - firstDay + 7) % 7;
            for (var row = 0; row < 8; row++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var columns = (cells.Count + offset + 6) / 7;
            grid.ColumnDefinitions.Add(new() { Width = new(45) });
            for (var col = 0; col < columns; col++) grid.ColumnDefinitions.Add(new() { Width = new((double)Application.Current.Resources["AnalyticsHeatCellSize"]) });
            for (var row = 0; row < 7; row++) { var label = Label(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((firstDay + row) % 7))); Grid.SetRow(label, row + 1); grid.Children.Add(label); }
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i]; var col = (i + offset) / 7 + 1; var row = (i + offset) % 7 + 1;
                if (cell.Date.Day == 1 && i > 6 || i == 0) { var month = Label(cell.Date.ToString("MMM yy")); Grid.SetColumn(month, col); Grid.SetColumnSpan(month, Math.Min(4, columns - col + 1)); grid.Children.Add(month); }
                var mark = cell.Value?.Boolean is { } boolean ? boolean ? "✓" : "×" : cell.TargetReached is { } hit ? hit ? "✓" : "×" : cell.Value is not null ? "•" : cell.Expected ? "–" : "";
                var color = cell.Value is null ? cell.Expected ? "Border" : "SurfaceSecondary" : cell.TargetReached == true || cell.Value.Boolean == true ? "Success" : "Accent";
                var description = $"{cell.Date:d} · " + (cell.Value is { } value ? TrackerPresentation.Amount(value, series.Tracker.Settings) : cell.Expected ? "Missing" : "Not expected")
                    + (cell.PeriodDate is { } period ? $" · Period {period:d}" : "") + (series.Tracker.Target is { } target ? " · Target " + TrackerPresentation.Amount(target, series.Tracker.Settings) : "");
                var button = new Button { Content = mark, Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new(0), Margin = new(1), Background = Brush(color),
                    Foreground = Brush(cell.Value is null ? "TextSecondary" : "Surface"), BorderThickness = new(0) };
                AutomationProperties.SetName(button, description); ToolTipService.SetToolTip(button, description); Grid.SetColumn(button, col); Grid.SetRow(button, row); grid.Children.Add(button);
            }
            Heatmaps.Children.Add(grid);
        }
    }
}
