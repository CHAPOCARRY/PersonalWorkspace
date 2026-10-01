using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record PageRow(WorkspaceItemReference Reference, PageItem Page, int Depth = 0, bool HasChildren = false, bool Expanded = false, string Context = "", bool Selected = false)
{
    public string Title => Page.Item.Title;
    public string Label => (Page.Icon switch { PageIcon.Book => "▤ ", PageIcon.Star => "☆ ", PageIcon.Flag => "⚑ ", PageIcon.Home => "⌂ ", _ => "" }) + Title;
    public string ToggleLabel => Expanded ? "Collapse " + Title : "Expand " + Title;
    public override string ToString() => Label + (Selected ? " (selected)" : "") + (Context.Length > 0 ? " · " + Context : "");
}
public sealed record PageParentChoice(Guid? Id, string Label);

public sealed partial class PageWorkspaceViewModel : ObservableObject
{
    private readonly IPageService service;
    private readonly IOrganizationService organization;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly ILogger<PageWorkspaceViewModel> logger;
    private PageGraph graph = new([]);
    private readonly HashSet<Guid> collapsed = [];
    private Guid? observedProfile, selected, createParent;
    private int revision;
    private bool busy, loading, creating;
    private PageCollection collection;
    private string title = "";
    private PageIcon icon;
    private PageParentChoice? parentChoice;
    private string? error;
    public PageWorkspaceViewModel(IPageService service, IOrganizationService organization, ICurrentProfile current, INavigationService navigation, ILogger<PageWorkspaceViewModel> logger)
    {
        this.service = service; this.organization = organization; this.current = current; this.navigation = navigation; this.logger = logger;
        observedProfile = current.Current?.Id;
        navigation.Changed += (_, _) => { creating = false; Notify(); _ = ReloadAsync(); };
        current.Changed += (_, _) =>
        {
            if (observedProfile == current.Current?.Id) return;
            observedProfile = current.Current?.Id; Clear();
            if (IsArea && navigation.Current.EntityId is not null) navigation.Navigate(new("Pages")); else _ = ReloadAsync();
        };
    }
    public bool IsArea => navigation.Current.Destination == "Pages";
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool HasDetail => !creating && Detail is not null;
    public bool ShowEditor => creating || HasDetail;
    public bool CanEdit => IsIdle && (creating || Detail?.Item.DeletedAtUtc is null);
    public bool CanCreateChild => IsIdle && Detail?.IsActive == true;
    public bool IsCreating => creating;
    public bool IsEmpty => Rows.Count == 0;
    public string Heading => creating ? createParent is null ? "New Page" : "New child Page" : Detail?.Item.Title ?? "Pages";
    public string Title { get => title; set => SetProperty(ref title, value); }
    public PageIcon Icon { get => icon; set => SetProperty(ref icon, value); }
    public IReadOnlyList<PageIcon> Icons { get; } = Enum.GetValues<PageIcon>();
    public IReadOnlyList<PageCollection> Collections { get; } = Enum.GetValues<PageCollection>();
    public PageCollection Collection { get => collection; set { if (SetProperty(ref collection, value)) { creating = false; selected = null; navigation.Navigate(new("Pages")); _ = ReloadAsync(); } } }
    public PageItem? Detail => selected is { } id ? graph.Pages.GetValueOrDefault(id) : null;
    public PageRow? DetailRow => Detail is { } page && current.Current is { } profile ? Row(profile.Id, page) : null;
    public IReadOnlyList<PageRow> Rows { get; private set; } = [];
    public IReadOnlyList<PageRow> Breadcrumbs { get; private set; } = [];
    public IReadOnlyList<PageRow> Children { get; private set; } = [];
    public IReadOnlyList<PageParentChoice> ParentChoices { get; private set; } = [];
    public PageParentChoice? ParentChoice { get => parentChoice; set => SetProperty(ref parentChoice, value); }
    public IReadOnlyList<OrganizationChoice> Assignments { get; private set; } = [];
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string Created => Detail?.Item.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string Modified => Detail?.Item.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public string Context => DetailRow?.Context ?? "";
    public void Clear()
    {
        ++revision; graph = new([]); selected = createParent = null; collapsed.Clear(); collection = PageCollection.Active;
        Rows = []; Breadcrumbs = []; Children = []; Assignments = []; ParentChoices = []; ParentChoice = null;
        creating = loading = false; Title = ""; Icon = PageIcon.None; Error = null; Notify();
    }
    public async Task ReloadAsync()
    {
        if (!IsArea) return;
        var request = ++revision; var profile = current.Current?.Id; loading = true; Error = null; Notify();
        try
        {
            if (profile is null) { Clear(); return; }
            var data = await service.GetAsync(profile.Value); var catalog = await organization.GetAsync(profile.Value);
            if (request != revision || profile != current.Current?.Id) return;
            graph = data;
            if (Guid.TryParse(navigation.Current.EntityId, out var routeId)) selected = routeId;
            if (selected is { } id && (!graph.Pages.TryGetValue(id, out var found) || !found.Matches(collection))) selected = null;
            selected ??= graph.Pages.Values.Where(p => p.Matches(collection)).OrderBy(p => p.ParentPageId.HasValue).ThenBy(p => p.SortOrder).ThenBy(p => p.Item.Id).Select(p => (Guid?)p.Item.Id).FirstOrDefault();
            BuildRows(); Breadcrumbs = []; Children = []; Assignments = []; ParentChoices = [];
            if (Detail is { } page)
            {
                var reference = new WorkspaceItemReference(profile.Value, page.Item.Id);
                Breadcrumbs = graph.Ancestors(page.Item.Id).Append(page).Select(p => Row(profile.Value, p)).ToArray();
                Children = graph.Children(page.Item.Id).Where(p => p.IsActive).Select(p => Row(profile.Value, p)).ToArray();
                var excluded = graph.Descendants(page.Item.Id); excluded.Add(page.Item.Id);
                ParentChoices = new[] { new PageParentChoice(null, "Root") }.Concat(graph.Pages.Values.Where(p => p.IsActive && !excluded.Contains(p.Item.Id)).OrderBy(p => p.Item.Title).ThenBy(p => p.Item.Id).Select(p => new PageParentChoice(p.Item.Id, p.Item.Title + " · " + p.Item.Id.ToString("N")[..8]))).ToArray();
                ParentChoice = ParentChoices.FirstOrDefault(p => p.Id == page.ParentPageId);
                Assignments = catalog.Tags.Select(t => new OrganizationChoice(reference, t.Id, OrganizationKind.Tag, t.Name, t.Color, catalog.ItemTags.Contains(new(page.Item.Id, t.Id))))
                    .Concat(catalog.Spaces.Where(s => s.ArchivedAtUtc is null || catalog.ItemSpaces.Contains(new(page.Item.Id, s.Id))).Select(s => new OrganizationChoice(reference, s.Id, OrganizationKind.Space, s.Name, s.Color, catalog.ItemSpaces.Contains(new(page.Item.Id, s.Id)), s.ArchivedAtUtc is not null))).ToArray();
                if (!creating) { Title = page.Item.Title; Icon = page.Icon; }
            }
        }
        catch (Exception exception) { if (request == revision) { graph = new([]); selected = null; Rows = []; Breadcrumbs = []; Children = []; Assignments = []; ParentChoices = []; Report(exception); } }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    private PageRow Row(Guid profile, PageItem page, int depth = 0, bool hasChildren = false) => new(new(profile, page.Item.Id), page, depth, hasChildren, !collapsed.Contains(page.Item.Id),
        page.ParentPageId is { } parent && graph.Pages.TryGetValue(parent, out var owner) && !owner.IsActive ? $"Parent: {owner.Item.Title} ({(owner.Item.DeletedAtUtc is null ? "archived" : "in Trash")})" : "", selected == page.Item.Id);
    private void BuildRows()
    {
        if (current.Current is not { } profile) { Rows = []; return; }
        var visible = graph.Pages.Values.Where(p => p.Matches(collection)).ToDictionary(p => p.Item.Id);
        var children = visible.Values.Where(p => p.ParentPageId is { } id && visible.ContainsKey(id)).ToLookup(p => p.ParentPageId!.Value);
        var roots = visible.Values.Where(p => p.ParentPageId is null || !visible.ContainsKey(p.ParentPageId.Value));
        static IEnumerable<PageItem> Ordered(IEnumerable<PageItem> pages) => pages.OrderBy(p => p.SortOrder).ThenBy(p => p.Item.Id);
        var pending = new Stack<(PageItem Page, int Depth)>(Ordered(roots).Reverse().Select(p => (p, 0))); var rows = new List<PageRow>();
        while (pending.TryPop(out var next))
        {
            var descendants = Ordered(children[next.Page.Item.Id]).ToArray(); rows.Add(Row(profile.Id, next.Page, next.Depth, descendants.Length > 0));
            if (!collapsed.Contains(next.Page.Item.Id)) foreach (var child in descendants.Reverse()) pending.Push((child, next.Depth + 1));
        }
        Rows = rows.ToArray();
    }
    public void Toggle(PageRow row) { if (row.Reference.ProfileId != current.Current?.Id) return; if (!collapsed.Add(row.Page.Item.Id)) collapsed.Remove(row.Page.Item.Id); BuildRows(); Notify(); }
    [RelayCommand] private void Open(PageRow? row)
    {
        if (row is null || row.Reference.ProfileId != current.Current?.Id || !IsIdle) return;
        creating = false; selected = row.Page.Item.Id; collection = row.Page.Item.DeletedAtUtc is not null ? PageCollection.Trash : row.Page.Item.ArchivedAtUtc is not null ? PageCollection.Archived : PageCollection.Active;
        navigation.Navigate(new("Pages", selected.Value.ToString("D"))); _ = ReloadAsync();
    }
    [RelayCommand] private void NewPage() => StartCreate(null);
    [RelayCommand] private void NewChild() { if (Detail?.IsActive == true) StartCreate(Detail.Item.Id); }
    private void StartCreate(Guid? parent) { if (!IsIdle) return; creating = true; createParent = parent; Title = ""; Icon = PageIcon.None; Error = null; Notify(); }
    [RelayCommand] private async Task CancelAsync() { creating = false; await ReloadAsync(); }
    [RelayCommand] private Task SaveAsync() => Run(async profile =>
    {
        var saved = creating ? await service.CreateAsync(profile, Title, createParent, Icon) : await service.UpdateAsync(new(profile, selected!.Value), Title, Icon);
        creating = false; selected = saved.Item.Id; if (saved.IsActive) collection = PageCollection.Active; navigation.Navigate(new("Pages", selected.Value.ToString("D")));
    });
    [RelayCommand] private Task MoveAsync() => Run(async profile => { if (selected is { } id && ParentChoice is { } target) await service.MoveAsync(new(profile, id), target.Id); });
    public Task ReorderAsync(PageRow row, int direction) => Run(async _ => await service.ReorderAsync(row.Reference, direction));
    public Task ApplyAsync(PageRow row, PageAction action) => Run(async _ => { await service.ApplyAsync(row.Reference, action); Fallback(row); });
    public Task DeleteAsync(PageRow row) => Run(async _ => { await service.DeleteAsync(row.Reference); Fallback(row); });
    public Task DuplicateAsync(PageRow row) => Run(async _ => { var copy = await service.DuplicateAsync(row.Reference); collection = PageCollection.Active; selected = copy.Item.Id; navigation.Navigate(new("Pages", copy.Item.Id.ToString("D"))); });
    private void Fallback(PageRow row)
    {
        if (row.Reference.ProfileId != current.Current?.Id || selected != row.Page.Item.Id) return;
        selected = row.Page.ParentPageId; navigation.Navigate(new("Pages", selected?.ToString("D")));
    }
    [RelayCommand] private Task AssignAsync(OrganizationChoice? choice) => Run(async _ => { if (choice is not null) await organization.AssignAsync(choice.Item, choice.Kind, choice.Id, !choice.IsAssigned); });
    private async Task Run(Func<Guid, Task> action)
    {
        if (!IsIdle || current.Current is not { } profile) return; busy = true; Error = null; Notify();
        try { await action(profile.Id); if (current.Current?.Id == profile.Id) await ReloadAsync(); }
        catch (Exception exception) { if (current.Current?.Id == profile.Id) Report(exception); }
        finally { busy = false; Notify(); }
    }
    private void Report(Exception exception)
    {
        if (exception is PageValidationException or PageOperationException or WorkspaceChangedException or OrganizationValidationException or OrganizationOperationException) Error = exception.Message;
        else { logger.LogError(exception, "Page presentation failed"); Error = "The Page could not be updated. Please try again."; }
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsArea), nameof(IsBusy), nameof(IsIdle), nameof(HasDetail), nameof(ShowEditor), nameof(CanEdit), nameof(CanCreateChild), nameof(IsCreating), nameof(IsEmpty), nameof(Heading), nameof(Detail), nameof(DetailRow), nameof(Rows), nameof(Breadcrumbs), nameof(Children), nameof(ParentChoices), nameof(Assignments), nameof(Created), nameof(Modified), nameof(Context), nameof(Collection) }) OnPropertyChanged(name);
    }
}
