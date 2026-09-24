using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed class ProfileTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly SqliteConnectionFactory connections;
    private readonly SqliteSettingsService settings;
    private readonly SqliteProfileRepository repository;
    private readonly CurrentProfile current;
    private readonly TestFiles files;
    private readonly TestWorkspaces workspaces;
    private readonly ProfileService service;
    private readonly WorkspaceOperationGate workspaceGate = new();

    public ProfileTests()
    {
        paths = new ApplicationPaths(root);
        connections = new SqliteConnectionFactory(paths);
        settings = new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance);
        repository = new SqliteProfileRepository(connections);
        current = new CurrentProfile(paths);
        files = new TestFiles(new ProfileFiles(paths));
        workspaces = new TestWorkspaces(new WorkspaceInitializer(paths, NullLogger<WorkspaceInitializer>.Instance));
        service = NewService(current);
    }

    private ProfileService NewService(CurrentProfile state) => new(repository, settings, files, workspaces, state, NullLogger<ProfileService>.Instance, workspaceGate);
    public Task InitializeAsync() => new DatabaseInitializer(paths, connections, MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

    [Fact]
    public async Task UpgradeFromPhaseZeroPreservesSettingsAndDoesNotRepeatMigration()
    {
        // Use a separate database that starts with exactly the shipped Phase 0 catalog.
        var oldPaths = new ApplicationPaths(Path.Combine(root, "upgrade"));
        var oldConnections = new SqliteConnectionFactory(oldPaths);
        var oldSettings = new SqliteSettingsService(oldConnections, NullLogger<SqliteSettingsService>.Instance);
        await new DatabaseInitializer(oldPaths, oldConnections, [MigrationCatalog.All[0]], NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await oldSettings.SetAsync(SettingKeys.SidebarCollapsed, true);
        var upgrade = new DatabaseInitializer(oldPaths, oldConnections, MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance);
        await upgrade.InitializeAsync();
        await upgrade.InitializeAsync();
        Assert.True(await oldSettings.GetAsync(SettingKeys.SidebarCollapsed, false));
        await using var connection = await oldConnections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations;";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM Profiles;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmptyStartupHasNoAutomaticProfile()
    {
        await service.RestoreAsync();
        Assert.Empty(await service.GetAllAsync());
        Assert.Null(current.Current);
        Assert.Null(current.WorkspaceDatabase);
        Assert.Null(await LastOpenedAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Profiles));
    }

    [Fact]
    public async Task CreationInitializesRecordFolderLedgerAndSelection()
    {
        var profile = await service.CreateAsync(" Tiago ");
        Assert.Equal("Tiago", profile.Name);
        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(profile.Id.ToString("D"), profile.FolderName);
        Assert.Equal(profile, Assert.Single(await service.GetAllAsync()));
        Assert.True(Directory.Exists(paths.ProfileAttachments(profile.Id)));
        Assert.True(File.Exists(paths.WorkspaceDatabase(profile.Id)));
        Assert.Equal(profile, current.Current);
        Assert.Equal(profile.Id, await LastOpenedAsync());
        Assert.Equal(paths.WorkspaceDatabase(profile.Id), current.WorkspaceDatabase);
        await using var workspace = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(profile.Id)};Pooling=False");
        await workspace.OpenAsync();
        using var command = workspace.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SchemaMigrations", reader.GetString(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WorkspaceItems", reader.GetString(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Tasks", reader.GetString(0));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task BlankNamesAreRejected(string name)
    {
        await Assert.ThrowsAsync<ProfileValidationException>(() => service.CreateAsync(name));
        Assert.Empty(await service.GetAllAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.Profiles));
    }

    [Theory]
    [InlineData("Work", "work")]
    [InlineData("Équipe", "éQUIPE")]
    [InlineData("Café", "Cafe\u0301")]
    public async Task DuplicateNamesAreCaseInsensitiveAndUnicodeNormalized(string first, string duplicate)
    {
        await service.CreateAsync(first);
        await Assert.ThrowsAsync<ProfileValidationException>(() => service.CreateAsync(duplicate));
        Assert.Single(await service.GetAllAsync());
        Assert.Single(Directory.EnumerateDirectories(paths.Profiles));
    }

    [Fact]
    public async Task DatabaseAlsoEnforcesCaseInsensitiveUniqueness()
    {
        await service.CreateAsync("Équipe");
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<ProfileValidationException>(() => repository.CreateAndSelectAsync(new Profile(id, "éQUIPE", id.ToString("D"), now, now, 2), default));
    }

    [Fact]
    public async Task RenameChangesOnlyDisplayNameAndRejectsDuplicates()
    {
        var first = await service.CreateAsync("Tiago");
        var originalPath = paths.WorkspaceDatabase(first.Id);
        await service.RenameAsync(first.Id, "Personal");
        var renamed = Assert.Single(await service.GetAllAsync());
        Assert.Equal(first with { Name = "Personal" }, renamed);
        Assert.Equal(renamed, current.Current);
        Assert.True(File.Exists(originalPath));
        await service.CreateAsync("Work");
        await Assert.ThrowsAsync<ProfileValidationException>(() => service.RenameAsync(first.Id, "wORK"));
        await Assert.ThrowsAsync<ProfileValidationException>(() => service.RenameAsync(first.Id, "  "));
    }

    [Fact]
    public async Task SwitchingUpdatesContextSettingAndTimestamp()
    {
        var first = await service.CreateAsync("Personal");
        await service.CreateAsync("Work");
        await service.SwitchAsync(first.Id);
        Assert.Equal(first.Id, current.Current?.Id);
        Assert.Equal(first.Id, await LastOpenedAsync());
        Assert.True(current.Current!.LastOpenedAtUtc > first.LastOpenedAtUtc);
        Assert.Equal(current.Current, (await service.GetAllAsync()).Single(p => p.Id == first.Id));
    }

    [Fact]
    public async Task RestartRestoresLastOpenedAndInvalidSettingFallsBackDeterministically()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        var restarted = new CurrentProfile(paths);
        using var otherService = NewService(restarted);
        await otherService.RestoreAsync();
        Assert.Equal(second.Id, restarted.Current?.Id);
        await service.SwitchAsync(first.Id);
        await settings.SetAsync(SettingKeys.LastOpenedProfileId, Guid.NewGuid());
        await otherService.RestoreAsync();
        Assert.Equal(first.Id, restarted.Current?.Id);
        Assert.Equal(first.Id, await LastOpenedAsync());
        await settings.SetAsync(SettingKeys.LastOpenedProfileId, "not a guid");
        await otherService.RestoreAsync();
        Assert.Equal(first.Id, restarted.Current?.Id);
    }

    [Fact]
    public async Task MissingLastOpenedSettingUsesFallback()
    {
        var profile = await service.CreateAsync("Personal");
        await ExecuteAsync("DELETE FROM AppSettings WHERE Key = 'LastOpenedProfileId';");
        await service.RestoreAsync();
        Assert.Equal(profile.Id, await LastOpenedAsync());
    }

    [Fact]
    public async Task TwoWorkspacesAreIndependentPhysicalDatabases()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        Assert.NotEqual(paths.WorkspaceDatabase(first.Id), paths.WorkspaceDatabase(second.Id));
        // A SQLite header pragma proves physical isolation without inventing business data.
        await using (var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(first.Id)};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 41;";
            await command.ExecuteNonQueryAsync();
        }
        await using var secondConnection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(second.Id)};Pooling=False");
        await secondConnection.OpenAsync();
        using var check = secondConnection.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeleteInactiveProfileRemovesFilesAndRetainsSelection()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        File.WriteAllText(Path.Combine(paths.ProfileAttachments(first.Id), "sample.txt"), "local attachment fixture");
        await service.DeleteAsync(first.Id);
        Assert.False(Directory.Exists(paths.ProfileDirectory(first.Id)));
        Assert.Equal(second.Id, Assert.Single(await service.GetAllAsync()).Id);
        Assert.Equal(second.Id, current.Current?.Id);
        Assert.Empty(await repository.GetPendingDeletionsAsync(default));
    }

    [Fact]
    public async Task DeletingCurrentAndFinalProfilesMaintainsValidSelection()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        await service.DeleteAsync(second.Id);
        Assert.Equal(first.Id, current.Current?.Id);
        Assert.Equal(first.Id, await LastOpenedAsync());
        await service.DeleteAsync(first.Id);
        Assert.Null(current.Current);
        Assert.Null(current.WorkspaceDatabase);
        Assert.Null(await LastOpenedAsync());
        Assert.Empty(await service.GetAllAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.Profiles));
        await service.RestoreAsync();
        Assert.Null(current.Current);
    }

    [Fact]
    public async Task FailedWorkspaceSwitchKeepsPreviousContextAndSetting()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        File.Delete(paths.WorkspaceDatabase(first.Id));
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.SwitchAsync(first.Id));
        Assert.Equal(second.Id, current.Current?.Id);
        Assert.Equal(second.Id, await LastOpenedAsync());
        Assert.False(File.Exists(paths.WorkspaceDatabase(first.Id)));
    }

    [Fact]
    public async Task FailedCreationRollsBackFilesAndDoesNotCreateMetadata()
    {
        workspaces.FailCreation = true;
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.CreateAsync("Personal"));
        Assert.Empty(await service.GetAllAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.Profiles));
        Assert.Null(current.Current);
    }

    [Fact]
    public async Task FileDeletionFailureRemovesMetadataAndRetriesOnRestart()
    {
        var profile = await service.CreateAsync("Personal");
        files.FailDeletion = true;
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.DeleteAsync(profile.Id));
        Assert.Empty(await service.GetAllAsync());
        Assert.Null(current.Current);
        Assert.Null(await LastOpenedAsync());
        Assert.Equal(profile.Id, Assert.Single(await repository.GetPendingDeletionsAsync(default)));
        Assert.True(Directory.Exists(paths.ProfileDirectory(profile.Id)));
        files.FailDeletion = false;
        await service.RestoreAsync();
        Assert.False(Directory.Exists(paths.ProfileDirectory(profile.Id)));
        Assert.Empty(await repository.GetPendingDeletionsAsync(default));
    }

    [Fact]
    public async Task DatabaseDeletionFailureLeavesProfileAndFilesUntouched()
    {
        var profile = await service.CreateAsync("Personal");
        await ExecuteAsync("CREATE TRIGGER FailDelete BEFORE DELETE ON Profiles BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.DeleteAsync(profile.Id));
        Assert.Equal(profile, Assert.Single(await service.GetAllAsync()));
        Assert.Equal(profile, current.Current);
        Assert.Equal(profile.Id, await LastOpenedAsync());
        Assert.True(File.Exists(paths.WorkspaceDatabase(profile.Id)));
        Assert.Empty(await repository.GetPendingDeletionsAsync(default));
    }

    [Fact]
    public async Task FailedReplacementInitializationDoesNotDeleteCurrentProfile()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        File.WriteAllText(paths.WorkspaceDatabase(first.Id), "not a database");
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.DeleteAsync(second.Id));
        Assert.Equal(2, (await service.GetAllAsync()).Count);
        Assert.Equal(second.Id, current.Current?.Id);
        Assert.True(File.Exists(paths.WorkspaceDatabase(second.Id)));
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerializedWithoutDuplicateProfiles()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await service.CreateAsync("Personal"); return true; }
            catch (ProfileValidationException) { return false; }
        }));
        Assert.Equal(1, results.Count(success => success));
        Assert.Single(await service.GetAllAsync());
    }

    private Task<Guid?> LastOpenedAsync() => settings.GetAsync<Guid?>(SettingKeys.LastOpenedProfileId, null);

    [Fact]
    public async Task MetadataCreationFailureCompensatesFilesAndPreservesSelection()
    {
        var existing = await service.CreateAsync("Personal");
        await ExecuteAsync("CREATE TRIGGER FailCreate BEFORE INSERT ON Profiles BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.CreateAsync("Work"));
        Assert.Equal(existing, Assert.Single(await service.GetAllAsync()));
        Assert.Equal(existing.Id, await LastOpenedAsync());
        Assert.Equal(existing, current.Current);
        Assert.Single(Directory.EnumerateDirectories(paths.Profiles));
    }

    [Fact]
    public async Task FailedSelectionTransactionRollsBackTimestampAndCurrentContext()
    {
        var first = await service.CreateAsync("Personal");
        var second = await service.CreateAsync("Work");
        await ExecuteAsync("CREATE TRIGGER FailSetting BEFORE UPDATE ON AppSettings WHEN NEW.Key = 'LastOpenedProfileId' BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<ProfileOperationException>(() => service.SwitchAsync(first.Id));
        Assert.Equal(second, current.Current);
        Assert.Equal(second.Id, await LastOpenedAsync());
        Assert.Equal(first.LastOpenedAtUtc, (await service.GetAllAsync()).Single(p => p.Id == first.Id).LastOpenedAtUtc);
    }

    [Fact]
    public async Task CancellationBeforeCreationMakesNoChanges()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync("Personal", cancellation.Token));
        Assert.Empty(await service.GetAllAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.Profiles));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        service.Dispose();
        workspaceGate.Dispose();
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    private sealed class TestFiles(IProfileFiles inner) : IProfileFiles
    {
        public bool FailDeletion { get; set; }
        public void Create(Guid id) => inner.Create(id);
        public void Delete(Guid id)
        {
            if (FailDeletion) throw new IOException("Simulated locked file.");
            inner.Delete(id);
        }
    }

    private sealed class TestWorkspaces(IWorkspaceInitializer inner) : IWorkspaceInitializer
    {
        public bool FailCreation { get; set; }
        public Task InitializeAsync(Guid id, bool create, CancellationToken cancellationToken = default)
        {
            if (create && FailCreation) throw new IOException("Simulated workspace initialization failure.");
            return inner.InitializeAsync(id, create, cancellationToken);
        }
    }
}
