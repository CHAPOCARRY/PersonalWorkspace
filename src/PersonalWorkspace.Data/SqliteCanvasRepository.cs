using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteCanvasRepository : ICanvasRepository
{
    public async Task<CanvasSnapshot> GetAsync(WorkspaceContext workspace, Guid pageId, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token); using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await Read(connection, transaction, pageId, token); await transaction.CommitAsync(token); return snapshot;
    }
    internal static async Task<CanvasSnapshot> Read(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, CancellationToken token)
    {
        bool active;
        using (var command = Command(connection, transaction, "SELECT w.ArchivedAtUtc IS NULL AND w.DeletedAtUtc IS NULL FROM Pages p JOIN WorkspaceItems w ON w.Id=p.ItemId WHERE p.ItemId=$page;", pageId))
        { var value = await command.ExecuteScalarAsync(token); if (value is null) throw new CanvasValidationException("This Page is no longer available."); active = Convert.ToBoolean(value); }
        var settings = new CanvasSettings();
        using (var command = Command(connection, transaction, "SELECT LayoutLocked,ZoomPercent FROM PageCanvasSettings WHERE PageId=$page;", pageId))
        using (var reader = await command.ExecuteReaderAsync(token)) if (await reader.ReadAsync(token)) settings = new(reader.GetBoolean(0), reader.GetInt32(1));
        var items = new List<CanvasItem>();
        using (var command = Command(connection, transaction, "SELECT Id,ParentContainerId,Kind,X,Y,Width,Height,Title,CreatedAtUtc,UpdatedAtUtc FROM PageCanvasItems WHERE PageId=$page ORDER BY CreatedAtUtc,Id;", pageId))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) items.Add(new(Guid.Parse(reader.GetString(0)), pageId, reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), (CanvasKind)reader.GetInt32(2), new(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)), reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)));
        return new(pageId, active, settings, items);
    }
    public async Task<CanvasSnapshot> EditAsync(WorkspaceContext workspace, Guid pageId, Func<CanvasSnapshot, CanvasSnapshot> edit, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token); using var transaction = connection.BeginTransaction(deferred: false);
        var before = await Read(connection, transaction, pageId, token); var after = edit(before);
        await Write(connection, transaction, pageId, before, after, token);
        await transaction.CommitAsync(token); return after;
    }
    // Shared transaction boundary for operations that attach content to generic layout items.
    internal static async Task Write(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, CanvasSnapshot before, CanvasSnapshot after, CancellationToken token)
    {
        if (after.PageId != pageId) throw new InvalidOperationException("Canvas ownership cannot change.");
        if (after.Settings != before.Settings)
        {
            using var command = Command(connection, transaction, after.Settings == new CanvasSettings() ? "DELETE FROM PageCanvasSettings WHERE PageId=$page;" : "INSERT INTO PageCanvasSettings(PageId,LayoutLocked,ZoomPercent) VALUES($page,$locked,$zoom) ON CONFLICT(PageId) DO UPDATE SET LayoutLocked=excluded.LayoutLocked,ZoomPercent=excluded.ZoomPercent;", pageId);
            command.Parameters.AddWithValue("$locked", after.Settings.LayoutLocked); command.Parameters.AddWithValue("$zoom", after.Settings.ZoomPercent); await command.ExecuteNonQueryAsync(token);
        }
        var original = before.Items.ToDictionary(i => i.Id);
        // Reparent survivors before removing Containers. Page deletion separately cascades the whole layout.
        foreach (var item in after.Items)
        {
            if (original.TryGetValue(item.Id, out var old) && old == item) continue;
            if (item.PageId != pageId || old is not null && (old.Kind != item.Kind || old.CreatedAtUtc != item.CreatedAtUtc)) throw new InvalidOperationException("Layout identity cannot change.");
            using var command = Command(connection, transaction, """
                INSERT INTO PageCanvasItems(Id,PageId,ParentContainerId,Kind,X,Y,Width,Height,Title,CreatedAtUtc,UpdatedAtUtc)
                VALUES($id,$page,$parent,$kind,$x,$y,$width,$height,$title,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET ParentContainerId=excluded.ParentContainerId,X=excluded.X,Y=excluded.Y,Width=excluded.Width,Height=excluded.Height,Title=excluded.Title,UpdatedAtUtc=excluded.UpdatedAtUtc WHERE PageId=excluded.PageId;
                """, pageId);
            Add(command, "$id", item.Id); Add(command, "$parent", item.ParentContainerId); Add(command, "$kind", (int)item.Kind); Add(command, "$x", item.Rect.X); Add(command, "$y", item.Rect.Y); Add(command, "$width", item.Rect.Width); Add(command, "$height", item.Rect.Height); Add(command, "$title", item.Title); Add(command, "$created", item.CreatedAtUtc); Add(command, "$updated", item.UpdatedAtUtc);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new CanvasValidationException("This layout item belongs to another Page.");
        }
        foreach (var id in original.Keys.Except(after.Items.Select(i => i.Id)))
        { using var command = Command(connection, transaction, "DELETE FROM PageCanvasItems WHERE Id=$id AND PageId=$page;", pageId); Add(command, "$id", id); await command.ExecuteNonQueryAsync(token); }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid page)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; Add(command, "$page", page); return command; }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value });
}
