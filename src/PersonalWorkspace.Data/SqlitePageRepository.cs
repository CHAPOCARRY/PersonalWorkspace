using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqlitePageRepository : IPageRepository
{
    public async Task<PageGraph> GetAsync(WorkspaceContext workspace, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        return await Read(connection, null, token);
    }
    private static async Task<PageGraph> Read(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT w.Id,w.Title,w.CreatedAtUtc,w.UpdatedAtUtc,w.ArchivedAtUtc,w.DeletedAtUtc,p.ParentPageId,p.SortOrder,p.Icon FROM Pages p JOIN WorkspaceItems w ON w.Id=p.ItemId ORDER BY p.SortOrder,p.ItemId;";
        var pages = new List<PageItem>(); using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) pages.Add(new(new(Guid.Parse(reader.GetString(0)), WorkspaceItemType.Page, reader.GetString(1), Utc(reader, 2)!.Value, Utc(reader, 3)!.Value, Utc(reader, 4), Utc(reader, 5)), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.GetInt32(7), reader.IsDBNull(8) ? PageIcon.None : (PageIcon)reader.GetInt32(8)));
        return new(pages);
    }
    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<PageGraph, T> change, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: false); var graph = await Read(connection, transaction, token);
        var before = graph.Pages.ToDictionary(); var result = change(graph);
        // Save surviving children before deleting parents. FK SET NULL remains the final safety net.
        foreach (var page in graph.Pages.Values)
        {
            var old = before.GetValueOrDefault(page.Item.Id); if (old == page) continue;
            if (page.Item.ItemType != WorkspaceItemType.Page) throw new InvalidOperationException("Invalid Page identity.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = old is null ? """
                INSERT INTO WorkspaceItems(Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc) VALUES($id,5,$title,$created,$updated);
                INSERT INTO Pages(ItemId,ParentPageId,SortOrder,Icon) VALUES($id,$parent,$order,$icon);
                """ : """
                UPDATE WorkspaceItems SET Title=$title,UpdatedAtUtc=$updated,ArchivedAtUtc=$archived,DeletedAtUtc=$deleted WHERE Id=$id;
                UPDATE Pages SET ParentPageId=$parent,SortOrder=$order,Icon=$icon WHERE ItemId=$id;
                """;
            Add(command, "$id", page.Item.Id); Add(command, "$title", page.Item.Title); Add(command, "$created", page.Item.CreatedAtUtc); Add(command, "$updated", page.Item.UpdatedAtUtc);
            Add(command, "$archived", page.Item.ArchivedAtUtc); Add(command, "$deleted", page.Item.DeletedAtUtc); Add(command, "$parent", page.ParentPageId); Add(command, "$order", page.SortOrder); Add(command, "$icon", page.Icon == PageIcon.None ? null : (int)page.Icon);
            await command.ExecuteNonQueryAsync(token);
        }
        foreach (var id in before.Keys.Except(graph.Pages.Keys))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM WorkspaceItems WHERE Id=$id AND ItemType=5 AND DeletedAtUtc IS NOT NULL;"; Add(command, "$id", id);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new PageValidationException("Move the Page to Trash before deleting it.");
        }
        await transaction.CommitAsync(token); return result;
    }
    private static DateTimeOffset? Utc(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value });
}
