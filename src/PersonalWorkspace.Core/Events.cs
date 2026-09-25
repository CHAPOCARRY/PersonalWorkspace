namespace PersonalWorkspace.Core;

public enum EventCollection { Archived, Trash }
public enum EventAction { Archive, RestoreArchive, Trash, RestoreTrash }
public sealed record EventDraft(string Title, bool AllDay, DateOnly StartDate, TimeOnly? StartTime, DateOnly EndDate, TimeOnly? EndTime);
public sealed record EventItem(WorkspaceItem Item, bool AllDay, DateOnly StartDate, TimeOnly? StartTime, DateOnly EndDate, TimeOnly? EndTime)
{
    // Calendar values are floating local wall dates/times, not UTC instants. Timed ends are exclusive.
    public bool OccursOn(DateOnly date) => date >= StartDate && date <= EndDate &&
        (AllDay || date != EndDate || EndTime != TimeOnly.MinValue);
    public bool IsPast(DateTime localNow) => AllDay ? DateOnly.FromDateTime(localNow) > EndDate
        : localNow >= EndDate.ToDateTime(EndTime!.Value);
}
public interface IEventRepository
{
    Task<IReadOnlyList<EventItem>> GetRangeAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventItem>> GetCollectionAsync(WorkspaceContext workspace, EventCollection collection, CancellationToken cancellationToken);
    Task<EventItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
    Task CreateAsync(WorkspaceContext workspace, EventItem item, CancellationToken cancellationToken);
    Task<EventItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<EventItem, EventItem> update, CancellationToken cancellationToken);
    Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
}
public interface IEventService
{
    Task<IReadOnlyList<EventItem>> GetRangeAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventItem>> GetCollectionAsync(Guid profileId, EventCollection collection, CancellationToken cancellationToken = default);
    Task<EventItem?> FindAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default);
    Task<EventItem> CreateAsync(Guid profileId, EventDraft draft, CancellationToken cancellationToken = default);
    Task<EventItem> UpdateAsync(WorkspaceItemReference reference, EventDraft draft, CancellationToken cancellationToken = default);
    Task<EventItem> MoveAsync(WorkspaceItemReference reference, DateOnly startDate, CancellationToken cancellationToken = default);
    Task<EventItem> ApplyAsync(WorkspaceItemReference reference, EventAction action, CancellationToken cancellationToken = default);
    // UI obtains an explicit confirmation naming the Event first.
    Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default);
}
public sealed class EventValidationException(string message) : Exception(message);
public sealed class EventOperationException(string message, Exception inner) : Exception(message, inner);
