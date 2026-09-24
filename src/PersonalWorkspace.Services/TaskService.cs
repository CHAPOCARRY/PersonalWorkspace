using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Services;

public sealed class TaskService(ITaskRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider time, ILogger<TaskService> logger) : ITaskService
{
    public Task<IReadOnlyList<TaskItem>> GetAsync(Guid profileId, TaskCollection collection, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetAsync(workspace, collection, DateOnly.FromDateTime(time.GetLocalNow().DateTime), cancellationToken), cancellationToken);

    public Task<TaskItem?> FindAsync(TaskReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, workspace => repository.FindAsync(workspace, reference.ItemId, cancellationToken), cancellationToken);

    public Task<TaskItem> CreateAsync(Guid profileId, TaskDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace =>
        {
            var task = NewTask(Validate(draft));
            await repository.CreateAsync(workspace, task, cancellationToken);
            return task;
        }, cancellationToken);

    public Task<TaskItem> UpdateAsync(TaskReference reference, TaskDraft draft, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task =>
        {
            RequireEditable(task);
            var valid = Validate(draft);
            return task with { Item = task.Item with { Title = valid.Title }, Description = valid.Description,
                Status = valid.Status, Priority = valid.Priority, ScheduledDate = valid.ScheduledDate };
        }, cancellationToken);

    public Task<TaskItem> ChangeStatusAsync(TaskReference reference, TaskStatus status, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task =>
        {
            RequireEditable(task);
            if (!Enum.IsDefined(status)) throw new TaskValidationException("Select a valid task status.");
            return task with { Status = status };
        }, cancellationToken);

    public Task<TaskItem> ScheduleAsync(TaskReference reference, DateOnly? date, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task => { RequireEditable(task); return task with { ScheduledDate = date }; }, cancellationToken);

    public Task<TaskItem> ApplyAsync(TaskReference reference, TaskAction action, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task =>
        {
            var now = time.GetUtcNow();
            if (action is TaskAction.Archive or TaskAction.RestoreArchive) RequireEditable(task);
            return action switch
            {
                TaskAction.Archive => task with { Item = task.Item with { ArchivedAtUtc = task.Item.ArchivedAtUtc ?? now } },
                TaskAction.RestoreArchive => task with { Item = task.Item with { ArchivedAtUtc = null } },
                TaskAction.Trash => task with { Item = task.Item with { DeletedAtUtc = task.Item.DeletedAtUtc ?? now } },
                // Restoring from Trash makes the task visible in the normal library, including previously archived tasks.
                TaskAction.RestoreTrash => task.Item.DeletedAtUtc is null ? task : task with { Item = task.Item with { DeletedAtUtc = null, ArchivedAtUtc = null } },
                _ => throw new TaskValidationException("Select a valid task action.")
            };
        }, cancellationToken);

    public Task<TaskItem> DuplicateAsync(TaskReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, async workspace =>
        {
            var source = await repository.FindAsync(workspace, reference.ItemId, cancellationToken)
                ?? throw new TaskValidationException("This task is no longer available.");
            var duplicate = NewTask(new TaskDraft(source.Item.Title, source.Description, TaskStatus.ToDo, source.Priority, source.ScheduledDate));
            await repository.CreateAsync(workspace, duplicate, cancellationToken);
            return duplicate;
        }, cancellationToken);

    public Task PermanentlyDeleteAsync(TaskReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, async workspace =>
        {
            await repository.PermanentlyDeleteAsync(workspace, reference.ItemId, cancellationToken);
            return true;
        }, cancellationToken);

    private Task<TaskItem> MutateAsync(TaskReference reference, Func<TaskItem, TaskItem> mutation, CancellationToken cancellationToken) =>
        RunAsync(reference.ProfileId, workspace => repository.UpdateAsync(workspace, reference.ItemId, task =>
        {
            var updated = mutation(task);
            if (updated == task) return task;
            var now = time.GetUtcNow();
            if (now <= task.Item.UpdatedAtUtc) now = task.Item.UpdatedAtUtc.AddTicks(1);
            return updated with { Item = updated.Item with { UpdatedAtUtc = now } };
        }, cancellationToken), cancellationToken);

    private TaskItem NewTask(TaskDraft draft)
    {
        var now = time.GetUtcNow();
        return new TaskItem(new WorkspaceItem(Guid.NewGuid(), WorkspaceItemType.Task, draft.Title, now, now, null, null),
            draft.Description, draft.Status, draft.Priority, draft.ScheduledDate);
    }

    private static TaskDraft Validate(TaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new TaskValidationException("Enter a task title.");
        if (!Enum.IsDefined(draft.Status)) throw new TaskValidationException("Select a valid task status.");
        if (!Enum.IsDefined(draft.Priority)) throw new TaskValidationException("Select a valid priority.");
        return draft with { Title = draft.Title.Trim(), Description = draft.Description ?? "" };
    }

    private static void RequireEditable(TaskItem task)
    {
        if (task.Item.DeletedAtUtc is not null) throw new TaskValidationException("Restore the task from Trash before editing it.");
    }

    private async Task<T> RunAsync<T>(Guid expectedProfileId, Func<WorkspaceContext, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        if (current.Current?.Id != expectedProfileId || current.WorkspaceDatabase is not { } database)
            throw new WorkspaceChangedException();
        try { return await operation(new WorkspaceContext(expectedProfileId, database)); }
        catch (TaskValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Task storage operation failed in profile {ProfileId}", expectedProfileId);
            throw new TaskOperationException("The task could not be loaded or saved. Check access to the local workspace and try again.", exception);
        }
    }
}
