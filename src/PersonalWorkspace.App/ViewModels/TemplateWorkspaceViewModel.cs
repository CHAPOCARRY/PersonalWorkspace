using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed record TemplateRow(TemplateReference Reference, TemplateMetadata Metadata)
{
    public string Name => Metadata.Name;
    public string Detail => $"{Metadata.Type} · {Metadata.UpdatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {Metadata.Id.ToString("N")[..8]}";
}
public sealed record TemplatePageDestination(Guid? Id, string Title)
{
    public override string ToString() => Id is { } id ? $"{Title} · {id.ToString("N")[..8]}" : "Root";
}
public sealed partial class TemplateWorkspaceViewModel : ObservableObject
{
    private readonly ITemplateService service;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private readonly ILogger<TemplateWorkspaceViewModel> logger;
    private Guid? observedProfile;
    private int revision;
    private bool busy, loading;
    private bool searchNavigationPending = true;
    private string filter = "All";
    private TemplateType sourceType = TemplateType.Task;
    private TemplateSource? source;
    private TemplateRow? selected;
    private TemplatePageDestination? destination;
    private string name = "", description = "", newTitle = "", captureName = "", captureDescription = "";
    public TemplateWorkspaceViewModel(ITemplateService service, ICurrentProfile current, INavigationService navigation, ILogger<TemplateWorkspaceViewModel> logger)
    {
        this.service = service; this.current = current; this.navigation = navigation; this.logger = logger; observedProfile = current.Current?.Id;
        navigation.Changed += (_, _) => { Clear(); _ = ReloadAsync(); };
        current.Changed += (_, _) => { if (observedProfile == current.Current?.Id) return; observedProfile = current.Current?.Id; Clear(); _ = ReloadAsync(); };
    }
    public bool IsArea => navigation.Current.Destination == "Templates";
    public bool IsBusy => busy || loading;
    public bool IsIdle => !IsBusy && current.Current is not null;
    public bool HasSelection => Snapshot is not null;
    public bool CanUse => IsIdle && HasSelection;
    public bool CanCapture => IsIdle && Source is not null;
    public bool CanReplace => CanUse && Source?.Type == Snapshot?.Metadata.Type;
    public IReadOnlyList<string> Filters { get; } = ["All", "Task", "List", "Journal", "Page"];
    public bool IsPageTemplate => Snapshot?.Metadata.Type == TemplateType.Page;
    public IReadOnlyList<TemplatePageDestination> Destinations { get; private set; } = [new(null, "Root")];
    public TemplatePageDestination? Destination { get => destination; set => SetProperty(ref destination, value); }
    public IReadOnlyList<TemplateType> Types { get; } = Enum.GetValues<TemplateType>();
    public IReadOnlyList<TemplateRow> Rows { get; private set; } = [];
    public IReadOnlyList<TemplateSource> Sources { get; private set; } = [];
    public TemplateSnapshot? Snapshot { get; private set; }
    public string Preview { get; private set; } = "Select a template to preview its saved definition.";
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string Filter { get => filter; set { if (SetProperty(ref filter, value)) { ClearDetail(); _ = ReloadAsync(); } } }
    public TemplateType SourceType { get => sourceType; set { if (SetProperty(ref sourceType, value)) { Source = null; Sources = []; OnPropertyChanged(nameof(Sources)); _ = ReloadAsync(); } } }
    public TemplateSource? Source { get => source; set { if (SetProperty(ref source, value)) { if (value is not null) CaptureName = value.Title; Notify(); } } }
    public TemplateRow? Selected { get => selected; set { if (SetProperty(ref selected, value)) _ = SelectAsync(value); } }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Description { get => description; set => SetProperty(ref description, value); }
    public string NewTitle { get => newTitle; set => SetProperty(ref newTitle, value); }
    public string CaptureName { get => captureName; set => SetProperty(ref captureName, value); }
    public string CaptureDescription { get => captureDescription; set => SetProperty(ref captureDescription, value); }
    public async Task ReloadAsync()
    {
        if (!IsArea || current.Current is not { } profile) return;
        var request = ++revision; loading = true; Error = null; Notify();
        try
        {
            var route = navigation.Current;
            if (route.Context is { } context && context.ProfileId != profile.Id) throw new WorkspaceChangedException();
            var rows = await service.CatalogAsync(profile.Id, Filter == "All" ? null : Enum.Parse<TemplateType>(Filter));
            var sources = await service.SourcesAsync(profile.Id, SourceType);
            var parents = SourceType == TemplateType.Page ? sources : await service.SourcesAsync(profile.Id, TemplateType.Page);
            if (!Fresh(request, profile.Id)) return;
            var destinationId = Destination?.Id;
            var sourceReference = Source?.Reference;
            Destinations = new[] { new TemplatePageDestination(null, "Root") }.Concat(parents.Select(p => new TemplatePageDestination(p.Reference.ItemId, p.Title))).ToArray();
            Rows = rows.Select(m => new TemplateRow(new(profile.Id, m.Id), m)).ToArray(); Sources = sources;
            // ComboBox clears SelectedItem while its ItemsSource changes. Publish the new
            // collections first, then restore selections using identities from before refresh.
            NotifyCollections();
            Destination = Destinations.FirstOrDefault(d => d.Id == destinationId) ?? Destinations[0];
            source = sources.SingleOrDefault(s => s.Reference == sourceReference);
            OnPropertyChanged(nameof(Source));
            if (searchNavigationPending && route.Context is not null && Guid.TryParse(route.EntityId, out var templateId))
            {
                var target = Rows.SingleOrDefault(r => r.Reference.Id == templateId) ?? throw new TemplateValidationException("This Template is no longer available. Search again.");
                // Apply the search target once. Later filtering, editing and deletion belong
                // to the catalog and must not replay a now-hidden or deleted target.
                searchNavigationPending = false;
                selected = target; OnPropertyChanged(nameof(Selected)); await SelectAsync(target);
            }
        }
        catch (Exception e) { if (Fresh(request, profile.Id)) Report(e); }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    public async Task SelectAsync(TemplateRow? row)
    {
        var request = ++revision; ClearDetail(false); loading = row is not null; Notify();
        if (row is null) return;
        try
        {
            var data = await service.ReadAsync(row.Reference);
            if (!Fresh(request, row.Reference.ProfileId)) return;
            Snapshot = data; Name = data.Metadata.Name; Description = data.Metadata.Description ?? ""; NewTitle = data.Blueprint.Title;
            Preview = Describe(data.Blueprint); Error = null; Notice = null;
        }
        catch (Exception e) { if (Fresh(request, row.Reference.ProfileId)) Report(e); }
        finally { if (request == revision) { loading = false; Notify(); } }
    }
    [RelayCommand] private Task CaptureAsync() => Source is { } chosen ? Execute(async () =>
    {
        var result = await service.CaptureAsync(chosen.Reference, chosen.Type, CaptureName, CaptureDescription);
        return $"Saved template “{result.Metadata.Name}”. The source is unchanged.";
    }) : Task.CompletedTask;
    [RelayCommand] private Task InstantiateAsync() => CurrentReference() is { } reference ? Execute(async () =>
    {
        var item = IsPageTemplate ? await service.InstantiatePageAsync(reference, NewTitle, Destination?.Id) : await service.InstantiateAsync(reference, NewTitle);
        return $"Created {item.ItemType} “{item.Title}”. Open it in {item.ItemType switch { WorkspaceItemType.Task => "Tasks", WorkspaceItemType.List => "Lists", WorkspaceItemType.Page => "Pages", _ => "Journal" }}.";
    }) : Task.CompletedTask;
    [RelayCommand] private Task SaveMetadataAsync() => CurrentReference() is { } reference ? Execute(async () =>
    { await service.RenameAsync(reference, Name, Description); return "Template name and description saved."; }) : Task.CompletedTask;
    public Task ReplaceAsync(TemplateReference reference, WorkspaceItemReference replacement) => Execute(async () =>
    { await service.ReplaceAsync(reference, replacement); return "Saved definition replaced. Existing instances are unchanged."; }, refreshTitle: true);
    public Task DeleteAsync(TemplateReference reference) => Execute(async () =>
    { await service.DeleteAsync(reference); return "Template deleted. Existing instances are unchanged."; }, clearSelection: true);
    public TemplateReference? CurrentReference() => Snapshot is { } data && selected is { } row && row.Reference.ProfileId == current.Current?.Id && data.Metadata.Id == row.Reference.Id ? row.Reference : null;
    private async Task Execute(Func<Task<string>> action, bool clearSelection = false, bool refreshTitle = false)
    {
        if (!IsIdle || current.Current is not { } profile) return;
        var request = revision; var reference = CurrentReference(); busy = true; Error = null; Notice = null; Notify();
        try
        {
            var message = await action();
            if (!Fresh(request, profile.Id)) return;
            if (clearSelection) ClearDetail();
            else if (reference is not null)
            {
                var snapshot = await service.ReadAsync(reference);
                if (!Fresh(request, profile.Id)) return;
                Snapshot = snapshot; Preview = Describe(snapshot.Blueprint);
                if (refreshTitle) NewTitle = snapshot.Blueprint.Title;
            }
            await ReloadAsync(); if (profile.Id == current.Current?.Id && IsArea) Notice = message;
        }
        catch (Exception e) { if (Fresh(request, profile.Id)) Report(e); }
        finally { busy = false; Notify(); }
    }
    private bool Fresh(int request, Guid profile) => request == revision && current.Current?.Id == profile && IsArea;
    private void ClearDetail(bool clearRow = true)
    {
        Snapshot = null; Name = Description = NewTitle = ""; Preview = "Select a template to preview its saved definition.";
        Destination = Destinations.FirstOrDefault(d => d.Id is null);
        if (clearRow) { selected = null; OnPropertyChanged(nameof(Selected)); }
    }
    private void Clear()
    {
        ++revision; loading = false; searchNavigationPending = true; Rows = []; Sources = []; source = null; filter = "All"; sourceType = TemplateType.Task;
        Destinations = [new(null, "Root")]; NotifyCollections(); Destination = Destinations[0];
        CaptureName = CaptureDescription = ""; ClearDetail(); Error = Notice = null;
        OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(Filter)); OnPropertyChanged(nameof(SourceType)); Notify();
    }
    private void Report(Exception e)
    {
        logger.LogError(e, "Template workspace operation failed");
        Error = e is TemplateValidationException or TemplateOperationException or TaskValidationException or TaskOperationException or ListValidationException or ListOperationException or JournalValidationException or JournalOperationException or WorkspaceChangedException ? e.Message : "The template operation failed. Please try again.";
    }
    private void Notify()
    {
        foreach (var property in new[] { nameof(IsArea), nameof(IsBusy), nameof(IsIdle), nameof(HasSelection), nameof(CanUse), nameof(CanCapture), nameof(CanReplace), nameof(Snapshot), nameof(Preview), nameof(Error), nameof(Notice), nameof(IsPageTemplate) }) OnPropertyChanged(property);
    }
    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(Sources)); OnPropertyChanged(nameof(Destinations));
    }
    public static string Describe(TemplateBlueprint blueprint) => blueprint switch
    {
        PageBlueprint p => $"{p.Title}\n{p.Canvas.Items.Count(i => i.Kind == CanvasKind.Block)} Blocks · {p.Canvas.Items.Count(i => i.Kind == CanvasKind.Container)} Containers · {p.Widgets.Count} Widgets · {p.Decorations.Count} Decorations\n" +
            string.Join(", ", p.Widgets.Select(w => w.Widget.Type).Distinct().Order()) + $"\nLayout {(p.Canvas.Settings.LayoutLocked ? "locked" : "unlocked")} · Zoom {p.Canvas.Settings.ZoomPercent}%\nSingle Page, without child Pages. Rich text is copied independently; canonical sources and attachment files are shared.",
        TaskBlueprint t => $"{t.Title}\n{t.Definition.Description}\nPriority: {t.Definition.Priority}\n" + (t.Definition.Value is { } v ? $"{v.Type} · Target { (v.Type == TaskValueType.Duration ? ExactValueText.Duration((long)v.Target) : ExactValueText.Input(v.Target)) } {v.Unit ?? v.CurrencyCode}" : "Checkbox") + "\nNew Task: ToDo, no Actual, no scheduled date.",
        ListBlueprint l => $"{l.Title}\n{l.Definition.Description}\nQuantity: {l.Definition.ShowQuantity} · Prices: {l.Definition.ShowPrices} {l.Definition.CurrencyCode}\n" + string.Join("\n", l.Items.Select((i, n) => $"{n + 1}. {i.Title}" + (i.Quantity is { } q ? $" · Qty {ExactValueText.Input(q)}" : "") + (i.UnitPrice is { } p ? $" · Price {ExactValueText.Input(p)}" : "") + (i.Note is null ? "" : $"\n   {i.Note}") + (i.Link is null ? "" : $"\n   {i.Link}"))) + "\nAll new items start unchecked.",
        JournalBlueprint j => $"{j.Title}\n{j.Description}\n" + string.Join("\n", j.Fields.Select((f, i) => $"{i + 1}. {f.Name} · {f.Type}" + (f.Type == JournalFieldType.Scale ? $" {f.ScaleMin}–{f.ScaleMax}" : "") + (f.CurrencyCode is null ? "" : $" {f.CurrencyCode}") + (f.Options is { Count: > 0 } options ? " · " + string.Join(", ", options.Select(o => o.Label)) : ""))) + "\nNew Journal: fresh fields and options, no saved entries.",
        _ => "Unsupported template definition."
    };
}
