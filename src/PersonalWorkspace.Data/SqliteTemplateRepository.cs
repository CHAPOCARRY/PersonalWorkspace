using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteTemplateRepository : ITemplateRepository
{
    private static Task<SqliteConnection> Open(WorkspaceContext w, CancellationToken token) => SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, Store(values[i]));
        return cmd;
    }
    private static object Store(object? value) => value switch
    {
        null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        decimal number => ExactValueText.Store(number), Enum e => Convert.ToInt32(e), _ => value
    };
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static decimal? Number(SqliteDataReader r, int i) => Text(r, i) is { } text ? ExactValueText.Restore(text) : null;
    private static long? Integer(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static TemplateMetadata Metadata(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), (TemplateType)r.GetInt32(1), r.GetString(2), Text(r, 3), r.GetInt32(4), DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture));
    public async Task<IReadOnlyList<TemplateMetadata>> CatalogAsync(WorkspaceContext workspace, TemplateType? type, CancellationToken token)
    {
        await using var c = await Open(workspace, token);
        using var cmd = Command(c, null, "SELECT Id,TemplateType,Name,Description,FormatVersion,CreatedAtUtc,UpdatedAtUtc FROM Templates WHERE $p0 IS NULL OR TemplateType=$p0 ORDER BY Name COLLATE WORKSPACE_NAME,Id", type);
        var rows = new List<TemplateMetadata>(); using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) rows.Add(Metadata(r)); return rows;
    }
    public async Task<IReadOnlyList<TemplateSource>> SourcesAsync(WorkspaceContext workspace, TemplateType type, CancellationToken token)
    {
        var itemType = type switch { TemplateType.Task => WorkspaceItemType.Task, TemplateType.List => WorkspaceItemType.List, TemplateType.Journal => WorkspaceItemType.Journal, _ => throw new TemplateValidationException("Choose a supported template type.") };
        await using var c = await Open(workspace, token);
        using var cmd = Command(c, null, "SELECT Id,Title FROM WorkspaceItems WHERE ItemType=$p0 AND ArchivedAtUtc IS NULL AND DeletedAtUtc IS NULL ORDER BY Title COLLATE WORKSPACE_NAME,Id", itemType);
        var rows = new List<TemplateSource>(); using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) rows.Add(new(new(workspace.ProfileId, Guid.Parse(r.GetString(0))), type, r.GetString(1))); return rows;
    }
    public async Task<TemplateSnapshot> ReadAsync(WorkspaceContext workspace, Guid id, CancellationToken token)
    {
        await using var c = await Open(workspace, token); using var tx = c.BeginTransaction(deferred: true);
        TemplateMetadata metadata;
        using (var cmd = Command(c, tx, "SELECT Id,TemplateType,Name,Description,FormatVersion,CreatedAtUtc,UpdatedAtUtc FROM Templates WHERE Id=$p0", id))
        using (var r = await cmd.ExecuteReaderAsync(token))
        { if (!await r.ReadAsync(token)) throw new TemplateValidationException("This template is no longer available in this Profile."); metadata = Metadata(r); }
        if (metadata.FormatVersion != 1) throw new TemplateValidationException("This template format version is not supported.");
        TemplateBlueprint blueprint;
        if (metadata.Type == TemplateType.Task)
        {
            using var cmd = Command(c, tx, "SELECT Title,Description,Priority,ValueType,Target,TargetSeconds,CurrencyCode,Unit FROM TaskTemplateDefinitions WHERE TemplateId=$p0", id);
            using var r = await cmd.ExecuteReaderAsync(token); await Required(r, token);
            var type = (TaskValueType)r.GetInt32(3);
            blueprint = new TaskBlueprint(new(r.GetString(0), r.GetString(1), Priority: (TaskPriority)r.GetInt32(2), Value: type == TaskValueType.Checkbox ? null : new(type, type == TaskValueType.Duration ? r.GetInt64(5) : Number(r, 4)!.Value, CurrencyCode: Text(r, 6), Unit: Text(r, 7))));
        }
        else if (metadata.Type == TemplateType.List)
        {
            ListDraft draft;
            using (var cmd = Command(c, tx, "SELECT Title,Description,ShowQuantity,ShowPrices,CurrencyCode FROM ListTemplateDefinitions WHERE TemplateId=$p0", id))
            using (var r = await cmd.ExecuteReaderAsync(token)) { await Required(r, token); draft = new(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), Text(r, 4)); }
            var items = new List<ListItemDraft>();
            using (var cmd = Command(c, tx, "SELECT Title,Quantity,UnitPrice,Note,Link FROM ListTemplateItems WHERE TemplateId=$p0 ORDER BY SortOrder,Id", id))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) items.Add(new(r.GetString(0), Number(r, 1), Number(r, 2), Text(r, 3), Text(r, 4)));
            blueprint = new ListBlueprint(draft, items);
        }
        else if (metadata.Type == TemplateType.Journal)
        {
            string title, description;
            using (var cmd = Command(c, tx, "SELECT Title,Description FROM JournalTemplateDefinitions WHERE TemplateId=$p0", id))
            using (var r = await cmd.ExecuteReaderAsync(token)) { await Required(r, token); title = r.GetString(0); description = r.GetString(1); }
            var fields = new List<(Guid Id, JournalFieldDraft Draft)>(); var options = new Dictionary<Guid, List<JournalOption>>();
            using (var cmd = Command(c, tx, "SELECT Id,Name,FieldType,CurrencyCode,ScaleMin,ScaleMax FROM JournalTemplateFields WHERE TemplateId=$p0 ORDER BY SortOrder,Id", id))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) fields.Add((Guid.Parse(r.GetString(0)), new(r.GetString(1), (JournalFieldType)r.GetInt32(2), Text(r, 3), Integer(r, 4), Integer(r, 5))));
            using (var cmd = Command(c, tx, "SELECT o.Id,o.FieldId,o.Label,o.SortOrder FROM JournalTemplateOptions o JOIN JournalTemplateFields f ON f.Id=o.FieldId WHERE f.TemplateId=$p0 ORDER BY o.SortOrder,o.Id", id))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
            { var field = Guid.Parse(r.GetString(1)); if (!options.TryGetValue(field, out var list)) options[field] = list = []; list.Add(new(Guid.Parse(r.GetString(0)), r.GetString(2), r.GetInt32(3))); }
            blueprint = new JournalBlueprint(title, description, fields.Select(f => f.Draft with { Options = options.GetValueOrDefault(f.Id)?.ToArray() ?? [] }).ToArray());
        }
        else throw new TemplateValidationException("This template type is not supported.");
        await tx.CommitAsync(token); return new(metadata, blueprint);
    }
    private static async Task Required(SqliteDataReader r, CancellationToken token)
    { if (!await r.ReadAsync(token)) throw new TemplateValidationException("This template definition is incomplete."); }
    private static async Task Execute(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params object?[] values)
    { using var cmd = Command(c, tx, sql, values); await cmd.ExecuteNonQueryAsync(token); }
    public async Task SaveAsync(WorkspaceContext workspace, TemplateSnapshot snapshot, bool create, CancellationToken token)
    {
        await using var c = await Open(workspace, token); using var tx = c.BeginTransaction(deferred: false); var m = snapshot.Metadata;
        if (create) await Execute(c, tx, "INSERT INTO Templates VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", token, m.Id, m.Type, m.Name, m.Description, m.FormatVersion, m.CreatedAtUtc, m.UpdatedAtUtc);
        else
        {
            using var cmd = Command(c, tx, "UPDATE Templates SET UpdatedAtUtc=$p0 WHERE Id=$p1 AND TemplateType=$p2 AND FormatVersion=$p3", m.UpdatedAtUtc, m.Id, m.Type, m.FormatVersion);
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new TemplateValidationException("This template changed or is no longer available.");
            await Execute(c, tx, "DELETE FROM TaskTemplateDefinitions WHERE TemplateId=$p0; DELETE FROM ListTemplateDefinitions WHERE TemplateId=$p0; DELETE FROM JournalTemplateDefinitions WHERE TemplateId=$p0;", token, m.Id);
        }
        switch (snapshot.Blueprint)
        {
            case TaskBlueprint task:
                var d = task.Definition; var v = d.Value;
                await Execute(c, tx, "INSERT INTO TaskTemplateDefinitions(TemplateId,Title,Description,Priority,ValueType,Target,TargetSeconds,CurrencyCode,Unit) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", token,
                    m.Id, d.Title, d.Description, d.Priority, v?.Type ?? TaskValueType.Checkbox, v?.Type == TaskValueType.Duration ? null : v?.Target, v?.Type == TaskValueType.Duration ? (long)v.Target : null, v?.CurrencyCode, v?.Unit); break;
            case ListBlueprint list:
                var l = list.Definition;
                await Execute(c, tx, "INSERT INTO ListTemplateDefinitions(TemplateId,Title,Description,ShowQuantity,ShowPrices,CurrencyCode) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", token, m.Id, l.Title, l.Description, l.ShowQuantity, l.ShowPrices, l.CurrencyCode);
                using (var cmd = Command(c, tx, "INSERT INTO ListTemplateItems VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)", new object?[8]))
                {
                    cmd.Prepare(); for (var i = 0; i < list.Items.Count; i++)
                    { var item = list.Items[i]; Set(cmd, Guid.NewGuid(), m.Id, item.Title, item.Quantity, item.UnitPrice, item.Note, item.Link, i); await cmd.ExecuteNonQueryAsync(token); }
                }
                break;
            case JournalBlueprint journal:
                await Execute(c, tx, "INSERT INTO JournalTemplateDefinitions(TemplateId,Title,Description) VALUES($p0,$p1,$p2)", token, m.Id, journal.Title, journal.Description);
                using (var fieldCmd = Command(c, tx, "INSERT INTO JournalTemplateFields VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)", new object?[8]))
                using (var optionCmd = Command(c, tx, "INSERT INTO JournalTemplateOptions VALUES($p0,$p1,$p2,$p3)", new object?[4]))
                {
                    fieldCmd.Prepare(); optionCmd.Prepare();
                    for (var i = 0; i < journal.Fields.Count; i++)
                    {
                        var f = journal.Fields[i]; var fieldId = Guid.NewGuid(); Set(fieldCmd, fieldId, m.Id, f.Name, f.Type, i, f.CurrencyCode, f.ScaleMin, f.ScaleMax); await fieldCmd.ExecuteNonQueryAsync(token);
                        foreach (var o in f.Options ?? []) { Set(optionCmd, Guid.NewGuid(), fieldId, o.Label, o.SortOrder); await optionCmd.ExecuteNonQueryAsync(token); }
                    }
                }
                break;
            default: throw new TemplateValidationException("This template type is not supported.");
        }
        await tx.CommitAsync(token);
    }
    private static void Set(SqliteCommand cmd, params object?[] values)
    { for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = Store(values[i]); }
    public async Task RenameAsync(WorkspaceContext workspace, Guid id, string name, string? description, DateTimeOffset now, CancellationToken token)
    {
        await using var c = await Open(workspace, token); using var tx = c.BeginTransaction(deferred: false);
        using var cmd = Command(c, tx, "UPDATE Templates SET Name=$p0,Description=$p1,UpdatedAtUtc=$p2 WHERE Id=$p3", name, description, now, id);
        if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new TemplateValidationException("This template is no longer available."); await tx.CommitAsync(token);
    }
    public async Task DeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken token)
    { await using var c = await Open(workspace, token); using var cmd = Command(c, null, "DELETE FROM Templates WHERE Id=$p0", id); await cmd.ExecuteNonQueryAsync(token); }
}
