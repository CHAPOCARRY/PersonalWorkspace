namespace PersonalWorkspace.Core;

public enum JournalFieldType { ShortText, LongText, Number, Percentage, Currency, Duration, Checkbox, Scale, Select, MultiSelect, Date, Link, TaskReference, TrackerReference }
public enum JournalCollection { Active, Archived, Trash }
public enum JournalAction { Archive, RestoreArchive, Trash, RestoreTrash }
public sealed record JournalOption(Guid Id, string Label, int SortOrder, bool HasValues = false);
public sealed record JournalField(Guid Id, Guid JournalId, string Name, JournalFieldType Type, int SortOrder,
    string? CurrencyCode = null, long? ScaleMin = null, long? ScaleMax = null, bool HistoryLocked = false,
    bool HasValues = false, IReadOnlyList<JournalOption>? Options = null)
{
    public IReadOnlyList<JournalOption> OrderedOptions => (Options ?? []).OrderBy(o => o.SortOrder).ThenBy(o => o.Id).ToArray();
}
public sealed record JournalFieldDraft(string Name, JournalFieldType Type, string? CurrencyCode = null, long? ScaleMin = null,
    long? ScaleMax = null, IReadOnlyList<JournalOption>? Options = null);
public sealed record JournalDefinition(WorkspaceItem Item, string Description, IReadOnlyList<JournalField> Fields)
{
    public bool IsActive => Item.ArchivedAtUtc is null && Item.DeletedAtUtc is null;
}
public sealed record JournalValue(string? Text = null, decimal? Number = null, long? Integer = null,
    DateOnly? Date = null, Guid? ReferenceId = null, IReadOnlyList<Guid>? OptionIds = null);
public sealed record JournalEntry(Guid Id, Guid JournalId, DateOnly Date, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<Guid, JournalValue> Values);
public sealed record JournalSnapshot(IReadOnlyList<JournalDefinition> Journals, IReadOnlyList<JournalEntry> Entries,
    IReadOnlyList<WorkspaceItem> References);
public sealed class JournalState(JournalDefinition definition, JournalEntry? entry, IReadOnlyList<WorkspaceItem> references)
{
    public JournalDefinition Definition { get; set; } = definition;
    public JournalEntry? Entry { get; set; } = entry;
    public IReadOnlyList<WorkspaceItem> References { get; } = references;
}
public interface IJournalRepository
{
    // Null dates load definitions only. Entry reads always require both inclusive bounds.
    Task<JournalSnapshot> ReadAsync(WorkspaceContext workspace, Guid? journalId, JournalCollection collection, DateOnly? from, DateOnly? through, CancellationToken token);
    Task CreateAsync(WorkspaceContext workspace, JournalDefinition definition, CancellationToken token);
    Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid id, DateOnly? date, Func<JournalState, T> change, CancellationToken token);
    Task DeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken token);
}
public interface IJournalService
{
    Task<JournalSnapshot> GetAsync(Guid profileId, JournalCollection collection = JournalCollection.Active, DateOnly? date = null, CancellationToken token = default);
    Task<JournalSnapshot> GetRangeAsync(WorkspaceItemReference reference, DateOnly from, DateOnly through, CancellationToken token = default);
    Task<JournalDefinition> CreateAsync(Guid profileId, string title, string description = "", CancellationToken token = default);
    Task<JournalDefinition> UpdateAsync(WorkspaceItemReference reference, string title, string description, CancellationToken token = default);
    Task<JournalDefinition> SaveFieldAsync(WorkspaceItemReference reference, Guid? fieldId, JournalFieldDraft draft, CancellationToken token = default);
    Task<JournalDefinition> MoveFieldAsync(WorkspaceItemReference reference, Guid fieldId, int direction, CancellationToken token = default);
    Task<JournalDefinition> DeleteFieldAsync(WorkspaceItemReference reference, Guid fieldId, bool confirmHistoricalDeletion = false, CancellationToken token = default);
    // Patch only the supplied fields; null/empty explicitly clears that field. All changes are atomic.
    Task<JournalEntry?> SaveEntryAsync(WorkspaceItemReference reference, DateOnly date, IReadOnlyDictionary<Guid, JournalValue?> changes, CancellationToken token = default);
    Task<JournalDefinition> ApplyAsync(WorkspaceItemReference reference, JournalAction action, CancellationToken token = default);
    Task<JournalDefinition> DuplicateAsync(WorkspaceItemReference reference, CancellationToken token = default);
    Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken token = default);
}
public sealed class JournalValidationException(string message) : Exception(message);
public sealed class JournalOperationException(string message, Exception inner) : Exception(message, inner);

public static class JournalRules
{
    public static string Name(string text) => string.IsNullOrWhiteSpace(text) || text.Trim().Length > 200 || text.Any(char.IsControl)
        ? throw new JournalValidationException("Enter a name of 1–200 characters without line breaks.") : text.Trim();
    public static JournalFieldDraft Validate(JournalFieldDraft draft)
    {
        if (!Enum.IsDefined(draft.Type)) throw new JournalValidationException("Choose a supported field type.");
        var code = draft.Type == JournalFieldType.Currency ? draft.CurrencyCode?.Trim().ToUpperInvariant() : null;
        if (draft.Type == JournalFieldType.Currency && (code?.Length != 3 || code.Any(c => c is < 'A' or > 'Z')))
            throw new JournalValidationException("Enter a three-letter currency code, such as EUR.");
        var scale = draft.Type == JournalFieldType.Scale;
        if (scale && (draft.ScaleMin is null || draft.ScaleMax is null || draft.ScaleMax <= draft.ScaleMin))
            throw new JournalValidationException("Scale maximum must be greater than its minimum.");
        var options = draft.Type is JournalFieldType.Select or JournalFieldType.MultiSelect ? draft.Options ?? [] : [];
        if (options.Count > 200 || options.Any(o => o.Id == Guid.Empty) || options.Select(o => o.Id).Distinct().Count() != options.Count)
            throw new JournalValidationException("Use up to 200 distinct options with stable identities.");
        options = options.Select((o, i) => o with { Label = Name(o.Label), SortOrder = i }).ToArray();
        if (options.Select(o => o.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
            throw new JournalValidationException("Option labels must be distinct within the field.");
        return draft with { Name = Name(draft.Name), CurrencyCode = code, ScaleMin = scale ? draft.ScaleMin : null, ScaleMax = scale ? draft.ScaleMax : null, Options = options };
    }
    public static JournalValue? Validate(JournalField field, JournalValue? value, IReadOnlyList<WorkspaceItem> references)
    {
        if (value is null) return null;
        var count = (value.Text is not null ? 1 : 0) + (value.Number is not null ? 1 : 0) + (value.Integer is not null ? 1 : 0)
            + (value.Date is not null ? 1 : 0) + (value.ReferenceId is not null ? 1 : 0) + (value.OptionIds is { Count: > 0 } ? 1 : 0);
        if (count == 0) return null;
        if (count != 1) throw new JournalValidationException($"{field.Name}: supply one value of the configured type.");
        bool valid;
        switch (field.Type)
        {
            case JournalFieldType.ShortText: case JournalFieldType.LongText: case JournalFieldType.Link:
                if (value.Text is null) break;
                var text = value.Text.Trim(); if (text.Length == 0) return null;
                if (text.Length > (field.Type == JournalFieldType.LongText ? 50000 : 2048)) throw new JournalValidationException($"{field.Name}: text is too long.");
                if (field.Type != JournalFieldType.LongText && text.Any(char.IsControl)) throw new JournalValidationException($"{field.Name}: use a single line.");
                if (field.Type == JournalFieldType.Link && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto")))
                    throw new JournalValidationException($"{field.Name}: enter an absolute http, https or mailto link.");
                return new(Text: text);
            case JournalFieldType.Number: case JournalFieldType.Currency: valid = value.Number is not null; if (valid) return new(Number: value.Number); break;
            case JournalFieldType.Percentage: if (value.Number is >= 0 and <= 100) return new(Number: value.Number); break;
            case JournalFieldType.Duration: if (value.Integer is >= 0 and <= 922337203685) return new(Integer: value.Integer); break;
            case JournalFieldType.Checkbox: if (value.Integer is 0 or 1) return new(Integer: value.Integer); break;
            case JournalFieldType.Scale: if (value.Integer is { } number && number >= field.ScaleMin && number <= field.ScaleMax) return new(Integer: number); break;
            case JournalFieldType.Date: if (value.Date is not null) return new(Date: value.Date); break;
            case JournalFieldType.Select: case JournalFieldType.MultiSelect:
                var ids = value.OptionIds ?? [];
                if (ids.Count == 0) break;
                if ((field.Type == JournalFieldType.Select && ids.Count != 1) || ids.Distinct().Count() != ids.Count || ids.Any(id => !field.OrderedOptions.Any(o => o.Id == id)))
                    throw new JournalValidationException($"{field.Name}: select valid, distinct options belonging to this field.");
                return new(OptionIds: field.OrderedOptions.Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToArray());
            case JournalFieldType.TaskReference: case JournalFieldType.TrackerReference:
                if (value.ReferenceId is { } reference && references.Any(r => r.Id == reference && r.ItemType == (field.Type == JournalFieldType.TaskReference ? WorkspaceItemType.Task : WorkspaceItemType.Tracker))) return new(ReferenceId: reference);
                throw new JournalValidationException($"{field.Name}: choose an existing { (field.Type == JournalFieldType.TaskReference ? "Task" : "Tracker") } in this profile.");
        }
        throw new JournalValidationException($"{field.Name}: the value does not match the field type or its allowed range.");
    }
    public static bool Equal(JournalValue a, JournalValue b) => a.Text == b.Text && a.Number == b.Number && a.Integer == b.Integer && a.Date == b.Date && a.ReferenceId == b.ReferenceId && (a.OptionIds ?? []).SequenceEqual(b.OptionIds ?? []);
}
