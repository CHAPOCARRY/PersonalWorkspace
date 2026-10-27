using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteActivityHistoryRepository : IActivityHistoryRepository
{
    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] values) => SqliteAuditWriter.Command(c, tx, sql, values);
    private static async Task<Guid> Epoch(SqliteConnection c, SqliteTransaction tx, CancellationToken token)
    { using var cmd = Cmd(c, tx, "SELECT Epoch FROM AuditHistoryState WHERE Id=1"); return Guid.Parse((string)(await cmd.ExecuteScalarAsync(token))!); }
    private static Guid? Id(SqliteDataReader r, int column) => r.IsDBNull(column) ? null : Guid.Parse(r.GetString(column));
    private static DateOnly? Date(SqliteDataReader r, int column) => r.IsDBNull(column) ? null : DateOnly.ParseExact(r.GetString(column), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static AuditEvent Event(SqliteDataReader r, Guid profile) => new(r.GetInt64(0), Id(r, 1)!.Value, Id(r, 2)!.Value, profile, new(r.GetInt64(3), TimeSpan.Zero),
        (AuditAction)r.GetInt32(4), new((AuditEntityType)r.GetInt32(5), Id(r, 6)!.Value, r.GetString(7), Id(r, 8), Date(r, 9), TitleTruncated: r.GetBoolean(10)), r.GetInt32(11), r.GetInt32(12));
    private const string Columns = "e.Sequence,e.Id,e.OperationId,e.OccurredAtUtcTicks,e.Action,e.EntityType,e.EntityId,e.Title,e.ParentId,e.ContextDate,e.TitleTruncated,e.ChangeCount,e.FormatVersion";
    public async Task<AuditPage> ReadAsync(WorkspaceContext w, AuditQuery query, CancellationToken token)
    {
        if (query.Limit is < 1 or > 100 || query.TitlePrefix.Length > 256 || query.From > query.Until || query.Type is { } type && !Enum.IsDefined(type) || query.Action is { } action && !Enum.IsDefined(action))
            throw new ActivityHistoryException("Choose a valid history filter and a page size from 1 to 100.");
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token); using var tx = c.BeginTransaction();
        var epoch = await Epoch(c, tx, token);
        if (query.Cursor is { } stale && stale.Epoch != epoch) throw new ActivityHistoryException("History was cleared. Refresh the timeline.");
        using var max = Cmd(c, tx, "SELECT coalesce(max(Sequence),0) FROM AuditEvents");
        var high = query.Cursor?.HighWater ?? (long)(await max.ExecuteScalarAsync(token))!;
        var where = new List<string> { "e.Sequence<=$high" }; var args = new List<(string, object?)> { ("high", high), ("limit", query.Limit + 1) };
        if (query.From is { } from) { where.Add("e.OccurredAtUtcTicks >= $from"); args.Add(("from", from.UtcTicks)); }
        if (query.Until is { } until) { where.Add("e.OccurredAtUtcTicks < $until"); args.Add(("until", until.UtcTicks)); }
        if (query.Type is { } kind) { where.Add("e.EntityType=$type"); args.Add(("type", (int)kind)); }
        if (query.Action is { } op) { where.Add("e.Action=$action"); args.Add(("action", (int)op)); }
        if (query.TargetId is { } target) { where.Add("EXISTS(SELECT 1 FROM AuditEventTargets t WHERE t.EntityId=$target AND t.EventSequence=e.Sequence)"); args.Add(("target", target)); }
        if (query.TitlePrefix.Length > 0)
        { where.Add("e.TitleKey >= $prefix AND e.TitleKey < $end"); var prefix = SqliteAuditWriter.TitleKey(query.TitlePrefix); args.Add(("prefix", prefix)); args.Add(("end", prefix + '\uffff')); }
        if (query.Cursor is { } cursor) { where.Add("(e.OccurredAtUtcTicks < $time OR (e.OccurredAtUtcTicks=$time AND e.Sequence<$sequence))"); args.Add(("time", cursor.Ticks)); args.Add(("sequence", cursor.Sequence)); }
        using var cmd = Cmd(c, tx, $"SELECT {Columns} FROM AuditEvents e WHERE {string.Join(" AND ", where)} ORDER BY e.OccurredAtUtcTicks DESC,e.Sequence DESC LIMIT $limit", args.ToArray());
        using var r = await cmd.ExecuteReaderAsync(token); var rows = new List<AuditEvent>();
        while (await r.ReadAsync(token)) rows.Add(Event(r, w.ProfileId));
        var more = rows.Count > query.Limit; if (more) rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault(); return new(rows, more && last is not null ? new(epoch, high, last.OccurredAtUtc.UtcTicks, last.Sequence) : null, epoch);
    }
    public async Task<AuditDetails> DetailsAsync(WorkspaceContext w, long sequence, int afterChange, CancellationToken token)
    {
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token); using var tx = c.BeginTransaction();
        AuditEvent e;
        using (var cmd = Cmd(c, tx, $"SELECT {Columns} FROM AuditEvents e WHERE Sequence=$event", ("event", sequence)))
        using (var r = await cmd.ExecuteReaderAsync(token)) { if (!await r.ReadAsync(token)) throw new ActivityHistoryException("This history event is no longer available."); e = Event(r, w.ProfileId); }
        var targets = new Dictionary<int, AuditTarget>();
        using (var cmd = Cmd(c, tx, """
            SELECT Ordinal,EntityType,EntityId,Title,ParentId,ContextDate,Role,TitleTruncated FROM AuditEventTargets
            WHERE EventSequence=$event AND (Ordinal IN
                (SELECT TargetOrdinal FROM AuditEventChanges WHERE EventSequence=$event AND Ordinal>$after ORDER BY Ordinal LIMIT 101)
                OR Ordinal IN (SELECT Ordinal FROM AuditEventTargets WHERE EventSequence=$event AND Role IN('Primary','Parent','Source','Related') ORDER BY Ordinal LIMIT 100))
            ORDER BY Ordinal
            """, ("event", sequence), ("after", afterChange)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) targets.Add(r.GetInt32(0), new((AuditEntityType)r.GetInt32(1), Id(r, 2)!.Value, r.GetString(3), Id(r, 4), Date(r, 5), r.GetString(6), r.GetBoolean(7)));
        var changes = new List<AuditChange>();
        using (var cmd = Cmd(c, tx, "SELECT Ordinal,TargetOrdinal,Field,BeforeKind,BeforeState,BeforeValue,BeforeLength,AfterKind,AfterState,AfterValue,AfterLength FROM AuditEventChanges WHERE EventSequence=$event AND Ordinal>$after ORDER BY Ordinal LIMIT 101", ("event", sequence), ("after", afterChange)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) changes.Add(new(r.GetInt32(0), targets[r.GetInt32(1)], r.GetString(2), Value(r, 3), Value(r, 7)));
        return new(e, targets.Values.ToArray(), changes.Take(100).ToArray(), changes.Count > 100);
    }
    private static AuditValue Value(SqliteDataReader r, int at) => new((AuditValueKind)r.GetInt32(at), (AuditValueState)r.GetInt32(at + 1), r.IsDBNull(at + 2) ? null : r.GetString(at + 2), r.IsDBNull(at + 3) ? null : r.GetInt32(at + 3));
    public async Task ClearAsync(WorkspaceContext w, Guid expectedEpoch, CancellationToken token)
    {
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token); using var tx = c.BeginTransaction(deferred: false);
        if (await Epoch(c, tx, token) != expectedEpoch) throw new ActivityHistoryException("History changed. Refresh before clearing it.");
        using var cmd = Cmd(c, tx, "DELETE FROM AuditEvents; UPDATE AuditHistoryState SET Epoch=$epoch WHERE Id=1", ("epoch", Guid.NewGuid()));
        await cmd.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
    }
    public async Task<NavigationRoute> ResolveAsync(WorkspaceContext w, AuditTarget target, CancellationToken token)
    {
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token); using var tx = c.BeginTransaction();
        var id = target.Id; Guid owner = id; Guid? child = null; DateOnly? date = target.Date;
        var sql = target.Type switch
        {
            AuditEntityType.Occurrence => "SELECT TaskId,OccurrenceDate FROM TaskOccurrences WHERE Id=$id AND IsSuppressed=0",
            AuditEntityType.TrackerEntry => "SELECT TrackerId,PeriodDate FROM TrackerEntries WHERE Id=$id",
            AuditEntityType.JournalEntry => "SELECT JournalId,EntryDate FROM JournalEntries WHERE Id=$id",
            AuditEntityType.JournalField => "SELECT JournalId,NULL FROM JournalFields WHERE Id=$id",
            AuditEntityType.JournalOption => "SELECT f.JournalId,NULL FROM JournalFieldOptions o JOIN JournalFields f ON f.Id=o.FieldId WHERE o.Id=$id",
            AuditEntityType.ListItem => "SELECT ListId,NULL FROM ListItems WHERE Id=$id",
            AuditEntityType.CanvasItem => "SELECT PageId,NULL FROM PageCanvasItems WHERE Id=$id",
            AuditEntityType.Widget => "SELECT c.PageId,NULL FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId WHERE w.Id=$id",
            AuditEntityType.Decoration => "SELECT PageId,NULL FROM CanvasDecorations WHERE Id=$id",
            AuditEntityType.RichText => "SELECT OwnerItemId,NULL FROM RichTextReferences WHERE DocumentId=$id AND OwnerItemId=$parent LIMIT 1",
            AuditEntityType.AssetReference => "SELECT OwnerItemId,NULL FROM AssetReferences WHERE Id=$id",
            _ => null
        };
        if (target.Type == AuditEntityType.Template)
        { await Require(c, tx, "SELECT 1 FROM Templates WHERE Id=$id", id, token); return new("Templates", id.ToString("D"), new(w.ProfileId)); }
        if (target.Type is AuditEntityType.Tag or AuditEntityType.Space)
        { await Require(c, tx, target.Type == AuditEntityType.Tag ? "SELECT 1 FROM Tags WHERE Id=$id" : "SELECT 1 FROM Spaces WHERE Id=$id", id, token); return new("Organization", id.ToString("D"), new(w.ProfileId)); }
        if (sql is not null)
        {
            using var cmd = Cmd(c, tx, sql, ("id", id), ("parent", target.ParentId)); using var r = await cmd.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw Unavailable(); owner = Id(r, 0)!.Value;
            if (target.ParentId is { } parent && parent != owner) throw Unavailable();
            if (!r.IsDBNull(1)) date = Date(r, 1);
            if (target.Type == AuditEntityType.JournalEntry && date != target.Date) throw Unavailable();
            if (target.Type is AuditEntityType.ListItem or AuditEntityType.Widget or AuditEntityType.TrackerEntry) child = id;
        }
        using var item = Cmd(c, tx, "SELECT ItemType,ArchivedAtUtc,DeletedAtUtc FROM WorkspaceItems WHERE Id=$id", ("id", owner));
        using var row = await item.ExecuteReaderAsync(token); if (!await row.ReadAsync(token)) throw Unavailable();
        if (!row.IsDBNull(2)) throw new ActivityHistoryException("This target is in Trash. Restore it before opening it.");
        var kind = (WorkspaceItemType)row.GetInt32(0);
        if (sql is null && (int)target.Type != (int)kind) throw Unavailable();
        var destination = kind switch { WorkspaceItemType.Task => "Task", WorkspaceItemType.Event => "Event", WorkspaceItemType.Tracker => "Tracker", WorkspaceItemType.Journal => "Journal", WorkspaceItemType.List => "Lists", WorkspaceItemType.Page => "Pages", _ => throw Unavailable() };
        if (target.Type == AuditEntityType.Occurrence) return new("Occurrence", id.ToString("D"), new(w.ProfileId));
        return new(destination, destination == "Journal" ? $"{owner:D}|{(date ?? DateOnly.FromDateTime(DateTime.Now)):yyyy-MM-dd}" : owner.ToString("D"), new(w.ProfileId, child, !row.IsDBNull(1), target.Type == AuditEntityType.TrackerEntry ? date : null));
    }
    private static ActivityHistoryException Unavailable() => new("This historical target was deleted or is no longer available. Its history has been preserved.");
    private static async Task Require(SqliteConnection c, SqliteTransaction tx, string sql, Guid id, CancellationToken token)
    { using var cmd = Cmd(c, tx, sql, ("id", id)); if (await cmd.ExecuteScalarAsync(token) is null) throw Unavailable(); }
}
