using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private async Task<Guid> ActivityLegacyWorkspace()
    {
        // Historical fixture is populated through explicit SQL, never current audited writers.
        var legacy = await PopulatedPhaseTwenty();
        foreach (var migration in WorkspaceMigrationCatalog.All.Where(m => m.Version is 19 or 20))
            await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var workspace = new WorkspaceContext(legacy, paths.WorkspaceDatabase(legacy));
        await new SqliteSearchRepository().SearchAsync(workspace, "Training", false, p => p.Body, default);
        Assert.Equal(20L, await Sql("SELECT count(*) FROM SchemaMigrations", legacy));
        Assert.True((long)(await Sql("SELECT count(*) FROM SearchDocuments", legacy))! > 0);
        return legacy;
    }
    [Fact]
    public async Task ActivityPopulatedPhaseTwentyTwoUpgradePreservesEveryTableAndIsIdempotent()
    {
        var legacy = await ActivityLegacyWorkspace(); var before = await AuditDatabaseSnapshot(legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        foreach (var (table, rows) in before.Where(p => p.Key != "SchemaMigrations")) Assert.Equal(rows, await TableRows(legacy, table));
        Assert.Equal(21L, await Sql("SELECT count(*) FROM SchemaMigrations", legacy));
        Assert.Equal(20L, await Sql("SELECT count(*) FROM SchemaMigrations WHERE AppliedAtUtc='original'", legacy));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM AuditEvents", legacy));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check", legacy)); Assert.Null(await Sql("PRAGMA foreign_key_check", legacy));
    }
    [Fact]
    public async Task ActivityMigrationFailureRollsBackAllSchemaAndLedgerChanges()
    {
        var legacy = await ActivityLegacyWorkspace(); await Sql("CREATE TABLE AuditEventTargets(Conflict TEXT)", legacy);
        var before = await AuditDatabaseSnapshot(legacy); var schema = await TableRows(legacy, "sqlite_master");
        await Assert.ThrowsAnyAsync<Exception>(() => initializer.InitializeAsync(legacy, false));
        Assert.Equal(schema, await TableRows(legacy, "sqlite_master"));
        foreach (var (table, rows) in before) Assert.Equal(rows, await TableRows(legacy, table));
        await Sql("DROP TABLE AuditEventTargets", legacy); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(21L, await Sql("SELECT count(*) FROM SchemaMigrations", legacy)); Assert.Null(await Sql("PRAGMA foreign_key_check", legacy));
    }
    [Theory]
    [InlineData("AuditEvents")][InlineData("AuditEventTargets")][InlineData("AuditEventChanges")]
    public async Task ActivityFailureAtEachAuditInsertRollsBackAndHistoryIsImmutable(string table)
    {
        await Sql($"CREATE TRIGGER RejectAudit BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'failure'); END");
        var before = await AuditDatabaseSnapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => JournalTasks().CreateAsync(profile, new("Roll back")));
        foreach (var (name, rows) in before) Assert.Equal(rows, await TableRows(profile, name));
        await Sql("DROP TRIGGER RejectAudit"); await JournalTasks().CreateAsync(profile, new("Immutable"));
        var field = table == "AuditEvents" ? "Title" : table == "AuditEventTargets" ? "Title" : "Field";
        await Assert.ThrowsAnyAsync<Exception>(() => Sql($"UPDATE {table} SET {field}='Tampered'"));
        Assert.Equal("Immutable", (await OnlyActivity()).Event.Target.Title);
    }
    [Fact]
    public async Task ActivityClearFailureRollsBackAllHistoryAndEpoch()
    {
        await JournalTasks().CreateAsync(profile, new("Keep history")); var epoch = (await History().ReadAsync(profile, new())).Epoch;
        await Sql("CREATE TRIGGER RejectClear BEFORE UPDATE ON AuditHistoryState BEGIN SELECT RAISE(ABORT,'failure'); END");
        var before = await AuditDatabaseSnapshot(); await Assert.ThrowsAnyAsync<Exception>(() => History().ClearAsync(profile, epoch));
        foreach (var (table, rows) in before) Assert.Equal(rows, await TableRows(profile, table));
    }
}
