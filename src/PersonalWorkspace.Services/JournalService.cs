using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class JournalService(IJournalRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<JournalService> logger) : IJournalService
{
    public Task<JournalSnapshot> GetAsync(Guid profileId, JournalCollection collection = JournalCollection.Active, DateOnly? date = null, CancellationToken token = default) =>
        Run(profileId, workspace => repository.ReadAsync(workspace, null, collection, date, date, token), token);
    public Task<JournalSnapshot> GetRangeAsync(WorkspaceItemReference reference, DateOnly from, DateOnly through, CancellationToken token = default) => Run(reference.ProfileId, async workspace =>
    {
        if (through < from || through.DayNumber - from.DayNumber >= 366) throw new JournalValidationException("Choose a range of 1–366 days.");
        var snapshot = await repository.ReadAsync(workspace, reference.ItemId, JournalCollection.Active, from, through, token);
        if (snapshot.Journals.Count == 0) throw new JournalValidationException("This Journal is no longer available.");
        return snapshot;
    }, token);
    public Task<JournalDefinition> CreateAsync(Guid profileId, string title, string description = "", CancellationToken token = default) => Run(profileId, async workspace =>
    {
        var now = clock.GetUtcNow(); var definition = new JournalDefinition(new(Guid.NewGuid(), WorkspaceItemType.Journal, JournalRules.Name(title), now, now, null, null), Description(description), []);
        await repository.CreateAsync(workspace, definition, token); return definition;
    }, token);
    public Task<JournalDefinition> UpdateAsync(WorkspaceItemReference reference, string title, string description, CancellationToken token = default) => Change(reference, state =>
    {
        Editable(state.Definition); title = JournalRules.Name(title); description = Description(description);
        if (state.Definition.Item.Title != title || state.Definition.Description != description) state.Definition = Touch(state.Definition with { Item = state.Definition.Item with { Title = title }, Description = description });
        return state.Definition;
    }, token);
    public Task<JournalDefinition> SaveFieldAsync(WorkspaceItemReference reference, Guid? fieldId, JournalFieldDraft draft, CancellationToken token = default) => Change(reference, state =>
    {
        Editable(state.Definition); draft = JournalRules.Validate(draft);
        var fields = state.Definition.Fields.ToList(); var old = fieldId is { } id ? Field(state, id) : null;
        if (old?.HistoryLocked == true && (old.Type != draft.Type || old.CurrencyCode != draft.CurrencyCode || old.ScaleMin != draft.ScaleMin || old.ScaleMax != draft.ScaleMax))
            throw new JournalValidationException("Field type, currency and scale bounds are fixed after the first stored value. Create a new field for a different configuration.");
        if (old?.OrderedOptions.Any(o => o.HasValues && !draft.Options!.Any(n => n.Id == o.Id)) == true)
            throw new JournalValidationException("An option used by saved entries cannot be deleted. Rename it instead.");
        var options = draft.Options!.Select(o => o with { HasValues = old?.OrderedOptions.Any(previous => previous.Id == o.Id && previous.HasValues) == true }).ToArray();
        var field = new JournalField(old?.Id ?? Guid.NewGuid(), reference.ItemId, draft.Name, draft.Type, old?.SortOrder ?? fields.Count,
            draft.CurrencyCode, draft.ScaleMin, draft.ScaleMax, old?.HistoryLocked ?? false, old?.HasValues ?? false, options);
        if (old is not null && field with { Options = old.Options } == old && field.OrderedOptions.SequenceEqual(old.OrderedOptions)) return state.Definition;
        if (old is null) fields.Add(field); else fields[fields.IndexOf(old)] = field;
        return state.Definition = Touch(state.Definition with { Fields = fields.ToArray() });
    }, token);
    public Task<JournalDefinition> MoveFieldAsync(WorkspaceItemReference reference, Guid fieldId, int direction, CancellationToken token = default) => Change(reference, state =>
    {
        Editable(state.Definition); if (direction is not (-1 or 1)) throw new JournalValidationException("Move a field up or down.");
        var fields = state.Definition.Fields.ToList(); var index = fields.IndexOf(Field(state, fieldId)); var next = index + direction;
        if (next < 0 || next >= fields.Count) return state.Definition;
        (fields[index], fields[next]) = (fields[next], fields[index]);
        return state.Definition = Touch(state.Definition with { Fields = fields.Select((f, i) => f with { SortOrder = i }).ToArray() });
    }, token);
    public Task<JournalDefinition> DeleteFieldAsync(WorkspaceItemReference reference, Guid fieldId, bool confirmHistoricalDeletion = false, CancellationToken token = default) => Change(reference, state =>
    {
        Editable(state.Definition); var field = Field(state, fieldId);
        if (field.HasValues && !confirmHistoricalDeletion) throw new JournalValidationException("This field contains saved values. Confirm deletion of the field and its historical values first.");
        return state.Definition = Touch(state.Definition with { Fields = state.Definition.Fields.Where(f => f.Id != fieldId).Select((f, i) => f with { SortOrder = i }).ToArray() });
    }, token);
    public Task<JournalEntry?> SaveEntryAsync(WorkspaceItemReference reference, DateOnly date, IReadOnlyDictionary<Guid, JournalValue?> changes, CancellationToken token = default) => Run(reference.ProfileId,
        workspace => repository.TransactAsync(workspace, reference.ItemId, date, state =>
        {
            Editable(state.Definition); if (!state.Definition.IsActive) throw new JournalValidationException("Restore this Journal before editing daily entries.");
            var validated = changes.ToDictionary(pair => pair.Key, pair => JournalRules.Validate(Field(state, pair.Key), pair.Value, state.References));
            var values = state.Entry?.Values.ToDictionary() ?? [];
            foreach (var pair in validated) { if (pair.Value is null) values.Remove(pair.Key); else values[pair.Key] = pair.Value; }
            if (values.Count == 0) return state.Entry = null;
            if (state.Entry is { } old && old.Values.Count == values.Count && values.All(p => old.Values.TryGetValue(p.Key, out var value) && JournalRules.Equal(value, p.Value))) return old;
            var now = clock.GetUtcNow();
            state.Entry = state.Entry is { } previous ? previous with { Values = values, UpdatedAtUtc = Next(previous.UpdatedAtUtc) }
                : new(Guid.NewGuid(), reference.ItemId, date, now, now, values);
            // First-use history locks are metadata, not a Journal definition content edit.
            state.Definition = state.Definition with { Fields = state.Definition.Fields.Select(f => values.ContainsKey(f.Id) ? f with { HistoryLocked = true, HasValues = true } : f).ToArray() };
            return state.Entry;
        }, token), token);
    public Task<JournalDefinition> ApplyAsync(WorkspaceItemReference reference, JournalAction action, CancellationToken token = default) => Change(reference, state =>
    {
        var item = state.Definition.Item;
        if (action is JournalAction.Archive or JournalAction.RestoreArchive) Editable(state.Definition);
        var updated = action switch
        {
            JournalAction.Archive => item with { ArchivedAtUtc = item.ArchivedAtUtc ?? clock.GetUtcNow() },
            JournalAction.RestoreArchive => item with { ArchivedAtUtc = null },
            JournalAction.Trash => item with { DeletedAtUtc = item.DeletedAtUtc ?? clock.GetUtcNow() },
            JournalAction.RestoreTrash => item.DeletedAtUtc is null ? item : item with { DeletedAtUtc = null, ArchivedAtUtc = null },
            _ => throw new JournalValidationException("Choose a valid Journal action.")
        };
        return state.Definition = updated == item ? state.Definition : Touch(state.Definition with { Item = updated });
    }, token);
    public Task<JournalDefinition> DuplicateAsync(WorkspaceItemReference reference, CancellationToken token = default) => Run(reference.ProfileId, async workspace =>
    {
        var original = (await repository.ReadAsync(workspace, reference.ItemId, JournalCollection.Active, null, null, token)).Journals.SingleOrDefault() ?? throw new JournalValidationException("This Journal is no longer available.");
        Editable(original); var now = clock.GetUtcNow(); var id = Guid.NewGuid();
        var copy = new JournalDefinition(new(id, WorkspaceItemType.Journal, original.Item.Title, now, now, null, null), original.Description,
            original.Fields.Select(f => f with { Id = Guid.NewGuid(), JournalId = id, HistoryLocked = false, HasValues = false, Options = f.OrderedOptions.Select(o => o with { Id = Guid.NewGuid(), HasValues = false }).ToArray() }).ToArray());
        await repository.CreateAsync(workspace, copy, token); return copy;
    }, token);
    public Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken token = default) => Run(reference.ProfileId, async workspace => { await repository.DeleteAsync(workspace, reference.ItemId, token); return true; }, token);
    private Task<T> Change<T>(WorkspaceItemReference reference, Func<JournalState, T> change, CancellationToken token) => Run(reference.ProfileId, workspace => repository.TransactAsync(workspace, reference.ItemId, null, change, token), token);
    private JournalDefinition Touch(JournalDefinition definition) => definition with { Item = definition.Item with { UpdatedAtUtc = Next(definition.Item.UpdatedAtUtc) } };
    private DateTimeOffset Next(DateTimeOffset previous) { var now = clock.GetUtcNow(); return now > previous ? now : previous.AddTicks(1); }
    private static JournalField Field(JournalState state, Guid id) => state.Definition.Fields.SingleOrDefault(f => f.Id == id) ?? throw new JournalValidationException("This field is no longer available. Reload the Journal.");
    private static void Editable(JournalDefinition definition) { if (definition.Item.DeletedAtUtc is not null) throw new JournalValidationException("Restore this Journal from Trash before editing it."); }
    private static string Description(string description) => description.Length > 50000 ? throw new JournalValidationException("The description is too long.") : description.Trim();
    private async Task<T> Run<T>(Guid profileId, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profileId || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profileId, database)); }
        catch (JournalValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Journal operation failed in profile {ProfileId}", profileId); throw new JournalOperationException("The Journal could not be loaded or saved. Check access to the local workspace and try again.", exception); }
    }
}
