using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Data;

public sealed class SqliteTaskRepository : ITaskRepository
{
    public async Task<TaskGraph> GetGraphAsync(WorkspaceContext workspace, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction();
        var graph = await ReadGraphAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return graph;
    }

    private static async Task<TaskGraph> ReadGraphAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var tasks = new List<TaskItem>(); var dependencies = new List<TaskDependency>();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE w.ItemType = 1;";
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tasks.Add(Read(reader));
        command.CommandText = "SELECT TaskId, DependsOnTaskId, CreatedAtUtc FROM TaskDependencies;";
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) dependencies.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), ReadUtc(reader, 2)!.Value));
        return new(tasks, dependencies);
    }

    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<TaskGraph, T> change, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var graph = await ReadGraphAsync(connection, transaction, cancellationToken);
        var original = graph.Tasks.ToDictionary(); var dependencies = graph.Dependencies.ToArray();
        var result = change(graph);
        // Persist only differences. Parent propagation and relation changes commit together.
        foreach (var task in graph.Tasks.Values)
        {
            if (original.TryGetValue(task.Item.Id, out var old) && old == task) continue;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = old is null ? """
                INSERT INTO WorkspaceItems (Id, ItemType, Title, CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc, DeletedAtUtc)
                VALUES ($id, 1, $title, $created, $updated, $archived, $deleted);
                INSERT INTO Tasks (ItemId, Description, Status, Priority, ScheduledDate, ParentTaskId)
                VALUES ($id, $description, $status, $priority, $scheduled, $parent);
                """ : """
                UPDATE WorkspaceItems SET Title=$title, UpdatedAtUtc=$updated, ArchivedAtUtc=$archived, DeletedAtUtc=$deleted WHERE Id=$id;
                UPDATE Tasks SET Description=$description, Status=$status, Priority=$priority, ScheduledDate=$scheduled, ParentTaskId=$parent WHERE ItemId=$id;
                """;
            Bind(command, task);
            command.Parameters.AddWithValue("$parent", (object?)task.ParentTaskId?.ToString("D") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var removed in dependencies.Except(graph.Dependencies))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM TaskDependencies WHERE TaskId=$task AND DependsOnTaskId=$blocker;";
            command.Parameters.AddWithValue("$task", removed.TaskId.ToString("D")); command.Parameters.AddWithValue("$blocker", removed.DependsOnTaskId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var added in graph.Dependencies.Except(dependencies))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO TaskDependencies VALUES ($task,$blocker,$created);";
            command.Parameters.AddWithValue("$task", added.TaskId.ToString("D")); command.Parameters.AddWithValue("$blocker", added.DependsOnTaskId.ToString("D"));
            command.Parameters.AddWithValue("$created", added.CreatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var id in original.Keys.Except(graph.Tasks.Keys))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM WorkspaceItems WHERE Id=$id AND ItemType=1;";
            command.Parameters.AddWithValue("$id", id.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<IReadOnlyList<TaskItem>> GetScheduledAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        CalendarQueryAsync(workspace, from, through, cancellationToken);
    public Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(WorkspaceContext workspace, CancellationToken cancellationToken) =>
        CalendarQueryAsync(workspace, null, null, cancellationToken);

    private static async Task<IReadOnlyList<TaskItem>> CalendarQueryAsync(WorkspaceContext workspace, DateOnly? from, DateOnly? through, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE w.ItemType = 1 AND w.ArchivedAtUtc IS NULL AND w.DeletedAtUtc IS NULL AND " +
            (from is null ? "t.ScheduledDate IS NULL ORDER BY w.CreatedAtUtc DESC, w.Id LIMIT 100;"
                : "t.ScheduledDate >= $from AND t.ScheduledDate <= $through ORDER BY t.ScheduledDate, w.CreatedAtUtc, w.Id;");
        if (from is { } start)
        {
            command.Parameters.AddWithValue("$from", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$through", through!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TaskItem>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    private const string SelectSql = """
        SELECT w.Id, w.ItemType, w.Title, w.CreatedAtUtc, w.UpdatedAtUtc, w.ArchivedAtUtc, w.DeletedAtUtc,
               t.Description, t.Status, t.Priority, t.ScheduledDate, t.ParentTaskId
        FROM WorkspaceItems w JOIN Tasks t ON t.ItemId = w.Id
        """;

    public async Task<IReadOnlyList<TaskItem>> GetAsync(WorkspaceContext workspace, TaskCollection collection, DateOnly today, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var command = connection.CreateCommand();
        var predicate = collection switch
        {
            TaskCollection.Active => "w.DeletedAtUtc IS NULL AND w.ArchivedAtUtc IS NULL",
            TaskCollection.Today => "w.DeletedAtUtc IS NULL AND w.ArchivedAtUtc IS NULL AND t.ScheduledDate = $today",
            TaskCollection.Archived => "w.DeletedAtUtc IS NULL AND w.ArchivedAtUtc IS NOT NULL",
            TaskCollection.Trash => "w.DeletedAtUtc IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(collection))
        };
        command.CommandText = SelectSql + " WHERE w.ItemType = 1 AND " + predicate + " ORDER BY w.CreatedAtUtc DESC, w.Id;";
        if (collection == TaskCollection.Today) command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tasks = new List<TaskItem>();
        while (await reader.ReadAsync(cancellationToken)) tasks.Add(Read(reader));
        return tasks;
    }

    public async Task<TaskItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        return await FindAsync(connection, null, id, cancellationToken);
    }

    public async Task CreateAsync(WorkspaceContext workspace, TaskItem task, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO WorkspaceItems (Id, ItemType, Title, CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc, DeletedAtUtc)
            VALUES ($id, 1, $title, $created, $updated, $archived, $deleted);
            INSERT INTO Tasks (ItemId, Description, Status, Priority, ScheduledDate)
            VALUES ($id, $description, $status, $priority, $scheduled);
            """;
        if (task.ParentTaskId is { } parent)
        {
            command.CommandText += " UPDATE Tasks SET ParentTaskId=$parent WHERE ItemId=$id;";
            command.Parameters.AddWithValue("$parent", parent.ToString("D"));
        }
        Bind(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TaskItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindAsync(connection, transaction, id, cancellationToken)
            ?? throw new TaskValidationException("This task is no longer available.");
        var task = update(existing);
        if (task.Item.Id != id || task.Item.ItemType != WorkspaceItemType.Task)
            throw new InvalidOperationException("A task update cannot change its identity.");
        if (task != existing)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE WorkspaceItems SET Title = $title, UpdatedAtUtc = $updated,
                    ArchivedAtUtc = $archived, DeletedAtUtc = $deleted WHERE Id = $id;
                UPDATE Tasks SET Description = $description, Status = $status, Priority = $priority,
                    ScheduledDate = $scheduled, ParentTaskId = $parent WHERE ItemId = $id;
                """;
            Bind(command, task);
            command.Parameters.AddWithValue("$parent", (object?)task.ParentTaskId?.ToString("D") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var task = await FindAsync(connection, transaction, id, cancellationToken)
            ?? throw new TaskValidationException("This task is no longer available.");
        if (task.Item.DeletedAtUtc is null) throw new TaskValidationException("Move the task to Trash before permanently deleting it.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM WorkspaceItems WHERE Id = $id AND ItemType = 1;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken); // Foreign-key cascade removes the Task row in the same transaction.
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task<SqliteConnection> OpenAsync(WorkspaceContext workspace, CancellationToken cancellationToken) =>
        SqliteConnectionFactory.OpenDatabaseAsync(workspace.DatabasePath, create: false, cancellationToken);

    private static async Task<TaskItem?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE w.Id = $id AND w.ItemType = 1;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static TaskItem Read(SqliteDataReader reader) => new(
        new WorkspaceItem(Guid.ParseExact(reader.GetString(0), "D"), (WorkspaceItemType)reader.GetInt32(1), reader.GetString(2),
            ReadUtc(reader, 3)!.Value, ReadUtc(reader, 4)!.Value, ReadUtc(reader, 5), ReadUtc(reader, 6)),
        reader.GetString(7), (TaskStatus)reader.GetInt32(8), (TaskPriority)reader.GetInt32(9),
        reader.IsDBNull(10) ? null : DateOnly.ParseExact(reader.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)));

    private static DateTimeOffset? ReadUtc(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null
        : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);

    private static void Bind(SqliteCommand command, TaskItem task)
    {
        command.Parameters.AddWithValue("$id", task.Item.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", task.Item.Title);
        command.Parameters.AddWithValue("$created", task.Item.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", task.Item.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$archived", (object?)task.Item.ArchivedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", (object?)task.Item.DeletedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$scheduled", (object?)task.ScheduledDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
    }
}
