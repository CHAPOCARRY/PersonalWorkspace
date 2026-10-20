using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

// TemplateService holds the originating Profile's gate for this entire operation.
public sealed class PageTemplateService(IPageTemplateRepository repository, IAssetFileStore files, TimeProvider clock) : IPageTemplateService
{
    public Task<TemplateSnapshot> CaptureAsync(WorkspaceContext workspace, Guid source, TemplateMetadata metadata, bool create, CancellationToken token) =>
        repository.CaptureAsync(workspace, source, metadata, create, async (blueprint, assets) =>
        {
            await Validate(workspace, blueprint, assets, token);
            return Remap(blueprint, blueprint.SourcePageId, default, false);
        }, token);

    public Task<WorkspaceItem> InstantiateAsync(WorkspaceContext workspace, Guid template, string? title, Guid? parent, CancellationToken token) =>
        repository.InstantiateAsync(workspace, template, async (snapshot, graph, assets) =>
        {
            if (snapshot.Blueprint is not PageBlueprint blueprint) throw new TemplateValidationException("Choose a Page template.");
            await Validate(workspace, blueprint, assets, token);
            var now = clock.GetUtcNow();
            var page = PageCreation.Create(graph, string.IsNullOrWhiteSpace(title) ? blueprint.Title : title, parent, blueprint.Icon, now);
            var content = Remap(blueprint, page.Item.Id, now, true);
            ValidateStructure(content);
            return new(page, content);
        }, token);

    private async Task Validate(WorkspaceContext workspace, PageBlueprint page, IReadOnlyList<Asset> assets, CancellationToken token)
    {
        ValidateStructure(page); var available = assets.ToDictionary(a => a.Id);
        foreach (var entry in page.Widgets)
        {
            if (entry.Text is { } text)
            {
                if (text.Format != RichTextFormat.Name || text.FormatVersion != RichTextFormat.Version || RichTextFormat.Validate(text.Content) != text.Content)
                    throw new TemplateValidationException("The Page contains unsupported or noncanonical rich text.");
            }
            if (entry.File is { } file && (!available.TryGetValue(file.AssetId, out var asset) || asset.PendingDelete ||
                entry.Widget.Type == WidgetType.Image && asset.MediaType is not ("image/png" or "image/jpeg")))
                throw new TemplateValidationException("A required template Asset is missing, incompatible or pending cleanup.");
        }
        foreach (var id in page.Widgets.Where(w => w.File is not null).Select(w => w.File!.AssetId).Distinct())
            await files.UseFileAsync(workspace.ProfileId, available[id], Availability.Instance, token);
    }
    private sealed class Availability : IAssetFileConsumer
    {
        public static Availability Instance { get; } = new();
        public Task UseAsync(string validatedPath, Asset asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(validatedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != asset.ByteLength) throw new ContentValidationException("A template attachment has an unexpected length.");
            return Task.CompletedTask;
        }
    }
    public static void ValidateStructure(PageBlueprint page)
    {
        _ = PageCreation.Title(page.Title); _ = PageCreation.Icon(page.Icon);
        if (page.SourcePageId == Guid.Empty || page.Canvas.PageId == Guid.Empty || page.Canvas.Settings.ZoomPercent is < 25 or > 200 || page.Canvas.Settings.ZoomPercent % 25 != 0)
            throw new TemplateValidationException("The Page template settings are invalid.");
        var canvas = new CanvasState(page.Canvas); CanvasLayout.Validate(canvas);
        var hosts = new HashSet<Guid>(); var widgets = new HashSet<Guid>();
        foreach (var entry in page.Widgets)
        {
            var w = entry.Widget; WidgetRules.Validate(w.Type, w.Mode, w.Chart, w.List);
            if (w.Id == Guid.Empty || !widgets.Add(w.Id) || !hosts.Add(w.CanvasItemId) || !canvas.Items.TryGetValue(w.CanvasItemId, out var host) || host.Kind != CanvasKind.Block)
                throw new TemplateValidationException("Each Widget must have a unique Block on this Page.");
            if (w.RichText is not null || w.Asset is not null || WidgetRules.IsContent(w.Type) && w.SourceItemId is not null ||
                (w.Type == WidgetType.RichText) != (entry.Text is not null) || (w.Type is WidgetType.Image or WidgetType.Attachment) != (entry.File is not null))
                throw new TemplateValidationException("The Widget snapshot is missing required content or has incompatible settings.");
            if (w.Type == WidgetType.PageLink && w.SourceItemId == page.Canvas.PageId) throw new TemplateValidationException("A PageLink cannot link to its own Page.");
            if (entry.File is { } file && (file.AssetId == Guid.Empty || !Enum.IsDefined(file.Fit) || file.AltText.Length > 1000 || file.AltText.Any(char.IsControl)))
                throw new TemplateValidationException("The Widget image settings are invalid.");
        }
        if (page.Decorations.Count > DecorationRules.MaxDecorations || page.Decorations.Sum(d => (long)(d.Points?.Count ?? 0)) > DecorationRules.MaxPagePoints || page.Decorations.Select(d => d.Id).Distinct().Count() != page.Decorations.Count)
            throw new TemplateValidationException("The Page exceeds the supported decoration capacity or contains duplicate identities.");
        foreach (var d in page.Decorations) DecorationRules.Validate(d with { Start = Fallback(d.Start, page.Canvas), End = Fallback(d.End, page.Canvas) }, page.Canvas);
    }
    private static DecorationEndpoint? Fallback(DecorationEndpoint? e, CanvasSnapshot canvas) => e?.Target is { } target && !canvas.Items.Any(i => i.Id == target) ? e with { Target = null } : e;

    public static PageBlueprint Remap(PageBlueprint original, Guid page, DateTimeOffset now, bool rewriteLinks)
    {
        var canvasMap = original.Canvas.Items.ToDictionary(i => i.Id, _ => Guid.NewGuid());
        var widgetMap = original.Widgets.ToDictionary(w => w.Widget.Id, _ => Guid.NewGuid());
        // Persisted draw-order ties use Guid.CompareTo. Allocate fresh IDs in that same order.
        var freshDecorationIds = Enumerable.Range(0, original.Decorations.Count).Select(_ => Guid.NewGuid()).Order().ToArray();
        var decorationMap = original.Decorations.Select((d, i) => (d.Id, New: freshDecorationIds[i])).ToDictionary(p => p.Id, p => p.New);
        DecorationEndpoint? Endpoint(DecorationEndpoint? endpoint)
        {
            if (endpoint is null) return null;
            var safe = Fallback(endpoint, original.Canvas)!;
            return safe with { Point = DecorationRules.Resolve(safe, original.Canvas), Target = safe.Target is { } id ? canvasMap[id] : null };
        }
        var canvas = new CanvasSnapshot(page, true, original.Canvas.Settings, original.Canvas.Items.Select(i => i with
        { Id = canvasMap[i.Id], PageId = page, ParentContainerId = i.ParentContainerId is { } parent ? canvasMap[parent] : null, CreatedAtUtc = now, UpdatedAtUtc = now }).ToArray());
        var widgets = original.Widgets.Select(entry => entry with
        {
            Widget = entry.Widget with { Id = widgetMap[entry.Widget.Id], CanvasItemId = canvasMap[entry.Widget.CanvasItemId], CreatedAtUtc = now, UpdatedAtUtc = now },
            Text = rewriteLinks && entry.Text is { } text ? text with { Content = PageTemplateLinks.Remap(text.Content, original.SourcePageId, page) } : entry.Text
        }).ToArray();
        var decorations = original.Decorations.Select(d => d with { Id = decorationMap[d.Id], PageId = page, CreatedAtUtc = now, UpdatedAtUtc = now,
            Start = Endpoint(d.Start), End = Endpoint(d.End), Points = d.Points?.ToArray() }).ToArray();
        return original with { Canvas = canvas, Widgets = widgets, Decorations = decorations };
    }
}
