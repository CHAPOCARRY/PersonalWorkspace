namespace PersonalWorkspace.Core;

public sealed record TaskDependency(Guid TaskId, Guid DependsOnTaskId, DateTimeOffset CreatedAtUtc);
public sealed record TaskProgress(int Completed, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100d * Completed / Total, MidpointRounding.AwayFromZero);
    public override string ToString() => Total == 0 ? "" : $"{Completed} / {Total} · {Percent}%";
}

// An operation-scoped batch; it is never cached across profiles or shared with an active write.
public sealed class TaskGraph(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency> dependencies)
{
    public Dictionary<Guid, TaskItem> Tasks { get; } = tasks.ToDictionary(task => task.Item.Id);
    public List<TaskDependency> Dependencies { get; } = dependencies.ToList();

    public IReadOnlyDictionary<Guid, TaskProgress> Progress()
    {
        var children = Tasks.Values.Where(t => t.ParentTaskId.HasValue).ToLookup(t => t.ParentTaskId!.Value);
        var result = new Dictionary<Guid, TaskProgress>();
        // Explicit stacks support deep nesting without consuming the call stack.
        foreach (var root in Tasks.Keys)
        {
            var stack = new Stack<(Guid Id, bool Visited)>(); stack.Push((root, false));
            while (stack.TryPop(out var node))
            {
                if (result.ContainsKey(node.Id)) continue;
                if (!node.Visited)
                {
                    stack.Push((node.Id, true));
                    foreach (var child in children[node.Id]) stack.Push((child.Item.Id, false));
                }
                else
                {
                    var descendants = children[node.Id].ToArray();
                    result[node.Id] = descendants.Length == 0
                        ? new(Tasks[node.Id].Status == TaskStatus.Done ? 1 : 0, 1)
                        : new(descendants.Sum(c => result[c.Item.Id].Completed), descendants.Sum(c => result[c.Item.Id].Total));
                }
            }
        }
        return result.Where(pair => children.Contains(pair.Key)).ToDictionary();
    }

    public bool CanDependOn(Guid task, Guid blocker) => task != blocker && Tasks.ContainsKey(blocker)
        && !Dependencies.Any(d => d.TaskId == task && d.DependsOnTaskId == blocker)
        && !Reaches(blocker, task);

    private bool Reaches(Guid start, Guid target)
    {
        var edges = Prerequisites();
        var pending = new Stack<Guid>(); pending.Push(start);
        var seen = new HashSet<Guid>();
        while (pending.TryPop(out var id))
        {
            if (id == target) return true;
            if (seen.Add(id)) foreach (var next in edges[id]) pending.Push(next);
        }
        return false;
    }

    public ILookup<Guid, Guid> Prerequisites() => Tasks.Values.Where(t => t.ParentTaskId.HasValue)
        .Select(t => (Task: t.ParentTaskId!.Value, Required: t.Item.Id))
        .Concat(Dependencies.Select(d => (Task: d.TaskId, Required: d.DependsOnTaskId)))
        .Distinct().ToLookup(edge => edge.Task, edge => edge.Required);

    public IReadOnlyList<Guid> CompletionOrder()
    {
        var edges = Prerequisites();
        var remaining = Tasks.Keys.ToDictionary(id => id, id => edges[id].Count());
        var reverse = edges.SelectMany(group => group.Select(required => (Required: required, Task: group.Key)))
            .ToLookup(edge => edge.Required, edge => edge.Task);
        var ready = new Queue<Guid>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = new List<Guid>();
        while (ready.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var dependent in reverse[id]) if (--remaining[dependent] == 0) ready.Enqueue(dependent);
        }
        if (order.Count != Tasks.Count) throw new TaskValidationException("This relationship would create a hierarchy or dependency cycle. Choose a task that does not require this task to finish.");
        return order;
    }
}
