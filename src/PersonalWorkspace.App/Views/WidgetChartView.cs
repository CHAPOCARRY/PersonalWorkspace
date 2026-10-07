using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.Foundation;

namespace PersonalWorkspace.App.Views;

// Plots immutable canonical Analytics points; no aggregation or business calculations live here.
public sealed class WidgetChartView : UserControl
{
    private readonly TrackerAnalyticsResult result;
    private readonly WidgetChartKind kind;
    private readonly Canvas plot = new();
    public WidgetChartView(TrackerAnalyticsResult result, WidgetChartKind kind)
    {
        this.result = result; this.kind = kind; Content = plot; Height = 160; MinWidth = 80;
        SizeChanged += (_, _) => Render(); Loaded += (_, _) => Render();
    }
    private void Render()
    {
        plot.Children.Clear(); var series = result.Series[0]; var points = series.Points;
        var numbers = points.Where(p => p.Value?.Number is not null).Select(p => (double)p.Value!.Number!.Value).ToArray();
        if (numbers.Length == 0) { plot.Children.Add(new TextBlock { Text = "No recorded values in this range.", TextWrapping = TextWrapping.Wrap, Width = Math.Max(80, ActualWidth) }); return; }
        var brush = (Brush)Application.Current.Resources["Accent"]; var muted = (Brush)Application.Current.Resources["TextSecondary"];
        var min = numbers.Min(); var max = numbers.Max(); var settings = series.Tracker.Settings;
        if (settings.Type == TrackerValueType.Percentage) { min = 0; max = 100; }
        else if (settings.Type == TrackerValueType.Scale) { min = (double)settings.ScaleMin!.Value; max = (double)settings.ScaleMax!.Value; }
        else if (kind != WidgetChartKind.Line) { min = Math.Min(0, min); max = Math.Max(0, max); }
        if (max == min) { min -= 1; max += 1; }
        var width = Math.Max(32, ActualWidth - 56); const double top = 22, height = 112, left = 44;
        double X(DateOnly date) => left + width * (date.DayNumber - result.Range.From.DayNumber) / Math.Max(1, result.Range.Through.DayNumber - result.Range.From.DayNumber);
        double Y(double value) => top + height * (1 - (value - min) / (max - min));
        var high = new TextBlock { Text = max.ToString("G4"), FontSize = 10, Foreground = muted }; Canvas.SetTop(high, top); plot.Children.Add(high);
        var low = new TextBlock { Text = min.ToString("G4"), FontSize = 10, Foreground = muted }; Canvas.SetTop(low, top + height - 12); plot.Children.Add(low);
        var label = new TextBlock { Text = kind + " · " + (settings.Unit ?? settings.CurrencyCode ?? settings.Type.ToString()), FontSize = 10, Foreground = muted }; plot.Children.Add(label);
        var segment = new List<Point>(); var baseline = Y(Math.Clamp(0, min, max));
        void Flush()
        {
            if (segment.Count == 0) return;
            if (kind == WidgetChartKind.Area)
            {
                var area = new Polygon { Fill = brush, Opacity = .12, IsHitTestVisible = false }; area.Points.Add(new(segment[0].X, baseline)); foreach (var p in segment) area.Points.Add(p); area.Points.Add(new(segment[^1].X, baseline)); plot.Children.Add(area);
            }
            if (kind != WidgetChartKind.Bar) { var line = new Polyline { Stroke = brush, StrokeThickness = 1.5, IsHitTestVisible = false }; foreach (var p in segment) line.Points.Add(p); plot.Children.Add(line); }
            segment.Clear();
        }
        foreach (var point in points)
        {
            if (point.Value?.Number is not { } number) { Flush(); continue; }
            var x = X(point.Date); var y = Y((double)number); segment.Add(new(x, y)); var description = AnalyticsPresentation.Point(series, point);
            if (kind == WidgetChartKind.Bar)
            {
                var bar = new Rectangle { Width = Math.Max(2, Math.Min(16, width / Math.Max(1, points.Count) * .7)), Height = Math.Max(1, Math.Abs(baseline - y)), Fill = brush };
                Canvas.SetLeft(bar, x - bar.Width / 2); Canvas.SetTop(bar, Math.Min(y, baseline)); ToolTipService.SetToolTip(bar, description); plot.Children.Add(bar);
            }
            var marker = new Button { Width = 8, Height = 8, MinWidth = 0, MinHeight = 0, Padding = new(0), Background = brush };
            Canvas.SetLeft(marker, x - 4); Canvas.SetTop(marker, y - 4); Canvas.SetZIndex(marker, 1); AutomationProperties.SetName(marker, description); ToolTipService.SetToolTip(marker, description); plot.Children.Add(marker);
        }
        Flush();
    }
}
