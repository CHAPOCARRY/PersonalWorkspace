using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed class SubtaskDependencyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly WorkspaceOperationGate gate = new();
    private readonly CurrentProfile current;
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly TaskService tasks;
    private readonly OrganizationService organization;
    private Guid profile;
    public SubtaskDependencyTests()
    {
        paths = new(root); current = new(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        tasks = new(new SqliteTaskRepository(), current, gate, TimeProvider.System, NullLogger<TaskService>.Instance);
        organization = new(new SqliteOrganizationRepository(), current, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
    }
    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profile = (await profiles.CreateAsync("Personal")).Id;
    }
    private TaskReference Ref(TaskItem item) => new(profile, item.Item.Id);
    private Task<TaskItem> Create(string title = "Task") => tasks.CreateAsync(profile, new(title));
    private Task<TaskItem> Child(TaskItem parent, string title = "Child") => tasks.CreateSubtaskAsync(Ref(parent), title);
    private Task<TaskItem> Status(TaskItem task, TaskStatus status = TaskStatus.Done) => tasks.ChangeStatusAsync(Ref(task), status);
    private Task<TaskItem?> Read(TaskItem task) => tasks.FindAsync(Ref(task));
    private async Task<TaskGraph> Graph() => await tasks.GetGraphAsync(profile);
    private async Task<object?> Sql(string sql, Guid? workspace = null)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(workspace ?? profile)};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync(); connection.CreateCollation("WORKSPACE_NAME", (a,b) => StringComparer.OrdinalIgnoreCase.Compare(a,b));
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task PhaseFourUpgradePreservesTasksEventsAndLedgerAndIsIdempotent()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(3))
            await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES ({migration.Version}, '{migration.Name}', 'original');", legacy);
        var context = new WorkspaceContext(legacy, paths.WorkspaceDatabase(legacy));
        var now = DateTimeOffset.UtcNow;
        var oldTask = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Existing", now, now, null, null), "Details", TaskStatus.Doing, TaskPriority.High, new(2026,9,26));
        await new SqliteTaskRepository().CreateAsync(context, oldTask, default);
        var oldEvent = new EventItem(new(Guid.NewGuid(), WorkspaceItemType.Event, "Holiday", now, now, null, null), true, new(2026,9,26), null, new(2026,9,28), null);
        await new SqliteEventRepository().CreateAsync(context, oldEvent, default);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(oldTask, await new SqliteTaskRepository().FindAsync(context, oldTask.Item.Id, default));
        Assert.Equal(oldEvent, await new SqliteEventRepository().FindAsync(context, oldEvent.Item.Id, default));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TaskDependencies;", legacy));
    }

    [Fact]
    public async Task ChildDefaultsAndOrganizationDoNotInheritAndRemainIndependent()
    {
        var parent = await tasks.CreateAsync(profile, new("Parent", "Description", TaskStatus.Doing, TaskPriority.High, new(2026,9,26)));
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("Parent tag"));
        await organization.AssignAsync(new(profile, parent.Item.Id), OrganizationKind.Tag, tag, true);
        var parentSpace = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Parent space"));
        var childSpace = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Child space"));
        await organization.AssignAsync(new(profile, parent.Item.Id), OrganizationKind.Space, parentSpace, true);
        var child = await Child(parent, " Child ");
        Assert.Equal("Child", child.Item.Title); Assert.Equal(parent.Item.Id, child.ParentTaskId);
        Assert.Equal(TaskStatus.ToDo, child.Status); Assert.Equal(TaskPriority.None, child.Priority);
        Assert.Null(child.ScheduledDate); Assert.Equal("", child.Description);
        Assert.DoesNotContain((await organization.GetAsync(profile)).ItemTags, link => link.ItemId == child.Item.Id);
        Assert.DoesNotContain((await organization.GetAsync(profile)).ItemSpaces, link => link.ItemId == child.Item.Id);
        await organization.AssignAsync(new(profile, child.Item.Id), OrganizationKind.Space, childSpace, true);
        Assert.True((await organization.GetAsync(profile)).Matches(child.Item.Id, null, childSpace));
        Assert.False((await organization.GetAsync(profile)).Matches(child.Item.Id, null, parentSpace));
        await Assert.ThrowsAsync<TaskValidationException>(() => Child(parent, "  "));
        await tasks.UpdateAsync(Ref(child), new("Edited", "Independent", TaskStatus.Doing, TaskPriority.Critical, new(2026,9,27)));
        Assert.Equal(parent, await Read(parent));
    }

    [Fact]
    public async Task LeafProgressCompletionAndReopeningPropagateWithTimestamps()
    {
        var trip = await Create("Prepare trip"); var flights = await Child(trip, "Flights"); var hotel = await Child(trip, "Hotel");
        var booking = await Child(hotel, "Booking"); var airbnb = await Child(hotel, "Airbnb"); var bags = await Child(trip, "Bags");
        Assert.Equal(new TaskProgress(0,4), (await Graph()).Progress()[trip.Item.Id]);
        await Status(flights); await Status(booking);
        Assert.Equal(new TaskProgress(2,4), (await Graph()).Progress()[trip.Item.Id]);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(trip));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.UpdateAsync(Ref(trip), new("Trip", Status: TaskStatus.Done)));
        await Status(airbnb); Assert.Equal(TaskStatus.Done, (await Read(hotel))!.Status);
        await Status(bags); var complete = (await Read(trip))!; Assert.Equal(TaskStatus.Done, complete.Status);
        Assert.True(complete.Item.UpdatedAtUtc > trip.Item.UpdatedAtUtc);
        await Status(booking, TaskStatus.Doing);
        Assert.Equal(TaskStatus.ToDo, (await Read(hotel))!.Status); Assert.Equal(TaskStatus.ToDo, (await Read(trip))!.Status);
        Assert.True((await Read(trip))!.Item.UpdatedAtUtc > complete.Item.UpdatedAtUtc);
        Assert.Equal(TaskStatus.Done, (await Read(flights))!.Status);
    }

    [Fact]
    public async Task DeepHierarchyCountsOnlyLeavesAndRoundsTwoThirds()
    {
        var rootTask = await Create(); var branch = rootTask;
        for (var i=0; i<40; i++) branch = await Child(branch);
        var second = await Child(rootTask); await Child(rootTask);
        await Status(branch); await Status(second);
        var progress = (await Graph()).Progress()[rootTask.Item.Id];
        Assert.Equal(new TaskProgress(2,3), progress); Assert.Equal(67, progress.Percent);
    }

    [Fact]
    public async Task CreatingIncompleteChildReopensCompletedAncestors()
    {
        var parent = await Create(); var child = await Child(parent); await Status(child);
        var newChild = await Child(child);
        Assert.Equal(TaskStatus.ToDo, (await Read(child))!.Status);
        Assert.Equal(TaskStatus.ToDo, (await Read(parent))!.Status);
        Assert.Equal(child.Item.Id, newChild.ParentTaskId);
    }

    [Fact]
    public async Task HierarchySelfDirectAndDeepCyclesAreRejectedAtomically()
    {
        var a = await Create("A"); var b = await Child(a,"B"); var c = await Child(b,"C");
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.SetParentAsync(Ref(a), a.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.SetParentAsync(Ref(a), b.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.SetParentAsync(Ref(a), c.Item.Id));
        Assert.Null((await Read(a))!.ParentTaskId);
        await tasks.SetParentAsync(Ref(c), a.Item.Id);
        Assert.Equal(a.Item.Id, (await Read(c))!.ParentTaskId);
        await tasks.SetParentAsync(Ref(c), null); Assert.Null((await Read(c))!.ParentTaskId);
    }

    [Theory]
    [InlineData(TaskAction.Archive)]
    [InlineData(TaskAction.Trash)]
    public async Task ParentLifecycleAffectsOnlyParentAndChildrenStayDiscoverable(TaskAction action)
    {
        var parent = await Create(); var child = await Child(parent); var grandchild = await Child(child);
        await tasks.ApplyAsync(Ref(parent), action);
        var active = await tasks.GetAsync(profile, TaskCollection.Active);
        Assert.Contains(child, active); Assert.Contains(grandchild, active); Assert.DoesNotContain(active, t => t.Item.Id == parent.Item.Id);
        Assert.Equal(parent.Item.Id, (await Read(child))!.ParentTaskId);
        await tasks.ApplyAsync(Ref(parent), action == TaskAction.Trash ? TaskAction.RestoreTrash : TaskAction.RestoreArchive);
        Assert.Equal(parent.Item.Id, (await Read(child))!.ParentTaskId);
    }

    [Fact]
    public async Task PermanentDeleteDetachesChildrenAndCascadesOnlyRelationships()
    {
        var parent = await Create(); var child = await Child(parent); var grandchild = await Child(child);
        var dependent = await Create(); await tasks.AddDependencyAsync(Ref(dependent), parent.Item.Id);
        await tasks.ApplyAsync(Ref(parent), TaskAction.Trash); await tasks.PermanentlyDeleteAsync(Ref(parent));
        Assert.Null(await Read(parent)); Assert.Null((await Read(child))!.ParentTaskId);
        Assert.Equal(child.Item.Id, (await Read(grandchild))!.ParentTaskId);
        Assert.Empty((await Graph()).Dependencies); Assert.NotNull(await Read(dependent));
    }

    [Fact]
    public async Task MultipleDependenciesGuardOnlyCompletionAndNeverCompleteLeafDependents()
    {
        var publish = await Create("Publish"); var review = await Create("Review"); var approval = await Create("Approval");
        await tasks.AddDependencyAsync(Ref(publish), review.Item.Id); await tasks.AddDependencyAsync(Ref(publish), approval.Item.Id);
        await tasks.UpdateAsync(Ref(publish), new("Edited", "Description", TaskStatus.Doing, TaskPriority.High, new(2026,9,26)));
        Assert.Equal(TaskStatus.Doing, (await Read(publish))!.Status);
        var error = await Assert.ThrowsAsync<TaskValidationException>(() => Status(publish)); Assert.Contains("Review", error.Message);
        await Status(review); await Assert.ThrowsAsync<TaskValidationException>(() => Status(publish));
        await Status(approval); Assert.Equal(TaskStatus.Doing, (await Read(publish))!.Status);
        await Status(publish); Assert.Equal(TaskStatus.Done, (await Read(publish))!.Status);
        await Status(review, TaskStatus.ToDo); Assert.Equal(TaskStatus.Done, (await Read(publish))!.Status);
        await Status(publish, TaskStatus.ToDo); await Assert.ThrowsAsync<TaskValidationException>(() => Status(publish));
    }

    [Fact]
    public async Task DependencyDuplicatesSelfDirectAndDeepCyclesAreRejected()
    {
        var a = await Create("A"); var b = await Create("B"); var c = await Create("C");
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(a), a.Item.Id));
        await tasks.AddDependencyAsync(Ref(a), b.Item.Id);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(a), b.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(b), a.Item.Id));
        await tasks.AddDependencyAsync(Ref(b), c.Item.Id);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(c), a.Item.Id));
        Assert.Equal(2, (await Graph()).Dependencies.Count);
    }

    [Fact]
    public async Task MixedHierarchyDependencyCyclesAndReparentingDeadlocksAreRejected()
    {
        var a = await Create("A"); var b = await Child(a,"B"); var c = await Child(b,"C"); var x = await Create("X");
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(b), a.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(c), a.Item.Id));
        await tasks.AddDependencyAsync(Ref(a), c.Item.Id); // Parent requiring a descendant is redundant, not a deadlock.
        await tasks.AddDependencyAsync(Ref(c), x.Item.Id);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(x), a.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.SetParentAsync(Ref(a), x.Item.Id));
        await Status(x); await Status(c); Assert.Equal(TaskStatus.Done, (await Read(a))!.Status);
    }

    [Fact]
    public async Task ParentCompletionAlsoHonorsDependenciesAndReevaluatesWhenTheyComplete()
    {
        var parent = await Create(); var child = await Child(parent); var blocker = await Create();
        await tasks.AddDependencyAsync(Ref(parent), blocker.Item.Id); await Status(child);
        Assert.Equal(100, (await Graph()).Progress()[parent.Item.Id].Percent);
        Assert.Equal(TaskStatus.ToDo, (await Read(parent))!.Status);
        await Status(blocker); Assert.Equal(TaskStatus.Done, (await Read(parent))!.Status);
        await Status(blocker, TaskStatus.ToDo); Assert.Equal(TaskStatus.ToDo, (await Read(parent))!.Status);
        await tasks.RemoveDependencyAsync(Ref(parent), blocker.Item.Id); Assert.Equal(TaskStatus.Done, (await Read(parent))!.Status);
    }

    [Theory]
    [InlineData(TaskAction.Archive)]
    [InlineData(TaskAction.Trash)]
    public async Task IncompleteHiddenDependenciesAndChildrenRemainRequired(TaskAction action)
    {
        var parent = await Create(); var child = await Child(parent); var task = await Create();
        await tasks.AddDependencyAsync(Ref(task), child.Item.Id); await tasks.ApplyAsync(Ref(child), action);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(task));
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(parent));
        Assert.Equal(new TaskProgress(0,1), (await Graph()).Progress()[parent.Item.Id]);
        await tasks.RemoveDependencyAsync(Ref(task), child.Item.Id); await Status(task);
    }

    [Fact]
    public async Task ParentPropagationFailureRollsBackChildAndRelationshipWrites()
    {
        var parent = await Create(); var child = await Child(parent);
        await Sql($"CREATE TRIGGER FailParent BEFORE UPDATE ON Tasks WHEN NEW.ItemId='{parent.Item.Id:D}' BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Status(child));
        Assert.Equal(TaskStatus.ToDo, (await Read(child))!.Status); Assert.Equal(parent, await Read(parent));
        await Sql("DROP TRIGGER FailParent;"); await Status(child);
        await Sql($"CREATE TRIGGER FailParent BEFORE UPDATE ON Tasks WHEN NEW.ItemId='{parent.Item.Id:D}' BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Child(parent));
        Assert.Equal(2, (await Graph()).Tasks.Count);
        Assert.Equal(TaskStatus.Done, (await Read(parent))!.Status);
    }

    [Fact]
    public async Task ProfileIsolationRejectsForeignIdsAndStaleReferencesAndPersists()
    {
        var parent = await Create(); var child = await Child(parent); var blocker = await Create();
        await tasks.AddDependencyAsync(Ref(child), blocker.Item.Id);
        var second = await profiles.CreateAsync("Other"); var other = await tasks.CreateAsync(second.Id, new("Other"));
        Assert.Single((await tasks.GetGraphAsync(second.Id)).Tasks); Assert.Empty((await tasks.GetGraphAsync(second.Id)).Dependencies);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Child(parent));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.SetParentAsync(new(second.Id, other.Item.Id), parent.Item.Id));
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(new(second.Id, other.Item.Id), child.Item.Id));
        await profiles.SwitchAsync(profile); await profiles.RestoreAsync();
        Assert.Equal(parent.Item.Id, (await Read(child))!.ParentTaskId); Assert.Single((await Graph()).Dependencies);
    }

    [Fact]
    public async Task ScheduledSubtaskAppearsInTodayCalendarAndSpaceWithoutCopies()
    {
        var parent = await Create(); var child = await Child(parent); var today = DateOnly.FromDateTime(DateTime.Now);
        await tasks.ScheduleAsync(Ref(child), today);
        Assert.Equal(child.Item.Id, Assert.Single(await tasks.GetAsync(profile, TaskCollection.Today)).Item.Id);
        Assert.Equal(child.Item.Id, Assert.Single(await tasks.GetScheduledAsync(profile, today, today)).Item.Id);
        await Status(child); Assert.Equal(TaskStatus.Done, (await Read(parent))!.Status);
        await tasks.ScheduleAsync(Ref(child), today.AddDays(1)); Assert.Equal(2, (await Graph()).Tasks.Count);
        Assert.Null((await Read(parent))!.ScheduledDate);
    }

    [Fact]
    public async Task DetailPickerHierarchyFiltersAndProfileSwitchKeepContextCorrect()
    {
        var parent = await Create("Prepare trip"); var child = await Child(parent,"Flights"); var blocker = await Create("Review");
        await tasks.AddDependencyAsync(Ref(child), blocker.Item.Id);
        var navigation = new NavigationService(); navigation.Navigate(new("Tasks"));
        var model = new TaskWorkspaceViewModel(tasks, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, organization);
        await model.ReloadAsync(); model.Filter = "Flights";
        Assert.Contains("Prepare trip", Assert.Single(model.Rows).ContextText);
        navigation.Navigate(new("Task", child.Item.Id.ToString("D"))); await model.ReloadAsync();
        Assert.DoesNotContain(model.DependencyCandidates, row => row.Task.Item.Id == parent.Item.Id || row.Task.Item.Id == child.Item.Id || row.Task.Item.Id == blocker.Item.Id);
        Assert.Single(model.Dependencies); Assert.Contains("1", model.DependencyText);
        model.EditorTitle = "Unsaved title"; model.SubtaskTitle = "Nested"; await model.AddSubtaskCommand.ExecuteAsync(null);
        Assert.Equal("Unsaved title", model.EditorTitle); Assert.Single(model.Subtasks);
        Assert.Equal("Flights", (await Read(child))!.Item.Title);
        await profiles.CreateAsync("Other"); await model.ReloadAsync();
        Assert.Empty(model.Subtasks); Assert.Empty(model.Dependencies); Assert.Empty(model.Rows); Assert.Empty(model.DependencyCandidates);
    }

    [Fact]
    public async Task ForeignKeysDetachChildrenAndRemoveDependenciesWithoutDeletingOtherTasks()
    {
        var parent = await Create(); var child = await Child(parent); var other = await Create();
        await tasks.AddDependencyAsync(Ref(other), parent.Item.Id);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE Tasks SET ParentTaskId='{Guid.NewGuid():D}' WHERE ItemId='{child.Item.Id:D}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO TaskDependencies VALUES ('{other.Item.Id:D}','{parent.Item.Id:D}','duplicate');"));
        await Sql($"DELETE FROM WorkspaceItems WHERE Id='{parent.Item.Id:D}';");
        Assert.Null((await Read(child))!.ParentTaskId); Assert.NotNull(await Read(other)); Assert.Empty((await Graph()).Dependencies);
        await tasks.AddDependencyAsync(Ref(other), child.Item.Id);
        await tasks.ApplyAsync(Ref(other), TaskAction.Trash); await tasks.PermanentlyDeleteAsync(Ref(other));
        Assert.NotNull(await Read(child)); Assert.Empty((await Graph()).Dependencies);
    }

    [Fact]
    public async Task FailedPermanentDeletionRestoresParentLinksAndDependencies()
    {
        var parent = await Create(); var child = await Child(parent); var other = await Create();
        await tasks.AddDependencyAsync(Ref(other), parent.Item.Id); await tasks.ApplyAsync(Ref(parent), TaskAction.Trash);
        await Sql("CREATE TRIGGER FailDelete BEFORE DELETE ON Tasks BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => tasks.PermanentlyDeleteAsync(Ref(parent)));
        Assert.Equal(parent.Item.Id, (await Read(child))!.ParentTaskId); Assert.Single((await Graph()).Dependencies);
        Assert.NotNull(await Read(parent));
    }

    [Fact]
    public async Task CompletedLeafRequiresExplicitReopeningBeforeAddingIncompleteDependency()
    {
        var task = await Create(); var blocker = await Create(); await Status(task);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.AddDependencyAsync(Ref(task), blocker.Item.Id));
        Assert.Empty((await Graph()).Dependencies); Assert.Equal(TaskStatus.Done, (await Read(task))!.Status);
        await Status(task, TaskStatus.Blocked); await tasks.AddDependencyAsync(Ref(task), blocker.Item.Id);
        Assert.Equal(TaskStatus.Blocked, (await Read(task))!.Status);
        await Status(blocker); Assert.Equal(TaskStatus.Blocked, (await Read(task))!.Status);
    }

    [Fact]
    public async Task DuplicationCreatesIndependentTopLevelTaskWithoutRelationships()
    {
        var parent = await Create(); var child = await Child(parent); var blocker = await Create();
        await tasks.AddDependencyAsync(Ref(child), blocker.Item.Id); await Child(child);
        var duplicate = await tasks.DuplicateAsync(Ref(child));
        Assert.Null(duplicate.ParentTaskId); Assert.Equal(TaskStatus.ToDo, duplicate.Status);
        var graph = await Graph(); Assert.DoesNotContain(graph.Dependencies, d => d.TaskId == duplicate.Item.Id);
        Assert.DoesNotContain(graph.Tasks.Values, t => t.ParentTaskId == duplicate.Item.Id);
    }

    [Fact]
    public void ProgressHandlesThousandsOfLevelsWithoutRecursion()
    {
        var items = new List<TaskItem>(); Guid? parent = null; var now = DateTimeOffset.UtcNow;
        for (var i=0; i<3000; i++)
        {
            var task = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Level", now, now, null, null), "", TaskStatus.Done, TaskPriority.None, null, parent);
            items.Add(task); parent = task.Item.Id;
        }
        var graph = new TaskGraph(items, []);
        Assert.Equal(3000, graph.CompletionOrder().Count);
        Assert.Equal(new TaskProgress(1,1), graph.Progress()[items[0].Item.Id]);
    }

    public Task DisposeAsync()
    {
        profiles.Dispose(); gate.Dispose();
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
