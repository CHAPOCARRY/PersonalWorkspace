using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteEventRepository : IEventRepository
{
    private const string SelectSql = """
        SELECT w.Id, w.Title, w.CreatedAtUtc, w.UpdatedAtUtc, w.ArchivedAtUtc, w.DeletedAtUtc,
               e.AllDay, e.StartDate, e.StartTime, e.EndDate, e.EndTime
        FROM Events e JOIN WorkspaceItems w ON w.Id = e.ItemId
        """;
    public Task<IReadOnlyList<EventItem>> GetRangeAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        QueryAsync(workspace, "w.ArchivedAtUtc IS NULL AND w.DeletedAtUtc IS NULL AND e.StartDate <= $through AND e.EndDate >= $from " +
            "AND (e.AllDay = 1 OR e.EndDate > $from OR e.EndTime > '00:00:00.0000000')", from, through, cancellationToken);
    public Task<IReadOnlyList<EventItem>> GetCollectionAsync(WorkspaceContext workspace, EventCollection collection, CancellationToken cancellationToken) =>
        QueryAsync(workspace, collection switch
        {
            EventCollection.Archived => "w.ArchivedAtUtc IS NOT NULL AND w.DeletedAtUtc IS NULL",
            EventCollection.Trash => "w.DeletedAtUtc IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(collection))
        }, null, null, cancellationToken);
    private static async Task<IReadOnlyList<EventItem>> QueryAsync(WorkspaceContext workspace, string predicate, DateOnly? from, DateOnly? through, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE w.ItemType = 2 AND " + predicate + " ORDER BY e.StartDate, e.AllDay DESC, e.StartTime, w.Id;";
        if (from is { } start)
        {
            command.Parameters.AddWithValue("$from", Date(start));
            command.Parameters.AddWithValue("$through", Date(through!.Value));
        }
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<EventItem>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }
    public async Task<EventItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        return await FindAsync(connection, null, id, cancellationToken);
    }
    public async Task CreateAsync(WorkspaceContext workspace, EventItem item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WorkspaceItems (Id, ItemType, Title, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, 2, $title, $created, $updated);
            INSERT INTO Events (ItemId, AllDay, StartDate, StartTime, EndDate, EndTime) VALUES ($id, $allDay, $startDate, $startTime, $endDate, $endTime);
            """;
        Bind(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<EventItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<EventItem, EventItem> update, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = await FindAsync(connection, transaction, id, cancellationToken) ?? throw new EventValidationException("This event is no longer available.");
        var item = update(previous);
        if (item.Item.Id != id || item.Item.ItemType != WorkspaceItemType.Event) throw new InvalidOperationException("An event update cannot change its identity.");
        if (item != previous)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                UPDATE WorkspaceItems SET Title = $title, UpdatedAtUtc = $updated, ArchivedAtUtc = $archived, DeletedAtUtc = $deleted WHERE Id = $id;
                UPDATE Events SET AllDay = $allDay, StartDate = $startDate, StartTime = $startTime, EndDate = $endDate, EndTime = $endTime WHERE ItemId = $id;
                """;
            Bind(command, item);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return item;
    }
    public async Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await FindAsync(connection, transaction, id, cancellationToken) ?? throw new EventValidationException("This event is no longer available.");
        if (item.Item.DeletedAtUtc is null) throw new EventValidationException("Move the event to Trash before permanently deleting it.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM WorkspaceItems WHERE Id = $id AND ItemType = 2;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    private static async Task<EventItem?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE w.Id = $id AND w.ItemType = 2;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }
    private static EventItem Read(SqliteDataReader reader) => new(
        new(Guid.Parse(reader.GetString(0)), WorkspaceItemType.Event, reader.GetString(1), Utc(reader, 2)!.Value, Utc(reader, 3)!.Value, Utc(reader, 4), Utc(reader, 5)),
        reader.GetBoolean(6), DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture), ReadTime(reader, 8),
        DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture), ReadTime(reader, 10));
    private static DateTimeOffset? Utc(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private static TimeOnly? ReadTime(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : TimeOnly.ParseExact(reader.GetString(index), "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static void Bind(SqliteCommand command, EventItem item)
    {
        command.Parameters.AddWithValue("$id", item.Item.Id.ToString("D")); command.Parameters.AddWithValue("$title", item.Item.Title);
        command.Parameters.AddWithValue("$created", item.Item.CreatedAtUtc.ToString("O")); command.Parameters.AddWithValue("$updated", item.Item.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$archived", (object?)item.Item.ArchivedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", (object?)item.Item.DeletedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$allDay", item.AllDay); command.Parameters.AddWithValue("$startDate", Date(item.StartDate)); command.Parameters.AddWithValue("$endDate", Date(item.EndDate));
        command.Parameters.AddWithValue("$startTime", (object?)item.StartTime?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$endTime", (object?)item.EndTime?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) ?? DBNull.Value);
    }
    private static Task<SqliteConnection> OpenAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, cancellationToken);
}
