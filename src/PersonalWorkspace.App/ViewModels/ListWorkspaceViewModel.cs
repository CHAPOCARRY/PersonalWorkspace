using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record ListRow(WorkspaceItemReference Reference, ListDefinition Definition)
{
    public string Title => Definition.Item.Title;
    public override string ToString() => Title;
}
public sealed record ListItemRow(ListItemReference Reference, ListItem Item, bool ShowQuantity, bool ShowPrices, string? Currency)
{
    public string Title => Item.Title;
    public bool IsChecked => Item.IsChecked;
    public double Opacity => IsChecked ? 0.6 : 1;
    public string CheckLabel => (IsChecked ? "Uncheck " : "Check ") + Title;
    public string EditLabel => "Edit " + Title;
    public string ActionsLabel => "Actions for " + Title;
    public override string ToString() => Title + (IsChecked ? " · Checked" : " · Unchecked") + (Detail.Length == 0 ? "" : " · " + Detail);
    public string Detail => string.Join(" · ", new[] {
        ShowQuantity && Item.Quantity is { } quantity ? "Qty " + ExactValueText.Input(quantity) : null,
        ShowPrices && Item.UnitPrice is { } price ? "Unit " + Money(price, Currency) + " · Subtotal " + Money(ListRules.Subtotal(Item)!.Value, Currency) : null,
        Item.Note is not null ? "Note" : null, Item.Link is not null ? "Link" : null }.Where(s => s is not null));
    public static string Money(decimal value, string? currency) => value.ToString("0.00##########################", CultureInfo.CurrentCulture) + (currency is null ? "" : " " + currency);
}

public sealed partial class ListWorkspaceViewModel : ObservableObject
{
    private readonly IListService service;
    private readonly IOrganizationService organization;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly ILogger<ListWorkspaceViewModel> logger;
    private Guid? observedProfile, selected, editing;
    private int revision;
    private bool busy, loading, creating;
    private ListCollection collection;
    private ListFilter filter;
    private string search = "";
    private ListSnapshot? snapshot;
    private string title = "";
    public string Title { get => title; set => SetProperty(ref title, value); }
    private string description = "";
    public string Description { get => description; set => SetProperty(ref description, value); }
    private bool showQuantity;
    public bool ShowQuantity { get => showQuantity; set => SetProperty(ref showQuantity, value); }
    private bool showPrices;
    public bool ShowPrices { get => showPrices; set => SetProperty(ref showPrices, value); }
    private string currencyCode = "";
    public string CurrencyCode { get => currencyCode; set => SetProperty(ref currencyCode, value); }
    private string quickTitle = "";
    public string QuickTitle { get => quickTitle; set => SetProperty(ref quickTitle, value); }
    private string itemTitle = "";
    public string ItemTitle { get => itemTitle; set => SetProperty(ref itemTitle, value); }
    private string quantity = "";
    public string Quantity { get => quantity; set => SetProperty(ref quantity, value); }
    private string unitPrice = "";
    public string UnitPrice { get => unitPrice; set => SetProperty(ref unitPrice, value); }
    private string note = "";
    public string Note { get => note; set => SetProperty(ref note, value); }
    private string link = "";
    public string Link { get => link; set => SetProperty(ref link, value); }
    private string? error;
    public string? Error { get => error; set => SetProperty(ref error, value); }
    private string? notice;
    public string? Notice { get => notice; set => SetProperty(ref notice, value); }
    public ListWorkspaceViewModel(IListService service, IOrganizationService organization, ICurrentProfile current, INavigationService navigation, ILogger<ListWorkspaceViewModel> logger)
    {
        this.service = service; this.organization = organization; this.current = current; this.navigation = navigation; this.logger = logger;
        observedProfile = current.Current?.Id;
        navigation.Changed += (_, _) => { ++revision; loading = false; creating = false; ClearDetail(); Notify(); _ = ReloadAsync(); };
        current.Changed += (_, _) => { if (observedProfile == current.Current?.Id) return; observedProfile = current.Current?.Id; Clear(); if (IsArea && navigation.Current.EntityId is not null) navigation.Navigate(new("Lists")); else _ = ReloadAsync(); };
    }
    public bool IsArea => navigation.Current.Destination == "Lists";
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool HasDetail => !creating && snapshot is not null;
    public bool ShowEditor => creating || HasDetail;
    public bool IsCreating => creating;
    public bool CanEdit => IsIdle && (creating || snapshot?.Definition.IsActive == true);
    public bool HasItemEditor => editing.HasValue && HasDetail;
    public bool ShowItemQuantity => snapshot?.Definition.ShowQuantity == true;
    public bool ShowItemPrices => snapshot?.Definition.ShowPrices == true;
    public string Heading => creating ? "New List" : snapshot?.Definition.Item.Title ?? "Lists";
    public string Context => snapshot is null || snapshot.Definition.IsActive ? "" : "This List is read-only. Restore it to edit its items.";
    public string Totals => snapshot?.Definition.ShowPrices == true ? "Remaining: " + ListItemRow.Money(snapshot.Totals.Remaining, snapshot.Definition.CurrencyCode) + "     Total: " + ListItemRow.Money(snapshot.Totals.Total, snapshot.Definition.CurrencyCode) : "";
    public string ItemCount => snapshot is null ? "" : $"{snapshot.Items.Count} items · {snapshot.Items.Count(i => i.IsChecked)} checked";
    public string EmptyMessage => snapshot?.Items.Count == 0 ? "Add the first item above." : Items.Count == 0 ? "No items match this filter." : "";
    public ListRow? DetailRow => snapshot is { } data && current.Current is { } profile ? new(new(profile.Id, data.Definition.Item.Id), data.Definition) : null;
    public IReadOnlyList<ListRow> Rows { get; private set; } = [];
    public IReadOnlyList<ListItemRow> Items { get; private set; } = [];
    public IReadOnlyList<OrganizationChoice> Assignments { get; private set; } = [];
    public IReadOnlyList<ListCollection> Collections { get; } = Enum.GetValues<ListCollection>();
    public IReadOnlyList<ListFilter> Filters { get; } = Enum.GetValues<ListFilter>();
    public ListCollection Collection { get => collection; set { if (SetProperty(ref collection, value)) { selected = null; creating = false; navigation.Navigate(new("Lists")); _ = ReloadAsync(); } } }
    public ListFilter Filter { get => filter; set { if (SetProperty(ref filter, value)) { BuildItems(); Notify(); } } }
    public string Search { get => search; set { if (SetProperty(ref search, value)) { BuildItems(); Notify(); } } }
    public void Clear()
    {
        ++revision; selected = null; collection = ListCollection.Active; filter = ListFilter.All; search = ""; creating = loading = false;
        Rows = []; ClearDetail(); Title = Description = CurrencyCode = ""; ShowQuantity = ShowPrices = false; Error = Notice = null; Notify();
    }
    private void ClearDetail() { snapshot = null; Items = []; Assignments = []; QuickTitle = ""; ResetItem(); }
    private void ResetItem() { editing = null; ItemTitle = Quantity = UnitPrice = Note = Link = ""; }
    public async Task ReloadAsync()
    {
        if (!IsArea) return;
        var request = ++revision; var profile = current.Current?.Id; loading = true; Error = null; Notify();
        try
        {
            if (profile is null) { Clear(); return; }
            var definitions = await service.GetDefinitionsAsync(profile.Value);
            if (request != revision || profile != current.Current?.Id) return;
            var eligible = definitions.Where(d => d.Matches(collection)).ToArray();
            var chosen = Guid.TryParse(navigation.Current.EntityId, out var route) ? (Guid?)route : selected;
            var definition = eligible.FirstOrDefault(d => d.Item.Id == chosen) ?? eligible.FirstOrDefault();
            var detail = definition is null ? null : await service.ReadAsync(new(profile.Value, definition.Item.Id));
            var catalog = await organization.GetAsync(profile.Value);
            if (request != revision || profile != current.Current?.Id) return;
            Rows = eligible.Select(d => new ListRow(new(profile.Value, d.Item.Id), d)).ToArray(); selected = definition?.Item.Id; snapshot = detail; ResetItem();
            Assignments = [];
            if (definition is not null)
            {
                var reference = new WorkspaceItemReference(profile.Value, definition.Item.Id);
                Assignments = catalog.Tags.Select(t => new OrganizationChoice(reference, t.Id, OrganizationKind.Tag, t.Name, t.Color, catalog.ItemTags.Contains(new(definition.Item.Id, t.Id))))
                    .Concat(catalog.Spaces.Where(s => s.ArchivedAtUtc is null || catalog.ItemSpaces.Contains(new(definition.Item.Id, s.Id))).Select(s => new OrganizationChoice(reference, s.Id, OrganizationKind.Space, s.Name, s.Color, catalog.ItemSpaces.Contains(new(definition.Item.Id, s.Id)), s.ArchivedAtUtc is not null))).ToArray();
                if (!creating) { Title = definition.Item.Title; Description = definition.Description; ShowQuantity = definition.ShowQuantity; ShowPrices = definition.ShowPrices; CurrencyCode = definition.CurrencyCode ?? ""; }
            }
            BuildItems();
        }
        catch (Exception exception) { if (request == revision) { ClearDetail(); Rows = []; selected = null; Report(exception); } }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    private void BuildItems() => Items = snapshot is { } data && current.Current is { } profile ? data.Items.Where(i => ListRules.Matches(i, filter, search)).Select(i => new ListItemRow(new(profile.Id, i.ListId, i.Id), i, data.Definition.ShowQuantity, data.Definition.ShowPrices, data.Definition.CurrencyCode)).ToArray() : [];
    [RelayCommand] private void Open(ListRow? row)
    {
        if (!IsIdle || row is null || row.Reference.ProfileId != current.Current?.Id) return;
        filter = ListFilter.All; search = ""; selected = row.Reference.ItemId; Notice = null; navigation.Navigate(new("Lists", selected.Value.ToString("D"))); _ = ReloadAsync();
    }
    [RelayCommand] private void NewList()
    {
        if (!IsIdle) return; creating = true; ClearDetail(); Title = Description = CurrencyCode = ""; ShowQuantity = ShowPrices = false; Error = Notice = null; Notify();
    }
    [RelayCommand] private async Task CancelAsync() { creating = false; await ReloadAsync(); }
    [RelayCommand] private Task SaveAsync() => Run(async profile =>
    {
        var draft = new ListDraft(Title, Description, ShowQuantity, ShowPrices, CurrencyCode);
        var result = creating ? await service.CreateAsync(profile, draft) : await service.UpdateAsync(new(profile, selected!.Value), draft);
        if (profile != current.Current?.Id || !IsArea) return;
        creating = false; collection = ListCollection.Active; selected = result.Item.Id; navigation.Navigate(new("Lists", selected.Value.ToString("D")));
    });
    [RelayCommand] private Task QuickAddAsync() => Run(async profile =>
    {
        if (snapshot is null) return; var id = snapshot.Definition.Item.Id;
        await service.AddAsync(new(profile, id), new(QuickTitle));
        if (profile == current.Current?.Id && selected == id) QuickTitle = "";
    });
    [RelayCommand] private void EditItem(ListItemRow? row)
    {
        if (!CanEdit || !Valid(row)) return; var item = row!.Item; editing = item.Id;
        ItemTitle = item.Title; Quantity = item.Quantity is { } quantity ? ExactValueText.Input(quantity) : ""; UnitPrice = item.UnitPrice is { } price ? ExactValueText.Input(price) : ""; Note = item.Note ?? ""; Link = item.Link ?? ""; Error = null; Notify();
    }
    [RelayCommand] private void CancelItem() { ResetItem(); Notify(); }
    [RelayCommand] private Task SaveItemAsync() => Run(async profile =>
    {
        if (editing is not { } id || selected is not { } list) return;
        static decimal? Number(string text) => string.IsNullOrWhiteSpace(text) ? null : ExactValueText.ParseInput(text);
        await service.UpdateItemAsync(new(profile, list, id), new(ItemTitle, Number(Quantity), Number(UnitPrice), Note, Link));
    });
    private bool Valid(ListItemRow? row) => row is not null && row.Reference.ProfileId == current.Current?.Id && row.Reference.ListId == selected;
    public Task CheckAsync(ListItemRow row) => Run(async _ => { if (Valid(row)) await service.SetCheckedAsync(row.Reference, !row.IsChecked); });
    public Task ReorderAsync(ListItemRow row, int direction) => Run(async _ => { if (Valid(row)) await service.ReorderAsync(row.Reference, direction); });
    public Task DeleteItemAsync(ListItemRow row) => Run(async _ => { if (Valid(row)) await service.DeleteItemAsync(row.Reference); });
    public Task CreateTaskAsync(ListItemRow row) => Run(async profile =>
    {
        if (!Valid(row)) return; var task = await service.CreateTaskAsync(row.Reference);
        if (profile == current.Current?.Id && selected == row.Reference.ListId) Notice = $"Created Task: {task.Item.Title}. The List item is unchanged.";
    });
    public Task ClearCheckedAsync(WorkspaceItemReference list) => Run(async _ => await service.ClearCheckedAsync(list));
    [RelayCommand] private Task UncheckAllAsync() => Run(async profile => { if (selected is { } id) await service.UncheckAllAsync(new(profile, id)); });
    public Task ApplyAsync(ListRow row, ListAction action) => Run(async profile =>
    {
        await service.ApplyAsync(row.Reference, action); if (profile == current.Current?.Id) { selected = null; navigation.Navigate(new("Lists")); }
    });
    public Task DeleteAsync(ListRow row) => Run(async profile =>
    {
        await service.DeleteAsync(row.Reference); if (profile == current.Current?.Id) { selected = null; navigation.Navigate(new("Lists")); }
    });
    public Task DuplicateAsync(ListRow row) => Run(async profile =>
    {
        var copy = await service.DuplicateAsync(row.Reference);
        if (profile == current.Current?.Id) { collection = ListCollection.Active; selected = copy.Item.Id; navigation.Navigate(new("Lists", selected.Value.ToString("D"))); }
    });
    [RelayCommand] private Task AssignAsync(OrganizationChoice? choice) => Run(async _ => { if (choice is not null) await organization.AssignAsync(choice.Item, choice.Kind, choice.Id, !choice.IsAssigned); });
    private async Task Run(Func<Guid, Task> action)
    {
        if (!IsIdle || current.Current is not { } profile) return;
        busy = true; Error = Notice = null; Notify();
        try { await action(profile.Id); if (profile.Id == current.Current?.Id) await ReloadAsync(); }
        catch (Exception exception) { if (profile.Id == current.Current?.Id) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void Report(Exception exception)
    {
        if (exception is ListValidationException or ListOperationException or WorkspaceChangedException or OrganizationValidationException or OrganizationOperationException or TaskValidationException or TaskOperationException or FormatException) Error = exception.Message;
        else { logger.LogError(exception, "List presentation failed"); Error = "The List could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsArea), nameof(IsBusy), nameof(IsIdle), nameof(HasDetail), nameof(ShowEditor), nameof(IsCreating), nameof(CanEdit), nameof(HasItemEditor), nameof(ShowItemQuantity), nameof(ShowItemPrices), nameof(Heading), nameof(Context), nameof(Totals), nameof(ItemCount), nameof(EmptyMessage), nameof(DetailRow), nameof(Rows), nameof(Items), nameof(Assignments), nameof(Collection), nameof(Filter), nameof(Search) }) OnPropertyChanged(name);
    }
}
