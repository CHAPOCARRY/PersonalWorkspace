using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

internal static class SqliteAuditWriter
{
    private static readonly AuditValue Absent = new(AuditValueKind.Text, AuditValueState.Absent);
    internal static async Task Append(SqliteConnection c, SqliteTransaction tx, WorkspaceContext workspace,
        IReadOnlyList<AuditEntity> before, IReadOnlyList<AuditEntity> after, CancellationToken token, AuditAction? action = null)
    {
        var old = Merge(before); var current = Merge(after); var changes = new List<AuditChange>();
        foreach (var key in old.Keys.Union(current.Keys).OrderBy(k => k.Type).ThenBy(k => k.Id))
        {
            old.TryGetValue(key, out var a); current.TryGetValue(key, out var b); var target = (b ?? a)!.Target;
            if ((a ?? b)!.Fields.Count == 0 && (b ?? a)!.Fields.Count == 0) continue;
            if (a is null || b is null)
            {
                changes.Add(new(changes.Count, target, "Exists", new(AuditValueKind.Boolean, AuditValueState.Value, a is null ? "false" : "true"), new(AuditValueKind.Boolean, AuditValueState.Value, b is null ? "false" : "true")));
                // Creation/deletion is not a full-row snapshot. Identity and context survive.
                continue;
            }
            foreach (var field in a.Fields.Keys.Union(b.Fields.Keys).Order(StringComparer.Ordinal))
            {
                a.Fields.TryGetValue(field, out var x); b.Fields.TryGetValue(field, out var y);
                if (x?.Value == y?.Value && Equals(x?.Comparison, y?.Comparison)) continue;
                changes.Add(new(changes.Count, target, field, x?.Value ?? Absent, y?.Value ?? Absent));
            }
        }
        if (changes.Count == 0) return;
        var operation = workspace.Audit ?? AuditOperation.User(); // Direct repository calls remain audited.
        // Widget creation/removal includes its Canvas host. Present the user's Widget action.
        var primary = operation.Primary ?? changes.FirstOrDefault(ch => ch.Field == "Exists" && (int)ch.Target.Type <= (int)AuditEntityType.List)?.Target
            ?? changes.FirstOrDefault(ch => ch.Field == "Exists" && ch.Target.Type == AuditEntityType.Widget)?.Target
            ?? changes.FirstOrDefault(ch => ch.Field == "Exists")?.Target ?? changes[0].Target;
        if (primary.Type == AuditEntityType.Page && primary.Title == "Page")
        {
            using var title = Command(c, tx, "SELECT Title FROM WorkspaceItems WHERE Id=$id", ("id", primary.Id));
            if (await title.ExecuteScalarAsync(token) is string name) primary = primary with { Title = name };
        }
        var category = operation.Action ?? action ?? Category(changes, primary);
        var targets = changes.Select(ch => ch.Target).Prepend(primary with { Role = "Primary" }).ToList();
        var identities = changes.SelectMany(ch => new[] { ch.Before, ch.After }).Where(v => v.Kind == AuditValueKind.Guid)
            .Select(v => Guid.TryParse(v.Value, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id!.Value).Distinct();
        foreach (var id in identities)
        {
            var related = before.Concat(after).FirstOrDefault(e => e.Target.Id == id)?.Target;
            if (related is null)
            {
                using var lookup = Command(c, tx, "SELECT ItemType,Title FROM WorkspaceItems WHERE Id=$id", ("id", id));
                using var r = await lookup.ExecuteReaderAsync(token);
                if (await r.ReadAsync(token)) related = new((AuditEntityType)r.GetInt32(0), id, r.GetString(1));
            }
            if (related is not null) targets.Add(related with { Role = "Related" });
        }
        targets.AddRange(before.Concat(after).Where(e => e.Fields.Count == 0).Select(e => e.Target with { Role = "Related" }));
        if (operation.Source is { } source) targets.Add(source with { Role = "Source" });
        foreach (var target in targets.ToArray()) if (target.ParentId is { } parent)
        {
            var known = before.Concat(after).FirstOrDefault(e => e.Target.Id == parent)?.Target;
            if (known is null)
            {
                using var cmd = Command(c, tx, "SELECT ItemType,Title FROM WorkspaceItems WHERE Id=$id", ("id", parent));
                using var r = await cmd.ExecuteReaderAsync(token);
                if (await r.ReadAsync(token)) known = new((AuditEntityType)r.GetInt32(0), parent, r.GetString(1));
            }
            if (known is not null) targets.Add(known with { Role = "Parent" });
        }
        targets = targets.DistinctBy(t => (t.Type, t.Id)).Select(Bound).ToList(); primary = Bound(primary);
        using var insert = Command(c, tx, """
            INSERT INTO AuditEvents(Id,OperationId,OccurredAtUtcTicks,Action,EntityType,EntityId,Title,TitleKey,TitleTruncated,ParentId,ContextDate,ChangeCount,FormatVersion)
            VALUES($id,$operation,$time,$action,$type,$target,$title,$key,$truncated,$parent,$date,$count,1) RETURNING Sequence
            """, ("id", Guid.NewGuid()), ("operation", operation.Id), ("time", DateTimeOffset.UtcNow.UtcTicks), ("action", (int)category),
            ("type", (int)primary.Type), ("target", primary.Id), ("title", primary.Title), ("key", TitleKey(primary.Title)), ("truncated", primary.TitleTruncated),
            ("parent", primary.ParentId), ("date", primary.Date), ("count", changes.Count));
        var sequence = (long)(await insert.ExecuteScalarAsync(token))!;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i]; using var cmd = Command(c, tx, "INSERT INTO AuditEventTargets VALUES($event,$ordinal,$type,$id,$title,$truncated,$parent,$date,$role)",
                ("event", sequence), ("ordinal", i), ("type", (int)t.Type), ("id", t.Id), ("title", t.Title), ("truncated", t.TitleTruncated), ("parent", t.ParentId), ("date", t.Date), ("role", t.Role));
            await cmd.ExecuteNonQueryAsync(token);
        }
        using var change = Command(c, tx, "INSERT INTO AuditEventChanges VALUES($event,$ordinal,$target,$field,$bk,$bs,$bv,$bl,$ak,$as,$av,$al)",
            ("event", sequence), ("ordinal", 0), ("target", 0), ("field", ""), ("bk", 0), ("bs", 0), ("bv", null), ("bl", null), ("ak", 0), ("as", 0), ("av", null), ("al", null));
        change.Prepare();
        foreach (var ch in changes)
        {
            Set(change, "ordinal", ch.Ordinal); Set(change, "target", targets.FindIndex(t => t.Type == ch.Target.Type && t.Id == ch.Target.Id)); Set(change, "field", ch.Field);
            Set(change, "bk", (int)ch.Before.Kind); Set(change, "bs", (int)ch.Before.State); Set(change, "bv", ch.Before.Value); Set(change, "bl", ch.Before.Length);
            Set(change, "ak", (int)ch.After.Kind); Set(change, "as", (int)ch.After.State); Set(change, "av", ch.After.Value); Set(change, "al", ch.After.Length);
            await change.ExecuteNonQueryAsync(token);
        }
    }
    private static Dictionary<(AuditEntityType Type, Guid Id), AuditEntity> Merge(IEnumerable<AuditEntity> entities)
    {
        var result = new Dictionary<(AuditEntityType, Guid), AuditEntity>();
        foreach (var e in entities)
        {
            var key = (e.Target.Type, e.Target.Id);
            if (!result.TryGetValue(key, out var previous)) result[key] = e with { Fields = new(e.Fields) };
            else foreach (var field in e.Fields) previous.Fields[field.Key] = field.Value;
        }
        return result;
    }
    private static AuditAction Category(List<AuditChange> changes, AuditTarget primary)
    {
        var own = changes.Where(c => c.Target.Id == primary.Id).ToArray();
        foreach (var ch in own)
        {
            if (ch.Field == "Exists") return ch.After.Value == "true" ? AuditAction.Created : AuditAction.PermanentlyDeleted;
            if (ch.Field == "Trashed") return ch.After.Value == "true" ? AuditAction.Trashed : AuditAction.Restored;
            if (ch.Field == "Archived") return ch.After.Value == "true" ? AuditAction.Archived : AuditAction.Restored;
        }
        if (own.Any(c => c.Field == "Status" && c.After.Value == "Done" || c.Field == "Checked" && c.After.Value == "true")) return AuditAction.Completed;
        if (own.Any(c => c.Field == "Status" || c.Field == "Checked")) return AuditAction.StatusChanged;
        if (own.Any(c => c.Field.StartsWith("Dependency:", StringComparison.Ordinal))) return own.Any(c => c.After.State == AuditValueState.Absent) ? AuditAction.Unlinked : AuditAction.Linked;
        if (own.All(c => c.Field is "Order" or "ZOrder")) return AuditAction.Reordered;
        if (own.Any(c => c.Field is "Parent" or "ParentTaskId" or "ScheduledDate" or "X" or "Y" || c.Field == "Date" && primary.Type == AuditEntityType.Occurrence)) return AuditAction.Moved;
        return AuditAction.Updated;
    }
    internal static AuditTarget Bound(AuditTarget t) => t with { Title = t.Title[..Math.Min(256, t.Title.Length)], TitleTruncated = t.TitleTruncated || t.Title.Length > 256 };
    internal static string TitleKey(string text) => text.Normalize().ToUpperInvariant();
    internal static object Store(object? value) => value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), _ => value };
    internal static void Set(SqliteCommand cmd, string name, object? value) => cmd.Parameters["$" + name].Value = Store(value);
    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] values)
    { var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (name, value) in values) cmd.Parameters.AddWithValue("$" + name, Store(value)); return cmd; }
}
