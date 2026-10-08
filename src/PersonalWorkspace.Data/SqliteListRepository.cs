using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteListRepository : IListRepository
{
    public async Task<IReadOnlyList<ListDefinition>> GetDefinitionsAsync(WorkspaceContext workspace, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        return await Definitions(connection, null, null, token);
    }
    public async Task<ListSnapshot> ReadAsync(WorkspaceContext workspace, Guid id, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await Read(connection, transaction, id, token);
    }
    public async Task<IReadOnlyList<ListItem>> GetItemsAsync(WorkspaceContext workspace, Guid id, ListItemQuery query, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: true);
        _ = (await Definitions(connection, transaction, id, token)).SingleOrDefault() ?? throw new ListValidationException("This List is no longer available in this profile.");
        return await Items(connection, transaction, id, query, token);
    }
    private static async Task<ListSnapshot> Read(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        var definition = (await Definitions(connection, transaction, id, token)).SingleOrDefault() ?? throw new ListValidationException("This List is no longer available in this profile.");
        return new(definition, await Items(connection, transaction, id, null, token));
    }
    private static async Task<IReadOnlyList<ListDefinition>> Definitions(SqliteConnection connection, SqliteTransaction? transaction, Guid? id, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT w.Id,w.Title,w.CreatedAtUtc,w.UpdatedAtUtc,w.ArchivedAtUtc,w.DeletedAtUtc,l.Description,l.ShowQuantity,l.ShowPrices,l.CurrencyCode FROM Lists l JOIN WorkspaceItems w ON w.Id=l.ItemId" + (id.HasValue ? " WHERE l.ItemId=$id" : "") + " ORDER BY w.Title,w.Id;";
        if (id.HasValue) Add(command, "$id", id);
        var result = new List<ListDefinition>(); using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(new(Guid.Parse(reader.GetString(0)), WorkspaceItemType.List, reader.GetString(1), Utc(reader, 2)!.Value, Utc(reader, 3)!.Value, Utc(reader, 4), Utc(reader, 5)), reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8), Text(reader, 9)));
        return result;
    }
    private static async Task<IReadOnlyList<ListItem>> Items(SqliteConnection connection, SqliteTransaction transaction, Guid id, ListItemQuery? query, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Id,ListId,Title,IsChecked,Quantity,UnitPrice,Note,Link,SortOrder,CreatedAtUtc,UpdatedAtUtc FROM ListItems WHERE ListId=$id";
        Add(command, "$id", id);
        if (query is not null)
        {
            if (query.Filter != ListFilter.All) { command.CommandText += " AND IsChecked=$checked"; Add(command, "$checked", query.Filter == ListFilter.Checked); }
            if (!string.IsNullOrWhiteSpace(query.Title))
            {
                connection.CreateFunction<string, string, bool>("LIST_CONTAINS", (value, search) => value.Contains(search, StringComparison.OrdinalIgnoreCase));
                command.CommandText += " AND LIST_CONTAINS(Title,$search)"; Add(command, "$search", query.Title.Trim());
            }
        }
        command.CommandText += " ORDER BY SortOrder,Id";
        if (query is not null) { command.CommandText += " LIMIT $limit OFFSET $offset"; Add(command, "$limit", query.Limit); Add(command, "$offset", query.Offset); }
        var result = new List<ListItem>(); using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetBoolean(3), Number(reader, 4), Number(reader, 5), Text(reader, 6), Text(reader, 7), reader.GetInt32(8), Utc(reader, 9)!.Value, Utc(reader, 10)!.Value));
        return result;
    }
    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid? id, Func<ListState, T> change, CancellationToken token)
    {
        await using var connection = await SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
        using var transaction = connection.BeginTransaction(deferred: false);
        var state = new ListState(id is { } list ? await Read(connection, transaction, list, token) : null);
        var oldLists = state.Lists.ToDictionary(); var oldItems = state.Items.ToDictionary(); var result = change(state);
        foreach (var definition in state.Lists.Values)
        {
            var old = oldLists.GetValueOrDefault(definition.Item.Id); if (old == definition) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = old is null ? """
                INSERT INTO WorkspaceItems(Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc) VALUES($id,6,$title,$created,$updated);
                INSERT INTO Lists VALUES($id,$description,$quantity,$prices,$currency);
                """ : """
                UPDATE WorkspaceItems SET Title=$title,UpdatedAtUtc=$updated,ArchivedAtUtc=$archived,DeletedAtUtc=$deleted WHERE Id=$id;
                UPDATE Lists SET Description=$description,ShowQuantity=$quantity,ShowPrices=$prices,CurrencyCode=$currency WHERE ItemId=$id;
                """;
            Add(command, "$id", definition.Item.Id); Add(command, "$title", definition.Item.Title); Add(command, "$created", definition.Item.CreatedAtUtc); Add(command, "$updated", definition.Item.UpdatedAtUtc);
            Add(command, "$archived", definition.Item.ArchivedAtUtc); Add(command, "$deleted", definition.Item.DeletedAtUtc); Add(command, "$description", definition.Description);
            Add(command, "$quantity", definition.ShowQuantity); Add(command, "$prices", definition.ShowPrices); Add(command, "$currency", definition.CurrencyCode);
            await command.ExecuteNonQueryAsync(token);
        }
        foreach (var removed in oldItems.Keys.Except(state.Items.Keys))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM ListItems WHERE Id=$id;"; Add(command, "$id", removed); await command.ExecuteNonQueryAsync(token);
        }
        // Move changed positions to a disjoint positive range before final writes; the unique index stays valid throughout.
        var reordered = state.Items.Values.Where(i => oldItems.TryGetValue(i.Id, out var old) && i.SortOrder != old.SortOrder).ToArray();
        var temporaryOrder = (long)oldItems.Values.Select(i => i.SortOrder).DefaultIfEmpty(-1).Max() + state.Items.Count + 1;
        foreach (var item in reordered)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE ListItems SET SortOrder=$order WHERE Id=$id;";
            Add(command, "$id", item.Id); Add(command, "$order", temporaryOrder++); await command.ExecuteNonQueryAsync(token);
        }
        foreach (var item in state.Items.Values)
        {
            if (oldItems.GetValueOrDefault(item.Id) == item) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ListItems VALUES($id,$list,$title,$checked,$quantity,$price,$note,$link,$order,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET Title=$title,IsChecked=$checked,Quantity=$quantity,UnitPrice=$price,Note=$note,Link=$link,SortOrder=$order,UpdatedAtUtc=$updated;
                """;
            Add(command, "$id", item.Id); Add(command, "$list", item.ListId); Add(command, "$title", item.Title); Add(command, "$checked", item.IsChecked);
            Add(command, "$quantity", item.Quantity); Add(command, "$price", item.UnitPrice); Add(command, "$note", item.Note); Add(command, "$link", item.Link);
            Add(command, "$order", item.SortOrder); Add(command, "$created", item.CreatedAtUtc); Add(command, "$updated", item.UpdatedAtUtc); await command.ExecuteNonQueryAsync(token);
        }
        foreach (var removed in oldLists.Keys.Except(state.Lists.Keys))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM WorkspaceItems WHERE Id=$id AND ItemType=6 AND DeletedAtUtc IS NOT NULL;"; Add(command, "$id", removed);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new ListValidationException("Move the List to Trash before permanently deleting it.");
        }
        await transaction.CommitAsync(token); return result;
    }
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static decimal? Number(SqliteDataReader reader, int index) => Text(reader, index) is { } text ? ExactValueText.Restore(text) : null;
    private static DateTimeOffset? Utc(SqliteDataReader reader, int index) => Text(reader, index) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch { null => DBNull.Value, Guid guid => guid.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), decimal number => ExactValueText.Store(number), _ => value });
}
