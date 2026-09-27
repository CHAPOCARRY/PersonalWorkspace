using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Services;

public sealed class RecurrenceService(IRecurrenceRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<RecurrenceService> logger) : IRecurrenceService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    public Task<IReadOnlyList<OccurrenceItem>> GetRangeAsync(Guid profile, DateOnly from, DateOnly through, CancellationToken cancellationToken = default) =>
        RangeAsync(profile, null, from, through, false, cancellationToken);
    public Task<IReadOnlyList<OccurrenceItem>> GetHistoryAsync(TaskReference task, DateOnly from, DateOnly through, CancellationToken cancellationToken = default) =>
        RangeAsync(task.ProfileId, task.ItemId, from, through, true, cancellationToken);
    private Task<IReadOnlyList<OccurrenceItem>> RangeAsync(Guid profile, Guid? task, DateOnly from, DateOnly through, bool history, CancellationToken cancellationToken)
    {
        if (through < from || through.DayNumber - from.DayNumber > 365) throw new TaskValidationException("Choose a date window of at most 366 days.");
        return RunAsync<IReadOnlyList<OccurrenceItem>>(profile, new(task, From: from, Through: through), state =>
        {
            var now = clock.GetUtcNow();
            var slots = state.Occurrences.Values.ToDictionary(o => (o.TaskId,o.SlotDate));
            foreach (var segment in state.Segments.Values.Where(s => s.Enabled))
            {
                var definition = state.Graph.Tasks[segment.TaskId];
                if (!definition.IsRecurring || !Active(definition)) continue;
                foreach (var date in segment.Rule.Dates(from, through).Where(segment.Matches))
                {
                    if (slots.TryGetValue((segment.TaskId,date), out var existing))
                    {
                        // Never resurrect a skipped/edited execution, or a historical suppressed slot.
                        if (existing.IsSuppressed && !existing.IsProtected(Today))
                            state.Occurrences[existing.Id] = existing with { SegmentId = segment.Id, IsSuppressed = false, UpdatedAtUtc = Stamp(existing.UpdatedAtUtc) };
                        continue;
                    }
                    var occurrence = new TaskOccurrence(Guid.NewGuid(), segment.TaskId, segment.Id, date, date, TaskStatus.ToDo, null, false, false, false, now, now);
                    state.Occurrences.Add(occurrence.Id, occurrence); slots.Add((segment.TaskId,date), occurrence);
                    state.Graph.Tasks[segment.TaskId] = definition with { HasOccurrences = true };
                }
            }
            return state.Occurrences.Values.Where(o => o.OccurrenceDate >= from && o.OccurrenceDate <= through && !o.IsSuppressed
                    && (history || !o.IsSkipped && state.Graph.Tasks[o.TaskId].IsRecurring && Active(state.Graph.Tasks[o.TaskId])))
                .OrderBy(o => o.OccurrenceDate).ThenBy(o => o.SlotDate).ThenBy(o => o.Id)
                .Select(o => new OccurrenceItem(state.Graph.Tasks[o.TaskId],o)).ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<RecurrenceSegment>> GetSegmentsAsync(TaskReference task, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<RecurrenceSegment>>(task.ProfileId, new(task.ItemId, RulesOnly: true),
            state => state.Segments.Values.OrderBy(s => s.From).ToArray(), cancellationToken);

    public Task<OccurrenceItem> FindAsync(OccurrenceReference occurrence, CancellationToken cancellationToken = default) =>
        RunAsync(occurrence.ProfileId, new(OccurrenceId: occurrence.Id), state => Item(state, occurrence.Id), cancellationToken);

    public Task SetRuleAsync(TaskReference task, RecurrenceRule rule, RecurrenceScope scope = RecurrenceScope.EntireSeries, Guid? fromOccurrence = null, CancellationToken cancellationToken = default) =>
        RunAsync(task.ProfileId, new(task.ItemId), state =>
        {
            rule.Validate();
            if (!Enum.IsDefined(scope)) throw new TaskValidationException("Choose a recurrence edit scope.");
            var definition = Definition(state, task.ItemId); RequireActive(definition);
            if (!definition.IsRecurring && (definition.Status == TaskStatus.Done || definition.Value?.Actual is not null))
                throw new TaskValidationException("Reopen this task and clear its one-off Actual before enabling recurrence. Existing results are never transferred silently.");
            var boundary = definition.IsRecurring ? Today : rule.StartDate;
            if (scope == RecurrenceScope.ThisAndFuture)
            {
                if (!definition.IsRecurring || fromOccurrence is not { } id || !state.Occurrences.TryGetValue(id,out var selected))
                    throw new TaskValidationException("Open an occurrence to change this and future dates.");
                boundary = selected.SlotDate;
                if (boundary < Today) throw new TaskValidationException("Choose today or a future occurrence. Past schedule segments are preserved.");
                if (rule.StartDate != boundary) throw new TaskValidationException("For this and future, the start must be the selected occurrence's original slot date.");
            }
            foreach (var segment in state.Segments.Values.ToArray())
            {
                if (!segment.Enabled || segment.Until <= boundary) continue;
                state.Segments[segment.Id] = segment.From >= boundary ? segment with { Enabled = false } : segment with { Until = boundary };
            }
            var next = new RecurrenceSegment(Guid.NewGuid(),task.ItemId,rule, rule.StartDate > boundary ? rule.StartDate : boundary, null,true,clock.GetUtcNow());
            state.Segments.Add(next.Id,next);
            foreach (var occurrence in state.Occurrences.Values.ToArray())
            {
                if (occurrence.SlotDate < boundary || occurrence.IsProtected(Today)) continue;
                state.Occurrences[occurrence.Id] = occurrence with { SegmentId = next.Matches(occurrence.SlotDate) ? next.Id : occurrence.SegmentId,
                    IsSuppressed = !next.Matches(occurrence.SlotDate), UpdatedAtUtc = Stamp(occurrence.UpdatedAtUtc) };
            }
            state.Graph.Tasks[task.ItemId] = definition with { IsRecurring = true, Status = TaskStatus.ToDo, ScheduledDate = null,
                Item = definition.Item with { UpdatedAtUtc = Stamp(definition.Item.UpdatedAtUtc) } };
            // Enabling a series must reopen any previously completed definition ancestors.
            TaskService.RecalculateGraph(state.Graph, clock);
            return true;
        }, cancellationToken);

    public Task RemoveAsync(TaskReference task, CancellationToken cancellationToken = default) =>
        RunAsync(task.ProfileId, new(task.ItemId), state =>
        {
            var definition = Definition(state, task.ItemId); RequireActive(definition);
            foreach (var segment in state.Segments.Values.ToArray()) state.Segments[segment.Id] = segment with { Enabled = false };
            foreach (var occurrence in state.Occurrences.Values.ToArray())
                if (!occurrence.IsProtected(Today)) state.Occurrences[occurrence.Id] = occurrence with { IsSuppressed = true, UpdatedAtUtc = Stamp(occurrence.UpdatedAtUtc) };
            state.Graph.Tasks[task.ItemId] = definition with { IsRecurring = false, Status = TaskStatus.ToDo, ScheduledDate = null,
                Item = definition.Item with { UpdatedAtUtc = Stamp(definition.Item.UpdatedAtUtc) } };
            return true;
        }, cancellationToken);

    public Task<OccurrenceItem> UpdateAsync(OccurrenceReference occurrence, DateOnly date, TaskStatus status, decimal? actual, bool skipped, CancellationToken cancellationToken = default) =>
        RunAsync(occurrence.ProfileId, new(OccurrenceId: occurrence.Id), state =>
        {
            var item = Item(state, occurrence.Id); RequireActive(item.Definition);
            var old = item.Occurrence;
            if (old.IsSuppressed) throw new TaskValidationException("This unused occurrence was removed from the schedule.");
            if (!Enum.IsDefined(status)) throw new TaskValidationException("Choose a valid occurrence status.");
            var value = item.Definition.Value is { } configured ? (configured with { Actual = actual }).Validate() : null;
            if (value is null && actual is not null) throw new TaskValidationException("Checkbox occurrences do not have Actual values.");
            if (old.Status == TaskStatus.Done && status != TaskStatus.Done && value?.IsReached == true && actual == old.Actual)
                throw new TaskValidationException("Lower or clear Actual to reopen this occurrence.");
            var guards = state.Graph.Prerequisites()[old.TaskId].All(id => state.Graph.Tasks[id].Status == TaskStatus.Done && !state.Graph.Tasks[id].IsRecurring);
            if (status == TaskStatus.Done && old.Status != TaskStatus.Done && (!guards || value is { IsReached: false }))
                throw new TaskValidationException("Reach the target and complete the required subtasks and dependencies before marking this occurrence Done.");
            if (value is not null) status = value.IsReached && guards ? TaskStatus.Done : status == TaskStatus.Done ? TaskStatus.ToDo : status;
            var updated = old with { OccurrenceDate = date, Status = status, Actual = actual, IsSkipped = skipped, IsOverride = true };
            if (updated != old) updated = updated with { UpdatedAtUtc = Stamp(old.UpdatedAtUtc) };
            state.Occurrences[old.Id] = updated;
            return item with { Occurrence = updated };
        }, cancellationToken);

    public Task MoveAsync(OccurrenceReference occurrence, DateOnly date, CancellationToken cancellationToken = default) =>
        RunAsync(occurrence.ProfileId, new(OccurrenceId: occurrence.Id), state =>
        {
            var item = Item(state, occurrence.Id); RequireActive(item.Definition);
            if (item.Occurrence.IsSuppressed) throw new TaskValidationException("This occurrence is no longer scheduled.");
            state.Occurrences[occurrence.Id] = item.Occurrence with { OccurrenceDate = date, IsOverride = true, UpdatedAtUtc = Stamp(item.Occurrence.UpdatedAtUtc) };
            return true;
        }, cancellationToken);

    private static TaskItem Definition(RecurrenceState state, Guid id) => state.Graph.Tasks.GetValueOrDefault(id) ?? throw new TaskValidationException("This task is unavailable.");
    private static OccurrenceItem Item(RecurrenceState state, Guid id) => state.Occurrences.TryGetValue(id,out var occurrence)
        ? new(Definition(state,occurrence.TaskId),occurrence) : throw new TaskValidationException("This occurrence is unavailable.");
    private static bool Active(TaskItem task) => task.Item.ArchivedAtUtc is null && task.Item.DeletedAtUtc is null;
    private static void RequireActive(TaskItem task) { if (!Active(task)) throw new TaskValidationException("Restore this task before editing its recurrence or occurrences."); }
    private DateTimeOffset Stamp(DateTimeOffset previous) => clock.GetUtcNow() > previous ? clock.GetUtcNow() : previous.AddTicks(1);
    private async Task<T> RunAsync<T>(Guid profile, RecurrenceQuery query, Func<RecurrenceState,T> operation, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await repository.TransactAsync(new(profile,database),query,operation,cancellationToken); }
        catch (TaskValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception,"Recurrence storage operation failed in profile {ProfileId}",profile);
            throw new TaskOperationException("The recurrence could not be loaded or saved. Check the local workspace and try again.",exception);
        }
    }
}
