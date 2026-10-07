using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class TrackerService(ITrackerRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<TrackerService> logger) : ITrackerService
{
    public Task<IReadOnlyList<TrackerPeriodSummary>> GetPeriodsAsync(Guid profileId, IReadOnlyList<Guid> ids, DateOnly date, CancellationToken token = default) =>
        Run<IReadOnlyList<TrackerPeriodSummary>>(profileId, async workspace =>
        {
            var snapshot = await repository.ReadPeriodsAsync(workspace, ids.Distinct().ToArray(), date, token);
            var entries = snapshot.Entries.ToLookup(e => e.TrackerId);
            return snapshot.Items.Select(item =>
            {
                var period = item.Schedule.PeriodOn(date); var rows = entries[item.Item.Id].ToArray();
                var currentRows = rows.Where(e => e.PeriodDate == period).ToArray(); var latest = rows.Length == 0 ? period : rows.Max(e => e.PeriodDate);
                return new TrackerPeriodSummary(Period(item, period, currentRows, item.IsActive && item.Schedule.IsExpected(date)),
                    Period(item, latest, rows.Where(e => e.PeriodDate == latest).ToArray(), false));
            }).ToArray();
        }, token);
    public Task<TrackerItem?> FindAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, workspace => repository.FindAsync(workspace, reference.ItemId, cancellationToken), cancellationToken);
    public Task<TrackerItem> CreateAsync(Guid profileId, TrackerDraft draft, CancellationToken cancellationToken = default) =>
        Run(profileId, workspace => Create(workspace, draft, cancellationToken), cancellationToken);
    private async Task<TrackerItem> Create(WorkspaceContext workspace, TrackerDraft draft, CancellationToken token)
    {
        draft = TrackerRules.Validate(draft); var now = clock.GetUtcNow();
        var item = new TrackerItem(new(Guid.NewGuid(), WorkspaceItemType.Tracker, draft.Title, now, now, null, null),
            draft.Settings, draft.Schedule, draft.Target, draft.EntryMode, draft.Aggregation);
        await repository.CreateAsync(workspace, item, token); return item;
    }
    public Task<TrackerItem> UpdateAsync(WorkspaceItemReference reference, TrackerDraft draft, CancellationToken cancellationToken = default) =>
        Mutate(reference, item =>
        {
            Editable(item); draft = TrackerRules.Validate(draft);
            if (item.HasEntries && (draft.Settings != item.Settings || draft.Schedule != item.Schedule || draft.EntryMode != item.EntryMode || draft.Aggregation != item.Aggregation))
                throw new TrackerValidationException("Value settings, dates, frequency, entry mode and aggregation are fixed once entries exist. Duplicate the Tracker for a different configuration.");
            return item with { Item = item.Item with { Title = draft.Title }, Settings = draft.Settings, Schedule = draft.Schedule,
                Target = draft.Target, EntryMode = draft.EntryMode, Aggregation = draft.Aggregation };
        }, cancellationToken);
    public Task<TrackerItem> ApplyAsync(WorkspaceItemReference reference, TrackerAction action, CancellationToken cancellationToken = default) =>
        Mutate(reference, item =>
        {
            if (action is TrackerAction.Archive or TrackerAction.RestoreArchive) Editable(item);
            return action switch
            {
                TrackerAction.Archive => item with { Item = item.Item with { ArchivedAtUtc = item.Item.ArchivedAtUtc ?? clock.GetUtcNow() } },
                TrackerAction.RestoreArchive => item with { Item = item.Item with { ArchivedAtUtc = null } },
                TrackerAction.Trash => item with { Item = item.Item with { DeletedAtUtc = item.Item.DeletedAtUtc ?? clock.GetUtcNow() } },
                TrackerAction.RestoreTrash => item.Item.DeletedAtUtc is null ? item : item with { Item = item.Item with { DeletedAtUtc = null, ArchivedAtUtc = null } },
                _ => throw new TrackerValidationException("Choose a valid Tracker action.")
            };
        }, cancellationToken);
    private Task<TrackerItem> Mutate(WorkspaceItemReference reference, Func<TrackerItem, TrackerItem> update, CancellationToken token) =>
        Run(reference.ProfileId, workspace => repository.TransactAsync(workspace, reference.ItemId, _ => null, state =>
        {
            var item = update(state.Item);
            if (item != state.Item) item = item with { Item = item.Item with { UpdatedAtUtc = Next(state.Item.Item.UpdatedAtUtc) } };
            return state.Item = item;
        }, token), token);
    public Task<TrackerItem> DuplicateAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, async workspace =>
        {
            var item = await Required(workspace, reference.ItemId, cancellationToken); Editable(item);
            return await Create(workspace, item.Draft, cancellationToken);
        }, cancellationToken);
    public Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, async workspace => { await repository.PermanentlyDeleteAsync(workspace, reference.ItemId, cancellationToken); return true; }, cancellationToken);
    public Task<IReadOnlyList<TrackerEntry>> GetEntriesAsync(WorkspaceItemReference reference, TrackerEntryQuery query, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, async workspace =>
        {
            ValidateQuery(query); await Required(workspace, reference.ItemId, cancellationToken);
            return await repository.GetEntriesAsync(workspace, reference.ItemId, query, cancellationToken);
        }, cancellationToken);
    public Task<TrackerPeriod> GetPeriodAsync(WorkspaceItemReference reference, DateOnly periodDate, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, async workspace =>
        {
            var item = await Required(workspace, reference.ItemId, cancellationToken);
            var entries = await repository.GetEntriesAsync(workspace, reference.ItemId, new(Period: periodDate), cancellationToken);
            return Period(item, periodDate, entries, item.IsActive && item.Schedule.IsExpected(periodDate));
        }, cancellationToken);
    public Task<IReadOnlyList<TrackerPeriod>> GetAsync(Guid profileId, TrackerCollection collection = TrackerCollection.Active, bool todayOnly = false, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<TrackerPeriod>>(profileId, async workspace =>
        {
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            var items = await repository.GetAsync(workspace, collection, cancellationToken);
            var result = new List<TrackerPeriod>();
            foreach (var item in items)
            {
                var expected = item.IsActive && item.Schedule.IsExpected(today);
                if (todayOnly && !expected) continue;
                var period = item.Schedule.PeriodOn(today);
                IReadOnlyList<TrackerEntry> entries = period is { } date
                    ? await repository.GetEntriesAsync(workspace, item.Item.Id, new(Period: date), cancellationToken) : [];
                if (!todayOnly && entries.Count == 0)
                {
                    // One indexed latest-period lookup, never a lifetime-history scan.
                    var latest = await repository.GetEntriesAsync(workspace, item.Item.Id, new(LatestPeriod: true), cancellationToken);
                    result.Add(new(item, latest.FirstOrDefault()?.PeriodDate ?? period, TrackerRules.Aggregate(item, latest), latest.Count, expected));
                }
                else result.Add(Period(item, period, entries, expected));
            }
            return result;
        }, cancellationToken);
    public Task<TrackerEntry> SaveEntryAsync(WorkspaceItemReference reference, DateOnly localDate, TimeOnly localTime,
        TrackerValue value, string? note = null, Guid? entryId = null, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, workspace => repository.TransactAsync(workspace, reference.ItemId,
            item => entryId is { } id ? new(EntryId: id) : new(Period: item.Schedule.PeriodOn(localDate)
                ?? throw new TrackerValidationException("This date is outside the Tracker's expected periods or start/end dates.")), state =>
        {
            var item = state.Item; Editable(item);
            if (item.Item.ArchivedAtUtc is not null) throw new TrackerValidationException("Restore the archived Tracker before recording values.");
            TrackerRules.ValidateValue(item.Settings, value);
            var previous = entryId is { } id ? state.Entries.GetValueOrDefault(id) ?? throw new TrackerValidationException("This entry is no longer available.")
                : item.EntryMode == TrackerEntryMode.Single ? state.Entries.Values.SingleOrDefault() : null;
            var now = clock.GetUtcNow();
            var entry = previous is null ? new TrackerEntry(Guid.NewGuid(), item.Item.Id, item.Schedule.PeriodOn(localDate)!.Value,
                localDate, localTime, value, CleanNote(note), now, now) : previous with { Value = value, Note = CleanNote(note) };
            if (previous is not null && entry != previous) entry = entry with { UpdatedAtUtc = Next(previous.UpdatedAtUtc) };
            state.Entries[entry.Id] = entry;
            TrackerRules.Aggregate(item, state.Entries.Values); // Reject unsupported totals atomically.
            return entry;
        }, cancellationToken), cancellationToken);
    public Task DeleteEntryAsync(WorkspaceItemReference reference, Guid entryId, CancellationToken cancellationToken = default) =>
        Run(reference.ProfileId, workspace => repository.TransactAsync(workspace, reference.ItemId, _ => new(EntryId: entryId), state =>
        {
            Editable(state.Item);
            if (!state.Entries.Remove(entryId)) throw new TrackerValidationException("This entry is no longer available.");
            TrackerRules.Aggregate(state.Item, state.Entries.Values); return true;
        }, cancellationToken), cancellationToken);
    private static TrackerPeriod Period(TrackerItem item, DateOnly? date, IReadOnlyList<TrackerEntry> entries, bool expected) =>
        new(item, date, TrackerRules.Aggregate(item, entries), entries.Count, expected && entries.Count == 0);
    private async Task<TrackerItem> Required(WorkspaceContext workspace, Guid id, CancellationToken token) =>
        await repository.FindAsync(workspace, id, token) ?? throw new TrackerValidationException("This Tracker is no longer available.");
    private DateTimeOffset Next(DateTimeOffset previous) { var now = clock.GetUtcNow(); return now > previous ? now : previous.AddTicks(1); }
    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    private static void Editable(TrackerItem item)
    {
        if (item.Item.DeletedAtUtc is not null) throw new TrackerValidationException("Restore the Tracker from Trash before editing it.");
    }
    private static void ValidateQuery(TrackerEntryQuery query)
    {
        var selectors = (query.From is not null || query.Through is not null ? 1 : 0) + (query.Period is not null ? 1 : 0)
            + (query.Recent is not null ? 1 : 0) + (query.EntryId is not null ? 1 : 0) + (query.LatestPeriod ? 1 : 0);
        if (selectors != 1 || query.Recent is < 1 or > 500 || ((query.From is null) != (query.Through is null)) || query.From > query.Through)
            throw new TrackerValidationException("Choose one period, a valid date range, or between 1 and 500 recent entries.");
    }
    private async Task<T> Run<T>(Guid profileId, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profileId || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profileId, database)); }
        catch (TrackerValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tracker storage operation failed in profile {ProfileId}", profileId);
            throw new TrackerOperationException("The Tracker could not be loaded or saved. Check access to the local workspace and try again.", exception);
        }
    }
}
