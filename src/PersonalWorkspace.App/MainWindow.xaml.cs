using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PersonalWorkspace.App.Infrastructure;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel viewModel;

    public MainWindow(ShellViewModel viewModel, WindowStateController windowState)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        Root.DataContext = viewModel;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Organization.PropertyChanged += OnOrganizationChanged;
        RefreshSpaces();
        windowState.Attach(this, viewModel);
        var todayTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        todayTimer.Tick += async (_, _) => await viewModel.Tasks.RefreshTodayIfNeededAsync();
        todayTimer.Start();
        Closed += (_, _) => todayTimer.Stop();
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
        Closed += (_, _) => viewModel.Organization.PropertyChanged -= OnOrganizationChanged;
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is OrganizationRow space) viewModel.OpenSpace(space);
        else if (args.InvokedItemContainer?.Tag is string destination)
        {
            if (destination == "NewSpace") viewModel.NewSpace();
            else viewModel.Navigate(destination);
        }
    }

    private void OnOrganizationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OrganizationViewModel.ActiveSpaces)) RefreshSpaces();
    }
    private void RefreshSpaces()
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>().Where(item => item.Tag is OrganizationRow || item.Tag as string == "NewSpace").ToArray())
            Navigation.MenuItems.Remove(item);
        var converter = new OrganizationColorConverter();
        foreach (var space in viewModel.Organization.ActiveSpaces)
            Navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = space.Name, Tag = space,
                Icon = new FontIcon { Glyph = string.IsNullOrWhiteSpace(space.Icon) ? "\uE8B7" : space.Icon,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)converter.Convert(space.Color, typeof(Microsoft.UI.Xaml.Media.Brush), "", "") }
            });
        Navigation.MenuItems.Add(new NavigationViewItem { Content = "+ New Space", Tag = "NewSpace", Icon = new SymbolIcon(Symbol.Add), SelectsOnInvoked = false });
        SynchronizeSelection();
    }

    private void OnProfileMenuOpening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        foreach (var profile in viewModel.Profiles.Profiles)
        {
            var item = new ToggleMenuFlyoutItem { Text = profile.Name, IsChecked = profile.Id == viewModel.Profiles.CurrentId };
            item.Click += (_, _) => viewModel.Profiles.SwitchCommand.Execute(profile.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "New Profile", Command = viewModel.Profiles.NewProfileCommand });
        menu.Items.Add(new MenuFlyoutItem { Text = "Manage profiles", Command = viewModel.Profiles.ManageCommand });
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ShellViewModel.Title) or nameof(ShellViewModel.ActiveSpaceId)) SynchronizeSelection();
    }

    private void SynchronizeSelection() => Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
        .OfType<NavigationViewItem>().FirstOrDefault(item => viewModel.ActiveSpaceId is { } id
            ? item.Tag is OrganizationRow space && space.Id == id : item.Tag as string == viewModel.Title);
}
