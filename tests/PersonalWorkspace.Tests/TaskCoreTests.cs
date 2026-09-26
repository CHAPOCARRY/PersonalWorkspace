using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;
using Xunit;
using PersonalWorkspace.App.ViewModels;

namespace PersonalWorkspace.Tests;

public sealed class TaskCoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly WorkspaceOperationGate gate = new();
    private readonly CurrentProfile current;
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly SqliteTaskRepository repository = new();
    private readonly TestClock clock = new();
    private readonly TaskService tasks;
    private readonly OrganizationService organization;
    private Guid profileId;

    public TaskCoreTests()
    {
        paths = new ApplicationPaths(root);
        current = new CurrentProfile(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new WorkspaceInitializer(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new ProfileService(new SqliteProfileRepository(connections),
            new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance), new ProfileFiles(paths),
            initializer, current, NullLogger<ProfileService>.Instance, gate);
        tasks = new TaskService(repository, current, gate, clock, NullLogger<TaskService>.Instance);
        organization = new OrganizationService(new SqliteOrganizationRepository(), current, gate, clock, NullLogger<OrganizationService>.Instance);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profileId = (await profiles.CreateAsync("Personal")).Id;
    }
    private TaskReference Ref(TaskItem task) => new(profileId, task.Item.Id);
    private Task<TaskItem> Create(string title = "Task") => tasks.CreateAsync(profileId, new TaskDraft(title));
    private Task<IReadOnlyList<TaskItem>> Query(TaskCollection collection = TaskCollection.Active) => tasks.GetAsync(profileId, collection);

    [Fact]
    public async Task MigrationRetainsTaskCoreAndIsIdempotent()
    {
        var task = await Create();
        await initializer.InitializeAsync(profileId, false);
        await initializer.InitializeAsync(profileId, false);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Version = 1;"));
        Assert.Equal(9L, await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"));
        Assert.Equal(task, await tasks.FindAsync(Ref(task)));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync();
        using var command = global.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Tasks', 'WorkspaceItems');";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExistingPhaseOneWorkspaceUpgradesFromEmptyLedger()
    {
        var legacyId = Guid.NewGuid();
        new ProfileFiles(paths).Create(legacyId);
        await using (var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(legacyId)};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE SchemaMigrations (Version INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }
        await initializer.InitializeAsync(legacyId, false);
        await initializer.InitializeAsync(legacyId, false);
        await using var upgraded = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(legacyId)};Pooling=False");
        await upgraded.OpenAsync();
        using var check = upgraded.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM Tasks;";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
        check.CommandText = "SELECT COUNT(*) FROM SchemaMigrations;";
        Assert.Equal(4L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreationTrimsTitleUsesDefaultsAndAllowsDuplicateTitles()
    {
        var first = await Create("  Comprar leite  ");
        var second = await Create("Comprar leite");
        Assert.Equal("Comprar leite", first.Item.Title);
        Assert.Equal(TaskStatus.ToDo, first.Status);
        Assert.Equal(TaskPriority.None, first.Priority);
        Assert.Null(first.ScheduledDate);
        Assert.Equal(WorkspaceItemType.Task, first.Item.ItemType);
        Assert.Equal(TimeSpan.Zero, first.Item.CreatedAtUtc.Offset);
        Assert.NotEqual(first.Item.Id, second.Item.Id);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t\r\n")]
    public async Task WhitespaceTitleIsRejectedWithoutRows(string title)
    {
        await Assert.ThrowsAsync<TaskValidationException>(() => Create(title));
        Assert.Empty(await Query());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
    }

    [Fact]
    public async Task FailedTaskInsertRollsBackWorkspaceItem()
    {
        await Execute("CREATE TRIGGER FailTask BEFORE INSERT ON Tasks BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Create());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
    }

    [Fact]
    public async Task ReadAndNoOpUpdatePreserveTimestampMeaningfulEditsAdvanceIt()
    {
        var task = await Create();
        clock.Advance();
        Assert.Equal(task, await tasks.FindAsync(Ref(task)));
        Assert.Equal(task, await tasks.UpdateAsync(Ref(task), new TaskDraft(task.Item.Title)));
        var changed = await tasks.UpdateAsync(Ref(task), new TaskDraft(" Updated ", "Plain text\nSecond line", TaskStatus.Blocked, TaskPriority.Critical));
        Assert.Equal("Updated", changed.Item.Title);
        Assert.Equal("Plain text\nSecond line", changed.Description);
        Assert.Equal(TaskPriority.Critical, changed.Priority);
        Assert.Equal(TaskStatus.Blocked, changed.Status);
        Assert.Equal(task.Item.CreatedAtUtc, changed.Item.CreatedAtUtc);
        Assert.True(changed.Item.UpdatedAtUtc > task.Item.UpdatedAtUtc);
        Assert.Equal(changed, await tasks.FindAsync(Ref(changed)));
    }

    [Fact]
    public async Task FailedTaskUpdateRollsBackBothRows()
    {
        var task = await Create();
        await Execute("CREATE TRIGGER FailUpdate BEFORE UPDATE ON Tasks BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => tasks.UpdateAsync(Ref(task), new TaskDraft("Changed", "Description")));
        Assert.Equal(task, await tasks.FindAsync(Ref(task)));
    }

    [Theory]
    [InlineData(TaskStatus.ToDo)]
    [InlineData(TaskStatus.Doing)]
    [InlineData(TaskStatus.Blocked)]
    public async Task DoneCanBeReopenedToAnyIncompleteStatus(TaskStatus reopenedStatus)
    {
        var task = await Create();
        var done = await tasks.ChangeStatusAsync(Ref(task), TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
        var reopened = await tasks.ChangeStatusAsync(Ref(task), reopenedStatus);
        Assert.Equal(reopenedStatus, reopened.Status);
        Assert.True(reopened.Item.UpdatedAtUtc > done.Item.UpdatedAtUtc);
    }

    [Fact]
    public async Task SchedulingIsDateOnlyAndCanBeCleared()
    {
        var task = await Create();
        var date = new DateOnly(2026, 10, 25); // DST transition must not move the calendar date.
        var scheduled = await tasks.ScheduleAsync(Ref(task), date);
        Assert.Equal(date, scheduled.ScheduledDate);
        Assert.Equal("2026-10-25", await Scalar("SELECT ScheduledDate FROM Tasks;"));
        var unscheduled = await tasks.ScheduleAsync(Ref(task), null);
        Assert.Null(unscheduled.ScheduledDate);
        Assert.Single(await Query());
    }

    [Fact]
    public async Task TodayUsesLocalDateAndSamePersistedRecords()
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        Assert.NotEqual(DateOnly.FromDateTime(clock.GetUtcNow().DateTime), today);
        var scheduled = await tasks.CreateAsync(profileId, new TaskDraft("Today", ScheduledDate: today));
        await Create("Unscheduled");
        await tasks.CreateAsync(profileId, new TaskDraft("Tomorrow", ScheduledDate: today.AddDays(1)));
        var fromToday = Assert.Single(await Query(TaskCollection.Today));
        Assert.Equal(scheduled.Item.Id, fromToday.Item.Id);
        await tasks.ChangeStatusAsync(Ref(fromToday), TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, (await Query()).Single(t => t.Item.Id == scheduled.Item.Id).Status);
        Assert.Equal(TaskStatus.Done, Assert.Single(await Query(TaskCollection.Today)).Status);
    }

    [Fact]
    public async Task ArchiveAndTrashAreIndependentOfStatusAndExcludedFromToday()
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var task = await tasks.CreateAsync(profileId, new TaskDraft("Task", Status: TaskStatus.Doing, ScheduledDate: today));
        var archived = await tasks.ApplyAsync(Ref(task), TaskAction.Archive);
        Assert.Equal(TaskStatus.Doing, archived.Status);
        Assert.Empty(await Query());
        Assert.Empty(await Query(TaskCollection.Today));
        Assert.Equal(task.Item.Id, Assert.Single(await Query(TaskCollection.Archived)).Item.Id);
        await tasks.ApplyAsync(Ref(task), TaskAction.RestoreArchive);
        Assert.Single(await Query());
        await tasks.ApplyAsync(Ref(task), TaskAction.Archive);
        await tasks.ApplyAsync(Ref(task), TaskAction.Trash);
        Assert.Empty(await Query(TaskCollection.Archived));
        Assert.Empty(await Query(TaskCollection.Today));
        Assert.NotNull(Assert.Single(await Query(TaskCollection.Trash)).Item.DeletedAtUtc);
        var restored = await tasks.ApplyAsync(Ref(task), TaskAction.RestoreTrash);
        Assert.Null(restored.Item.DeletedAtUtc);
        Assert.Null(restored.Item.ArchivedAtUtc);
        Assert.Single(await Query());
        Assert.Single(await Query(TaskCollection.Today));
    }

    [Fact]
    public async Task PermanentDeletionRequiresTrashAndCascadesBothRows()
    {
        var task = await Create();
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.PermanentlyDeleteAsync(Ref(task)));
        await tasks.ApplyAsync(Ref(task), TaskAction.Trash);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
        await tasks.PermanentlyDeleteAsync(Ref(task));
        Assert.Null(await tasks.FindAsync(Ref(task)));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
    }

    [Fact]
    public async Task CascadeFailureRollsBackPermanentDeletion()
    {
        var task = await Create();
        await tasks.ApplyAsync(Ref(task), TaskAction.Trash);
        await Execute("CREATE TRIGGER FailDelete BEFORE DELETE ON Tasks BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => tasks.PermanentlyDeleteAsync(Ref(task)));
        Assert.Single(await Query(TaskCollection.Trash));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
    }

    [Fact]
    public async Task DuplicateCopiesOnlyAllowedFieldsAndIsIndependent()
    {
        var original = await tasks.CreateAsync(profileId, new TaskDraft("Original", "Description", TaskStatus.Done, TaskPriority.High, new DateOnly(2026, 9, 25)));
        await tasks.ApplyAsync(Ref(original), TaskAction.Archive);
        await tasks.ApplyAsync(Ref(original), TaskAction.Trash);
        clock.Advance();
        var duplicate = await tasks.DuplicateAsync(Ref(original));
        Assert.NotEqual(original.Item.Id, duplicate.Item.Id);
        Assert.Equal(original.Item.Title, duplicate.Item.Title);
        Assert.Equal(original.Description, duplicate.Description);
        Assert.Equal(original.Priority, duplicate.Priority);
        Assert.Equal(original.ScheduledDate, duplicate.ScheduledDate);
        Assert.Equal(TaskStatus.ToDo, duplicate.Status);
        Assert.Null(duplicate.Item.ArchivedAtUtc);
        Assert.Null(duplicate.Item.DeletedAtUtc);
        Assert.True(duplicate.Item.CreatedAtUtc > original.Item.CreatedAtUtc);
        Assert.Equal(duplicate.Item.CreatedAtUtc, duplicate.Item.UpdatedAtUtc);
        Assert.Equal(duplicate, Assert.Single(await Query()));
    }

    [Fact]
    public async Task ProfileIsolationRejectsStaleWritesAndPersistsAcrossReopen()
    {
        var taskA = await Create("A");
        var profileB = await profiles.CreateAsync("Testing");
        Assert.Empty(await tasks.GetAsync(profileB.Id, TaskCollection.Active));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => tasks.UpdateAsync(Ref(taskA), new TaskDraft("Wrong workspace")));
        var taskB = await tasks.CreateAsync(profileB.Id, new TaskDraft("B"));
        await profiles.SwitchAsync(profileId);
        Assert.Equal(taskA, Assert.Single(await Query()));
        Assert.Null(await tasks.FindAsync(new TaskReference(profileId, taskB.Item.Id)));
        await profiles.RestoreAsync();
        Assert.Equal(taskA, Assert.Single(await Query()));
        await profiles.SwitchAsync(profileB.Id);
        Assert.Equal(taskB, Assert.Single(await tasks.GetAsync(profileB.Id, TaskCollection.Active)));
    }

    [Fact]
    public async Task NoCurrentProfileDoesNotOpenAWorkspace()
    {
        await profiles.DeleteAsync(profileId);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Create());
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Query());
        Assert.Empty(Directory.EnumerateDirectories(paths.Profiles));
    }

    [Fact]
    public async Task InvalidEnumsAndEditingTrashAreRejected()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.CreateAsync(profileId, new TaskDraft("Task", Priority: (TaskPriority)99)));
        var task = await Create();
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.ChangeStatusAsync(Ref(task), (TaskStatus)99));
        await tasks.ApplyAsync(Ref(task), TaskAction.Trash);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.UpdateAsync(Ref(task), new TaskDraft("Changed")));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.ScheduleAsync(Ref(task), new DateOnly(2026, 9, 24)));
    }

    [Fact]
    public async Task ProfileSwitchWaitsForInFlightTaskOperation()
    {
        var profileB = await profiles.CreateAsync("Testing");
        await profiles.SwitchAsync(profileId);
        var delayed = new DelayedRepository(repository);
        var delayedService = new TaskService(delayed, current, gate, clock, NullLogger<TaskService>.Instance);
        var create = delayedService.CreateAsync(profileId, new TaskDraft("In flight"));
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = profiles.SwitchAsync(profileB.Id);
        Assert.False(switching.IsCompleted);
        Assert.Equal(profileId, current.Current?.Id);
        delayed.Continue.SetResult();
        var task = await create;
        await switching;
        Assert.Empty(await tasks.GetAsync(profileB.Id, TaskCollection.Active));
        await profiles.SwitchAsync(profileId);
        Assert.Equal(task, Assert.Single(await Query()));
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(profileId)};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private async Task Execute(string sql) => await Scalar(sql);

    [Fact]
    public async Task ProfileChangeClearsDetailDraftAndReloadsOnlyNewProfileRows()
    {
        var task = await Create("Personal task");
        var navigation = new NavigationService();
        navigation.Navigate(new NavigationRoute("Tasks"));
        var model = new TaskWorkspaceViewModel(tasks, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, organization);
        await model.ReloadAsync();
        model.OpenCommand.Execute(Assert.Single(model.Rows));
        await model.ReloadAsync();
        Assert.True(model.IsDetail);
        model.EditorTitle = "Unsaved personal draft";
        var testing = await profiles.CreateAsync("Testing");
        await model.ReloadAsync();
        Assert.False(model.IsDetail);
        Assert.Null(model.Detail);
        Assert.Equal("", model.EditorTitle);
        Assert.Empty(model.Rows);
        await tasks.CreateAsync(testing.Id, new TaskDraft("Testing task"));
        await model.ReloadAsync();
        Assert.Equal("Testing task", Assert.Single(model.Rows).Title);
        await profiles.SwitchAsync(profileId);
        await model.ReloadAsync();
        Assert.Equal(task, Assert.Single(model.Rows).Task);
    }

    [Fact]
    public async Task DelayedOldProfileReadCannotRepopulateNewProfileUi()
    {
        await Create("Personal task");
        var delayed = new DelayedReadService(tasks);
        var navigation = new NavigationService();
        navigation.Navigate(new NavigationRoute("Tasks"));
        var model = new TaskWorkspaceViewModel(delayed, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, organization);
        var oldRead = model.ReloadAsync();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await profiles.CreateAsync("Testing");
        await model.ReloadAsync();
        Assert.Empty(model.Rows);
        delayed.Continue.SetResult();
        await oldRead;
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task LibraryTitleFilterDoesNotHideTasksInOtherAreas()
    {
        await tasks.CreateAsync(profileId, new TaskDraft("Scheduled", ScheduledDate: DateOnly.FromDateTime(clock.GetLocalNow().DateTime)));
        var navigation = new NavigationService();
        navigation.Navigate(new NavigationRoute("Tasks"));
        var model = new TaskWorkspaceViewModel(tasks, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, organization);
        await model.ReloadAsync();
        model.Filter = "No match";
        Assert.Empty(model.Rows);
        navigation.Navigate(new NavigationRoute("Today"));
        await model.ReloadAsync();
        Assert.Single(model.Rows);
    }

    private sealed class DelayedReadService(ITaskService inner) : ITaskService
    {
        public Task<TaskGraph> GetGraphAsync(Guid profileId, CancellationToken cancellationToken = default) => inner.GetGraphAsync(profileId, cancellationToken);
        public Task<TaskItem> CreateSubtaskAsync(TaskReference parent, string title, CancellationToken cancellationToken = default) => inner.CreateSubtaskAsync(parent, title, cancellationToken);
        public Task SetParentAsync(TaskReference task, Guid? parentId, CancellationToken cancellationToken = default) => inner.SetParentAsync(task, parentId, cancellationToken);
        public Task AddDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default) => inner.AddDependencyAsync(task, dependencyId, cancellationToken);
        public Task RemoveDependencyAsync(TaskReference task, Guid dependencyId, CancellationToken cancellationToken = default) => inner.RemoveDependencyAsync(task, dependencyId, cancellationToken);
        private int delayNext = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<TaskItem>> GetAsync(Guid profileId, TaskCollection collection, CancellationToken cancellationToken = default)
        {
            var result = await inner.GetAsync(profileId, collection, cancellationToken);
            if (Interlocked.Exchange(ref delayNext, 0) == 1)
            {
                Entered.SetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public Task<IReadOnlyList<TaskItem>> GetScheduledAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default) => inner.GetScheduledAsync(profileId, from, through, cancellationToken);
        public Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(Guid profileId, CancellationToken cancellationToken = default) => inner.GetUnscheduledAsync(profileId, cancellationToken);
        public Task<TaskItem?> FindAsync(TaskReference reference, CancellationToken cancellationToken = default) => inner.FindAsync(reference, cancellationToken);
        public Task<TaskItem> CreateAsync(Guid profileId, TaskDraft draft, CancellationToken cancellationToken = default) => inner.CreateAsync(profileId, draft, cancellationToken);
        public Task<TaskItem> UpdateAsync(TaskReference reference, TaskDraft draft, CancellationToken cancellationToken = default) => inner.UpdateAsync(reference, draft, cancellationToken);
        public Task<TaskItem> ChangeStatusAsync(TaskReference reference, TaskStatus status, CancellationToken cancellationToken = default) => inner.ChangeStatusAsync(reference, status, cancellationToken);
        public Task<TaskItem> ScheduleAsync(TaskReference reference, DateOnly? date, CancellationToken cancellationToken = default) => inner.ScheduleAsync(reference, date, cancellationToken);
        public Task<TaskItem> ApplyAsync(TaskReference reference, TaskAction action, CancellationToken cancellationToken = default) => inner.ApplyAsync(reference, action, cancellationToken);
        public Task<TaskItem> DuplicateAsync(TaskReference reference, CancellationToken cancellationToken = default) => inner.DuplicateAsync(reference, cancellationToken);
        public Task PermanentlyDeleteAsync(TaskReference reference, CancellationToken cancellationToken = default) => inner.PermanentlyDeleteAsync(reference, cancellationToken);
    }

    public Task DisposeAsync()
    {
        profiles.Dispose();
        gate.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 9, 24, 23, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utc;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("TestLocal", TimeSpan.FromHours(2), "TestLocal", "TestLocal");
        public void Advance() => utc = utc.AddMinutes(1);
    }

    private sealed class DelayedRepository(ITaskRepository inner) : ITaskRepository
    {
        public Task<TaskGraph> GetGraphAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => inner.GetGraphAsync(workspace, cancellationToken);
        public Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<TaskGraph,T> change, CancellationToken cancellationToken) => inner.TransactAsync(workspace, change, cancellationToken);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CreateAsync(WorkspaceContext workspace, TaskItem task, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            await inner.CreateAsync(workspace, task, cancellationToken);
        }
        public Task<IReadOnlyList<TaskItem>> GetAsync(WorkspaceContext workspace, TaskCollection collection, DateOnly today, CancellationToken cancellationToken) => inner.GetAsync(workspace, collection, today, cancellationToken);
        public Task<IReadOnlyList<TaskItem>> GetScheduledAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken) => inner.GetScheduledAsync(workspace, from, through, cancellationToken);
        public Task<IReadOnlyList<TaskItem>> GetUnscheduledAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => inner.GetUnscheduledAsync(workspace, cancellationToken);
        public Task<TaskItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken) => inner.FindAsync(workspace, id, cancellationToken);
        public Task<TaskItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<TaskItem, TaskItem> update, CancellationToken cancellationToken) => inner.UpdateAsync(workspace, id, update, cancellationToken);
        public Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken) => inner.PermanentlyDeleteAsync(workspace, id, cancellationToken);
    }
}
