using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

// Presentation only: all content actions return to the canonical services through the view model.
public sealed class WidgetContentView : UserControl
{
    private WidgetContent? row;
    private WidgetWorkspaceViewModel? model;
    private readonly List<Control> interactions = [];
    private Button? actions;
    private TextBlock? error;
    private RichContentView? richView;
    private AssetContentView? assetView;
    public Border Header { get; private set; } = new();
    public Action<WidgetContent>? ConfigureRequested { get; set; }
    public WidgetContentView()
    {
        IsTabStop = false;
        SizeChanged += (_, _) =>
        {
            Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
            if (assetView is not null && row?.Widget.Type == WidgetType.Image) assetView.Height = Math.Max(64, ActualHeight - 88);
        };
        Unloaded += (_, _) => { if (row is not null) row.PropertyChanged -= RowChanged; };
        Loaded += (_, _) => { if (row is not null) { row.PropertyChanged -= RowChanged; row.PropertyChanged += RowChanged; } };
    }
    public bool IsHeader(DependencyObject? source)
    { for (var node = source; node is not null && node != this; node = VisualTreeHelper.GetParent(node)) if (node == Header) return true; return false; }
    public void Bind(WidgetContent content, WidgetWorkspaceViewModel owner, bool canEditLayout)
    {
        model = owner;
        if (!ReferenceEquals(row, content))
        {
            if (row is not null) row.PropertyChanged -= RowChanged;
            row = content; row.PropertyChanged += RowChanged; Build();
        }
        foreach (var control in interactions) control.IsEnabled = owner.CanInteract;
        if (richView is not null) richView.Editable = owner.CanInteract;
        if (assetView is not null) assetView.CanOpen = owner.CanInteract;
        if (actions is not null) actions.Visibility = canEditLayout ? Visibility.Visible : Visibility.Collapsed;
        Header.Background = (Brush)Application.Current.Resources[canEditLayout ? "SurfaceSecondary" : "Surface"];
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args) { if (error is not null) error.Text = row?.Error ?? ""; }
    private static TextBlock Text(string value, bool title = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = title ? 16 : 12, Foreground = (Brush)Application.Current.Resources[title ? "TextPrimary" : "TextSecondary"] };
    private void Build()
    {
        interactions.Clear(); richView = null; assetView = null; var item = row!;
        AutomationProperties.SetAutomationId(this, "Widget_" + item.Widget.Id.ToString("N"));
        AutomationProperties.SetName(this, item.Widget.Type + ": " + item.Title);
        if (item.Widget.Type == WidgetType.List) AutomationProperties.SetName(this, "List widget, " + item.Title);
        var root = new Grid { RowSpacing = 4 }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var headerGrid = new Grid(); headerGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); headerGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var type = Text(item.Widget.Type == WidgetType.TrackerChart ? "Tracker chart" : item.Widget.Type == WidgetType.PageLink ? "Page link" : item.Widget.Type.ToString());
        type.IsHitTestVisible = false; headerGrid.Children.Add(type);
        actions = new Button { Content = "⋯", Padding = new(4, 0, 4, 0), MinHeight = 24, MinWidth = 24 };
        AutomationProperties.SetName(actions, "Widget actions for " + item.Title);
        actions.PointerPressed += (_, args) => args.Handled = true;
        var flyout = new MenuFlyout();
        void Action(string label, Func<Task> action) { var button = new MenuFlyoutItem { Text = label }; button.Click += async (_, _) => await action(); flyout.Items.Add(button); }
        Action(item.Source is null && !item.IsContent ? "Replace source / Configure" : "Configure Widget", () => { ConfigureRequested?.Invoke(item); return Task.CompletedTask; });
        Action("Duplicate Widget", async () => await model!.EditAsync(new DuplicateWidget(item.Widget.Id)));
        Action("Remove Widget content", async () => await model!.EditAsync(new RemoveWidget(item.Widget.Id)));
        Action("Remove from Page", async () => await model!.EditAsync(new RemoveWidget(item.Widget.Id, true)));
        actions.Flyout = flyout; Grid.SetColumn(actions, 1); headerGrid.Children.Add(actions);
        Header = new Border { Child = headerGrid, Padding = new(4), MinHeight = 28 }; AutomationProperties.SetName(Header, "Drag " + item.Title); root.Children.Add(Header);
        var body = new StackPanel { Spacing = 6 };
        var title = Text(item.Title, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(title, item.Title); body.Children.Add(title);
        if (item.Lifecycle.Length > 0) body.Children.Add(Text(item.Lifecycle));
        body.Children.Add(Text(item.Summary));
        if (item.RichText is { } rich)
        {
            richView = new(rich.Document, async text => { await model!.SaveRichTextAsync(item, text); return item.Error is null; }, async uri =>
            {
                if (!model!.CanInteract) return;
                if (uri.Scheme == "personalworkspace") await model.OpenInternalLinkAsync(item, Guid.Parse(uri.AbsolutePath.TrimStart('/')));
                else if (!await Windows.System.Launcher.LaunchUriAsync(uri)) item.Error = "Windows could not open this link.";
            });
            body.Children.Add(richView);
        }
        if (item.Asset is { } asset && item.Widget.Asset is { } settings)
        {
            assetView = new(asset.Asset, settings, item.Widget.Type == WidgetType.Image, consumer => model!.UseAssetAsync(item, consumer), message => item.Error = message);
            if (item.Widget.Type == WidgetType.Image && ActualHeight > 0) assetView.Height = Math.Max(64, ActualHeight - 88);
            body.Children.Add(assetView);
        }
        foreach (var listItem in item.ListItems)
        {
            var label = new StackPanel { Spacing = 2, Opacity = listItem.Opacity };
            label.Children.Add(Text(listItem.Title));
            if (item.ListMode == WidgetMode.Shopping && listItem.Detail.Length > 0) label.Children.Add(Text(listItem.Detail));
            var check = new CheckBox { Content = label, IsChecked = listItem.IsChecked, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(check, listItem.CheckLabel + " in " + item.Title);
            check.Click += async (_, _) => { check.IsChecked = listItem.IsChecked; await model!.CheckListItemAsync(item, listItem); };
            body.Children.Add(check); interactions.Add(check);
        }
        if (item.QuickAdd)
        {
            var input = new TextBox { PlaceholderText = "Add item…", Text = item.Input, MaxLength = 200 };
            AutomationProperties.SetName(input, "Add item to " + item.Title); AutomationProperties.SetAutomationId(input, "WidgetListAdd_" + item.Widget.Id.ToString("N"));
            input.TextChanged += (_, _) => item.Input = input.Text;
            input.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await model!.QuickAddAsync(item); } };
            body.Children.Add(input); interactions.Add(input);
            var add = new Button { Content = "Add item" }; AutomationProperties.SetName(add, "Add item to " + item.Title);
            add.Click += async (_, _) => await model!.QuickAddAsync(item); body.Children.Add(add); interactions.Add(add);
        }
        if (item.CanComplete)
        {
            var check = new CheckBox { Content = item.Done ? "Done — reopen" : "Complete Task", IsChecked = item.Done };
            AutomationProperties.SetName(check, "Task completion: " + item.Title);
            check.Click += async (_, _) => { check.IsChecked = item.Done; await model!.CompleteAsync(item); }; body.Children.Add(check); interactions.Add(check);
        }
        if (item.CanCompleteToday)
        {
            var complete = new Button { Content = item.Done ? "Reopen today's occurrence" : "Complete today's occurrence" };
            complete.Click += async (_, _) => await model!.CompleteAsync(item); body.Children.Add(complete); interactions.Add(complete);
        }
        if (item.QuickEntry)
        {
            var input = new TextBox { Header = "Value", Text = item.Input, PlaceholderText = item.Periods!.Current.Tracker.Settings.Type == TrackerValueType.Boolean ? "true or false" : "Enter value" };
            AutomationProperties.SetName(input, "Value for " + item.Title); AutomationProperties.SetAutomationId(input, "WidgetValue_" + item.Widget.Id.ToString("N"));
            input.TextChanged += (_, _) => item.Input = input.Text; body.Children.Add(input); interactions.Add(input);
            var save = new Button { Content = item.Periods.Current.Tracker.EntryMode == TrackerEntryMode.Multiple ? "Add value" : "Record value" };
            save.Click += async (_, _) => await model!.RecordAsync(item); body.Children.Add(save); interactions.Add(save);
        }
        if (item.Analytics is { } chart) body.Children.Add(new WidgetChartView(chart, item.Widget.Chart!.Kind));
        error = Text(item.Error ?? ""); error.Foreground = (Brush)Application.Current.Resources["Danger"]; AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite); body.Children.Add(error);
        if (item.Route is not null)
        {
            var open = new Button { Content = item.Widget.Type switch { WidgetType.TrackerChart => "Open Tracker", WidgetType.PageLink => "Open Page", WidgetType.Journal => "Open today's Journal", _ => "Open " + item.Widget.Type } };
            open.Click += (_, _) => model!.Open(item); body.Children.Add(open); interactions.Add(open);
        }
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll); Content = root;
    }
}
