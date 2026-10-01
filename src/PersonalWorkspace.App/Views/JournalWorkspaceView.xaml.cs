using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.Views;

public sealed partial class JournalWorkspaceView : UserControl
{
    private JournalWorkspaceViewModel? model;
    private bool syncing;
    public JournalWorkspaceView()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = null; };
    }
    private void Attach() { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as JournalWorkspaceViewModel; if (model is not null) model.PropertyChanged += Changed; Sync(); Render(); }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(JournalWorkspaceViewModel.SelectedDate) or nameof(JournalWorkspaceViewModel.SelectedJournal) or nameof(JournalWorkspaceViewModel.Rows)) Sync(); if (args.PropertyName == nameof(JournalWorkspaceViewModel.Inputs)) Render(); }
    private void Sync() { syncing = true; try { DayPicker.Date = model?.SelectedDate; JournalPicker.SelectedItem = model?.SelectedJournal; } finally { syncing = false; } }
    private void OnJournalChanged(object sender, SelectionChangedEventArgs args) { if (!syncing && model?.IsIdle == true && JournalPicker.SelectedItem is JournalSummaryRow row) model.SelectedJournal = row; }
    private void OnDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (!syncing && args.NewDate is { } date && model is not null) model.SelectedDate = date; }
    private static Binding Bind(string property) => new() { Path = new(property), Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
    private void Render()
    {
        EntryFields.Children.Clear(); if (model is null) return;
        if (model.Inputs.Count == 0) EntryFields.Children.Add(new TextBlock { Text = "No fields yet. Choose Configure Journal to add your first field.", Style = (Style)Application.Current.Resources["BodyStyle"] });
        foreach (var input in model.Inputs)
        {
            var field = input.Field; var panel = new StackPanel { Spacing = (double)Application.Current.Resources["Space8"], DataContext = input };
            FrameworkElement control;
            if (field.Type == JournalFieldType.Checkbox)
            {
                var checkbox = new CheckBox { Content = field.Name, IsThreeState = true, IsChecked = input.Choice?.Id is null ? null : input.Choice.Label == "true" };
                void Set() => input.Choice = input.Choices[checkbox.IsChecked is null ? 0 : checkbox.IsChecked.Value ? 2 : 1];
                checkbox.Checked += (_, _) => Set(); checkbox.Unchecked += (_, _) => Set(); checkbox.Indeterminate += (_, _) => Set(); control = checkbox;
                panel.Children.Add(new TextBlock { Text = "Unchecked = false · indeterminate = not set", Style = (Style)Application.Current.Resources["BodyStyle"] });
            }
            else if (field.Type == JournalFieldType.MultiSelect)
            {
                var options = new StackPanel { Spacing = (double)Application.Current.Resources["Space4"] }; options.Children.Add(new TextBlock { Text = field.Name });
                foreach (var option in input.Options) { var checkbox = new CheckBox { Content = option.Label, DataContext = option }; checkbox.SetBinding(CheckBox.IsCheckedProperty, Bind(nameof(JournalOptionInput.Selected))); options.Children.Add(checkbox); }
                control = options;
            }
            else if (field.Type is JournalFieldType.Select or JournalFieldType.TaskReference or JournalFieldType.TrackerReference)
            {
                var combo = new ComboBox { Header = field.Name, ItemsSource = input.Choices, DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SetBinding(ComboBox.SelectedItemProperty, Bind(nameof(JournalFieldInput.Choice))); control = combo;
                if (field.Type is JournalFieldType.TaskReference or JournalFieldType.TrackerReference) panel.Children.Add(new TextBlock { Text = "Reference only — values are not copied or updated.", Style = (Style)Application.Current.Resources["BodyStyle"] });
            }
            else if (field.Type == JournalFieldType.Date)
            {
                var picker = new CalendarDatePicker { Header = field.Name, Date = input.Date }; picker.DateChanged += (_, args) => input.Date = args.NewDate; control = picker;
                var clear = new Button { Content = "Clear date" }; clear.Click += (_, _) => picker.Date = null; panel.Children.Add(clear);
            }
            else
            {
                var box = new TextBox { Header = field.Name + (field.Type == JournalFieldType.Currency ? " · " + field.CurrencyCode : field.Type == JournalFieldType.Scale ? $" · {field.ScaleMin}–{field.ScaleMax}" : ""), AcceptsReturn = field.Type == JournalFieldType.LongText,
                    TextWrapping = field.Type == JournalFieldType.LongText ? TextWrapping.Wrap : TextWrapping.NoWrap };
                if (field.Type == JournalFieldType.LongText) box.MinHeight = 100;
                if (field.Type == JournalFieldType.Duration) box.PlaceholderText = "minutes:seconds or hours:minutes:seconds";
                box.SetBinding(TextBox.TextProperty, Bind(nameof(JournalFieldInput.Text))); control = box;
            }
            AutomationProperties.SetAutomationId(control, "JournalValue_" + field.Id.ToString("N")); AutomationProperties.SetName(control, field.Name);
            panel.Children.Add(control); EntryFields.Children.Add(panel);
        }
    }
    private void OnEditField(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: JournalField field }) model?.EditFieldCommand.Execute(field); }
    private async void OnUp(object sender, RoutedEventArgs args) { if (model is not null && sender is FrameworkElement { Tag: JournalField field }) await model.MoveFieldAsync(field, -1); }
    private async void OnDown(object sender, RoutedEventArgs args) { if (model is not null && sender is FrameworkElement { Tag: JournalField field }) await model.MoveFieldAsync(field, 1); }
    private async void OnDeleteField(object sender, RoutedEventArgs args)
    {
        if (model is not null && sender is FrameworkElement { Tag: JournalField field } && await TrackerMenus.Confirm(XamlRoot, "Delete field?", $"Delete “{field.Name}” and all its historical values? This cannot be undone.", "Delete field")) await model.DeleteFieldAsync(field, true);
    }
    private void OnRemoveOption(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: JournalOptionEditor option }) model?.RemoveOptionCommand.Execute(option); }
    private void MoveOption(object sender, int direction) { if (model is not null && sender is FrameworkElement { Tag: JournalOptionEditor option }) { var list = model.FieldEditor.Options; var index = list.IndexOf(option); if (index >= 0 && index + direction >= 0 && index + direction < list.Count) list.Move(index, index + direction); } }
    private void OnOptionUp(object sender, RoutedEventArgs args) => MoveOption(sender, -1);
    private void OnOptionDown(object sender, RoutedEventArgs args) => MoveOption(sender, 1);
    private void OnAssign(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: OrganizationChoice choice }) model?.AssignCommand.Execute(choice); }
    private void OnActionsOpening(object sender, object args) { if (model is not null && sender is MenuFlyout menu && menu.Target is FrameworkElement { Tag: JournalSummaryRow row }) JournalMenus.Fill(menu, model, row, XamlRoot); }
}
