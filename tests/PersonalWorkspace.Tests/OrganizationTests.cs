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

public sealed class OrganizationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly WorkspaceOperationGate gate = new();
    private readonly CurrentProfile current;
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly OrganizationService organization;
    private readonly TaskService tasks;
    private Guid profileId;

    public OrganizationTests()
    {
        paths = new(root);
        current = new(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        organization = new(new SqliteOrganizationRepository(), current, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
        tasks = new(new SqliteTaskRepository(), current, gate, TimeProvider.System, NullLogger<TaskService>.Instance);
    }
    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profileId = (await profiles.CreateAsync("Personal")).Id;
    }
    private Task<Guid> Create(OrganizationKind kind, string name) => organization.SaveAsync(profileId, kind, null, new(name));
    private Task<OrganizationSnapshot> Snapshot() => organization.GetAsync(profileId);
    private Task Assign(TaskItem task, OrganizationKind kind, Guid id, bool assigned = true) => organization.AssignAsync(new(profileId, task.Item.Id), kind, id, assigned);
    private TaskWorkspaceViewModel Model(NavigationService navigation) => new(tasks, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, organization);

    [Fact]
    public async Task PhaseTwoWorkspaceUpgradesWithoutChangingTasksOrOriginalLedger()
    {
        var legacyId = Guid.NewGuid();
        new ProfileFiles(paths).Create(legacyId);
        await using (var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(legacyId)};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaMigrations (Version INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
                INSERT INTO SchemaMigrations VALUES (1, 'Create task core', '2026-09-01T00:00:00.0000000+00:00');
                """ + WorkspaceMigrationCatalog.All[0].Sql;
            await command.ExecuteNonQueryAsync();
        }
        var now = DateTimeOffset.UtcNow;
        var task = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Existing", now, now, null, null), "Retained", TaskStatus.Blocked, TaskPriority.High, new DateOnly(2026, 9, 23));
        var context = new WorkspaceContext(legacyId, paths.WorkspaceDatabase(legacyId));
        var repository = new SqliteTaskRepository();
        await repository.CreateAsync(context, task, default);
        await initializer.InitializeAsync(legacyId, false);
        await initializer.InitializeAsync(legacyId, false);
        Assert.Equal(task, await repository.FindAsync(context, task.Item.Id, default));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM SchemaMigrations;", legacyId));
        Assert.Equal("2026-09-01T00:00:00.0000000+00:00", await Scalar("SELECT AppliedAtUtc FROM SchemaMigrations WHERE Version=1;", legacyId));
        Assert.Equal(4L, await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Tags','Spaces','ItemTags','ItemSpaces');", legacyId));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync();
        using var check = global.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Tags','Spaces','ItemTags','ItemSpaces');";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(OrganizationKind.Tag, "")]
    [InlineData(OrganizationKind.Tag, " \t\n ")]
    [InlineData(OrganizationKind.Space, "")]
    [InlineData(OrganizationKind.Space, " \t\n ")]
    public async Task BlankNamesAreRejected(OrganizationKind kind, string name)
    {
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Create(kind, name));
        Assert.Empty((await Snapshot()).Tags);
        Assert.Empty((await Snapshot()).Spaces);
    }

    [Theory]
    [InlineData(OrganizationKind.Tag)]
    [InlineData(OrganizationKind.Space)]
    public async Task NamesAreTrimmedNormalizedUniqueAndRenamePreservesIdentity(OrganizationKind kind)
    {
        var id = await Create(kind, "  Cafe\u0301  ");
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Create(kind, "CAFÉ"));
        var other = await Create(kind, "Other");
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.SaveAsync(profileId, kind, other, new("café")));
        var before = await Snapshot();
        Assert.Equal("Café", kind == OrganizationKind.Tag ? before.Tags.Single(tag => tag.Id == id).Name : before.Spaces.Single(space => space.Id == id).Name);
        await organization.SaveAsync(profileId, kind, id, new(" Renamed ", OrganizationColor.Green, "Description", "★"));
        var after = await Snapshot();
        if (kind == OrganizationKind.Tag)
        {
            var old = before.Tags.Single(tag => tag.Id == id);
            var updated = after.Tags.Single(tag => tag.Id == id);
            Assert.Equal("Renamed", updated.Name);
            Assert.Equal(OrganizationColor.Green, updated.Color);
            Assert.Equal(old.CreatedAtUtc, updated.CreatedAtUtc);
            Assert.True(updated.UpdatedAtUtc > old.UpdatedAtUtc);
            await organization.SaveAsync(profileId, kind, id, new("Renamed", OrganizationColor.Green));
            Assert.Equal(updated, (await Snapshot()).Tags.Single(tag => tag.Id == id));
        }
        else
        {
            var old = before.Spaces.Single(space => space.Id == id);
            var updated = after.Spaces.Single(space => space.Id == id);
            Assert.Equal("Renamed", updated.Name);
            Assert.Equal("Description", updated.Description);
            Assert.Equal("★", updated.Icon);
            Assert.Equal(OrganizationColor.Green, updated.Color);
            Assert.Equal(old.SortOrder, updated.SortOrder);
            Assert.Equal(old.CreatedAtUtc, updated.CreatedAtUtc);
            Assert.True(updated.UpdatedAtUtc > old.UpdatedAtUtc);
            await organization.SaveAsync(profileId, kind, id, new("Renamed", OrganizationColor.Green, "Description", "★"));
            Assert.Equal(updated, (await Snapshot()).Spaces.Single(space => space.Id == id));
        }
    }

    [Theory]
    [InlineData(OrganizationKind.Tag)]
    [InlineData(OrganizationKind.Space)]
    public async Task InvalidPaletteAndUnknownUpdateAreRejected(OrganizationKind kind)
    {
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.SaveAsync(profileId, kind, null, new("Name", (OrganizationColor)99)));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.SaveAsync(profileId, kind, Guid.NewGuid(), new("Missing")));
    }

    [Fact]
    public async Task MultipleIndependentAssignmentsAreUniqueAndRemovingOnePreservesEverythingElse()
    {
        var task = await tasks.CreateAsync(profileId, new("20 push-ups", "Description", TaskStatus.Doing, TaskPriority.High, new DateOnly(2026, 9, 25)));
        var tags = new[] { await Create(OrganizationKind.Tag, "health"), await Create(OrganizationKind.Tag, "strength") };
        var spaces = new[] { await Create(OrganizationKind.Space, "Personal"), await Create(OrganizationKind.Space, "Training") };
        foreach (var tag in tags) { await Assign(task, OrganizationKind.Tag, tag); await Assign(task, OrganizationKind.Tag, tag); }
        foreach (var space in spaces) { await Assign(task, OrganizationKind.Space, space); await Assign(task, OrganizationKind.Space, space); }
        var all = await Snapshot();
        Assert.Equal(2, all.ItemTags.Count);
        Assert.Equal(2, all.ItemSpaces.Count);
        await Assign(task, OrganizationKind.Tag, tags[0], false);
        await Assign(task, OrganizationKind.Space, spaces[1], false);
        var remaining = await Snapshot();
        Assert.Equal(tags[1], Assert.Single(remaining.ItemTags).EntityId);
        Assert.Equal(spaces[0], Assert.Single(remaining.ItemSpaces).EntityId);
        Assert.Equal(task, await tasks.FindAsync(new(profileId, task.Item.Id))); // Includes timestamps and every task field.
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
        Assert.Equal(2, remaining.Tags.Count);
        Assert.Equal(2, remaining.Spaces.Count);
    }

    [Fact]
    public async Task DeletingTagCascadesOnlyItsAssignments()
    {
        var task = await tasks.CreateAsync(profileId, new("Keep me"));
        var deleted = await Create(OrganizationKind.Tag, "urgent");
        var kept = await Create(OrganizationKind.Tag, "health");
        var space = await Create(OrganizationKind.Space, "Personal");
        await Assign(task, OrganizationKind.Tag, deleted);
        await Assign(task, OrganizationKind.Tag, kept);
        await Assign(task, OrganizationKind.Space, space);
        await organization.DeleteTagAsync(profileId, deleted);
        var result = await Snapshot();
        Assert.Equal(kept, Assert.Single(result.Tags).Id);
        Assert.Equal(kept, Assert.Single(result.ItemTags).EntityId);
        Assert.Single(result.ItemSpaces);
        Assert.Equal(task, await tasks.FindAsync(new(profileId, task.Item.Id)));
    }

    [Fact]
    public async Task ArchiveRestoreSpaceRetainsTasksAndAssignmentsAndHidesSidebarEntry()
    {
        var task = await tasks.CreateAsync(profileId, new("Keep active"));
        var space = await Create(OrganizationKind.Space, "Training");
        await Assign(task, OrganizationKind.Space, space);
        var model = new OrganizationViewModel(organization, current, new NavigationService(), NullLogger<OrganizationViewModel>.Instance);
        await model.ReloadAsync();
        Assert.Single(model.ActiveSpaces);
        await organization.ArchiveSpaceAsync(profileId, space, true);
        await model.ReloadAsync();
        Assert.Empty(model.ActiveSpaces);
        Assert.True(Assert.Single(model.Rows).IsArchived);
        var archived = await Snapshot();
        Assert.NotNull(Assert.Single(archived.Spaces).ArchivedAtUtc);
        Assert.Single(archived.ItemSpaces);
        Assert.Equal(task, Assert.Single(await tasks.GetAsync(profileId, TaskCollection.Active)));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Assign(task, OrganizationKind.Space, space));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Create(OrganizationKind.Space, "TRAINING"));
        await organization.ArchiveSpaceAsync(profileId, space, false);
        await model.ReloadAsync();
        Assert.Single(model.ActiveSpaces);
        Assert.Null(Assert.Single((await Snapshot()).Spaces).ArchivedAtUtc);
        Assert.Single((await Snapshot()).ItemSpaces);
    }

    [Fact]
    public async Task SpaceViewsShareSameTaskAndFiltersCombineWithoutLifecycleLeaks()
    {
        var personal = await Create(OrganizationKind.Space, "Personal");
        var training = await Create(OrganizationKind.Space, "Training");
        var urgent = await Create(OrganizationKind.Tag, "urgent");
        var pushups = await tasks.CreateAsync(profileId, new("20 push-ups"));
        var report = await tasks.CreateAsync(profileId, new("Prepare report"));
        var archived = await tasks.CreateAsync(profileId, new("Archived"));
        var deleted = await tasks.CreateAsync(profileId, new("Deleted"));
        foreach (var task in new[] { pushups, report, archived, deleted })
        {
            await Assign(task, OrganizationKind.Space, personal);
            await Assign(task, OrganizationKind.Tag, urgent);
        }
        await Assign(pushups, OrganizationKind.Space, training);
        await tasks.ApplyAsync(new(profileId, archived.Item.Id), TaskAction.Archive);
        await tasks.ApplyAsync(new(profileId, deleted.Item.Id), TaskAction.Trash);
        var navigation = new NavigationService();
        navigation.Navigate(new("Space", training.ToString("D")));
        var model = Model(navigation);
        await model.ReloadAsync();
        Assert.Equal(pushups.Item.Id, Assert.Single(model.Rows).Task.Item.Id);
        await model.ToggleDoneCommand.ExecuteAsync(Assert.Single(model.Rows));
        navigation.Navigate(new("Space", personal.ToString("D")));
        await model.ReloadAsync();
        Assert.Equal(2, model.Rows.Count());
        Assert.Equal(TaskStatus.Done, model.Rows.Single(row => row.Task.Item.Id == pushups.Item.Id).Task.Status);
        navigation.Navigate(new("Tasks"));
        await model.ReloadAsync();
        model.TagFilter = model.TagFilters.Single(filter => filter.Id == urgent);
        model.SpaceFilter = model.SpaceFilters.Single(filter => filter.Id == training);
        Assert.Equal(TaskStatus.Done, Assert.Single(model.Rows).Task.Status);
        model.Filter = "report";
        Assert.Empty(model.Rows);
        model.SpaceFilter = model.SpaceFilters.Single(filter => filter.Id == personal);
        Assert.Equal(report.Item.Id, Assert.Single(model.Rows).Task.Item.Id);
        navigation.Navigate(new("Archived"));
        await model.ReloadAsync();
        Assert.Equal(archived.Item.Id, Assert.Single(model.Rows).Task.Item.Id);
        navigation.Navigate(new("Trash"));
        await model.ReloadAsync();
        Assert.Equal(deleted.Item.Id, Assert.Single(model.Rows).Task.Item.Id);
        Assert.Equal(4L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
    }

    [Fact]
    public async Task AssignmentButtonsSaveImmediatelyWithoutDiscardingTaskDraftAndBackKeepsSpace()
    {
        var space = await Create(OrganizationKind.Space, "Training");
        var tag = await Create(OrganizationKind.Tag, "health");
        var task = await tasks.CreateAsync(profileId, new("20 push-ups"));
        await Assign(task, OrganizationKind.Space, space);
        var navigation = new NavigationService();
        navigation.Navigate(new("Space", space.ToString("D")));
        var model = Model(navigation);
        await model.ReloadAsync();
        model.OpenCommand.Execute(Assert.Single(model.Rows));
        await model.ReloadAsync();
        model.EditorTitle = "Unsaved title";
        await model.ToggleAssignmentCommand.ExecuteAsync(Assert.Single(model.TagChoices));
        Assert.True(Assert.Single(model.TagChoices).IsAssigned);
        Assert.Equal("Unsaved title", model.EditorTitle);
        Assert.Equal(task, await tasks.FindAsync(new(profileId, task.Item.Id)));
        await model.ToggleAssignmentCommand.ExecuteAsync(Assert.Single(model.SpaceChoices));
        model.BackCommand.Execute(null);
        await model.ReloadAsync();
        Assert.Equal(new NavigationRoute("Space", space.ToString("D")), navigation.Current);
        Assert.Empty(model.Rows);
        Assert.Single(await tasks.GetAsync(profileId, TaskCollection.Active));
    }

    [Fact]
    public async Task PermanentItemDeletionRemovesRelationshipsButKeepsCatalog()
    {
        var task = await tasks.CreateAsync(profileId, new("Delete item"));
        await Assign(task, OrganizationKind.Tag, await Create(OrganizationKind.Tag, "health"));
        await Assign(task, OrganizationKind.Space, await Create(OrganizationKind.Space, "Personal"));
        await tasks.ApplyAsync(new(profileId, task.Item.Id), TaskAction.Trash);
        await tasks.PermanentlyDeleteAsync(new(profileId, task.Item.Id));
        var result = await Snapshot();
        Assert.Empty(result.ItemTags); Assert.Empty(result.ItemSpaces);
        Assert.Single(result.Tags); Assert.Single(result.Spaces);
    }

    [Fact]
    public async Task ProfilesAllowSameNamesButRejectForeignRelationshipsAndClearPresentation()
    {
        var tagA = await Create(OrganizationKind.Tag, "health");
        var spaceA = await Create(OrganizationKind.Space, "Personal");
        var taskA = await tasks.CreateAsync(profileId, new("A"));
        await Assign(taskA, OrganizationKind.Tag, tagA);
        await Assign(taskA, OrganizationKind.Space, spaceA);
        var navigation = new NavigationService();
        navigation.Navigate(new("Space", spaceA.ToString("D")));
        var taskModel = Model(navigation);
        var manager = new OrganizationViewModel(organization, current, navigation, NullLogger<OrganizationViewModel>.Instance);
        await taskModel.ReloadAsync(); await manager.ReloadAsync();
        var staleChoice = new OrganizationChoice(new(profileId, taskA.Item.Id), tagA, OrganizationKind.Tag, "health", OrganizationColor.None, true);
        var profileB = await profiles.CreateAsync("Testing");
        await taskModel.ReloadAsync(); await manager.ReloadAsync();
        Assert.Empty(manager.Rows); Assert.Empty(manager.ActiveSpaces); Assert.Empty(taskModel.Rows);
        Assert.Empty(taskModel.TagChoices); Assert.Empty(taskModel.SpaceChoices);
        Assert.Single(taskModel.TagFilters); Assert.Single(taskModel.SpaceFilters);
        Assert.Equal("Tasks", navigation.Current.Destination);
        var tagB = await organization.SaveAsync(profileB.Id, OrganizationKind.Tag, null, new("health"));
        var spaceB = await organization.SaveAsync(profileB.Id, OrganizationKind.Space, null, new("Personal"));
        Assert.NotEqual(tagA, tagB); Assert.NotEqual(spaceA, spaceB);
        var taskB = await tasks.CreateAsync(profileB.Id, new("B"));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Assign(taskA, OrganizationKind.Tag, tagA));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => organization.SaveAsync(profileId, OrganizationKind.Tag, tagA, new("Stale")));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => organization.DeleteTagAsync(profileId, tagA));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => organization.ArchiveSpaceAsync(profileId, spaceA, true));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.AssignAsync(new(profileB.Id, taskB.Item.Id), OrganizationKind.Tag, tagA, true));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.AssignAsync(new(profileB.Id, taskA.Item.Id), OrganizationKind.Space, spaceB, true));
        await taskModel.ToggleAssignmentCommand.ExecuteAsync(staleChoice);
        Assert.Empty((await organization.GetAsync(profileB.Id)).ItemTags);
        await profiles.SwitchAsync(profileId);
        Assert.Single((await Snapshot()).ItemTags); Assert.Single((await Snapshot()).ItemSpaces);
        // New service instance / reopened profile reads persisted relationships without a cache.
        var restarted = new OrganizationService(new SqliteOrganizationRepository(), current, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
        Assert.True((await restarted.GetAsync(profileId)).Matches(taskA.Item.Id, tagA, spaceA));
    }

    [Fact]
    public async Task UnknownAndTrashedItemsCannotBeAssignedAndSqlForeignKeysRemainEnforced()
    {
        var tag = await Create(OrganizationKind.Tag, "health");
        var task = await tasks.CreateAsync(profileId, new("Trash"));
        await tasks.ApplyAsync(new(profileId, task.Item.Id), TaskAction.Trash);
        await Assert.ThrowsAsync<OrganizationValidationException>(() => Assign(task, OrganizationKind.Tag, tag));
        await Assert.ThrowsAsync<OrganizationValidationException>(() => organization.AssignAsync(new(profileId, Guid.NewGuid()), OrganizationKind.Tag, tag, true));
        await Assert.ThrowsAsync<SqliteException>(() => Scalar($"INSERT INTO ItemTags VALUES ('{Guid.NewGuid():D}', '{tag:D}');"));
        Assert.Empty((await Snapshot()).ItemTags);
    }

    [Fact]
    public async Task FailedCascadeRollsBackTagAndRelationships()
    {
        var tag = await Create(OrganizationKind.Tag, "health");
        var task = await tasks.CreateAsync(profileId, new("Keep"));
        await Assign(task, OrganizationKind.Tag, tag);
        await Scalar("CREATE TRIGGER FailTagDelete BEFORE DELETE ON ItemTags BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<OrganizationOperationException>(() => organization.DeleteTagAsync(profileId, tag));
        var result = await Snapshot();
        Assert.Single(result.Tags); Assert.Single(result.ItemTags);
        Assert.Equal(task, await tasks.FindAsync(new(profileId, task.Item.Id)));
    }

    private async Task<object?> Scalar(string sql, Guid? id = null)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(id ?? profileId)};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        connection.CreateCollation("WORKSPACE_NAME", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task DatabaseConstraintsRejectDuplicateRelationshipsAndCaseInsensitiveNames()
    {
        var tag = await Create(OrganizationKind.Tag, "health");
        var space = await Create(OrganizationKind.Space, "Personal");
        var task = await tasks.CreateAsync(profileId, new("One"));
        await Assign(task, OrganizationKind.Tag, tag);
        await Assign(task, OrganizationKind.Space, space);
        await Assert.ThrowsAsync<SqliteException>(() => Scalar($"INSERT INTO ItemTags VALUES ('{task.Item.Id:D}', '{tag:D}');"));
        await Assert.ThrowsAsync<SqliteException>(() => Scalar($"INSERT INTO ItemSpaces VALUES ('{task.Item.Id:D}', '{space:D}');"));
        await Assert.ThrowsAsync<SqliteException>(() => Scalar($"INSERT INTO Tags SELECT '{Guid.NewGuid():D}', 'HEALTH', Color, CreatedAtUtc, UpdatedAtUtc FROM Tags;"));
        await Assert.ThrowsAsync<SqliteException>(() => Scalar($"INSERT INTO Spaces SELECT '{Guid.NewGuid():D}', 'PERSONAL', Description, Icon, Color, SortOrder, CreatedAtUtc, UpdatedAtUtc, ArchivedAtUtc FROM Spaces;"));
    }

    [Fact]
    public async Task NoCurrentProfileRejectsOrganizationAccess()
    {
        var emptyCurrent = new CurrentProfile(paths);
        var emptyService = new OrganizationService(new SqliteOrganizationRepository(), emptyCurrent, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => emptyService.GetAsync(profileId));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => emptyService.SaveAsync(profileId, OrganizationKind.Tag, null, new("health")));
    }

    [Fact]
    public async Task DelayedOldCatalogCannotRepopulateNewProfileSidebarOrFilters()
    {
        await Create(OrganizationKind.Space, "Private space");
        var delayed = new DelayedOrganizationService(organization);
        var navigation = new NavigationService();
        var manager = new OrganizationViewModel(delayed, current, navigation, NullLogger<OrganizationViewModel>.Instance);
        var oldRead = manager.ReloadAsync();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await profiles.CreateAsync("Testing");
        await manager.ReloadAsync();
        Assert.Empty(manager.ActiveSpaces);
        delayed.Continue.SetResult();
        await oldRead;
        Assert.Empty(manager.ActiveSpaces); Assert.Empty(manager.Rows);

        await profiles.SwitchAsync(profileId);
        var delayedTasks = new DelayedOrganizationService(organization);
        navigation.Navigate(new("Tasks"));
        var taskModel = new TaskWorkspaceViewModel(tasks, current, navigation, NullLogger<TaskWorkspaceViewModel>.Instance, delayedTasks);
        var oldTaskRead = taskModel.ReloadAsync();
        await delayedTasks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await profiles.CreateAsync("Other");
        await taskModel.ReloadAsync();
        delayedTasks.Continue.SetResult();
        await oldTaskRead;
        Assert.Single(taskModel.SpaceFilters);
        Assert.Empty(taskModel.Rows);
    }

    [Fact]
    public async Task SwitchingWaitsForInFlightOrganizationWrite()
    {
        var other = await profiles.CreateAsync("Other");
        await profiles.SwitchAsync(profileId);
        var delayed = new DelayedOrganizationRepository(new SqliteOrganizationRepository());
        var service = new OrganizationService(delayed, current, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
        var write = service.SaveAsync(profileId, OrganizationKind.Space, null, new("Personal"));
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = profiles.SwitchAsync(other.Id);
        Assert.False(switching.IsCompleted);
        delayed.Continue.SetResult();
        var id = await write;
        await switching;
        Assert.Empty((await organization.GetAsync(other.Id)).Spaces);
        await profiles.SwitchAsync(profileId);
        Assert.Equal(id, Assert.Single((await Snapshot()).Spaces).Id);
    }

    private sealed class DelayedOrganizationService(IOrganizationService inner) : IOrganizationService
    {
        private int delay = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OrganizationSnapshot> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            var result = await inner.GetAsync(profileId, cancellationToken);
            if (Interlocked.Exchange(ref delay, 0) == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return result;
        }
        public Task<Guid> SaveAsync(Guid profileId, OrganizationKind kind, Guid? id, OrganizationDraft draft, CancellationToken cancellationToken = default) => inner.SaveAsync(profileId, kind, id, draft, cancellationToken);
        public Task DeleteTagAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) => inner.DeleteTagAsync(profileId, id, cancellationToken);
        public Task ArchiveSpaceAsync(Guid profileId, Guid id, bool archive, CancellationToken cancellationToken = default) => inner.ArchiveSpaceAsync(profileId, id, archive, cancellationToken);
        public Task AssignAsync(WorkspaceItemReference item, OrganizationKind kind, Guid id, bool assigned, CancellationToken cancellationToken = default) => inner.AssignAsync(item, kind, id, assigned, cancellationToken);
    }

    private sealed class DelayedOrganizationRepository(IOrganizationRepository inner) : IOrganizationRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OrganizationSnapshot> GetAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => inner.GetAsync(workspace, cancellationToken);
        public async Task SaveAsync(WorkspaceContext workspace, OrganizationKind kind, Guid id, OrganizationDraft draft, bool create, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken);
            await inner.SaveAsync(workspace, kind, id, draft, create, now, cancellationToken);
        }
        public Task DeleteTagAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken) => inner.DeleteTagAsync(workspace, id, cancellationToken);
        public Task ArchiveSpaceAsync(WorkspaceContext workspace, Guid id, bool archive, DateTimeOffset now, CancellationToken cancellationToken) => inner.ArchiveSpaceAsync(workspace, id, archive, now, cancellationToken);
        public Task AssignAsync(WorkspaceContext workspace, Guid itemId, OrganizationKind kind, Guid id, bool assigned, CancellationToken cancellationToken) => inner.AssignAsync(workspace, itemId, kind, id, assigned, cancellationToken);
    }
    public Task DisposeAsync()
    {
        profiles.Dispose(); gate.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
