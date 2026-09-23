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
        windowState.Attach(this, viewModel);
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string destination) viewModel.Navigate(destination);
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
        if (args.PropertyName == nameof(ShellViewModel.Title))
            Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
                .OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == viewModel.Title);
    }
}
