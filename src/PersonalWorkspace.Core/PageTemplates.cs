namespace PersonalWorkspace.Core;

public sealed record PageTemplateText(string Content, string Format, int FormatVersion);
public sealed record PageTemplateAsset(Guid AssetId, ImageFitMode Fit, string AltText);
public sealed record PageTemplateWidget(WidgetInstance Widget, PageTemplateText? Text = null, PageTemplateAsset? File = null);
public sealed record PageBlueprint(string PageTitle, PageIcon Icon, Guid SourcePageId, CanvasSnapshot Canvas,
    IReadOnlyList<PageTemplateWidget> Widgets, IReadOnlyList<CanvasDecoration> Decorations) : TemplateBlueprint(PageTitle);
public sealed record PageTemplateInstance(PageItem Page, PageBlueprint Content);

// Callbacks run inside the repository transaction and the service's existing workspace gate.
// SQLite handles never cross the Data boundary.
public interface IPageTemplateRepository
{
    Task<TemplateSnapshot> CaptureAsync(WorkspaceContext workspace, Guid source, TemplateMetadata metadata, bool create,
        Func<PageBlueprint, IReadOnlyList<Asset>, Task<PageBlueprint>> prepare, CancellationToken token);
    Task<WorkspaceItem> InstantiateAsync(WorkspaceContext workspace, Guid template,
        Func<TemplateSnapshot, PageGraph, IReadOnlyList<Asset>, Task<PageTemplateInstance>> prepare, CancellationToken token);
}
public interface IPageTemplateService
{
    Task<TemplateSnapshot> CaptureAsync(WorkspaceContext workspace, Guid source, TemplateMetadata metadata, bool create, CancellationToken token);
    Task<WorkspaceItem> InstantiateAsync(WorkspaceContext workspace, Guid template, string? title, Guid? parent, CancellationToken token);
}

public static class PageCreation
{
    public static string Title(string title) => title.Trim() is { Length: > 0 and <= 200 } value && !value.Any(char.IsControl)
        ? value : throw new PageValidationException("Enter a Page title of 1–200 characters without control characters.");
    public static PageIcon Icon(PageIcon icon) => Enum.IsDefined(icon) ? icon : throw new PageValidationException("Choose a built-in Page icon.");
    public static PageItem Create(PageGraph graph, string title, Guid? parent, PageIcon icon, DateTimeOffset now)
    {
        var id = Guid.NewGuid(); graph.ValidateParent(id, parent);
        var page = new PageItem(new(id, WorkspaceItemType.Page, Title(title), now, now, null, null), parent, graph.Children(parent).Count, Icon(icon));
        graph.Pages.Add(id, page); return page;
    }
}
