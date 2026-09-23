using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string temporaryRoot = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly SqliteConnectionFactory connections;
    private readonly SqliteSettingsService settings;

    public InfrastructureTests()
    {
        paths = new ApplicationPaths(temporaryRoot);
        connections = new SqliteConnectionFactory(paths);
        settings = new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance);
    }

    private DatabaseInitializer Initializer(IEnumerable<DatabaseMigration>? migrations = null) =>
        new(paths, connections, migrations ?? MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance);

    [Fact]
    public async Task CreatesOnlyGlobalDatabaseAndFoundationTables()
    {
        await Initializer().InitializeAsync();
        Assert.True(File.Exists(paths.Database));
        Assert.True(Directory.Exists(paths.Logs));
        Assert.True(Directory.Exists(paths.Backups));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Profiles));
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Equal(new[] { "AppSettings", "SchemaMigrations" }, tables);
    }

    [Fact]
    public async Task InitialMigrationIsAppliedExactlyOnceAndPreservesSettings()
    {
        await Initializer().InitializeAsync();
        await settings.SetAsync("retained", 42);
        await Initializer().InitializeAsync();
        Assert.Equal(42, await settings.GetAsync("retained", 0));
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Version = 1;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SettingsCanBeWrittenReadAndUpdatedWithoutDuplicateKeys()
    {
        await Initializer().InitializeAsync();
        await settings.SetAsync(SettingKeys.SidebarCollapsed, true);
        Assert.True(await settings.GetAsync(SettingKeys.SidebarCollapsed, false));
        await settings.SetAsync(SettingKeys.SidebarCollapsed, false);
        Assert.False(await settings.GetAsync(SettingKeys.SidebarCollapsed, true));
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AppSettings;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MissingNullAndMalformedSettingsUseFallback()
    {
        await Initializer().InitializeAsync();
        Assert.Equal(123, await settings.GetAsync("missing", 123));
        await settings.SetAsync<string?>("null", null);
        Assert.Equal("default", await settings.GetAsync("null", "default"));
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AppSettings VALUES ('bad', '{invalid', '2026-01-01T00:00:00Z');";
        await command.ExecuteNonQueryAsync();
        Assert.True(await settings.GetAsync("bad", true));
        await settings.SetAsync("wrongType", "text");
        Assert.Equal(5, await settings.GetAsync("wrongType", 5));
    }

    [Fact]
    public async Task FailedMigrationRollsBackSchemaAndHistory()
    {
        await Initializer().InitializeAsync();
        var failing = MigrationCatalog.All.Concat(new[] { new DatabaseMigration(2, "Failure", "CREATE TABLE ShouldRollback (Id INTEGER); INVALID SQL;") });
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Initializer(failing).InitializeAsync());
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ShouldRollback';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PendingMigrationsRunInVersionOrder()
    {
        var migrations = new[]
        {
            new DatabaseMigration(3, "Insert", "INSERT INTO Ordered VALUES (1);"),
            new DatabaseMigration(2, "Create", "CREATE TABLE Ordered (Id INTEGER);"),
            MigrationCatalog.All[0]
        };
        await Initializer(migrations).InitializeAsync();
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Ordered;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConnectionsEnableForeignKeys()
    {
        await Initializer().InitializeAsync();
        await using var connection = await connections.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CanceledInitializationDoesNotApplyMigration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Initializer().InitializeAsync(cancellation.Token));
    }

    [Fact]
    public void NavigationPreservesEntityIdentityAndNotifiesOnce()
    {
        var navigation = new NavigationService();
        var notifications = 0;
        navigation.Changed += (_, _) => notifications++;
        var route = new NavigationRoute("Task", "123");
        navigation.Navigate(route);
        navigation.Navigate(route);
        Assert.Equal(route, navigation.Current);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(0, 800, false)]
    [InlineData(1200, -1, false)]
    [InlineData(int.MaxValue, 800, false)]
    [InlineData(1200, 800, true)]
    public void WindowSizeValidationRejectsUnsafeDimensions(int width, int height, bool expected) =>
        Assert.Equal(expected, new WindowPreferences(width, height).IsValid);

    public void Dispose()
    {
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
    }
}
