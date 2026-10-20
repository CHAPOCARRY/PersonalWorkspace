using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private async Task<Guid> PopulatedPhaseTwenty()
    {
        await TemplateDashboard();
        await Capture((await JournalTasks().CreateAsync(profile, new("Task template", Value: new(TaskValueType.Number, 20)))).Item, TemplateType.Task);
        var list = await Lists().CreateDefinitionAsync(profile, new("List template", ShowPrices: true), [new("A", 2, 1.25m), new("B")]);
        await Capture(list.Item, TemplateType.List);
        var journal = await Journals().CreateDefinitionAsync(profile, "Journal template", "Keep", [FieldDraft(JournalFieldType.Select), FieldDraft(JournalFieldType.Scale)]);
        await Capture(journal.Item, TemplateType.Journal);
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(18))
            await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        await using var c = await Connection(legacy); var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name<>'SchemaMigrations' ORDER BY rowid";
            using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "ATTACH DATABASE $path AS fixture"; cmd.Parameters.AddWithValue("$path", paths.WorkspaceDatabase(profile)); await cmd.ExecuteNonQueryAsync(); }
        foreach (var table in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT INTO main.\"{table}\" SELECT * FROM fixture.\"{table}\" WHERE true" + (table == "WidgetListSettings" ? " ON CONFLICT(WidgetId) DO UPDATE SET ItemFilter=excluded.ItemFilter" : "");
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var asset in await Assets().ListAsync(new(profile, (await Pages().GetAsync(profile)).Pages.Values.First(p => p.Item.Title == "Training").Item.Id)))
        {
            var destination = new PersonalWorkspace.Services.AssetPaths(paths).Resolve(legacy, asset.Asset.StorageRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(AssetPath(asset.Asset), destination);
        }
        return legacy;
    }
    [Fact]
    public async Task PageTemplateMigrationPreservesPopulatedPhaseTwentyCatalogContentFilesAndForeignKeys()
    {
        var legacy = await PopulatedPhaseTwenty(); var before = await PageTemplateDatabase(legacy);
        var bytes = Directory.GetFiles(paths.ProfileAttachments(legacy), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        foreach (var (table, rows) in before.Where(p => p.Key != "SchemaMigrations")) Assert.Equal(rows, await TableRows(legacy, table));
        foreach (var (file, data) in bytes) Assert.Equal(data, File.ReadAllBytes(file));
        Assert.Equal(19L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(18L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM Templates;", legacy));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageTemplateDefinitions;", legacy));
        Assert.Null(await Sql("PRAGMA foreign_key_check;", legacy)); Assert.Equal("ok", await Sql("PRAGMA integrity_check;", legacy));
    }
    [Fact]
    public async Task PageTemplateMigrationFailureRollsBackCatalogRebuildConstraintsAndLedger()
    {
        var legacy = await PopulatedPhaseTwenty();
        await Sql("CREATE TABLE PageTemplateDefinitions(ConflictingColumn TEXT);", legacy);
        var before = await PageTemplateDatabase(legacy); var schema = await TableRows(legacy, "sqlite_master");
        await Assert.ThrowsAnyAsync<Exception>(() => initializer.InitializeAsync(legacy, false));
        SameDatabase(before, await PageTemplateDatabase(legacy)); Assert.Equal(schema, await TableRows(legacy, "sqlite_master"));
        Assert.Null(await Sql("PRAGMA foreign_key_check;", legacy));
        await Sql("DROP TABLE PageTemplateDefinitions;", legacy); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM Templates;", legacy)); Assert.Equal(19L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
    }
    [Fact]
    public async Task PageTemplateDatabaseOwnershipGuardsRejectCrossTemplateRelationshipsAndWrongSourceTypes()
    {
        var page = await TemplateDashboard(); var a = await Capture(page.Item, TemplateType.Page); var b = await Capture(page.Item, TemplateType.Page);
        var ap = (PageBlueprint)a.Blueprint; var bp = (PageBlueprint)b.Blueprint;
        var child = ap.Canvas.Items.First(i => i.ParentContainerId is not null); var foreign = bp.Canvas.Items.Single(i => i.Kind == CanvasKind.Container);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE PageTemplateCanvasItems SET ParentContainerId='{foreign.Id}' WHERE Id='{child.Id}';"));
        var task = ap.Widgets.Single(w => w.Widget.Type == WidgetType.Task).Widget;
        var tracker = ap.Widgets.Single(w => w.Widget.Type == WidgetType.Tracker).Widget;
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE PageTemplateWidgets SET SourceItemId='{tracker.SourceItemId}' WHERE Id='{task.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO PageTemplateListSettings(WidgetId,ItemFilter) VALUES('{task.Id}',0);"));
        var connector = ap.Decorations.Single(d => d.Kind == DecorationKind.Connector);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE PageTemplateDecorationEndpoints SET TargetCanvasItemId='{foreign.Id}' WHERE DecorationId='{connector.Id}';"));
        var assetWidget = ap.Widgets.Single(w => w.Widget.Type == WidgetType.Image).Widget;
        var foreignReference = await Sql($"SELECT Id FROM TemplateAssetReferences WHERE TemplateId='{b.Metadata.Id}' LIMIT 1;");
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE PageTemplateAssetSettings SET ReferenceId='{foreignReference}' WHERE WidgetId='{assetWidget.Id}';"));
        await Templates().DeleteAsync(TRef(a)); await Templates().DeleteAsync(TRef(b));
        foreach (var table in (await PageTemplateDatabase()).Keys.Where(k => k.StartsWith("PageTemplate") || k == "TemplateAssetReferences"))
            Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM {table};"));
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
}
