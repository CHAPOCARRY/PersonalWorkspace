using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class EventService(IEventRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider time, ILogger<EventService> logger) : IEventService
{
    public Task<IReadOnlyList<EventItem>> GetRangeAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => from <= through ? repository.GetRangeAsync(workspace, from, through, cancellationToken)
            : throw new EventValidationException("The end date cannot be before the start date."), cancellationToken);
    public Task<IReadOnlyList<EventItem>> GetCollectionAsync(Guid profileId, EventCollection collection, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetCollectionAsync(workspace, collection, cancellationToken), cancellationToken);
    public Task<EventItem?> FindAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, workspace => repository.FindAsync(workspace, reference.ItemId, cancellationToken), cancellationToken);
    public Task<EventItem> CreateAsync(Guid profileId, EventDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace =>
        {
            var valid = Validate(draft);
            var now = time.GetUtcNow();
            var item = new EventItem(new(Guid.NewGuid(), WorkspaceItemType.Event, valid.Title, now, now, null, null),
                valid.AllDay, valid.StartDate, valid.StartTime, valid.EndDate, valid.EndTime);
            await repository.CreateAsync(workspace, item, cancellationToken);
            return item;
        }, cancellationToken);
    public Task<EventItem> UpdateAsync(WorkspaceItemReference reference, EventDraft draft, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, item =>
        {
            RequireEditable(item);
            var valid = Validate(draft);
            return item with { Item = item.Item with { Title = valid.Title }, AllDay = valid.AllDay,
                StartDate = valid.StartDate, StartTime = valid.StartTime, EndDate = valid.EndDate, EndTime = valid.EndTime };
        }, cancellationToken);
    public Task<EventItem> MoveAsync(WorkspaceItemReference reference, DateOnly startDate, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, item =>
        {
            RequireEditable(item);
            try { return item with { StartDate = startDate, EndDate = startDate.AddDays(item.EndDate.DayNumber - item.StartDate.DayNumber) }; }
            catch (ArgumentOutOfRangeException) { throw new EventValidationException("The moved event would end outside the supported calendar dates."); }
        }, cancellationToken);
    public Task<EventItem> ApplyAsync(WorkspaceItemReference reference, EventAction action, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, item =>
        {
            if (action is EventAction.Archive or EventAction.RestoreArchive) RequireEditable(item);
            return action switch
            {
                EventAction.Archive => item with { Item = item.Item with { ArchivedAtUtc = item.Item.ArchivedAtUtc ?? time.GetUtcNow() } },
                EventAction.RestoreArchive => item with { Item = item.Item with { ArchivedAtUtc = null } },
                EventAction.Trash => item with { Item = item.Item with { DeletedAtUtc = item.Item.DeletedAtUtc ?? time.GetUtcNow() } },
                EventAction.RestoreTrash => item.Item.DeletedAtUtc is null ? item : item with { Item = item.Item with { DeletedAtUtc = null, ArchivedAtUtc = null } },
                _ => throw new EventValidationException("Select a valid event action.")
            };
        }, cancellationToken);
    public Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, async workspace => { await repository.PermanentlyDeleteAsync(workspace, reference.ItemId, cancellationToken); return true; }, cancellationToken);
    private Task<EventItem> MutateAsync(WorkspaceItemReference reference, Func<EventItem, EventItem> update, CancellationToken cancellationToken) =>
        RunAsync(reference.ProfileId, workspace => repository.UpdateAsync(workspace, reference.ItemId, item =>
        {
            var updated = update(item);
            if (updated == item) return item;
            var now = time.GetUtcNow();
            return updated with { Item = updated.Item with { UpdatedAtUtc = now > item.Item.UpdatedAtUtc ? now : item.Item.UpdatedAtUtc.AddTicks(1) } };
        }, cancellationToken), cancellationToken);
    private static EventDraft Validate(EventDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new EventValidationException("Enter an event title.");
        if (draft.EndDate < draft.StartDate) throw new EventValidationException("The end date cannot be before the start date.");
        if (!draft.AllDay)
        {
            if (draft.StartTime is null || draft.EndTime is null) throw new EventValidationException("Choose both a start time and an end time.");
            if (draft.StartDate == draft.EndDate && draft.EndTime <= draft.StartTime) throw new EventValidationException("The end time must be later than the start time.");
        }
        return draft with { Title = draft.Title.Trim(), StartTime = draft.AllDay ? null : draft.StartTime, EndTime = draft.AllDay ? null : draft.EndTime };
    }
    private static void RequireEditable(EventItem item)
    {
        if (item.Item.DeletedAtUtc is not null) throw new EventValidationException("Restore the event from Trash before editing it.");
    }
    private async Task<T> RunAsync<T>(Guid profileId, Func<WorkspaceContext, Task<T>> action, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        if (current.Current?.Id != profileId || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profileId, database)); }
        catch (EventValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Event storage operation failed in profile {ProfileId}", profileId);
            throw new EventOperationException("The event could not be loaded or saved. Check access to the local workspace and try again.", exception);
        }
    }
}
