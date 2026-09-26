namespace PersonalWorkspace.Core;

public enum WorkspaceItemType { Task = 1, Event = 2 }
public enum TaskStatus { ToDo, Doing, Blocked, Done }
public enum TaskPriority { None, Low, Normal, High, Critical }
public enum TaskCollection { Active, Today, Archived, Trash }
public enum TaskAction { Archive, RestoreArchive, Trash, RestoreTrash }

public sealed record WorkspaceItem(Guid Id, WorkspaceItemType ItemType, string Title,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ArchivedAtUtc, DateTimeOffset? DeletedAtUtc);

public sealed record TaskItem(WorkspaceItem Item, string Description, TaskStatus Status, TaskPriority Priority, DateOnly? ScheduledDate, Guid? ParentTaskId = null, TaskValue? Value = null)
{
    public TaskValueType ValueType => Value?.Type ?? TaskValueType.Checkbox;
}
public sealed record TaskDraft(string Title, string Description = "", TaskStatus Status = TaskStatus.ToDo,
    TaskPriority Priority = TaskPriority.None, DateOnly? ScheduledDate = null, TaskValue? Value = null);
// UI actions carry their originating profile so a stale editor can never write to a different workspace.
public sealed record TaskReference(Guid ProfileId, Guid ItemId);
public sealed record WorkspaceContext(Guid ProfileId, string DatabasePath);

public interface IWorkspaceOperationGate
{
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}

public interface ITaskRepository
{
    Task<TaskGraph> GetGraphAsync(WorkspaceContext workspace, CancellationToken cancellationToken);
    Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<TaskGraph, T> change, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskItem>> GetScheduledAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(WorkspaceContext workspace, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskItem>> GetAsync(WorkspaceContext workspace, TaskCollection collection, DateOnly today, CancellationToken cancellationToken);
    Task<TaskItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
    Task CreateAsync(WorkspaceContext workspace, TaskItem task, CancellationToken cancellationToken);
    Task<TaskItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken);
    Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
}

public interface ITaskService
{
    Task<TaskItem> RecordActualAsync(TaskReference task, decimal? actual, CancellationToken cancellationToken = default);
    Task<TaskGraph> GetGraphAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TaskItem> CreateSubtaskAsync(TaskReference parent, string title, CancellationToken cancellationToken = default);
    Task SetParentAsync(TaskReference task, Guid? parentId, CancellationToken cancellationToken = default);
    Task AddDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default);
    Task RemoveDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetScheduledAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetAsync(Guid profileId, TaskCollection collection, CancellationToken cancellationToken = default);
    Task<TaskItem?> FindAsync(TaskReference reference, CancellationToken cancellationToken = default);
    Task<TaskItem> CreateAsync(Guid profileId, TaskDraft draft, CancellationToken cancellationToken = default);
    Task<TaskItem> UpdateAsync(TaskReference reference, TaskDraft draft, CancellationToken cancellationToken = default);
    Task<TaskItem> ChangeStatusAsync(TaskReference reference, TaskStatus status, CancellationToken cancellationToken = default);
    Task<TaskItem> ScheduleAsync(TaskReference reference, DateOnly? date, CancellationToken cancellationToken = default);
    Task<TaskItem> ApplyAsync(TaskReference reference, TaskAction action, CancellationToken cancellationToken = default);
    Task<TaskItem> DuplicateAsync(TaskReference reference, CancellationToken cancellationToken = default);
    // The UI must obtain explicit confirmation naming the task before invoking this operation.
    Task PermanentlyDeleteAsync(TaskReference reference, CancellationToken cancellationToken = default);
}

public sealed class TaskValidationException(string message) : Exception(message);
public sealed class WorkspaceChangedException() : Exception("The active profile changed. Reopen the task in its profile and try again.");
public sealed class TaskOperationException(string message, Exception inner) : Exception(message, inner);
