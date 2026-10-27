using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteWidgetRepository : IWidgetRepository
{
    public async Task<WidgetPage> ReadAsync(WorkspaceContext workspace, Guid pageId, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = await Read(connection, transaction, pageId, null, token); await transaction.CommitAsync(token); return result;
    }
    internal static async Task<WidgetPage> Read(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, Guid? extraSource, CancellationToken token)
    {
        var canvas = await SqliteCanvasRepository.Read(connection, transaction, pageId, token);
        var widgets = new List<WidgetInstance>();
        using (var command = Command(connection, transaction, """
            SELECT w.Id,w.CanvasItemId,w.WidgetType,w.SourceItemId,w.PresentationMode,w.CreatedAtUtc,w.UpdatedAtUtc,s.ChartKind,s.RangePreset,l.ItemFilter,r.ReferenceId,a.ReferenceId,a.FitMode,a.AltText
            FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId LEFT JOIN WidgetChartSettings s ON s.WidgetId=w.Id
            LEFT JOIN WidgetListSettings l ON l.WidgetId=w.Id LEFT JOIN WidgetRichTextSettings r ON r.WidgetId=w.Id
            LEFT JOIN WidgetAssetSettings a ON a.WidgetId=w.Id WHERE c.PageId=$page ORDER BY w.CreatedAtUtc,w.Id;
            """, pageId))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) widgets.Add(new(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (WidgetType)reader.GetInt32(2), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            (WidgetMode)reader.GetInt32(4), Utc(reader, 5)!.Value, Utc(reader, 6)!.Value,
            reader.IsDBNull(7) ? null : new((WidgetChartKind)reader.GetInt32(7), (AnalyticsPreset)reader.GetInt32(8)),
            reader.IsDBNull(9) ? null : new((ListFilter)reader.GetInt32(9)),
            reader.IsDBNull(10) ? null : new(Guid.Parse(reader.GetString(10))),
            reader.IsDBNull(11) ? null : new(Guid.Parse(reader.GetString(11)), (ImageFitMode)reader.GetInt32(12), reader.GetString(13))));
        var priced = new HashSet<Guid>();
        using (var command = Command(connection, transaction, "SELECT ItemId FROM Lists WHERE ShowPrices=1 AND (ItemId=$source OR ItemId IN (SELECT w.SourceItemId FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId WHERE c.PageId=$page));", pageId))
        {
            Add(command, "$source", extraSource); using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) priced.Add(Guid.Parse(reader.GetString(0)));
        }
        using var sources = Command(connection, transaction, """
            SELECT Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc,ArchivedAtUtc,DeletedAtUtc FROM WorkspaceItems
            WHERE Id=$source OR Id IN (SELECT w.SourceItemId FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId WHERE c.PageId=$page);
            """, pageId);
        Add(sources, "$source", extraSource); return new(canvas, widgets, await Sources(sources, token), priced);
    }
    public async Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceContext workspace, WorkspaceItemType type, Guid pageId, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var command = Command(connection, null, """
            SELECT Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc,ArchivedAtUtc,DeletedAtUtc FROM WorkspaceItems
            WHERE ItemType=$type AND ArchivedAtUtc IS NULL AND DeletedAtUtc IS NULL AND Id<>$page ORDER BY Title COLLATE NOCASE,Id;
            """, pageId);
        Add(command, "$type", (int)type); return await Sources(command, token);
    }
    public async Task<WidgetPage> EditAsync(WorkspaceContext workspace, Guid pageId, Guid? sourceId, Func<WidgetState, WidgetPage> change, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: false);
        var before = await Read(connection, transaction, pageId, sourceId, token); var after = change(new(before));
        var auditBefore = AuditProjection.Widgets(before); auditBefore.AddRange(AuditProjection.Decorations(await SqliteDecorationRepository.Read(connection, transaction, pageId, token)));
        after = await PrepareContent(connection, transaction, pageId, before, after, null, token);
        await Write(connection, transaction, pageId, before, after, token);
        var auditAfter = AuditProjection.Widgets(after); auditAfter.AddRange(AuditProjection.Decorations(await SqliteDecorationRepository.Read(connection, transaction, pageId, token)));
        await SqliteAuditWriter.Append(connection, transaction, workspace, auditBefore, auditAfter, token);
        await transaction.CommitAsync(token); return after;
    }
    internal static async Task Write(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, WidgetPage before, WidgetPage after, CancellationToken token)
    {
        foreach (var removed in before.Widgets.Where(w => !after.Widgets.Any(a => a.Id == w.Id)))
        { using var command = Command(connection, transaction, "DELETE FROM WidgetInstances WHERE Id=$id;", pageId); Add(command, "$id", removed.Id); await command.ExecuteNonQueryAsync(token); }
        await SqliteCanvasRepository.Write(connection, transaction, pageId, before.Canvas, after.Canvas, token);
        foreach (var widget in after.Widgets)
        {
            if (before.Widgets.Any(w => w == widget)) continue;
            if (!after.Canvas.Items.Any(i => i.Id == widget.CanvasItemId && i.Kind == CanvasKind.Block)) throw new WidgetValidationException("The Widget host must belong to this Page.");
            using var command = Command(connection, transaction, """
                INSERT INTO WidgetInstances(Id,CanvasItemId,WidgetType,SourceItemId,PresentationMode,CreatedAtUtc,UpdatedAtUtc)
                VALUES($id,$host,$type,$source,$mode,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET SourceItemId=excluded.SourceItemId,PresentationMode=excluded.PresentationMode,UpdatedAtUtc=excluded.UpdatedAtUtc
                WHERE CanvasItemId=excluded.CanvasItemId AND WidgetType=excluded.WidgetType;
                """, pageId);
            Add(command, "$id", widget.Id); Add(command, "$host", widget.CanvasItemId); Add(command, "$type", (int)widget.Type); Add(command, "$source", widget.SourceItemId);
            Add(command, "$mode", (int)widget.Mode); Add(command, "$created", widget.CreatedAtUtc); Add(command, "$updated", widget.UpdatedAtUtc);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new WidgetValidationException("Widget identity cannot change.");
            if (widget.Chart is { } chart)
            {
                using var settings = Command(connection, transaction, "INSERT INTO WidgetChartSettings(WidgetId,ChartKind,RangePreset) VALUES($id,$kind,$range) ON CONFLICT(WidgetId) DO UPDATE SET ChartKind=excluded.ChartKind,RangePreset=excluded.RangePreset;", pageId);
                Add(settings, "$id", widget.Id); Add(settings, "$kind", (int)chart.Kind); Add(settings, "$range", (int)chart.Range); await settings.ExecuteNonQueryAsync(token);
            }
            if (widget.List is { } list)
            {
                using var settings = Command(connection, transaction, "INSERT INTO WidgetListSettings(WidgetId,ItemFilter) VALUES($id,$filter) ON CONFLICT(WidgetId) DO UPDATE SET ItemFilter=excluded.ItemFilter;", pageId);
                Add(settings, "$id", widget.Id); Add(settings, "$filter", (int)list.Filter); await settings.ExecuteNonQueryAsync(token);
            }
            await WriteContent(connection, transaction, pageId, widget, before.Widgets.Any(w => w.Id == widget.Id), token);
        }
    }
    private static async Task<IReadOnlyList<WorkspaceItem>> Sources(SqliteCommand command, CancellationToken token)
    {
        var result = new List<WorkspaceItem>(); using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(Guid.Parse(reader.GetString(0)), (WorkspaceItemType)reader.GetInt32(1), reader.GetString(2), Utc(reader, 3)!.Value, Utc(reader, 4)!.Value, Utc(reader, 5), Utc(reader, 6)));
        return result;
    }
    private static DateTimeOffset? Utc(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture);
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, Guid page)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; Add(command, "$page", page); return command; }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value });
}
