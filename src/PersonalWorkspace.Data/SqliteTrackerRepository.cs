using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteTrackerRepository : ITrackerRepository
{
    private const string Select = """
        SELECT w.Id,w.Title,w.CreatedAtUtc,w.UpdatedAtUtc,w.ArchivedAtUtc,w.DeletedAtUtc,
            t.ValueType,t.Unit,t.CurrencyCode,t.ScaleMin,t.ScaleMax,t.Target,t.TargetInteger,
            t.Frequency,t.StartDate,t.EndDate,t.Interval,t.Weekdays,t.EntryMode,t.Aggregation,
            EXISTS(SELECT 1 FROM TrackerEntries e WHERE e.TrackerId=t.ItemId)
        FROM Trackers t JOIN WorkspaceItems w ON w.Id=t.ItemId AND w.ItemType=3
        """;
    public async Task<IReadOnlyList<TrackerItem>> GetAsync(WorkspaceContext workspace, TrackerCollection collection, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE " + (collection switch
        {
            TrackerCollection.Active => "w.ArchivedAtUtc IS NULL AND w.DeletedAtUtc IS NULL",
            TrackerCollection.Archived => "w.ArchivedAtUtc IS NOT NULL AND w.DeletedAtUtc IS NULL",
            TrackerCollection.Trash => "w.DeletedAtUtc IS NOT NULL", _ => throw new ArgumentOutOfRangeException(nameof(collection))
        }) + " ORDER BY w.Title COLLATE NOCASE,w.Id;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TrackerItem>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadItem(reader)); return result;
    }
    public async Task<TrackerItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        return await Find(connection, null, id, cancellationToken);
    }
    private static async Task<TrackerItem?> Find(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Select + " WHERE w.Id=$id;"; Add(command, "$id", id);
        using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadItem(reader) : null;
    }
    public async Task CreateAsync(WorkspaceContext workspace, TrackerItem item, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await WriteItem(connection, transaction, item, true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<TrackerEntry>> GetEntriesAsync(WorkspaceContext workspace, Guid id, TrackerEntryQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        return await Entries(connection, null, id, query, cancellationToken);
    }
    private static async Task<IReadOnlyList<TrackerEntry>> Entries(SqliteConnection connection, SqliteTransaction? transaction, Guid id, TrackerEntryQuery query, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; Add(command, "$id", id);
        var predicate = query.Period is { } period ? "PeriodDate=$period" : query.LatestPeriod
            ? "PeriodDate=(SELECT MAX(PeriodDate) FROM TrackerEntries WHERE TrackerId=$id)"
            : query.EntryId is not null ? "PeriodDate=(SELECT PeriodDate FROM TrackerEntries WHERE Id=$entry AND TrackerId=$id)"
            : query.Recent is not null ? "1=1" : query.From is not null && query.Through is not null ? "LocalDate BETWEEN $from AND $through"
            : throw new ArgumentException("A bounded entry query is required.", nameof(query));
        command.CommandText = "SELECT Id,TrackerId,PeriodDate,LocalDate,LocalTime,ValueType,Value,IntegerValue,Note,CreatedAtUtc,UpdatedAtUtc FROM TrackerEntries WHERE TrackerId=$id AND "
            + predicate + " ORDER BY LocalDate DESC,LocalTime DESC,CreatedAtUtc DESC,Id DESC" + (query.Recent is not null ? " LIMIT $limit;" : ";");
        Add(command, "$period", query.Period); Add(command, "$entry", query.EntryId);
        Add(command, "$from", query.From); Add(command, "$through", query.Through); Add(command, "$limit", query.Recent);
        using var reader = await command.ExecuteReaderAsync(token); var result = new List<TrackerEntry>();
        while (await reader.ReadAsync(token)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(reader.GetString(4), "HH:mm:ss.fffffff", CultureInfo.InvariantCulture), ReadValue(reader, (TrackerValueType)reader.GetInt32(5), 6, 7)!,
            Text(reader, 8), Utc(reader, 9)!.Value, Utc(reader, 10)!.Value));
        return result;
    }
    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid id, Func<TrackerItem, TrackerEntryQuery?> select,
        Func<TrackerState, T> change, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await Find(connection, transaction, id, cancellationToken) ?? throw new TrackerValidationException("This Tracker is no longer available.");
        var query = select(item);
        var entries = query is null ? [] : await Entries(connection, transaction, id, query, cancellationToken);
        var state = new TrackerState(item, entries); var result = change(state);
        if (state.Item.Item.Id != id || state.Item.Item.ItemType != WorkspaceItemType.Tracker) throw new InvalidOperationException("Tracker identity cannot change.");
        if (state.Item != item) await WriteItem(connection, transaction, state.Item, false, cancellationToken);
        var original = entries.ToDictionary(e => e.Id);
        foreach (var removed in original.Keys.Except(state.Entries.Keys))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM TrackerEntries WHERE Id=$entry AND TrackerId=$id;";
            Add(command, "$entry", removed); Add(command, "$id", id); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var entry in state.Entries.Values)
        {
            if (entry.TrackerId != id) throw new InvalidOperationException("Entry ownership cannot change.");
            if (original.GetValueOrDefault(entry.Id) != entry) await WriteEntry(connection, transaction, state.Item, entry, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken); return result;
    }
    public async Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await Open(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await Find(connection, transaction, id, cancellationToken) ?? throw new TrackerValidationException("This Tracker is no longer available.");
        if (item.Item.DeletedAtUtc is null) throw new TrackerValidationException("Move the Tracker to Trash before permanently deleting it.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM WorkspaceItems WHERE Id=$id AND ItemType=3;"; Add(command, "$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
    private static async Task WriteItem(SqliteConnection connection, SqliteTransaction transaction, TrackerItem item, bool create, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = create ? """
            INSERT INTO WorkspaceItems(Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc) VALUES($id,3,$title,$created,$updated);
            INSERT INTO Trackers(ItemId,ValueType,Unit,CurrencyCode,ScaleMin,ScaleMax,Target,TargetInteger,Frequency,StartDate,EndDate,Interval,Weekdays,EntryMode,Aggregation)
                VALUES($id,$type,$unit,$currency,$min,$max,$target,$integer,$frequency,$start,$end,$interval,$weekdays,$mode,$aggregation);
            """ : """
            UPDATE WorkspaceItems SET Title=$title,UpdatedAtUtc=$updated,ArchivedAtUtc=$archived,DeletedAtUtc=$deleted WHERE Id=$id;
            UPDATE Trackers SET ValueType=$type,Unit=$unit,CurrencyCode=$currency,ScaleMin=$min,ScaleMax=$max,Target=$target,TargetInteger=$integer,
                Frequency=$frequency,StartDate=$start,EndDate=$end,Interval=$interval,Weekdays=$weekdays,EntryMode=$mode,Aggregation=$aggregation WHERE ItemId=$id;
            """;
        Add(command, "$id", item.Item.Id); Add(command, "$title", item.Item.Title); Add(command, "$created", item.Item.CreatedAtUtc);
        Add(command, "$updated", item.Item.UpdatedAtUtc); Add(command, "$archived", item.Item.ArchivedAtUtc); Add(command, "$deleted", item.Item.DeletedAtUtc);
        Add(command, "$type", (int)item.Settings.Type); Add(command, "$unit", item.Settings.Unit); Add(command, "$currency", item.Settings.CurrencyCode);
        Add(command, "$min", item.Settings.ScaleMin); Add(command, "$max", item.Settings.ScaleMax);
        BindValue(command, item.Settings.Type, item.Target, "$target", "$integer");
        Add(command, "$frequency", (int)item.Schedule.Frequency); Add(command, "$start", item.Schedule.StartDate); Add(command, "$end", item.Schedule.EndDate);
        Add(command, "$interval", item.Schedule.Interval); Add(command, "$weekdays", item.Schedule.Weekdays);
        Add(command, "$mode", (int)item.EntryMode); Add(command, "$aggregation", (int)item.Aggregation);
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task WriteEntry(SqliteConnection connection, SqliteTransaction transaction, TrackerItem item, TrackerEntry entry, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TrackerEntries(Id,TrackerId,ValueType,EntryMode,PeriodDate,LocalDate,LocalTime,Value,IntegerValue,Note,CreatedAtUtc,UpdatedAtUtc)
                VALUES($entry,$id,$type,$mode,$period,$date,$time,$value,$integer,$note,$created,$updated)
            ON CONFLICT(Id) DO UPDATE SET Value=excluded.Value,IntegerValue=excluded.IntegerValue,Note=excluded.Note,UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        Add(command, "$entry", entry.Id); Add(command, "$id", entry.TrackerId); Add(command, "$type", (int)item.Settings.Type); Add(command, "$mode", (int)item.EntryMode);
        Add(command, "$period", entry.PeriodDate); Add(command, "$date", entry.LocalDate); Add(command, "$time", entry.LocalTime);
        BindValue(command, item.Settings.Type, entry.Value, "$value", "$integer"); Add(command, "$note", entry.Note);
        Add(command, "$created", entry.CreatedAtUtc); Add(command, "$updated", entry.UpdatedAtUtc); await command.ExecuteNonQueryAsync(token);
    }
    private static bool Integer(TrackerValueType type) => type is TrackerValueType.Integer or TrackerValueType.Duration or TrackerValueType.Boolean or TrackerValueType.Scale;
    private static void BindValue(SqliteCommand command, TrackerValueType type, TrackerValue? value, string text, string integer)
    {
        Add(command, text, !Integer(type) && value?.Number is { } number ? ExactValueText.Store(number) : null);
        Add(command, integer, value is null || !Integer(type) ? null : type == TrackerValueType.Boolean ? value.Boolean!.Value ? 1L : 0L : checked((long)value.Number!.Value));
    }
    private static TrackerValue? ReadValue(SqliteDataReader reader, TrackerValueType type, int text, int integer) =>
        reader.IsDBNull(text) && reader.IsDBNull(integer) ? null : type == TrackerValueType.Boolean ? new(Boolean: reader.GetInt64(integer) != 0)
        : new(Integer(type) ? reader.GetInt64(integer) : ExactValueText.Restore(reader.GetString(text)));
    private static TrackerItem ReadItem(SqliteDataReader reader)
    {
        var type = (TrackerValueType)reader.GetInt32(6);
        return new(new(Guid.Parse(reader.GetString(0)), WorkspaceItemType.Tracker, reader.GetString(1), Utc(reader, 2)!.Value, Utc(reader, 3)!.Value, Utc(reader, 4), Utc(reader, 5)),
            new(type, Text(reader, 7), Text(reader, 8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10)),
            new((TrackerFrequency)reader.GetInt32(13), Date(reader, 14), Date(reader, 15), reader.GetInt32(16), reader.GetInt32(17)),
            ReadValue(reader, type, 11, 12), (TrackerEntryMode)reader.GetInt32(18), (TrackerAggregation)reader.GetInt32(19), reader.GetBoolean(20));
    }
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset? Utc(SqliteDataReader reader, int index) => Text(reader, index) is { } value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : null;
    private static DateOnly? Date(SqliteDataReader reader, int index) => Text(reader, index) is { } value ? DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch
    {
        null => DBNull.Value, Guid id => id.ToString("D"), DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value
    });
    private static Task<SqliteConnection> Open(WorkspaceContext workspace, CancellationToken token) => SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
}
