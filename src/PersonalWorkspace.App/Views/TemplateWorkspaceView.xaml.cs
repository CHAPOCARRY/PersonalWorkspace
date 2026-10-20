using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App.Views;

public sealed partial class TemplateWorkspaceView : UserControl
{
    public TemplateWorkspaceView() => InitializeComponent();
    private TemplateWorkspaceViewModel? Model => DataContext as TemplateWorkspaceViewModel;
    private void OnSelect(object sender, RoutedEventArgs args)
    { if (Model is { IsIdle: true } model && sender is FrameworkElement { Tag: TemplateRow row }) model.Selected = row; }
    private async void OnDelete(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanUse: true } model || model.CurrentReference() is not { } reference) return;
        var name = model.Snapshot!.Metadata.Name;
        if (await TrackerMenus.Confirm(XamlRoot, "Delete template permanently?", $"Delete “{name}”? Existing Tasks, Lists, Journals and Pages will remain. This cannot be undone.", "Delete template")) await model.DeleteAsync(reference);
    }
    private async void OnReplace(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanReplace: true, Source: { } source } model || model.CurrentReference() is not { } reference) return;
        if (await TrackerMenus.Confirm(XamlRoot, "Replace saved definition?", $"Replace the definition in “{model.Snapshot!.Metadata.Name}” with “{source.Title}”? Existing instances will remain unchanged.", "Replace definition")) await model.ReplaceAsync(reference, source.Reference);
    }
}
