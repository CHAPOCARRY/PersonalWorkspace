using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteTemplateRepository
{
    public async Task<TemplateSnapshot> CaptureAsync(WorkspaceContext workspace, Guid source, TemplateMetadata metadata, bool create,
        Func<PageBlueprint, IReadOnlyList<Asset>, Task<PageBlueprint>> prepare, CancellationToken token)
    {
        await using var c = await Open(workspace, token); using var tx = c.BeginTransaction(deferred: false);
        var blueprint = await prepare(await CapturePage(c, tx, source, token), await PageAssets(c, tx, source, false, token));
        if (metadata.Type != TemplateType.Page || metadata.FormatVersion != 1) throw new TemplateValidationException("Choose a Page template.");
        if (create) await Execute(c, tx, "INSERT INTO Templates VALUES($p0,4,$p1,$p2,1,$p3,$p4)", token, metadata.Id, metadata.Name, metadata.Description, metadata.CreatedAtUtc, metadata.UpdatedAtUtc);
        else
        {
            using var cmd = Command(c, tx, "UPDATE Templates SET UpdatedAtUtc=$p1 WHERE Id=$p0 AND TemplateType=4 AND FormatVersion=1", metadata.Id, metadata.UpdatedAtUtc);
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new TemplateValidationException("This Page template is no longer available.");
        }
        await WritePage(c, tx, metadata, blueprint, token);
        var result = await Read(c, tx, metadata.Id, token); await tx.CommitAsync(token); return result;
    }
    private static async Task WritePage(SqliteConnection c, SqliteTransaction tx, TemplateMetadata metadata, PageBlueprint page, CancellationToken token)
    {
        var id = metadata.Id;
        // Keep the definition and retained intersections alive throughout replacement.
        await Execute(c, tx, """
            INSERT INTO PageTemplateDefinitions(TemplateId,Title,Icon,SourcePageId,LayoutLocked,ZoomPercent) VALUES($p0,$p1,$p2,$p3,$p4,$p5)
            ON CONFLICT(TemplateId) DO UPDATE SET Title=excluded.Title,Icon=excluded.Icon,SourcePageId=excluded.SourcePageId,LayoutLocked=excluded.LayoutLocked,ZoomPercent=excluded.ZoomPercent
            """, token, id, page.Title, page.Icon, page.SourcePageId, page.Canvas.Settings.LayoutLocked, page.Canvas.Settings.ZoomPercent);
        var retained = new Dictionary<Guid, Guid>();
        using (var cmd = Command(c, tx, "SELECT AssetId,Id FROM TemplateAssetReferences WHERE TemplateId=$p0", id))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) retained.Add(Id(r, 0), Id(r, 1));
        var needed = page.Widgets.Where(w => w.File is not null).Select(w => w.File!.AssetId).ToHashSet();
        foreach (var asset in needed.Except(retained.Keys).ToArray())
        {
            var reference = Guid.NewGuid();
            await Execute(c, tx, "INSERT INTO TemplateAssetReferences VALUES($p0,$p1,$p2,$p3)", token, reference, id, asset, metadata.UpdatedAtUtc);
            retained.Add(asset, reference);
        }
        await Execute(c, tx, "DELETE FROM PageTemplateDecorations WHERE TemplateId=$p0; DELETE FROM PageTemplateCanvasItems WHERE TemplateId=$p0;", token, id);
        using (var cmd = Command(c, tx, "INSERT INTO PageTemplateCanvasItems VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", new object?[9]))
        {
            cmd.Prepare(); foreach (var item in page.Canvas.Items.OrderByDescending(i => i.Kind))
            { Set(cmd, item.Id, id, item.ParentContainerId, item.Kind, item.Rect.X, item.Rect.Y, item.Rect.Width, item.Rect.Height, item.Title); await cmd.ExecuteNonQueryAsync(token); }
        }
        foreach (var entry in page.Widgets)
        {
            var w = entry.Widget;
            await Execute(c, tx, "INSERT INTO PageTemplateWidgets VALUES($p0,$p1,$p2,$p3,$p4,$p5)", token, w.Id, id, w.CanvasItemId, w.Type, w.SourceItemId, w.Mode);
            if (w.Chart is { } chart) await Execute(c, tx, "INSERT INTO PageTemplateChartSettings(WidgetId,ChartKind,RangePreset) VALUES($p0,$p1,$p2)", token, w.Id, chart.Kind, chart.Range);
            if (w.List is { } list) await Execute(c, tx, "INSERT INTO PageTemplateListSettings(WidgetId,ItemFilter) VALUES($p0,$p1)", token, w.Id, list.Filter);
            if (entry.Text is { } text) await Execute(c, tx, "INSERT INTO PageTemplateRichTextContent(WidgetId,Content,Format,FormatVersion) VALUES($p0,$p1,$p2,$p3)", token, w.Id, text.Content, text.Format, text.FormatVersion);
            if (entry.File is { } file) await Execute(c, tx, "INSERT INTO PageTemplateAssetSettings VALUES($p0,$p1,$p2,$p3,$p4,$p5)", token, w.Id, id, retained[file.AssetId], w.Type, file.Fit, file.AltText);
        }
        for (var index = 0; index < page.Decorations.Count; index++)
        {
            var d = page.Decorations[index];
            await Execute(c, tx, "INSERT INTO PageTemplateDecorations VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", token, d.Id, id, d.Kind, d.ZOrder, index, d.Style.Color, d.Style.Width, d.Style.Filled, d.Style.Dashed);
            if (d.Bounds is { } rect) await Execute(c, tx, "INSERT INTO PageTemplateShapeSettings VALUES($p0,$p1,$p2,$p3,$p4)", token, d.Id, rect.X, rect.Y, rect.Width, rect.Height);
            if (d.Start is not null) for (var slot = 0; slot < 2; slot++)
            {
                var e = slot == 0 ? d.Start : d.End!;
                await Execute(c, tx, "INSERT INTO PageTemplateDecorationEndpoints VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", token, d.Id, id, slot, e.Point.X, e.Point.Y, e.Target, e.Anchor);
            }
            if (d.Points is { } points)
            {
                using var cmd = Command(c, tx, "INSERT INTO PageTemplateStrokePoints VALUES($p0,$p1,$p2,$p3)", new object?[4]); cmd.Prepare();
                for (var p = 0; p < points.Count; p++) { Set(cmd, d.Id, p, points[p].X, points[p].Y); await cmd.ExecuteNonQueryAsync(token); }
            }
        }
        foreach (var asset in retained.Keys.Except(needed))
            await Execute(c, tx, "DELETE FROM TemplateAssetReferences WHERE Id=$p0 AND TemplateId=$p1", token, retained[asset], id);
    }

    public async Task<WorkspaceItem> InstantiateAsync(WorkspaceContext workspace, Guid template,
        Func<TemplateSnapshot, PageGraph, IReadOnlyList<Asset>, Task<PageTemplateInstance>> prepare, CancellationToken token)
    {
        await using var c = await Open(workspace, token); using var tx = c.BeginTransaction(deferred: false);
        var snapshot = await Read(c, tx, template, token);
        var graph = await SqlitePageRepository.Read(c, tx, token); var before = graph.Pages.ToDictionary();
        var assets = await PageAssets(c, tx, template, true, token);
        var instance = await prepare(snapshot, graph, assets);
        var page = instance.Page.Item.Id; var blueprint = instance.Content;
        await SqlitePageRepository.Write(c, tx, before, graph, token);
        var widgets = await SqliteContentRepository.CreatePageWidgetContent(c, tx, page, blueprint.Widgets, assets, token);
        var empty = new CanvasSnapshot(page, true, new(), []);
        var canvas = blueprint.Canvas with { Items = blueprint.Canvas.Items.OrderByDescending(i => i.Kind).ToArray() };
        await SqliteWidgetRepository.Write(c, tx, page, new(empty, [], []), new(canvas, widgets, []), token);
        await SqliteDecorationRepository.Write(c, tx, page, new(canvas, []), new(canvas, blueprint.Decorations), token);
        await tx.CommitAsync(token); return instance.Page.Item;
    }
}
