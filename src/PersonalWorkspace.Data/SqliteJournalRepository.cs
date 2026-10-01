using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteJournalRepository : IJournalRepository
{
    public async Task<JournalSnapshot> ReadAsync(WorkspaceContext workspace, Guid? journalId, JournalCollection collection, DateOnly? from, DateOnly? through, CancellationToken token)
    {
        await using var connection = await Open(workspace, token); using var transaction = connection.BeginTransaction(deferred: true);
        var result = await Read(connection, transaction, journalId, collection, from, through, token); await transaction.CommitAsync(token); return result;
    }
    private static async Task<JournalSnapshot> Read(SqliteConnection connection, SqliteTransaction transaction, Guid? id, JournalCollection collection, DateOnly? from, DateOnly? through, CancellationToken token)
    {
        if ((from is null) != (through is null) || from > through) throw new JournalValidationException("Choose a valid bounded date range.");
        var filter = id is not null ? "w.Id=$id" : collection switch
        {
            JournalCollection.Active => "w.ArchivedAtUtc IS NULL AND w.DeletedAtUtc IS NULL",
            JournalCollection.Archived => "w.ArchivedAtUtc IS NOT NULL AND w.DeletedAtUtc IS NULL",
            JournalCollection.Trash => "w.DeletedAtUtc IS NOT NULL", _ => throw new JournalValidationException("Choose a Journal collection.")
        };
        var owners = "SELECT j.ItemId FROM Journals j JOIN WorkspaceItems w ON w.Id=j.ItemId WHERE " + filter;
        SqliteCommand Query(string sql) { var command = Command(connection, transaction, sql); Add(command, "$id", id); Add(command, "$from", from); Add(command, "$through", through); return command; }
        var definitions = new List<JournalDefinition>(); var fields = new List<JournalField>(); var options = new Dictionary<Guid, List<JournalOption>>();
        using (var command = Query("SELECT w.Id,w.ItemType,w.Title,w.CreatedAtUtc,w.UpdatedAtUtc,w.ArchivedAtUtc,w.DeletedAtUtc,j.Description FROM Journals j JOIN WorkspaceItems w ON w.Id=j.ItemId WHERE " + filter + " ORDER BY w.Title COLLATE NOCASE,w.Id;"))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) definitions.Add(new(Item(reader), reader.GetString(7), []));
        using (var command = Query($"SELECT f.Id,f.JournalId,f.Name,f.FieldType,f.SortOrder,f.CurrencyCode,f.ScaleMin,f.ScaleMax,f.HistoryLocked,EXISTS(SELECT 1 FROM JournalValues v WHERE v.FieldId=f.Id) FROM JournalFields f WHERE f.JournalId IN ({owners}) ORDER BY f.SortOrder,f.Id;"))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) fields.Add(new(Id(reader, 0), Id(reader, 1), reader.GetString(2), (JournalFieldType)reader.GetInt32(3), reader.GetInt32(4), Text(reader, 5), Integer(reader, 6), Integer(reader, 7), reader.GetBoolean(8), reader.GetBoolean(9)));
        using (var command = Query($"SELECT o.Id,o.FieldId,o.Label,o.SortOrder,EXISTS(SELECT 1 FROM JournalSelections s WHERE s.OptionId=o.Id) FROM JournalFieldOptions o JOIN JournalFields f ON f.Id=o.FieldId WHERE f.JournalId IN ({owners}) ORDER BY o.SortOrder,o.Id;"))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token))
        { var field = Id(reader, 1); if (!options.TryGetValue(field, out var list)) options[field] = list = []; list.Add(new(Id(reader, 0), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4))); }
        fields = fields.Select(f => f with { Options = options.GetValueOrDefault(f.Id)?.ToArray() ?? [] }).ToList();
        definitions = definitions.Select(d => d with { Fields = fields.Where(f => f.JournalId == d.Item.Id).ToArray() }).ToList();
        var entries = new List<JournalEntry>(); var values = new Dictionary<Guid, Dictionary<Guid, JournalValue>>();
        if (from is not null)
        {
            var dates = $"SELECT e.Id FROM JournalEntries e WHERE e.JournalId IN ({owners}) AND e.EntryDate BETWEEN $from AND $through";
            using (var command = Query($"SELECT e.Id,e.JournalId,e.EntryDate,e.CreatedAtUtc,e.UpdatedAtUtc FROM JournalEntries e WHERE e.Id IN ({dates}) ORDER BY e.EntryDate,e.Id;"))
            using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token))
            { var entryId = Id(reader, 0); var map = values[entryId] = []; entries.Add(new(entryId, Id(reader, 1), Date(reader.GetString(2)), Utc(reader, 3)!.Value, Utc(reader, 4)!.Value, map)); }
            using (var command = Query($"SELECT v.EntryId,v.FieldId,v.TextValue,v.DecimalValue,v.IntegerValue,v.DateValue,v.TaskId,v.TrackerId FROM JournalValues v WHERE v.EntryId IN ({dates});"))
            using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) values[Id(reader, 0)][Id(reader, 1)] = new(Text(reader, 2), Text(reader, 3) is { } number ? ExactValueText.Restore(number) : null, Integer(reader, 4), Text(reader, 5) is { } date ? Date(date) : null, Text(reader, 6) is { } task ? Guid.Parse(task) : Text(reader, 7) is { } tracker ? Guid.Parse(tracker) : null);
            var selections = new Dictionary<(Guid Entry, Guid Field), List<Guid>>();
            using (var command = Query($"SELECT s.EntryId,s.FieldId,s.OptionId FROM JournalSelections s JOIN JournalFieldOptions o ON o.Id=s.OptionId WHERE s.EntryId IN ({dates}) ORDER BY o.SortOrder,o.Id;"))
            using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token))
            { var key = (Id(reader, 0), Id(reader, 1)); if (!selections.TryGetValue(key, out var list)) selections[key] = list = []; list.Add(Id(reader, 2)); }
            foreach (var pair in selections) values[pair.Key.Entry][pair.Key.Field] = values[pair.Key.Entry][pair.Key.Field] with { OptionIds = pair.Value.ToArray() };
        }
        var references = new List<WorkspaceItem>();
        using (var command = Query("SELECT w.Id,w.ItemType,w.Title,w.CreatedAtUtc,w.UpdatedAtUtc,w.ArchivedAtUtc,w.DeletedAtUtc FROM WorkspaceItems w WHERE (w.ItemType=1 AND EXISTS(SELECT 1 FROM Tasks t WHERE t.ItemId=w.Id)) OR (w.ItemType=3 AND EXISTS(SELECT 1 FROM Trackers t WHERE t.ItemId=w.Id)) ORDER BY w.Title COLLATE NOCASE,w.Id;"))
        using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) references.Add(Item(reader));
        return new(definitions, entries, references);
    }
    public async Task CreateAsync(WorkspaceContext workspace, JournalDefinition definition, CancellationToken token)
    {
        await using var connection = await Open(workspace, token); using var transaction = connection.BeginTransaction(deferred: false);
        await WriteDefinition(connection, transaction, definition, null, token); await transaction.CommitAsync(token);
    }
    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid id, DateOnly? date, Func<JournalState, T> change, CancellationToken token)
    {
        await using var connection = await Open(workspace, token); using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await Read(connection, transaction, id, JournalCollection.Active, date, date, token);
        var original = snapshot.Journals.SingleOrDefault() ?? throw new JournalValidationException("This Journal is no longer available.");
        var before = snapshot.Entries.SingleOrDefault(); var state = new JournalState(original, before, snapshot.References); var result = change(state);
        if (state.Definition.Item.Id != id || state.Definition.Item.ItemType != WorkspaceItemType.Journal) throw new InvalidOperationException("Journal identity cannot change.");
        if (state.Definition != original) await WriteDefinition(connection, transaction, state.Definition, original, token);
        if (date is not null && state.Entry != before) await WriteEntry(connection, transaction, state.Definition, state.Entry, before, date.Value, token);
        await transaction.CommitAsync(token); return result;
    }
    public async Task DeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken token)
    {
        await using var connection = await Open(workspace, token); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(connection, transaction, "DELETE FROM WorkspaceItems WHERE Id=$id AND ItemType=4 AND DeletedAtUtc IS NOT NULL;"); Add(command, "$id", id);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new JournalValidationException("Move the Journal to Trash before permanently deleting it.");
        await transaction.CommitAsync(token);
    }
    private static async Task WriteDefinition(SqliteConnection connection, SqliteTransaction transaction, JournalDefinition item, JournalDefinition? before, CancellationToken token)
    {
        if (before is null || item.Item != before.Item || item.Description != before.Description)
        {
            using var command = Command(connection, transaction, before is null ? "INSERT INTO WorkspaceItems(Id,ItemType,Title,CreatedAtUtc,UpdatedAtUtc) VALUES($id,4,$title,$created,$updated); INSERT INTO Journals(ItemId,Description) VALUES($id,$description);"
                : "UPDATE WorkspaceItems SET Title=$title,UpdatedAtUtc=$updated,ArchivedAtUtc=$archived,DeletedAtUtc=$deleted WHERE Id=$id; UPDATE Journals SET Description=$description WHERE ItemId=$id;");
            Add(command, "$id", item.Item.Id); Add(command, "$title", item.Item.Title); Add(command, "$created", item.Item.CreatedAtUtc); Add(command, "$updated", item.Item.UpdatedAtUtc); Add(command, "$archived", item.Item.ArchivedAtUtc); Add(command, "$deleted", item.Item.DeletedAtUtc); Add(command, "$description", item.Description); await command.ExecuteNonQueryAsync(token);
        }
        var previous = (before?.Fields ?? []).ToDictionary(f => f.Id);
        foreach (var removed in previous.Keys.Except(item.Fields.Select(f => f.Id)))
        { using var command = Command(connection, transaction, "DELETE FROM JournalFields WHERE Id=$id;"); Add(command, "$id", removed); await command.ExecuteNonQueryAsync(token); }
        foreach (var field in item.Fields)
        {
            if (field.JournalId != item.Item.Id) throw new InvalidOperationException("Field ownership cannot change.");
            var old = previous.GetValueOrDefault(field.Id); if (field == old) continue;
            using (var command = Command(connection, transaction, """
                INSERT INTO JournalFields(Id,JournalId,Name,FieldType,SortOrder,CurrencyCode,ScaleMin,ScaleMax,HistoryLocked) VALUES($id,$journal,$name,$type,$order,$currency,$min,$max,$locked)
                ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,FieldType=excluded.FieldType,SortOrder=excluded.SortOrder,CurrencyCode=excluded.CurrencyCode,ScaleMin=excluded.ScaleMin,ScaleMax=excluded.ScaleMax,HistoryLocked=excluded.HistoryLocked;
                """))
            {
                Add(command, "$id", field.Id); Add(command, "$journal", field.JournalId); Add(command, "$name", field.Name); Add(command, "$type", (int)field.Type); Add(command, "$order", field.SortOrder); Add(command, "$currency", field.CurrencyCode); Add(command, "$min", field.ScaleMin); Add(command, "$max", field.ScaleMax); Add(command, "$locked", field.HistoryLocked); await command.ExecuteNonQueryAsync(token);
            }
            foreach (var removed in (old?.OrderedOptions ?? []).Select(o => o.Id).Except(field.OrderedOptions.Select(o => o.Id)))
            { using var command = Command(connection, transaction, "DELETE FROM JournalFieldOptions WHERE Id=$id AND FieldId=$field;"); Add(command, "$id", removed); Add(command, "$field", field.Id); await command.ExecuteNonQueryAsync(token); }
            foreach (var option in field.OrderedOptions)
            {
                using var command = Command(connection, transaction, "INSERT INTO JournalFieldOptions(Id,FieldId,Label,SortOrder) VALUES($id,$field,$label,$order) ON CONFLICT(Id) DO UPDATE SET Label=excluded.Label,SortOrder=excluded.SortOrder WHERE FieldId=excluded.FieldId;");
                Add(command, "$id", option.Id); Add(command, "$field", field.Id); Add(command, "$label", option.Label); Add(command, "$order", option.SortOrder);
                if (await command.ExecuteNonQueryAsync(token) != 1) throw new JournalValidationException("An option belongs to a different field.");
            }
        }
    }
    private static async Task WriteEntry(SqliteConnection connection, SqliteTransaction transaction, JournalDefinition definition, JournalEntry? entry, JournalEntry? before, DateOnly date, CancellationToken token)
    {
        if (entry is null)
        { using var delete = Command(connection, transaction, "DELETE FROM JournalEntries WHERE Id=$id;"); Add(delete, "$id", before?.Id); await delete.ExecuteNonQueryAsync(token); return; }
        if (entry.JournalId != definition.Item.Id || entry.Date != date || before is not null && before.Id != entry.Id) throw new InvalidOperationException("Entry identity cannot change.");
        using (var command = Command(connection, transaction, "INSERT INTO JournalEntries(Id,JournalId,EntryDate,CreatedAtUtc,UpdatedAtUtc) VALUES($id,$journal,$date,$created,$updated) ON CONFLICT(Id) DO UPDATE SET UpdatedAtUtc=excluded.UpdatedAtUtc;"))
        { Add(command, "$id", entry.Id); Add(command, "$journal", entry.JournalId); Add(command, "$date", entry.Date); Add(command, "$created", entry.CreatedAtUtc); Add(command, "$updated", entry.UpdatedAtUtc); await command.ExecuteNonQueryAsync(token); }
        foreach (var removed in (before?.Values.Keys ?? []).Except(entry.Values.Keys))
        { using var command = Command(connection, transaction, "DELETE FROM JournalValues WHERE EntryId=$entry AND FieldId=$field;"); Add(command, "$entry", entry.Id); Add(command, "$field", removed); await command.ExecuteNonQueryAsync(token); }
        foreach (var pair in entry.Values)
        {
            if (before?.Values.TryGetValue(pair.Key, out var old) == true && JournalRules.Equal(old, pair.Value)) continue;
            var field = definition.Fields.Single(f => f.Id == pair.Key); var value = pair.Value;
            using var command = Command(connection, transaction, """
                DELETE FROM JournalValues WHERE EntryId=$entry AND FieldId=$field;
                INSERT INTO JournalValues(EntryId,FieldId,JournalId,FieldType,TextValue,DecimalValue,IntegerValue,DateValue,TaskId,TrackerId)
                    VALUES($entry,$field,$journal,$type,$text,$decimal,$integer,$date,$task,$tracker);
                """);
            Add(command, "$entry", entry.Id); Add(command, "$field", field.Id); Add(command, "$journal", entry.JournalId); Add(command, "$type", (int)field.Type);
            Add(command, "$text", value.Text); Add(command, "$decimal", value.Number is { } number ? ExactValueText.Store(number) : null); Add(command, "$integer", value.Integer); Add(command, "$date", value.Date);
            Add(command, "$task", field.Type == JournalFieldType.TaskReference ? value.ReferenceId : null); Add(command, "$tracker", field.Type == JournalFieldType.TrackerReference ? value.ReferenceId : null); await command.ExecuteNonQueryAsync(token);
            foreach (var option in value.OptionIds ?? [])
            { using var selected = Command(connection, transaction, "INSERT INTO JournalSelections(EntryId,FieldId,OptionId) VALUES($entry,$field,$option);"); Add(selected, "$entry", entry.Id); Add(selected, "$field", field.Id); Add(selected, "$option", option); await selected.ExecuteNonQueryAsync(token); }
        }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static WorkspaceItem Item(SqliteDataReader reader) => new(Id(reader, 0), (WorkspaceItemType)reader.GetInt32(1), reader.GetString(2), Utc(reader, 3)!.Value, Utc(reader, 4)!.Value, Utc(reader, 5), Utc(reader, 6));
    private static Guid Id(SqliteDataReader reader, int index) => Guid.Parse(reader.GetString(index));
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static long? Integer(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset? Utc(SqliteDataReader reader, int index) => Text(reader, index) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value switch
    { null => DBNull.Value, Guid id => id.ToString("D"), DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value });
    private static Task<SqliteConnection> Open(WorkspaceContext workspace, CancellationToken token) => SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, false, token);
}
