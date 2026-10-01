using System.ComponentModel;
using Microsoft.UI.Input;
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
using Windows.UI.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class CanvasWorkspaceView : UserControl
{
    private CanvasWorkspaceViewModel? model;
    private WorkspaceItemReference? displayedPage;
    private readonly Dictionary<Guid, Border> visuals = [];
    private readonly List<FrameworkElement> decorations = [];
    private Guid? dragging;
    private string resize = "";
    private Point start;
    private CanvasRect? original;
    private bool rendering, resetViewport, releasing;
    public CanvasWorkspaceView()
    {
        InitializeComponent(); Surface.Width = Surface.Height = CanvasLayout.Extent;
        // Keyboard focus must not scroll the surrounding Page during a pointer gesture.
        BringIntoViewRequested += (_, args) => { if (ReferenceEquals(args.TargetElement, this)) args.Handled = true; };
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { Cancel(); if (model is not null) model.PropertyChanged -= Changed; model = null; };
    }
    private void Attach() { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as CanvasWorkspaceViewModel; if (model is not null) model.PropertyChanged += Changed; Render(); }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(CanvasWorkspaceViewModel.DisplayItems)) Render(); }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static bool Key(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void Render()
    {
        if (rendering || model is null) return; rendering = true;
        try
        {
            if (displayedPage != model.Reference)
            {
                CancelCapture(); displayedPage = model.Reference; resetViewport = true;
            }
            if (!model.IsPreview)
            {
                CanvasScale.ScaleX = CanvasScale.ScaleY = model.Zoom / 100d;
                ScaledExtent.Width = ScaledExtent.Height = CanvasLayout.Extent * model.Zoom / 100d;
                if (resetViewport) { Viewport.ChangeView(0, 0, null, true); resetViewport = false; }
            }
            LockButton.Content = model.Locked ? "Unlock Layout" : "Lock Layout"; LockButton.IsEnabled = model.CanConfigure;
            AddBlock.IsEnabled = AddContainer.IsEnabled = model.CanEdit; ZoomLabel.Content = model.Zoom + "%";
            StatusText.Text = model.Status; StatusText.Foreground = Brush(model.Error is null ? "TextSecondary" : "Danger");
            EmptyText.Visibility = model.HasPage && model.DisplayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectionTools.Visibility = model.Selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DeleteButton.IsEnabled = model.CanEdit;
            var single = model.Selection.Count == 1 ? model.DisplayItems.SingleOrDefault(i => model.Selection.Contains(i.Id)) : null;
            Membership.Visibility = MembershipButton.Visibility = single?.Kind == CanvasKind.Block ? Visibility.Visible : Visibility.Collapsed;
            ContainerTitle.Visibility = TitleButton.Visibility = single?.Kind == CanvasKind.Container ? Visibility.Visible : Visibility.Collapsed;
            Membership.IsEnabled = MembershipButton.IsEnabled = ContainerTitle.IsEnabled = TitleButton.IsEnabled = model.CanEdit;
            if (!model.IsPreview)
            {
                var previous = (Membership.SelectedItem as CanvasContainerChoice)?.Id;
                Membership.ItemsSource = model.Containers; Membership.SelectedItem = model.Containers.FirstOrDefault(c => c.Id == previous);
                if (single?.Kind == CanvasKind.Container && !Equals(ContainerTitle.Tag, single.Id))
                { ContainerTitle.Text = single!.Title; ContainerTitle.Tag = single.Id; }
            }
            var ids = model.DisplayItems.Select(i => i.Id).ToHashSet();
            foreach (var id in visuals.Keys.Where(id => !ids.Contains(id)).ToArray()) { Surface.Children.Remove(visuals[id]); visuals.Remove(id); }
            foreach (var decoration in decorations) Surface.Children.Remove(decoration); decorations.Clear();
            if (model.Snapshot is not { } snapshot) return;
            var state = new CanvasState(snapshot with { Items = model.DisplayItems });
            foreach (var item in model.DisplayItems.OrderBy(i => i.Kind == CanvasKind.Container ? 0 : 1))
            {
                if (!visuals.TryGetValue(item.Id, out var border))
                {
                    border = new Border { Background = Brush(item.Kind == CanvasKind.Container ? "SurfaceSecondary" : "Surface"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)Application.Current.Resources["CardRadius"], Padding = new Thickness(8), Child = new TextBlock { TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false } };
                    border.PointerPressed += OnItemPressed; Surface.Children.Add(border); visuals[item.Id] = border;
                }
                var selected = model.Selection.Contains(item.Id); var absolute = CanvasLayout.Absolute(state, item);
                border.Tag = item; border.Width = absolute.Width; border.Height = absolute.Height;
                border.BorderBrush = Brush(selected ? model.PreviewValid ? "Accent" : "Danger" : "Border"); border.BorderThickness = new Thickness(selected ? 2 : 1);
                Canvas.SetLeft(border, absolute.X); Canvas.SetTop(border, absolute.Y); Canvas.SetZIndex(border, item.Kind == CanvasKind.Container ? 0 : 1);
                var label = item.Kind == CanvasKind.Block ? "Empty block" : item.Title.Length > 0 ? item.Title : "Container";
                ((TextBlock)border.Child).Text = label; AutomationProperties.SetName(border, $"{label}{(selected ? ", selected" : "")}, X {item.Rect.X}, Y {item.Rect.Y}, width {item.Rect.Width}, height {item.Rect.Height}"); AutomationProperties.SetAutomationId(border, "CanvasItem_" + item.Id.ToString("N"));
                if (selected && model.Selection.Count == 1 && model.CanEdit) AddHandles(item, absolute);
            }
            if (model.IsPreview && model.Guides is { } guide && model.Selection.Count > 0)
            {
                var owner = state.Find(model.Selection.First()).ParentContainerId; var originX = owner is { } x ? state.Find(x).Rect.X + CanvasLayout.Inset : 0; var originY = owner is { } y ? state.Find(y).Rect.Y + CanvasLayout.Header : 0;
                if (guide.GuideX is { } gx) Guide(gx + originX, 0, gx + originX, CanvasLayout.Extent);
                if (guide.GuideY is { } gy) Guide(0, gy + originY, CanvasLayout.Extent, gy + originY);
            }
        }
        finally { rendering = false; }
    }
    private void Guide(double x1, double y1, double x2, double y2)
    { var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush("Accent"), StrokeThickness = 1, Opacity = .45, IsHitTestVisible = false }; Canvas.SetZIndex(line, 3); Surface.Children.Add(line); decorations.Add(line); }
    private void AddHandles(CanvasItem item, CanvasRect rect)
    {
        var size = (double)Application.Current.Resources["CanvasHandleSize"] * 100 / (model?.Zoom ?? 100);
        foreach (var direction in new[] { "right", "bottom", "bottom-right" })
        {
            // A passive handle lets the canvas own pointer capture throughout resizing.
            var handle = new Border { Width = size, Height = size, Background = Brush("Accent"), Tag = (item.Id, direction) };
            AutomationProperties.SetName(handle, "Resize " + direction); AutomationProperties.SetAutomationId(handle, "CanvasResize_" + direction);
            Canvas.SetLeft(handle, (direction == "bottom" ? rect.X + rect.Width / 2d : rect.Right) - size / 2); Canvas.SetTop(handle, (direction == "right" ? rect.Y + rect.Height / 2d : rect.Bottom) - size / 2); Canvas.SetZIndex(handle, 4);
            handle.AddHandler(PointerPressedEvent, new PointerEventHandler(OnHandlePressed), true); Surface.Children.Add(handle); decorations.Add(handle);
        }
    }
    private void OnItemPressed(object sender, PointerRoutedEventArgs args)
    {
        if (model is null || sender is not Border { Tag: CanvasItem item } || !args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        args.Handled = true;
        if (Key(VirtualKey.Control)) { model.Select(item.Id, true); return; }
        if (!model.Selection.Contains(item.Id)) model.Select(item.Id);
        Begin(item.Id, "", args); Focus(FocusState.Programmatic);
    }
    private void OnHandlePressed(object sender, PointerRoutedEventArgs args)
    { if (sender is Border { Tag: ValueTuple<Guid, string> tag } && args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) { args.Handled = true; Begin(tag.Item1, tag.Item2, args); Focus(FocusState.Programmatic); } }
    private void Begin(Guid id, string direction, PointerRoutedEventArgs args)
    {
        if (model?.BeginGesture(id) != true) return;
        dragging = id; resize = direction; start = args.GetCurrentPoint(Surface).Position; original = model.Snapshot!.Items.Single(i => i.Id == id).Rect;
        Surface.CapturePointer(args.Pointer);
    }
    private void OnSurfacePressed(object sender, PointerRoutedEventArgs args)
    { if (args.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) { model?.Select(null); Focus(FocusState.Programmatic); args.Handled = true; } }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (dragging is not { } id || original is null || model is null) return;
        var point = args.GetCurrentPoint(Surface).Position; var dx = point.X - start.X; var dy = point.Y - start.Y;
        if (resize.Length == 0) model.PreviewMove(dx, dy);
        else model.PreviewResize(id, original.Width + (resize == "bottom" ? 0 : dx), original.Height + (resize == "right" ? 0 : dy));
        args.Handled = true;
    }
    private async void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    { if (dragging is null) return; OnPointerMoved(sender, args); CancelCapture(); if (model is not null) await model.EndGestureAsync(); args.Handled = true; }
    private void CancelCapture() { dragging = null; original = null; releasing = true; Surface.ReleasePointerCaptures(); releasing = false; }
    private void Cancel() { CancelCapture(); model?.CancelGesture(); }
    private void OnPointerCancelled(object sender, PointerRoutedEventArgs args) => Cancel();
    private void OnCaptureLost(object sender, PointerRoutedEventArgs args) { if (!releasing && dragging is not null) Cancel(); }
    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (model is null) return;
        for (var focus = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focus is not null; focus = VisualTreeHelper.GetParent(focus))
            if (focus is TextBox or ComboBox) return;
        if (args.Key == VirtualKey.Escape) { Cancel(); model.Select(null); args.Handled = true; }
        else if (args.Key == VirtualKey.Delete && model.CanEdit) { await model.DeleteAsync(); args.Handled = true; }
        else if (model.CanEdit && model.Selection.Count > 0 && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = CanvasLayout.Grid * (Key(VirtualKey.Shift) ? 10 : 1); var dx = args.Key == VirtualKey.Left ? -step : args.Key == VirtualKey.Right ? step : 0; var dy = args.Key == VirtualKey.Up ? -step : args.Key == VirtualKey.Down ? step : 0;
            await model.EditAsync(new MoveCanvasItems(model.Selection.ToArray(), dx, dy)); args.Handled = true;
        }
    }
    private async void OnLock(object sender, RoutedEventArgs args) { if (model is not null) await model.EditAsync(new LockCanvas(!model.Locked)); }
    private async void OnAddBlock(object sender, RoutedEventArgs args)
    {
        if (model is null) return; var parent = model.Selection.Count == 1 ? model.Snapshot?.Items.SingleOrDefault(i => model.Selection.Contains(i.Id) && i.Kind == CanvasKind.Container)?.Id : null;
        await model.EditAsync(new CreateCanvasItem(CanvasKind.Block, parent, parent is null ? CanvasLayout.Snap(Viewport.HorizontalOffset * 100 / model.Zoom + 24) : 8, parent is null ? CanvasLayout.Snap(Viewport.VerticalOffset * 100 / model.Zoom + 24) : 8));
    }
    private async void OnAddContainer(object sender, RoutedEventArgs args) { if (model is not null) await model.EditAsync(new CreateCanvasItem(CanvasKind.Container, X: CanvasLayout.Snap(Viewport.HorizontalOffset * 100 / model.Zoom + 24), Y: CanvasLayout.Snap(Viewport.VerticalOffset * 100 / model.Zoom + 24))); }
    private async void OnDelete(object sender, RoutedEventArgs args) { if (model?.CanEdit == true) await model.DeleteAsync(); }
    private async void OnMembership(object sender, RoutedEventArgs args) { if (model?.Selection.Count == 1 && Membership.SelectedItem is CanvasContainerChoice choice) await model.EditAsync(new ReparentCanvasItem(model.Selection.First(), choice.Id)); }
    private async void OnRename(object sender, RoutedEventArgs args) { if (model?.Selection.Count == 1) await model.EditAsync(new RenameCanvasContainer(model.Selection.First(), ContainerTitle.Text)); }
    private void OnZoomOut(object sender, RoutedEventArgs args) => SetZoom(-25);
    private void OnZoomIn(object sender, RoutedEventArgs args) => SetZoom(25);
    private void OnZoomReset(object sender, RoutedEventArgs args) => SetZoom(100 - (model?.Zoom ?? 100));
    private async void SetZoom(int delta, Point? focus = null)
    {
        if (model?.HasPage != true || model.IsPreview || model.IsBusy) return;
        var target = model; var reference = target.Reference;
        var old = model.Zoom / 100d; var percent = Math.Clamp(model.Zoom + delta, 25, 200); var zoom = percent / 100d;
        var point = focus ?? new Point(Viewport.ViewportWidth / 2, Viewport.ViewportHeight / 2);
        var x = Math.Max(0, (Viewport.HorizontalOffset + point.X) * zoom / old - point.X);
        var y = Math.Max(0, (Viewport.VerticalOffset + point.Y) * zoom / old - point.Y);
        if (!model.CanConfigure) return;
        await target.EditAsync(new ZoomCanvas(percent));
        if (ReferenceEquals(model, target) && target.Reference == reference && target.Zoom == percent) { Viewport.UpdateLayout(); Viewport.ChangeView(x, y, null, true); }
    }
    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Viewport);
        if (Key(VirtualKey.Control)) { SetZoom(point.Properties.MouseWheelDelta > 0 ? 25 : -25, point.Position); args.Handled = true; }
        else if (Key(VirtualKey.Shift)) { Viewport.ChangeView(Math.Max(0, Viewport.HorizontalOffset - point.Properties.MouseWheelDelta), null, null, true); args.Handled = true; }
    }
}
