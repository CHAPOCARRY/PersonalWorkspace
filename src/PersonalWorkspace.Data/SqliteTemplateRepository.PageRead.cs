using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteTemplateRepository
{
    private static Guid Id(SqliteDataReader r, int column) => Guid.Parse(r.GetString(column));
    private static Guid? OptionalId(SqliteDataReader r, int column) => r.IsDBNull(column) ? null : Id(r, column);
    private static async Task<PageBlueprint> ReadPage(SqliteConnection c, SqliteTransaction tx, Guid template, CancellationToken token)
    {
        string title; PageIcon icon; Guid source; CanvasSettings settings;
        using (var cmd = Command(c, tx, "SELECT Title,Icon,SourcePageId,LayoutLocked,ZoomPercent FROM PageTemplateDefinitions WHERE TemplateId=$p0", template))
        using (var r = await cmd.ExecuteReaderAsync(token))
        { await Required(r, token); title = r.GetString(0); icon = (PageIcon)r.GetInt32(1); source = Id(r, 2); settings = new(r.GetBoolean(3), r.GetInt32(4)); }
        var items = new List<CanvasItem>();
        using (var cmd = Command(c, tx, "SELECT Id,ParentContainerId,Kind,X,Y,Width,Height,Title FROM PageTemplateCanvasItems WHERE TemplateId=$p0 ORDER BY Kind DESC,Id", template))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
            items.Add(new(Id(r, 0), source, OptionalId(r, 1), (CanvasKind)r.GetInt32(2), new(r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), r.GetString(7), default, default));
        var widgets = new List<PageTemplateWidget>();
        using (var cmd = Command(c, tx, """
            SELECT w.Id,w.CanvasItemId,w.WidgetType,w.SourceItemId,w.PresentationMode,
                c.ChartKind,c.RangePreset,l.ItemFilter,t.Content,t.Format,t.FormatVersion,a.AssetId,f.FitMode,f.AltText
            FROM PageTemplateWidgets w
            LEFT JOIN PageTemplateChartSettings c ON c.WidgetId=w.Id
            LEFT JOIN PageTemplateListSettings l ON l.WidgetId=w.Id
            LEFT JOIN PageTemplateRichTextContent t ON t.WidgetId=w.Id
            LEFT JOIN PageTemplateAssetSettings f ON f.WidgetId=w.Id
            LEFT JOIN TemplateAssetReferences a ON a.Id=f.ReferenceId AND a.TemplateId=w.TemplateId
            WHERE w.TemplateId=$p0 ORDER BY w.Id
            """, template))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
            widgets.Add(new(new(Id(r, 0), Id(r, 1), (WidgetType)r.GetInt32(2), OptionalId(r, 3), (WidgetMode)r.GetInt32(4), default, default,
                r.IsDBNull(5) ? null : new((WidgetChartKind)r.GetInt32(5), (AnalyticsPreset)r.GetInt32(6)), r.IsDBNull(7) ? null : new((ListFilter)r.GetInt32(7))),
                r.IsDBNull(8) ? null : new(r.GetString(8), r.GetString(9), r.GetInt32(10)),
                r.IsDBNull(11) ? null : new(Id(r, 11), (ImageFitMode)r.GetInt32(12), r.GetString(13))));
        var decorations = new Dictionary<Guid, CanvasDecoration>(); var order = new List<Guid>();
        using (var cmd = Command(c, tx, "SELECT d.Id,d.Kind,d.ZOrder,d.Color,d.StrokeWidth,d.Filled,d.Dashed,s.X,s.Y,s.Width,s.Height FROM PageTemplateDecorations d LEFT JOIN PageTemplateShapeSettings s ON s.DecorationId=d.Id WHERE d.TemplateId=$p0 ORDER BY d.ZOrder,d.TieOrder", template))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Id(r, 0); order.Add(id);
            decorations.Add(id, new(id, source, (DecorationKind)r.GetInt32(1), new((DecorationColor)r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5), r.GetBoolean(6)), r.GetInt32(2), default, default,
                r.IsDBNull(7) ? null : new(r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10))));
        }
        using (var cmd = Command(c, tx, "SELECT DecorationId,Endpoint,X,Y,TargetCanvasItemId,Anchor FROM PageTemplateDecorationEndpoints WHERE TemplateId=$p0", template))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Id(r, 0); var endpoint = new DecorationEndpoint(new(r.GetDouble(2), r.GetDouble(3)), OptionalId(r, 4), (DecorationAnchor)r.GetInt32(5));
            decorations[id] = r.GetInt32(1) == 0 ? decorations[id] with { Start = endpoint } : decorations[id] with { End = endpoint };
        }
        var points = new Dictionary<Guid, List<DecorationPoint>>();
        using (var cmd = Command(c, tx, "SELECT p.DecorationId,p.PointIndex,p.X,p.Y FROM PageTemplateStrokePoints p JOIN PageTemplateDecorations d ON d.Id=p.DecorationId WHERE d.TemplateId=$p0 ORDER BY p.DecorationId,p.PointIndex", template))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Id(r, 0); if (!points.TryGetValue(id, out var list)) points[id] = list = [];
            if (list.Count != r.GetInt32(1)) throw new TemplateValidationException("The saved stroke point order is incomplete.");
            list.Add(new(r.GetDouble(2), r.GetDouble(3)));
        }
        foreach (var (id, list) in points) decorations[id] = decorations[id] with { Points = list.ToArray() };
        return new(title, icon, source, new(source, true, settings, items), widgets, order.Select(id => decorations[id]).ToArray());
    }

    private static async Task<IReadOnlyList<Asset>> PageAssets(SqliteConnection c, SqliteTransaction tx, Guid id, bool snapshot, CancellationToken token)
    {
        var sql = snapshot ? "SELECT a.* FROM Assets a JOIN TemplateAssetReferences r ON r.AssetId=a.Id WHERE r.TemplateId=$p0" : """
            SELECT DISTINCT a.* FROM Assets a JOIN AssetReferences r ON r.AssetId=a.Id
            JOIN WidgetAssetSettings s ON s.ReferenceId=r.Id JOIN WidgetInstances w ON w.Id=s.WidgetId
            JOIN PageCanvasItems i ON i.Id=w.CanvasItemId WHERE i.PageId=$p0 AND r.OwnerItemId=$p0
            """;
        using var cmd = Command(c, tx, sql, id); using var reader = await cmd.ExecuteReaderAsync(token); var result = new List<Asset>();
        while (await reader.ReadAsync(token)) result.Add(SqliteContentRepository.AssetRow(reader)); return result;
    }
    private static async Task<PageBlueprint> CapturePage(SqliteConnection c, SqliteTransaction tx, Guid id, CancellationToken token)
    {
        var graph = await SqlitePageRepository.Read(c, tx, token); var page = graph.Find(id);
        if (page.Item.DeletedAtUtc is not null) throw new TemplateValidationException("Restore this Page from Trash before capturing it.");
        var content = await SqliteWidgetRepository.Read(c, tx, id, null, token);
        var decorations = await SqliteDecorationRepository.Read(c, tx, id, token);
        var text = new Dictionary<Guid, PageTemplateText>(); var assets = new Dictionary<Guid, PageTemplateAsset>();
        using (var cmd = Command(c, tx, """
            SELECT w.Id,d.Content,d.Format,d.FormatVersion FROM WidgetInstances w JOIN PageCanvasItems i ON i.Id=w.CanvasItemId
            JOIN WidgetRichTextSettings s ON s.WidgetId=w.Id JOIN RichTextReferences r ON r.Id=s.ReferenceId
            JOIN RichTextDocuments d ON d.Id=r.DocumentId WHERE i.PageId=$p0 AND r.OwnerItemId=$p0
            """, id))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) text.Add(Id(r, 0), new(r.GetString(1), r.GetString(2), r.GetInt32(3)));
        using (var cmd = Command(c, tx, """
            SELECT w.Id,r.AssetId,s.FitMode,s.AltText FROM WidgetInstances w JOIN PageCanvasItems i ON i.Id=w.CanvasItemId
            JOIN WidgetAssetSettings s ON s.WidgetId=w.Id JOIN AssetReferences r ON r.Id=s.ReferenceId WHERE i.PageId=$p0 AND r.OwnerItemId=$p0
            """, id))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) assets.Add(Id(r, 0), new(Id(r, 1), (ImageFitMode)r.GetInt32(2), r.GetString(3)));
        return new(page.Item.Title, page.Icon, id, content.Canvas,
            content.Widgets.Select(w => new PageTemplateWidget(w with { RichText = null, Asset = null }, text.GetValueOrDefault(w.Id), assets.GetValueOrDefault(w.Id))).ToArray(), decorations.Items);
    }
}
