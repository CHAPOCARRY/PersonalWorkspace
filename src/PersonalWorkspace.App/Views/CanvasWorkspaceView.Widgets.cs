using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class CanvasWorkspaceView
{
    private sealed record SourceChoice(Guid Id, string Label);
    private sealed record RangeChoice(AnalyticsPreset Value, string Label);
    private async void OnAddWidget(object sender, RoutedEventArgs args) => await ConfigureWidgetAsync();
    private async void OnSetContent(object sender, RoutedEventArgs args)
    { if (model?.Selection.Count == 1) await ConfigureWidgetAsync(emptyBlock: model.Selection.First()); }
    private async Task ConfigureWidgetAsync(WidgetContent? existing = null, Guid? emptyBlock = null)
    {
        if (model?.CanEdit != true || model.Widgets is not { } widgets || widgets.Reference is not { } reference) return;
        var parent = model.Selection.Count == 1 ? model.Snapshot?.Items.SingleOrDefault(i => model.Selection.Contains(i.Id) && i.Kind == CanvasKind.Container)?.Id : null;
        var x = parent is null ? CanvasLayout.Snap(Viewport.HorizontalOffset * 100 / model.Zoom + 24) : 8;
        var y = parent is null ? CanvasLayout.Snap(Viewport.VerticalOffset * 100 / model.Zoom + 24) : 8;
        var type = new ComboBox { Header = "Widget type", ItemsSource = Enum.GetValues<WidgetType>(), SelectedItem = existing?.Widget.Type ?? WidgetType.Task, IsEnabled = existing is null, HorizontalAlignment = HorizontalAlignment.Stretch };
        var filter = new TextBox { Header = "Find source by title", PlaceholderText = "Filter titles" };
        var source = new ComboBox { Header = "Source", DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose an existing item" };
        var mode = new ComboBox { Header = "Presentation", HorizontalAlignment = HorizontalAlignment.Stretch };
        var itemFilter = new ComboBox { Header = "Visible List items", ItemsSource = Enum.GetValues<ListFilter>(), SelectedItem = existing?.Widget.List?.Filter ?? ListFilter.All };
        var chart = new ComboBox { Header = "Chart", ItemsSource = Enum.GetValues<WidgetChartKind>(), SelectedItem = existing?.Widget.Chart?.Kind ?? WidgetChartKind.Line };
        var ranges = new[] { new RangeChoice(AnalyticsPreset.SevenDays, "7 days"), new RangeChoice(AnalyticsPreset.ThirtyDays, "30 days"), new RangeChoice(AnalyticsPreset.NinetyDays, "90 days"), new RangeChoice(AnalyticsPreset.ThisMonth, "This month") };
        var range = new ComboBox { Header = "Range", ItemsSource = ranges, DisplayMemberPath = "Label", SelectedItem = ranges.Single(r => r.Value == (existing?.Widget.Chart?.Range ?? AnalyticsPreset.ThirtyDays)) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Danger"] };
        var chooseFile = new Button { Content = "Choose file…" };
        var fileLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var fit = new ComboBox { Header = "Image fit", ItemsSource = Enum.GetValues<ImageFitMode>(), SelectedItem = existing?.Widget.Asset?.Fit ?? ImageFitMode.Fit };
        var alt = new TextBox { Header = "Alt text", Text = existing?.Widget.Asset?.AltText ?? "", MaxLength = 1000 };
        string? selectedPath = null;
        AutomationProperties.SetAutomationId(chooseFile, "WidgetChooseFile"); AutomationProperties.SetAutomationId(fit, "WidgetImageFit"); AutomationProperties.SetAutomationId(alt, "WidgetImageAlt");
        var hint = new TextBlock { Text = "Sources stay in their libraries. Checkbox interaction applies to ordinary checkbox Tasks; recurring Tasks use today's existing occurrence.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStyle"] };
        foreach (var pair in new[] { ((DependencyObject)type, "WidgetType"), (filter, "WidgetSourceFilter"), (source, "WidgetSource"), (mode, "WidgetMode"), (chart, "WidgetChartKind"), (range, "WidgetChartRange"), (itemFilter, "WidgetListFilter"), (error, "WidgetPickerError") }) AutomationProperties.SetAutomationId(pair.Item1, pair.Item2);
        var stack = new StackPanel { Spacing = 8, Width = 400 }; foreach (var control in new UIElement[] { type, filter, source, mode, itemFilter, chart, range, chooseFile, fileLabel, fit, alt, hint, error }) stack.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = existing is null ? "Add Widget" : "Configure Widget", Content = new ScrollViewer { Content = stack, MaxHeight = 540 }, PrimaryButtonText = existing is null ? "Add Widget" : "Save Widget", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        var candidates = Array.Empty<SourceChoice>(); var generation = 0;
        void ListModes()
        {
            if (type.SelectedItem is not WidgetType.List) return;
            var selected = mode.SelectedItem as WidgetMode? ?? existing?.Widget.Mode ?? WidgetMode.Checklist;
            var choices = widgets.ListModes((source.SelectedItem as SourceChoice)?.Id ?? existing?.Widget.SourceItemId);
            mode.ItemsSource = choices; mode.SelectedItem = choices.Contains(selected) ? selected : WidgetMode.Checklist;
            itemFilter.Visibility = mode.SelectedItem is WidgetMode.Compact ? Visibility.Collapsed : Visibility.Visible;
        }
        void Filter()
        {
            var selected = (source.SelectedItem as SourceChoice)?.Id ?? existing?.Widget.SourceItemId;
            var matches = candidates.Where(s => s.Label.Contains(filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray(); source.ItemsSource = matches;
            source.SelectedItem = matches.FirstOrDefault(s => s.Id == selected);
        }
        async Task Load()
        {
            var request = ++generation; dialog.IsPrimaryButtonEnabled = false; error.Text = "";
            if (type.SelectedItem is not WidgetType selectedType) return;
            selectedPath = null; fileLabel.Text = "";
            var content = WidgetRules.IsContent(selectedType);
            filter.Visibility = source.Visibility = mode.Visibility = content ? Visibility.Collapsed : Visibility.Visible;
            chooseFile.Visibility = fileLabel.Visibility = existing is null && selectedType is WidgetType.Image or WidgetType.Attachment ? Visibility.Visible : Visibility.Collapsed;
            fit.Visibility = alt.Visibility = existing is not null && selectedType == WidgetType.Image ? Visibility.Visible : Visibility.Collapsed;
            mode.ItemsSource = WidgetRules.Modes(selectedType); mode.SelectedItem = existing?.Widget.Mode ?? WidgetRules.Modes(selectedType)[0];
            chart.Visibility = range.Visibility = selectedType == WidgetType.TrackerChart ? Visibility.Visible : Visibility.Collapsed;
            itemFilter.Visibility = selectedType == WidgetType.List ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = selectedType == WidgetType.List ? "List data stays canonical. Show up to 50 items in stored order. Counts and totals cover the whole List. Shopping requires prices. Compact shows a whole-List summary." : "Sources stay in their libraries. Checkbox interaction applies to ordinary checkbox Tasks; recurring Tasks use today's existing occurrence.";
            if (content) hint.Text = selectedType == WidgetType.RichText ? "Create an empty text Widget, then use Edit, Save and Cancel. Markdown stays canonical." : selectedType == WidgetType.Image ? "PNG and JPEG images. Fit is the default; configure Fit/Fill and alt text after adding." : "Import a local file. Open uses its Windows default application.";
            try
            {
                var list = await widgets.CandidatesAsync(selectedType);
                if (request != generation || widgets.Reference != reference) return;
                candidates = list.Select(s => new SourceChoice(s.Id, s.Title + " · " + s.Id.ToString("N")[..8])).ToArray(); Filter(); ListModes(); dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception) { if (request == generation) error.Text = "Sources could not be loaded. Reopen this picker and try again."; }
        }
        type.SelectionChanged += async (_, _) => await Load(); filter.TextChanged += (_, _) => Filter();
        chooseFile.Click += async (_, _) =>
        {
            var chosenType = (WidgetType)type.SelectedItem; var request = generation;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                foreach (var extension in chosenType == WidgetType.Image ? new[] { ".png", ".jpg", ".jpeg" } : new[] { "*" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (request == generation && widgets.Reference == reference && file is not null) { selectedPath = file.Path; fileLabel.Text = file.Name; }
            }
            catch (Exception) { error.Text = "The file picker could not be opened. Please try again."; }
        };
        source.SelectionChanged += (_, _) => ListModes();
        mode.SelectionChanged += (_, _) => itemFilter.Visibility = type.SelectedItem is WidgetType.List && mode.SelectedItem is not WidgetMode.Compact ? Visibility.Visible : Visibility.Collapsed;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral();
            try
            {
                if (widgets.Reference != reference || !widgets.CanConfigure) { error.Text = "The Page changed or its layout is locked. Close this picker."; return; }
                if (type.SelectedItem is not WidgetType selectedType || mode.SelectedItem is not WidgetMode selectedMode) return;
                var chosen = source.SelectedItem as SourceChoice;
                if (existing is null && !WidgetRules.IsContent(selectedType) && chosen is null) { error.Text = "Choose a source item."; return; }
                if (existing is null && selectedType is WidgetType.Image or WidgetType.Attachment && selectedPath is null) { error.Text = "Choose a file."; return; }
                var settings = selectedType == WidgetType.TrackerChart ? new WidgetChartSettings((WidgetChartKind)chart.SelectedItem, ((RangeChoice)range.SelectedItem).Value) : null;
                var listSettings = selectedType == WidgetType.List ? new WidgetListSettings((ListFilter)itemFilter.SelectedItem) : null;
                WidgetEdit edit = existing is null ? new AddWidget(selectedType, WidgetRules.IsContent(selectedType) ? null : chosen!.Id, selectedMode, emptyBlock, parent, x, y, settings, listSettings)
                    : selectedType == WidgetType.Image ? new ConfigureImageWidget(existing.Widget.Id, (ImageFitMode)fit.SelectedItem, alt.Text)
                    : new ConfigureWidget(existing.Widget.Id, selectedMode, settings, chosen?.Id, listSettings);
                if (await widgets.EditAsync(edit, selectedPath)) args.Cancel = false; else error.Text = widgets.Error ?? "The Widget could not be saved.";
            }
            finally { deferral.Complete(); }
        };
        void PageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (widgets.Reference != reference) { generation++; candidates = []; source.ItemsSource = null; filter.Text = ""; dialog.Hide(); } }
        widgets.PropertyChanged += PageChanged;
        try { await Load(); if (widgets.Reference == reference) await dialog.ShowAsync(); }
        finally { generation++; widgets.PropertyChanged -= PageChanged; }
    }
}
