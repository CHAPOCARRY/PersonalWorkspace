using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Services;

public sealed class TaskService(ITaskRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider time, ILogger<TaskService> logger) : ITaskService
{
    public Task<TaskItem> RecordActualAsync(TaskReference task, decimal? actual, CancellationToken cancellationToken = default) =>
        MutateAsync(task, existing =>
        {
            RequireEditable(existing);
            var value = existing.Value ?? throw new TaskValidationException("Choose a value type and save its target before recording an actual.");
            return existing with { Value = (value with { Actual = actual }).Validate() };
        }, cancellationToken, evaluateValue: true);

    public Task<TaskGraph> GetGraphAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetGraphAsync(workspace, cancellationToken), cancellationToken);

    public Task<TaskItem> CreateSubtaskAsync(TaskReference parent, string title, CancellationToken cancellationToken = default) =>
        GraphChangeAsync(parent.ProfileId, graph =>
        {
            RequireEditable(Find(graph, parent.ItemId));
            var child = NewTask(Validate(new(title))) with { ParentTaskId = parent.ItemId };
            graph.Tasks.Add(child.Item.Id, child);
            Recalculate(graph);
            return child;
        }, cancellationToken);

    public Task SetParentAsync(TaskReference task, Guid? parentId, CancellationToken cancellationToken = default) =>
        GraphChangeAsync(task.ProfileId, graph =>
        {
            var existing = Find(graph, task.ItemId); RequireEditable(existing);
            if (parentId is { } id) RequireEditable(Find(graph, id));
            graph.Tasks[task.ItemId] = Stamp(existing, existing with { ParentTaskId = parentId });
            Recalculate(graph);
            return true;
        }, cancellationToken);

    public Task AddDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default) =>
        GraphChangeAsync(task.ProfileId, graph =>
        {
            var existing = Find(graph, task.ItemId); RequireEditable(existing);
            var blocker = Find(graph, dependencyId);
            if (!graph.CanDependOn(task.ItemId, dependencyId))
                throw new TaskValidationException("This dependency is already assigned or would create a completion cycle with this task or its subtasks.");
            if (existing.Status == TaskStatus.Done && blocker.Status != TaskStatus.Done)
                throw new TaskValidationException("Reopen this task before adding an incomplete dependency.");
            graph.Dependencies.Add(new(task.ItemId, dependencyId, time.GetUtcNow()));
            Recalculate(graph);
            return true;
        }, cancellationToken);

    public Task RemoveDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default) =>
        GraphChangeAsync(task.ProfileId, graph =>
        {
            RequireEditable(Find(graph, task.ItemId));
            graph.Dependencies.RemoveAll(d => d.TaskId == task.ItemId && d.DependsOnTaskId == dependencyId);
            Recalculate(graph);
            return true;
        }, cancellationToken);

    private Task<T> GraphChangeAsync<T>(Guid profile, Func<TaskGraph, T> change, CancellationToken cancellationToken) =>
        RunAsync(profile, workspace => repository.TransactAsync(workspace, change, cancellationToken), cancellationToken);

    private static TaskItem Find(TaskGraph graph, Guid id) => graph.Tasks.GetValueOrDefault(id)
        ?? throw new TaskValidationException("This task is not available in the active profile.");

    private TaskItem Stamp(TaskItem old, TaskItem updated)
    {
        if (old == updated) return old;
        var now = time.GetUtcNow();
        if (now <= old.Item.UpdatedAtUtc) now = old.Item.UpdatedAtUtc.AddTicks(1);
        return updated with { Item = updated.Item with { UpdatedAtUtc = now } };
    }

    private void Recalculate(TaskGraph graph, Guid? valueTask = null)
    {
        var order = graph.CompletionOrder(); // Includes hierarchy AND dependency prerequisite edges.
        var children = graph.Tasks.Values.Where(t => t.ParentTaskId.HasValue).ToLookup(t => t.ParentTaskId!.Value, t => t.Item.Id);
        var prerequisites = graph.Prerequisites();
        foreach (var id in order)
        {
            if (!children.Contains(id) && id != valueTask) continue; // A blocker change alone never completes a leaf dependent.
            var task = graph.Tasks[id];
            var complete = (task.Value?.IsReached ?? true) && prerequisites[id].All(required => graph.Tasks[required].Status == TaskStatus.Done);
            var status = complete ? TaskStatus.Done : task.Status == TaskStatus.Done ? TaskStatus.ToDo : task.Status;
            graph.Tasks[id] = Stamp(task, task with { Status = status });
        }
    }

    public Task<IReadOnlyList<TaskItem>> GetScheduledAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => from <= through ? repository.GetScheduledAsync(workspace, from, through, cancellationToken)
            : throw new TaskValidationException("The end date cannot be before the start date."), cancellationToken);
    public Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetUnscheduledAsync(workspace, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TaskItem>> GetAsync(Guid profileId, TaskCollection collection, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetAsync(workspace, collection, DateOnly.FromDateTime(time.GetLocalNow().DateTime), cancellationToken), cancellationToken);

    public Task<TaskItem?> FindAsync(TaskReference reference, CancellationToken cancellationToken = default) =>
        RunAsync(reference.ProfileId, workspace => repository.FindAsync(workspace, reference.ItemId, cancellationToken), cancellationToken);

    public Task<TaskItem> CreateAsync(Guid profileId, TaskDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace =>
        {
            var task = NewTask(Validate(draft));
            if (task.Value is { } value)
            {
                if (task.Status == TaskStatus.Done && !value.IsReached) throw new TaskValidationException("Record an actual that reaches the target before marking this task Done.");
                if (value.IsReached) task = task with { Status = TaskStatus.Done };
            }
            await repository.CreateAsync(workspace, task, cancellationToken);
            return task;
        }, cancellationToken);

    public Task<TaskItem> UpdateAsync(TaskReference reference, TaskDraft draft, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task =>
        {
            RequireEditable(task);
            var valid = Validate(draft);
            return task with { Item = task.Item with { Title = valid.Title }, Description = valid.Description,
                Status = valid.Status, Priority = valid.Priority, ScheduledDate = valid.ScheduledDate, Value = valid.Value };
        }, cancellationToken, evaluateValue: true);

    public Task<TaskItem> ChangeStatusAsync(TaskReference reference, TaskStatus status, CancellationToken cancellationToken = default) =>
        MutateAsync(reference, task =>
        {
            RequireEditable(task);
            if (!Enum.IsDefined(status)) throw new TaskValidationException("Select a valid task status.");
            if (task.Status == TaskStatus.Done && status != TaskStatus.Done && task.Value?.IsReached == true)
                throw new TaskValidationException("Lower or clear Actual to reopen this value-based task.");
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
            var duplicate = NewTask(new TaskDraft(source.Item.Title, source.Description, TaskStatus.ToDo, source.Priority, source.ScheduledDate,
                source.Value is { } value ? value with { Actual = null } : null));
            await repository.CreateAsync(workspace, duplicate, cancellationToken);
            return duplicate;
        }, cancellationToken);

    public Task PermanentlyDeleteAsync(TaskReference reference, CancellationToken cancellationToken = default) =>
        GraphChangeAsync(reference.ProfileId, graph =>
        {
            var task = Find(graph, reference.ItemId);
            if (task.Item.DeletedAtUtc is null) throw new TaskValidationException("Move the task to Trash before permanently deleting it.");
            graph.Tasks.Remove(reference.ItemId);
            foreach (var child in graph.Tasks.Values.Where(t => t.ParentTaskId == reference.ItemId).ToArray())
                graph.Tasks[child.Item.Id] = Stamp(child, child with { ParentTaskId = null });
            graph.Dependencies.RemoveAll(d => d.TaskId == reference.ItemId || d.DependsOnTaskId == reference.ItemId);
            Recalculate(graph);
            return true;
        }, cancellationToken);

    private Task<TaskItem> MutateAsync(TaskReference reference, Func<TaskItem, TaskItem> mutation, CancellationToken cancellationToken, bool evaluateValue = false) =>
        GraphChangeAsync(reference.ProfileId, graph =>
        {
            var task = Find(graph, reference.ItemId);
            var updated = mutation(task);
            if (updated.Status == TaskStatus.Done && task.Status != TaskStatus.Done)
            {
                if (updated.Value is { IsReached: false })
                    throw new TaskValidationException("Record an actual that reaches the target before marking this task Done.");
                var incomplete = graph.Prerequisites()[task.Item.Id].Where(id => graph.Tasks[id].Status != TaskStatus.Done).ToArray();
                if (incomplete.Length > 0)
                    throw new TaskValidationException("Complete required subtasks and dependencies first: " + string.Join(", ", incomplete.Take(3).Select(id => graph.Tasks[id].Item.Title)) + ".");
            }
            if (updated.Status != TaskStatus.Done && task.Status == TaskStatus.Done && (updated.Value?.IsReached ?? true)
                && graph.Tasks.Values.Any(t => t.ParentTaskId == task.Item.Id))
                throw new TaskValidationException("Reopen a completed subtask to reopen this parent task.");
            graph.Tasks[task.Item.Id] = Stamp(task, updated);
            Recalculate(graph, evaluateValue && updated.Value is not null ? task.Item.Id : null);
            return graph.Tasks[task.Item.Id];
        }, cancellationToken);

    private TaskItem NewTask(TaskDraft draft)
    {
        var now = time.GetUtcNow();
        return new TaskItem(new WorkspaceItem(Guid.NewGuid(), WorkspaceItemType.Task, draft.Title, now, now, null, null),
            draft.Description, draft.Status, draft.Priority, draft.ScheduledDate, Value: draft.Value);
    }

    private static TaskDraft Validate(TaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new TaskValidationException("Enter a task title.");
        if (!Enum.IsDefined(draft.Status)) throw new TaskValidationException("Select a valid task status.");
        if (!Enum.IsDefined(draft.Priority)) throw new TaskValidationException("Select a valid priority.");
        return draft with { Title = draft.Title.Trim(), Description = draft.Description ?? "", Value = draft.Value?.Validate() };
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
