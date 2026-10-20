using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private async Task<PageItem> PageInstance(TemplateSnapshot template, string title = "Instance", Guid? parent = null)
    {
        var item = await Templates().InstantiatePageAsync(TRef(template), title, parent);
        return (await Pages().GetAsync(profile)).Find(item.Id);
    }
    private async Task DeletePage(PageItem page)
    { await Pages().ApplyAsync(PRef(page), PageAction.Trash); await Pages().DeleteAsync(PRef(page)); }
    private async Task<Dictionary<string, string>> PageTemplateDatabase(Guid? id = null)
    {
        var names = new List<string>();
        await using (var c = await Connection(id ?? profile))
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) names.Add(r.GetString(0));
        }
        var result = new Dictionary<string, string>(); foreach (var name in names) result.Add(name, await TableRows(id ?? profile, name)); return result;
    }
    private static void SameDatabase(Dictionary<string, string> before, Dictionary<string, string> after)
    { Assert.Equal(before.Keys, after.Keys); foreach (var (name, rows) in before) Assert.True(rows == after[name], "Unexpected changes in " + name); }
    private async Task<PageItem> TemplateDashboard()
    {
        var page = await CanvasPage();
        var container = await Block(page, 1024, 512, kind: CanvasKind.Container);
        await Edit(page, new RenameCanvasContainer(container.Id, "Training"));
        var a = await Block(page, 8, 8, container.Id); var b = await Block(page, 224, 8, container.Id);
        await WAdd(page, (await JournalTasks().CreateAsync(profile, new("Shared task"))).Item, host: a.Id);
        var tracker = await Create(); await WAdd(page, tracker.Item, WidgetType.Tracker, WidgetMode.CurrentValue, b.Id);
        await WAdd(page, tracker.Item, WidgetType.TrackerChart, WidgetMode.Chart, chart: new(WidgetChartKind.Area, AnalyticsPreset.NinetyDays));
        var ev = await WidgetEvents().CreateAsync(profile, new("Shared event", true, Day, null, Day, null));
        await WAdd(page, ev.Item, WidgetType.Event);
        var journal = await Journals().CreateDefinitionAsync(profile, "Shared journal", "", []);
        await WAdd(page, journal.Item, WidgetType.Journal, WidgetMode.TodaySummary);
        await WAdd(page, (await Page("External page")).Item, WidgetType.PageLink);
        await ListWidget(page, await List(), filter: ListFilter.Unchecked);
        var rich = await ContentWidget(page); var doc = await WidgetDocument(page, rich);
        await Rich().UpdateAsync(Key(doc.Reference.Id), $"# Training\n\n**Independent** [self](personalworkspace://item/{page.Item.Id:D})");
        var image = await ContentWidget(page, WidgetType.Image); await WEdit(page, new ConfigureImageWidget(image.Id, ImageFitMode.Fill, "Training image"));
        await ContentWidget(page, WidgetType.Attachment);
        await Block(page, 2048, 1024);
        foreach (var kind in new[] { DecorationKind.Rectangle, DecorationKind.RoundedRectangle, DecorationKind.Ellipse }) await Shape(page, kind);
        foreach (var kind in new[] { DecorationKind.Line, DecorationKind.Arrow }) await DEdit(page, new CreateDecoration(kind, new(DecorationColor.Warning, 3, Dashed: true), Start: new(new(16, 32)), End: new(new(480, 384))));
        await DEdit(page, new CreateDecoration(DecorationKind.Connector, new(), Start: new(new(0, 0), a.Id, DecorationAnchor.Right), End: new(new(0, 0), b.Id, DecorationAnchor.Left)));
        await DEdit(page, new CreateDecoration(DecorationKind.Stroke, new(DecorationColor.Success, 4), Points: [new(16, 600), new(48, 580), new(64, 640), new(80, 600)]));
        await Edit(page, new ZoomCanvas(75)); return page;
    }

    [Fact]
    public async Task PageTemplateClonesEveryWidgetContainerDecorationAndExactGeometryWithFreshIdentities()
    {
        var source = await TemplateDashboard(); var original = await Widgets().GetAsync(PRef(source)); var decorations = await DRead(source);
        await Edit(source, new LockCanvas(true)); var template = await Capture(source.Item, TemplateType.Page);
        var blueprint = Assert.IsType<PageBlueprint>(template.Blueprint);
        Assert.Equal(Enum.GetValues<WidgetType>().Order(), blueprint.Widgets.Select(w => w.Widget.Type).Order());
        Assert.Empty(blueprint.Canvas.Items.Select(i => i.Id).Intersect(original.Canvas.Items.Select(i => i.Id)));
        var sourceRows = await PageTemplateDatabase(); var a = await PageInstance(template, "A"); var b = await PageInstance(template, "B", source.Item.Id);
        Assert.NotEqual(a.Item.Id, b.Item.Id); Assert.Equal(source.Item.Id, b.ParentPageId);
        var aw = await Widgets().GetAsync(PRef(a)); var bw = await Widgets().GetAsync(PRef(b));
        Assert.Empty(aw.Canvas.Items.Select(i => i.Id).Intersect(bw.Canvas.Items.Select(i => i.Id)));
        Assert.Empty(aw.Widgets.Select(w => w.Id).Intersect(bw.Widgets.Select(w => w.Id)));
        Assert.True(aw.Canvas.Settings.LayoutLocked); Assert.Equal(75, aw.Canvas.Settings.ZoomPercent);
        foreach (var item in original.Canvas.Items)
        {
            var copy = aw.Canvas.Items.Single(i => i.Kind == item.Kind && i.Rect == item.Rect);
            Assert.NotEqual(item.Id, copy.Id); Assert.Equal(item.Title, copy.Title);
            if (item.ParentContainerId is not null) Assert.Equal(aw.Canvas.Items.Single(i => i.Kind == CanvasKind.Container).Id, copy.ParentContainerId);
        }
        foreach (var w in original.Widgets)
        {
            var copy = aw.Widgets.Single(i => i.Type == w.Type); Assert.NotEqual(w.Id, copy.Id);
            Assert.Equal(w.SourceItemId, copy.SourceItemId); Assert.Equal(w.Mode, copy.Mode); Assert.Equal(w.Chart, copy.Chart); Assert.Equal(w.List, copy.List);
        }
        var ad = await DRead(a); var bd = await DRead(b);
        Assert.Empty(ad.Items.Select(d => d.Id).Intersect(bd.Items.Select(d => d.Id)));
        foreach (var d in decorations.Items)
        {
            var copy = ad.Items.Single(i => i.Kind == d.Kind); Assert.NotEqual(d.Id, copy.Id); Assert.Equal(d.ZOrder, copy.ZOrder);
            Assert.Equal(d.Style, copy.Style); Assert.Equal(d.Bounds, copy.Bounds); Assert.Equal(d.Points?.ToArray(), copy.Points?.ToArray());
            if (d.Kind == DecorationKind.Connector)
            {
                Assert.Equal(aw.Widgets.Single(w => w.Type == WidgetType.Task).CanvasItemId, copy.Start!.Target);
                Assert.Equal(aw.Widgets.Single(w => w.Type == WidgetType.Tracker).CanvasItemId, copy.End!.Target);
                Assert.Equal(DecorationRules.Resolve(d.Start!, original.Canvas), copy.Start.Point);
            }
            else { Assert.Equal(d.Start, copy.Start); Assert.Equal(d.End, copy.End); }
        }
        foreach (var name in new[] { "Tasks", "TaskValues", "TaskOccurrences", "Trackers", "TrackerEntries", "Events", "Journals", "JournalEntries", "Lists", "ListItems" })
            Assert.Equal(sourceRows[name], await TableRows(profile, name));
        Assert.Null(await Sql("PRAGMA foreign_key_check;")); Assert.Equal("ok", await Sql("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task PageTemplateRichTextAndAssetsRemainIndependentAcrossSourceDeletionTemplateDeletionAndRestart()
    {
        var source = await CanvasPage(); var rich = await ContentWidget(source); var image = await ContentWidget(source, WidgetType.Image);
        var originalDoc = await WidgetDocument(source, rich); var asset = (await WidgetAsset(source, image)).Asset;
        var markdown = $"**Original** [self](personalworkspace://item/{source.Item.Id:D})";
        await Rich().UpdateAsync(Key(originalDoc.Reference.Id), markdown);
        var template = await Capture(source.Item, TemplateType.Page); var a = await PageInstance(template, "A"); var b = await PageInstance(template, "B");
        var aw = await Widgets().GetAsync(PRef(a)); var bw = await Widgets().GetAsync(PRef(b));
        var adoc = await WidgetDocument(a, aw.Widgets.Single(w => w.Type == WidgetType.RichText)); var bdoc = await WidgetDocument(b, bw.Widgets.Single(w => w.Type == WidgetType.RichText));
        Assert.Equal(3, new[] { originalDoc.Document.Id, adoc.Document.Id, bdoc.Document.Id }.Distinct().Count());
        Assert.Equal(markdown.Replace(source.Item.Id.ToString(), a.Item.Id.ToString()), adoc.Document.Content);
        await Rich().UpdateAsync(Key(adoc.Reference.Id), "A only");
        Assert.Contains("Original", (await Rich().GetAsync(Key(bdoc.Document.Id))).Content);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Assets;")); Assert.Single(StoredFiles());
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM AssetReferences;")); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM TemplateAssetReferences;"));
        await DeletePage(source); await Assets().CleanupAsync(profile); Assert.True(File.Exists(AssetPath(asset)));
        await initializer.InitializeAsync(profile, false); await profiles.RestoreAsync();
        var c = await PageInstance(template, "After restart"); Assert.Contains("Original", Assert.Single(await Rich().ListAsync(PRef(c))).Document.Content);
        await Templates().DeleteAsync(TRef(template)); Assert.True(File.Exists(AssetPath(asset)));
        await DeletePage(a); await DeletePage(b); await Assets().CleanupAsync(profile); Assert.True(File.Exists(AssetPath(asset)));
        await DeletePage(c); await Assets().CleanupAsync(profile); Assert.False(File.Exists(AssetPath(asset)));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM RichTextDocuments;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM AssetReferences;"));
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(WidgetType.Task)][InlineData(WidgetType.Tracker)][InlineData(WidgetType.TrackerChart)][InlineData(WidgetType.Event)]
    [InlineData(WidgetType.Journal)][InlineData(WidgetType.PageLink)][InlineData(WidgetType.List)]
    public async Task PageTemplateMissingCanonicalSourcePreservesWidgetIdentityTypeAndSettings(WidgetType type)
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page);
        var old = ((PageBlueprint)template.Blueprint).Widgets.Single(w => w.Widget.Type == type).Widget;
        await Sql($"DELETE FROM WorkspaceItems WHERE Id='{old.SourceItemId}';");
        var stored = (PageBlueprint)(await Templates().ReadAsync(TRef(template))).Blueprint;
        var missing = stored.Widgets.Single(w => w.Widget.Id == old.Id).Widget;
        Assert.Null(missing.SourceItemId); Assert.Equal(old.Type, missing.Type); Assert.Equal(old.Chart, missing.Chart); Assert.Equal(old.List, missing.List);
        Assert.Equal(10, stored.Widgets.Count);
        var copy = await PageInstance(template); var widget = (await Widgets().GetAsync(PRef(copy))).Widgets.Single(w => w.Type == type);
        Assert.Null(widget.SourceItemId); Assert.Equal(old.Mode, widget.Mode); Assert.Equal(old.Chart, widget.Chart); Assert.Equal(old.List, widget.List);
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("WorkspaceItems")][InlineData("Pages")][InlineData("PageCanvasItems")][InlineData("PageCanvasSettings")]
    [InlineData("WidgetInstances")][InlineData("WidgetChartSettings")][InlineData("WidgetListSettings")]
    [InlineData("RichTextDocuments")][InlineData("RichTextReferences")][InlineData("WidgetRichTextSettings")]
    [InlineData("AssetReferences")][InlineData("WidgetAssetSettings")][InlineData("CanvasDecorations")]
    [InlineData("CanvasShapeSettings")][InlineData("CanvasDecorationEndpoints")][InlineData("CanvasStrokePoints")]
    public async Task PageTemplateInstantiationRollsBackEveryRowWhenAnyMandatoryWriteFails(string table)
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page); var before = await PageTemplateDatabase(); var files = StoredFiles();
        await Sql($"CREATE TRIGGER RejectPageInstance BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
        await Assert.ThrowsAsync<TemplateOperationException>(() => PageInstance(template));
        SameDatabase(before, await PageTemplateDatabase()); Assert.Equal(files, StoredFiles()); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("PageTemplateDefinitions")][InlineData("TemplateAssetReferences")][InlineData("PageTemplateCanvasItems")]
    [InlineData("PageTemplateWidgets")][InlineData("PageTemplateRichTextContent")][InlineData("PageTemplateAssetSettings")]
    [InlineData("PageTemplateDecorations")][InlineData("PageTemplateStrokePoints")]
    public async Task PageTemplateCaptureAndReplacementRollbackPreservePriorBlueprintAndAssetRetention(string table)
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page); var before = await PageTemplateDatabase();
        await Sql($"CREATE TRIGGER RejectPageCapture BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
        await Assert.ThrowsAsync<TemplateOperationException>(() => Capture(page.Item, TemplateType.Page)); SameDatabase(before, await PageTemplateDatabase());
        if (table == "TemplateAssetReferences") await ContentWidget(page, WidgetType.Image);
        before = await PageTemplateDatabase();
        await Assert.ThrowsAsync<TemplateOperationException>(() => Templates().ReplaceAsync(TRef(template), PRef(page)));
        SameDatabase(before, await PageTemplateDatabase()); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task PageTemplateReplacementRetainsIntersectionsAndReleasesOnlyObsoleteAssets()
    {
        var page = await CanvasPage(); var first = await ContentWidget(page, WidgetType.Image); var second = await ContentWidget(page, WidgetType.Attachment);
        var a = (await WidgetAsset(page, first)).Asset; var b = (await WidgetAsset(page, second)).Asset;
        var template = await Capture(page.Item, TemplateType.Page); var copy = await PageInstance(template);
        var retained = await Sql($"SELECT Id FROM TemplateAssetReferences WHERE AssetId='{a.Id}';");
        await WEdit(page, new RemoveWidget(second.Id)); Assert.True(File.Exists(AssetPath(b)));
        await Templates().ReplaceAsync(TRef(template), PRef(page));
        Assert.Equal(retained, await Sql($"SELECT Id FROM TemplateAssetReferences WHERE AssetId='{a.Id}';"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM TemplateAssetReferences;")); Assert.True(File.Exists(AssetPath(b)));
        Assert.Equal(2, (await Widgets().GetAsync(PRef(copy))).Widgets.Count);
        await DeletePage(copy); await Assets().CleanupAsync(profile); Assert.False(File.Exists(AssetPath(b))); Assert.True(File.Exists(AssetPath(a)));
        await DeletePage(page); await Templates().DeleteAsync(TRef(template)); Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task PageTemplateSoleRetentionSurvivesSourceDeletionAndCleanupFailureRetriesDurably()
    {
        var page = await CanvasPage(); var image = await ContentWidget(page, WidgetType.Image); var asset = (await WidgetAsset(page, image)).Asset;
        var template = await Capture(page.Item, TemplateType.Page); await DeletePage(page);
        await Assets().CleanupAsync(profile); Assert.Equal(0L, await Sql("SELECT PendingDelete FROM Assets;")); Assert.True(File.Exists(AssetPath(asset)));
        using (var locked = new FileStream(AssetPath(asset), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Templates().DeleteAsync(TRef(template)); Assert.Equal(1L, await Sql("SELECT PendingDelete FROM Assets;")); Assert.True(File.Exists(AssetPath(asset)));
        }
        await ((IWorkspaceMaintenance)Assets()).RecoverAsync(profile, default); Assert.Empty(StoredFiles()); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Assets;"));
    }

    [Fact]
    public async Task PageTemplatePendingAssetCannotBeRetainedRevivedOrPhysicallyDeletedWhileOwned()
    {
        var page = await CanvasPage(); var image = await ContentWidget(page, WidgetType.Image); var asset = (await WidgetAsset(page, image)).Asset;
        var template = await Capture(page.Item, TemplateType.Page);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE Assets SET PendingDelete=1 WHERE Id='{asset.Id}';"));
        await DeletePage(page);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE Assets SET PendingDelete=1 WHERE Id='{asset.Id}';"));
        // Simulate a damaged legacy pending flag: cleanup must recheck ownership before touching bytes.
        await Sql($"DROP TRIGGER TR_Asset_Pending; UPDATE Assets SET PendingDelete=1 WHERE Id='{asset.Id}';");
        await Assets().CleanupAsync(profile); Assert.True(File.Exists(AssetPath(asset)));
        await Assert.ThrowsAsync<TemplateValidationException>(() => PageInstance(template));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO TemplateAssetReferences VALUES('{Guid.NewGuid()}','{template.Metadata.Id}','{asset.Id}','now');"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Pages;"));
    }

    [Fact]
    public async Task PageTemplateConnectorFollowsNewContainerAndDetachesAtResolvedPosition()
    {
        var page = await CanvasPage(); var container = await Block(page, 512, 512, kind: CanvasKind.Container); var child = await Block(page, 8, 8, container.Id);
        await Connector(page, child); await Edit(page, new MoveCanvasItems([container.Id], 128, 64));
        var template = await Capture(page.Item, TemplateType.Page); var copy = await PageInstance(template);
        var layout = await Layout(copy); var target = layout.Items.Single(i => i.Kind == CanvasKind.Block); var copiedContainer = layout.Items.Single(i => i.Kind == CanvasKind.Container);
        var connector = Assert.Single((await DRead(copy)).Items); Assert.Equal(target.Id, connector.Start!.Target);
        Assert.Equal(DecorationRules.Resolve(connector.Start, layout), connector.Start.Point);
        await Edit(copy, new MoveCanvasItems([copiedContainer.Id], 64, 0)); var position = DecorationRules.Resolve(connector.Start, await Layout(copy));
        Assert.Equal(connector.Start.Point.X + 64, position.X);
        await Edit(copy, new DeleteCanvasItems([target.Id])); var detached = Assert.Single((await DRead(copy)).Items);
        Assert.Null(detached.Start!.Target); Assert.Equal(position, detached.Start.Point);
        Assert.Equal(child.Id, Assert.Single((await DRead(page)).Items).Start!.Target);
    }

    [Fact]
    public async Task PageTemplateLifecycleSourceChangesAndEmptyDuplicationRemainIndependent()
    {
        var page = await TemplateDashboard(); var child = await Page("Not captured", page); var template = await Capture(page.Item, TemplateType.Page);
        await Pages().UpdateAsync(PRef(page), "Renamed", PageIcon.Star); await Pages().MoveAsync(PRef(page), (await Page("Parent")).Item.Id);
        await Edit(page, new MoveCanvasItems([(await Layout(page)).Items.First(i => i.ParentContainerId is null).Id], 4096, 0));
        await Pages().ApplyAsync(PRef(page), PageAction.Archive); var a = await PageInstance(template);
        await Pages().ApplyAsync(PRef(page), PageAction.Trash); var b = await PageInstance(template);
        await Pages().DeleteAsync(PRef(page)); var c = await PageInstance(template);
        Assert.NotNull((await Pages().GetAsync(profile)).Find(child.Item.Id));
        Assert.Equal(10, (await Widgets().GetAsync(PRef(c))).Widgets.Count);
        Assert.Empty((await Pages().GetAsync(profile)).Children(c.Item.Id));
        var duplicate = await Pages().DuplicateAsync(PRef(c)); Assert.Empty((await Layout(duplicate)).Items); Assert.Empty((await DRead(duplicate)).Items);
        var original = await Widgets().GetAsync(PRef(a));
        await Pages().ApplyAsync(PRef(a), PageAction.Archive); await Pages().ApplyAsync(PRef(a), PageAction.RestoreArchive);
        await Pages().ApplyAsync(PRef(a), PageAction.Trash); await Pages().ApplyAsync(PRef(a), PageAction.RestoreTrash);
        Assert.Equal(original.Widgets.ToArray(), (await Widgets().GetAsync(PRef(a))).Widgets.ToArray());
        await Templates().DeleteAsync(TRef(template)); Assert.Equal(10, (await Widgets().GetAsync(PRef(b))).Widgets.Count);
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("[self]({0})")][InlineData("[self](<{0}>)")][InlineData("<{0}>")]
    [InlineData("- **[self]({0})**")]
    public void PageTemplateSelfLinksRemapOnlyParsedDestinations(string pattern)
    {
        var source = Guid.NewGuid(); var target = Guid.NewGuid(); var url = $"personalworkspace://item/{source:D}";
        var markdown = string.Format(pattern, url) + $"\n\nLiteral {source:D} [external](https://example.com/{source:D})";
        var result = PageTemplateLinks.Remap(markdown, source, target);
        Assert.Contains($"personalworkspace://item/{target:D}", result); Assert.Contains($"Literal {source:D}", result); Assert.Contains($"https://example.com/{source:D}", result);
        Assert.DoesNotContain(url, result);
    }
    [Fact]
    public void PageTemplateLinksRetainTheExistingMarkdownSubsetBoundary()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ContentValidationException>(() => PageTemplateLinks.Remap($"[self][id]\n\n[id]: personalworkspace://item/{id:D}", id, Guid.NewGuid()));
    }
}
