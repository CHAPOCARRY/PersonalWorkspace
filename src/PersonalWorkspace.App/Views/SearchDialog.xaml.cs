using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using Windows.System;

namespace PersonalWorkspace.App.Views;

public sealed partial class SearchDialog : ContentDialog
{
    private readonly SearchViewModel model;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    public SearchDialog(SearchViewModel model)
    {
        this.model = model; InitializeComponent(); DataContext = model;
        Opened += (_, _) => { model.Open(); QueryInput.Focus(FocusState.Programmatic); timer.Start(); };
        Closed += (_, _) => { timer.Stop(); model.Close(); model.CloseRequested -= CloseRequested; };
        timer.Tick += async (_, _) => await model.RefreshIfChangedAsync(); model.CloseRequested += CloseRequested;
    }
    private void CloseRequested(object? sender, EventArgs args) => Hide();
    public void FocusQuery() => QueryInput.Focus(FocusState.Programmatic);
    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up or VirtualKey.Down) { args.Handled = true; model.Move(args.Key == VirtualKey.Up ? -1 : 1); if (model.Selected is { } row) ResultList.ScrollIntoView(row); }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; await model.OpenSelectedAsync(); }
        else if (args.Key == VirtualKey.Escape) { args.Handled = true; Hide(); }
    }
    private async void OnResultClick(object sender, ItemClickEventArgs args) { model.Selected = (SearchResult)args.ClickedItem; await model.OpenSelectedAsync(); }
    private async void OnRetry(object sender, RoutedEventArgs args) => await model.RefreshAsync(false);
    private async void OnRebuild(object sender, RoutedEventArgs args) => await model.RebuildAsync();
}
