using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.Foundation;
using Windows.System;

namespace PersonalWorkspace.App.Views;

public sealed partial class CanvasWorkspaceView
{
    private readonly Canvas decorationLayer = new() { IsHitTestVisible = false };
    private readonly Canvas decorationOverlay = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Dictionary<Guid, (Canvas Visual, CanvasDecoration Item, DecorationPoint? Start, DecorationPoint? End)> decorationVisuals = [];
    private bool decorationRendering, decorationDragging, decorationReleasing;
    private string decorationHandle = "";
    private DecorationPoint decorationStart = new(0, 0);
    private CanvasDecoration? gestureDecoration;
    private DecorationEndpoint? connectorStart;
    private readonly List<DecorationPoint> strokePoints = [];
    private Guid? styledDecoration;
    private DecorationWorkspaceViewModel? Drawing => model?.Decorations;
    private CanvasSnapshot? DrawingCanvas => model?.Snapshot is { } canvas ? canvas with { Items = model.DisplayItems } : null;
    private void InitializeDecorations()
    {
        decorationLayer.Width = decorationLayer.Height = decorationOverlay.Width = decorationOverlay.Height = CanvasLayout.Extent;
        Canvas.SetZIndex(decorationLayer, -10); Canvas.SetZIndex(decorationOverlay, 20);
        Surface.Children.Add(decorationLayer); Surface.Children.Add(decorationOverlay);
        DecorationToolPicker.ItemsSource = Enum.GetValues<DecorationTool>(); DecorationToolPicker.SelectedItem = DecorationTool.Layout;
        DecorationPalette.ItemsSource = Enum.GetValues<DecorationColor>(); DecorationPalette.SelectedItem = DecorationColor.Accent;
        AutomationProperties.SetAutomationId(decorationOverlay, "DecorationInput");
        decorationOverlay.PointerPressed += DecorationPressed;
        // ScrollViewer can mark motion handled; a captured drawing gesture still needs every sample.
        decorationOverlay.AddHandler(PointerMovedEvent, new PointerEventHandler(DecorationMoved), true);
        decorationOverlay.PointerReleased += DecorationReleased;
        decorationOverlay.PointerCanceled += (_, _) => CancelDecorationGesture();
        decorationOverlay.PointerCaptureLost += (_, _) => { if (!decorationReleasing && decorationDragging) CancelDecorationGesture(); };
    }
    private void RenderDecorations()
    {
        var drawing = Drawing; var canvas = DrawingCanvas;
        decorationRendering = true;
        try
        {
            DecorationToolPicker.IsEnabled = model?.CanEdit == true && !decorationDragging;
            DecorationToolPicker.SelectedItem = drawing?.Tool ?? DecorationTool.Layout;
            var editing = drawing is { Tool: not DecorationTool.Layout } && model?.Locked == false && canvas?.PageActive == true;
            decorationOverlay.IsHitTestVisible = editing && !drawing!.IsBusy;
            DecorationTools.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            DecorationTools.IsEnabled = model?.CanEdit == true && !decorationDragging;
            if (editing) { SelectionTools.Visibility = Visibility.Collapsed; AddBlock.IsEnabled = AddContainer.IsEnabled = AddWidgetButton.IsEnabled = false; }
            if (drawing?.Error is { } error) { StatusText.Text = error; StatusText.Foreground = Brush("Danger"); }
            else if (editing) StatusText.Text = drawing!.Selected is { } id ? $"{drawing.DisplayItems.SingleOrDefault(i => i.Id == id)?.Kind} selected · drag handles, or Delete" : drawing.Tool == DecorationTool.Select ? "Select a decoration · Layout returns to Widget interaction" : $"{drawing.Tool}: drag on the Page · Escape cancels";
            var items = drawing?.DisplayItems ?? [];
            EmptyText.Visibility = model?.HasPage == true && model.DisplayItems.Count == 0 && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var removed in decorationVisuals.Keys.Except(items.Select(i => i.Id)).ToArray()) { decorationLayer.Children.Remove(decorationVisuals[removed].Visual); decorationVisuals.Remove(removed); }
            decorationOverlay.Children.Clear();
            if (canvas is null) return;
            foreach (var item in items)
            {
                var a = item.Start is null ? null : DecorationRules.Resolve(item.Start, canvas); var b = item.End is null ? null : DecorationRules.Resolve(item.End, canvas);
                if (!decorationVisuals.TryGetValue(item.Id, out var old) || old.Item != item || old.Start != a || old.End != b)
                {
                    if (old.Visual is not null) decorationLayer.Children.Remove(old.Visual);
                    var visual = PaintDecoration(item, canvas); Canvas.SetZIndex(visual, item.ZOrder); decorationLayer.Children.Add(visual);
                    decorationVisuals[item.Id] = (visual, item, a, b);
                }
                if (editing && item.Id == drawing!.Selected)
                {
                    var outline = PaintDecoration(item, canvas, true); decorationOverlay.Children.Add(outline);
                    AutomationProperties.SetName(outline, $"Selected {item.Kind} decoration"); AutomationProperties.SetAutomationId(outline, "SelectedDecoration_" + item.Id.ToString("N"));
                    foreach (var (point, label) in DecorationHandles(item, canvas)) AddDecorationHandle(point, label);
                    if (styledDecoration != item.Id && !decorationDragging)
                    { DecorationPalette.SelectedItem = item.Style.Color; DecorationWidth.Value = item.Style.Width; DecorationFill.IsChecked = item.Style.Filled; DecorationDash.IsChecked = item.Style.Dashed; styledDecoration = item.Id; }
                }
            }
        }
        finally { decorationRendering = false; }
    }
    private Canvas PaintDecoration(CanvasDecoration item, CanvasSnapshot canvas, bool selection = false)
    {
        var visual = new Canvas { IsHitTestVisible = false };
        var brush = Brush(selection ? "Accent" : item.Style.Color switch { DecorationColor.Neutral => "TextSecondary", DecorationColor.Success => "Success", DecorationColor.Warning => "Warning", DecorationColor.Danger => "Danger", _ => "Accent" });
        var width = selection ? Math.Max(1, 100d / (model?.Zoom ?? 100)) : item.Style.Width;
        void Add(Shape shape)
        {
            shape.Stroke = brush; shape.StrokeThickness = width;
            if (selection || item.Style.Dashed) shape.StrokeDashArray = new DoubleCollection { 4, 3 };
            visual.Children.Add(shape);
        }
        if (item.Bounds is { } r)
        {
            Shape shape = item.Kind == DecorationKind.Ellipse ? new Ellipse() : new Rectangle { RadiusX = item.Kind == DecorationKind.RoundedRectangle ? 12 : 0, RadiusY = item.Kind == DecorationKind.RoundedRectangle ? 12 : 0 };
            shape.Width = r.Width; shape.Height = r.Height; Canvas.SetLeft(shape, r.X); Canvas.SetTop(shape, r.Y);
            if (!selection && item.Style.Filled) shape.Fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = .15 };
            Add(shape);
        }
        else if (item.Points is { } points)
        {
            var polyline = new Polyline { StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in points) polyline.Points.Add(new(p.X, p.Y)); Add(polyline);
        }
        else
        {
            var a = DecorationRules.Resolve(item.Start!, canvas); var b = DecorationRules.Resolve(item.End!, canvas);
            Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y });
            if (item.Kind is DecorationKind.Arrow or DecorationKind.Connector)
            {
                var distance = DecorationRules.Distance(a, b);
                if (distance > .01)
                {
                    var size = Math.Min(distance, Math.Max(12, item.Style.Width * 4)); var dx = (b.X - a.X) / distance; var dy = (b.Y - a.Y) / distance;
                    var head = new Polyline(); head.Points.Add(new(b.X - size * dx + size * .45 * dy, b.Y - size * dy - size * .45 * dx)); head.Points.Add(new(b.X, b.Y)); head.Points.Add(new(b.X - size * dx - size * .45 * dy, b.Y - size * dy + size * .45 * dx)); Add(head);
                }
            }
        }
        return visual;
    }
    private IEnumerable<(DecorationPoint Point, string Label)> DecorationHandles(CanvasDecoration item, CanvasSnapshot canvas)
    {
        if (item.Bounds is { } r) return new[] { (new DecorationPoint(r.Right, r.Y + r.Height / 2d), "right"), (new DecorationPoint(r.X + r.Width / 2d, r.Bottom), "bottom"), (new DecorationPoint(r.Right, r.Bottom), "bottom-right") };
        if (item.Start is not null) return new[] { (DecorationRules.Resolve(item.Start, canvas), "start"), (DecorationRules.Resolve(item.End!, canvas), "end") };
        return [];
    }
    private void AddDecorationHandle(DecorationPoint p, string label)
    {
        var size = 10d * 100 / model!.Zoom;
        var handle = new Border { Width = size, Height = size, Background = Brush("Surface"), BorderBrush = Brush("Accent"), BorderThickness = new(2), IsHitTestVisible = false };
        Canvas.SetLeft(handle, p.X - size / 2); Canvas.SetTop(handle, p.Y - size / 2);
        AutomationProperties.SetName(handle, "Decoration handle " + label); AutomationProperties.SetAutomationId(handle, "DecorationHandle_" + label); decorationOverlay.Children.Add(handle);
    }
    private static DecorationPoint Logical(Point point) => new(point.X, point.Y);
    private static DecorationPoint Snapped(DecorationPoint p) => new(CanvasLayout.Snap(p.X), CanvasLayout.Snap(p.Y));
    private DecorationStyle ChosenStyle(DecorationKind kind) => new(DecorationPalette.SelectedItem is DecorationColor color ? color : DecorationColor.Accent,
        double.IsFinite(DecorationWidth.Value) ? Math.Clamp((int)DecorationWidth.Value, 1, 16) : 2, DecorationRules.IsShape(kind) && DecorationFill.IsChecked == true, DecorationDash.IsChecked == true);
    private DecorationEndpoint EndpointAt(DecorationPoint point, bool attach)
    {
        var canvas = DrawingCanvas!; var state = new CanvasState(canvas);
        if (attach)
            foreach (var item in canvas.Items.OrderBy(i => i.Kind == CanvasKind.Block ? 0 : 1))
            {
                var r = CanvasLayout.Absolute(state, item);
                if (point.X < r.X || point.X > r.Right || point.Y < r.Y || point.Y > r.Bottom) continue;
                var anchor = Enum.GetValues<DecorationAnchor>().OrderBy(a => DecorationRules.Distance(point, DecorationRules.Resolve(new(point, item.Id, a), canvas))).First();
                return new(point, item.Id, anchor);
            }
        return new(Snapped(point));
    }
    private void OnDecorationTool(object sender, SelectionChangedEventArgs args)
    {
        if (decorationRendering || Drawing is not { } drawing || DecorationToolPicker.SelectedItem is not DecorationTool tool) return;
        Cancel(); CancelDecorationGesture(); model!.Select(null); drawing.SetTool(tool); styledDecoration = null; Render();
    }
    private void DecorationPressed(object sender, PointerRoutedEventArgs args)
    {
        if (Drawing is not { CanEdit: true } drawing || model?.CanEdit != true || !args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        args.Handled = true; Focus(FocusState.Programmatic); decorationStart = Logical(args.GetCurrentPoint(Surface).Position); decorationHandle = ""; gestureDecoration = null;
        if (drawing.Tool == DecorationTool.Select)
        {
            var selected = drawing.DisplayItems.SingleOrDefault(i => i.Id == drawing.Selected);
            if (selected is not null) decorationHandle = DecorationHandles(selected, DrawingCanvas!).FirstOrDefault(h => DecorationRules.Distance(h.Point, decorationStart) <= 9 * 100d / model.Zoom).Label ?? "";
            var hit = decorationHandle.Length > 0 ? selected : drawing.DisplayItems.Reverse().FirstOrDefault(i => DecorationRules.Hit(i, DrawingCanvas!, decorationStart, 7 * 100d / model.Zoom));
            drawing.Select(hit?.Id); if (hit is null) return; gestureDecoration = hit;
        }
        else if (drawing.Tool == DecorationTool.Connector) connectorStart = EndpointAt(decorationStart, true);
        strokePoints.Clear(); strokePoints.Add(decorationStart);
        if (!drawing.BeginGesture()) return; decorationDragging = true; decorationOverlay.CapturePointer(args.Pointer);
    }
    private void DecorationMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!decorationDragging || Drawing is not { } drawing) return; args.Handled = true;
        var point = Logical(args.GetCurrentPoint(Surface).Position);
        // Bounds validation remains authoritative; a pointer outside the finite canvas cancels the proposal.
        try
        {
            DecorationEdit edit;
            if (gestureDecoration is { } selected)
            {
                if (decorationHandle is "start" or "end") edit = new ChangeDecorationEndpoint(selected.Id, decorationHandle == "start", EndpointAt(point, selected.Kind == DecorationKind.Connector && !Key(VirtualKey.Menu)));
                else if (decorationHandle.Length > 0 && selected.Bounds is { } r) edit = new ResizeDecoration(selected.Id, r with { Width = decorationHandle == "bottom" ? r.Width : CanvasLayout.Snap(point.X - r.X), Height = decorationHandle == "right" ? r.Height : CanvasLayout.Snap(point.Y - r.Y) });
                else edit = new MoveDecoration(selected.Id, point.X - decorationStart.X, point.Y - decorationStart.Y);
            }
            else
            {
                var kind = drawing.Tool switch { DecorationTool.Rectangle => DecorationKind.Rectangle, DecorationTool.RoundedRectangle => DecorationKind.RoundedRectangle, DecorationTool.Ellipse => DecorationKind.Ellipse, DecorationTool.Line => DecorationKind.Line, DecorationTool.Arrow => DecorationKind.Arrow, DecorationTool.Connector => DecorationKind.Connector, _ => DecorationKind.Stroke };
                var style = ChosenStyle(kind);
                if (DecorationRules.IsShape(kind)) edit = new CreateDecoration(kind, style, new(CanvasLayout.Snap(Math.Min(decorationStart.X, point.X)), CanvasLayout.Snap(Math.Min(decorationStart.Y, point.Y)), CanvasLayout.Snap(Math.Abs(point.X - decorationStart.X)), CanvasLayout.Snap(Math.Abs(point.Y - decorationStart.Y))));
                else if (DecorationRules.IsLine(kind)) edit = new CreateDecoration(kind, style, Start: kind == DecorationKind.Connector ? connectorStart : new(Snapped(decorationStart)), End: EndpointAt(point, kind == DecorationKind.Connector));
                else
                {
                    if (strokePoints.Count < 32769 && DecorationRules.Distance(strokePoints[^1], point) >= 1) strokePoints.Add(point);
                    edit = new CreateDecoration(kind, style, Points: strokePoints.ToArray());
                }
            }
            drawing.Preview(edit);
        }
        catch (CanvasValidationException) { drawing.Preview(new MoveDecoration(Guid.Empty, 0, 0)); }
    }
    private async void DecorationReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!decorationDragging) return;
        if (Drawing?.Tool == DecorationTool.Pen && gestureDecoration is null && strokePoints.Count < 32769)
            strokePoints.Add(Logical(args.GetCurrentPoint(Surface).Position));
        DecorationMoved(sender, args); ReleaseDecorationCapture();
        if (Drawing is { } drawing) await drawing.EndGestureAsync(); args.Handled = true;
    }
    private void ReleaseDecorationCapture()
    { decorationDragging = false; decorationReleasing = true; decorationOverlay.ReleasePointerCaptures(); decorationReleasing = false; strokePoints.Clear(); gestureDecoration = null; connectorStart = null; }
    private void CancelDecorationGesture() { ReleaseDecorationCapture(); Drawing?.CancelGesture(); }
    private async Task<bool> DecorationKey(VirtualKey key)
    {
        if (Drawing is not { Tool: not DecorationTool.Layout } drawing) return false;
        if (key == VirtualKey.Escape) { CancelDecorationGesture(); drawing.Select(null); return true; }
        if (!drawing.CanEdit || drawing.Selected is not { } id) return false;
        if (key == VirtualKey.Delete) { await drawing.EditAsync(new DeleteDecoration(id)); return true; }
        if (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = Key(VirtualKey.Shift) ? 80 : 8;
            await drawing.EditAsync(new MoveDecoration(id, key == VirtualKey.Left ? -step : key == VirtualKey.Right ? step : 0, key == VirtualKey.Up ? -step : key == VirtualKey.Down ? step : 0)); return true;
        }
        return false;
    }
    private async void OnDecorationStyle(object sender, RoutedEventArgs args)
    { if (Drawing?.DisplayItems.SingleOrDefault(i => i.Id == Drawing.Selected) is { } item) await Drawing.EditAsync(new ChangeDecorationStyle(item.Id, ChosenStyle(item.Kind))); }
    private async void OnDecorationDelete(object sender, RoutedEventArgs args) { if (Drawing?.Selected is { } id) await Drawing.EditAsync(new DeleteDecoration(id)); }
    private async void OnDecorationDuplicate(object sender, RoutedEventArgs args) { if (Drawing?.Selected is { } id) await Drawing.EditAsync(new DuplicateDecoration(id)); }
    private async void OnDecorationFront(object sender, RoutedEventArgs args) { if (Drawing?.Selected is { } id) await Drawing.EditAsync(new ReorderDecoration(id, true)); }
    private async void OnDecorationBack(object sender, RoutedEventArgs args) { if (Drawing?.Selected is { } id) await Drawing.EditAsync(new ReorderDecoration(id, false)); }
    private sealed record EndpointTarget(Guid? Id, string Label);
    private async void OnDecorationEndpoints(object sender, RoutedEventArgs args)
    {
        if (Drawing is not { CanEdit: true } drawing || DrawingCanvas is not { } canvas || drawing.DisplayItems.SingleOrDefault(i => i.Id == drawing.Selected) is not { Kind: DecorationKind.Connector } item) return;
        var reference = drawing.Reference;
        var choices = new[] { new EndpointTarget(null, "Free point (detach)") }.Concat(canvas.Items.Select(i => new EndpointTarget(i.Id, (model!.Widgets?.Rows.SingleOrDefault(r => r.Widget.CanvasItemId == i.Id)?.Title ?? (i.Title.Length > 0 ? i.Title : i.Kind.ToString())) + " · " + i.Id.ToString("N")[..6]))).ToArray();
        var end = new ComboBox { Header = "Endpoint", ItemsSource = new[] { "Start", "End" }, SelectedIndex = 0 };
        var target = new ComboBox { Header = "Attach to", ItemsSource = choices, DisplayMemberPath = "Label" };
        var anchor = new ComboBox { Header = "Anchor", ItemsSource = Enum.GetValues<DecorationAnchor>() };
        void Load() { var value = end.SelectedIndex == 0 ? item.Start! : item.End!; target.SelectedItem = choices.SingleOrDefault(c => c.Id == value.Target) ?? choices[0]; anchor.SelectedItem = value.Anchor; }
        end.SelectionChanged += (_, _) => Load(); Load();
        AutomationProperties.SetAutomationId(end, "ConnectorEnd"); AutomationProperties.SetAutomationId(target, "ConnectorTarget"); AutomationProperties.SetAutomationId(anchor, "ConnectorAnchor");
        var panel = new StackPanel { Spacing = 8, Width = 320 }; panel.Children.Add(end); panel.Children.Add(target); panel.Children.Add(anchor);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("Danger") }; panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Connector endpoint", Content = panel, PrimaryButtonText = "Apply endpoint", CloseButtonText = "Cancel" };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true; var deferral = e.GetDeferral();
            try
            {
                if (drawing.Reference != reference || !drawing.CanEdit) { error.Text = "The Page changed or is locked."; return; }
                var old = end.SelectedIndex == 0 ? item.Start! : item.End!;
                await drawing.EditAsync(new ChangeDecorationEndpoint(item.Id, end.SelectedIndex == 0, new(DecorationRules.Resolve(old, canvas), ((EndpointTarget)target.SelectedItem).Id, (DecorationAnchor)anchor.SelectedItem)));
                error.Text = drawing.Error ?? ""; e.Cancel = drawing.Error is not null;
            }
            finally { deferral.Complete(); }
        };
        void ContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        { if (drawing.Reference != reference || drawing.Snapshot?.Canvas is not { PageActive: true, Settings.LayoutLocked: false }) dialog.Hide(); }
        drawing.PropertyChanged += ContextChanged;
        try { await dialog.ShowAsync(); }
        finally { drawing.PropertyChanged -= ContextChanged; }
    }
}
