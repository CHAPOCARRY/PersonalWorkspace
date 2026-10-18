using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private async Task<WidgetInstance> ContentWidget(PageItem page, WidgetType type = WidgetType.RichText, Guid? host = null, Guid? parent = null)
    {
        var old = (await Widgets().GetAsync(PRef(page))).Widgets.Select(w => w.Id).ToHashSet();
        var edit = new AddWidget(type, null, WidgetMode.Card, host, parent);
        var result = type == WidgetType.RichText ? await WEdit(page, edit)
            : await Widgets().ImportAsync(PRef(page), edit, await SourceFile(type == WidgetType.Image ? "photo.png" : "notes.txt", type == WidgetType.Image ? PngBytes : null));
        return result.Widgets.Single(w => !old.Contains(w.Id));
    }
    private async Task<RichTextAttachment> WidgetDocument(PageItem page, WidgetInstance widget) =>
        (await Rich().ListAsync(PRef(page))).Single(d => d.Reference.Id == widget.RichText!.ReferenceId);
    private async Task<AssetAttachment> WidgetAsset(PageItem page, WidgetInstance widget) =>
        (await Assets().ListAsync(PRef(page))).Single(a => a.Reference.Id == widget.Asset!.ReferenceId);

    [Fact]
    public async Task ContentWidgetMigrationPreservesAllPhaseSeventeenWidgetsAndSharedContent()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var m in WorkspaceMigrationCatalog.All.Take(15)) await Sql(m.Sql + $"INSERT INTO SchemaMigrations VALUES({m.Version},'{m.Name}','original');", legacy);
        var page = Guid.NewGuid(); var stamp = "2026-10-13T00:00:00+00:00";
        await Sql($"INSERT INTO WorkspaceItems VALUES('{page}',5,'Existing','{stamp}','{stamp}',NULL,NULL);INSERT INTO Pages VALUES('{page}',NULL,0,NULL);INSERT INTO PageCanvasSettings VALUES('{page}',0,125);", legacy);
        int[] types = [1,3,3,2,4,5,6]; int[] modes = [1,3,5,0,6,0,8];
        for (var i = 0; i < types.Length; i++)
        {
            var source = Guid.NewGuid(); var host = Guid.NewGuid(); var widget = Guid.NewGuid();
            await Sql($"INSERT INTO WorkspaceItems VALUES('{source}',{types[i]},'Source {i}','{stamp}','{stamp}',NULL,NULL);INSERT INTO PageCanvasItems VALUES('{host}','{page}',NULL,0,24,{24+i*160},320,128,'','{stamp}','{stamp}');", legacy);
            if (i == 2) await Sql($"INSERT INTO Trackers(ItemId,ValueType,Frequency,StartDate,Interval,Weekdays,EntryMode,Aggregation) VALUES('{source}',0,0,'2026-10-13',1,0,0,0);", legacy);
            if (i == 6) await Sql($"INSERT INTO Lists VALUES('{source}','Keep',1,1,'EUR');", legacy);
            await Sql($"INSERT INTO WidgetInstances VALUES('{widget}','{host}',{i},'{source}',{modes[i]},'{stamp}','{stamp}');", legacy);
            if (i == 2) await Sql($"INSERT INTO WidgetChartSettings VALUES('{widget}',2,1);", legacy);
            if (i == 6) await Sql($"UPDATE WidgetListSettings SET ItemFilter=2 WHERE WidgetId='{widget}';", legacy);
        }
        await Sql("UPDATE WidgetInstances SET SourceItemId=NULL WHERE WidgetType=0;", legacy);
        var repository = new SqliteContentRepository(); var context = new WorkspaceContext(legacy, paths.WorkspaceDatabase(legacy));
        var doc = await repository.CreateAsync(context, page, new(Guid.NewGuid(), "**Keep**", "markdown-subset", 1, clock.GetUtcNow(), clock.GetUtcNow()), default);
        var id = Guid.NewGuid(); var relative = AssetPaths.Relative(id, "notes.txt");
        await repository.BeginImportAsync(context, page, new(id, relative), default);
        var physical = new AssetPaths(paths).Resolve(legacy, relative); Directory.CreateDirectory(Path.GetDirectoryName(physical)!); await File.WriteAllTextAsync(physical, "Keep");
        await repository.CompleteImportAsync(context, page, new(id, relative, "notes.txt", "text/plain", 4, new string('A',64), clock.GetUtcNow()), default);
        string[] tables = ["WorkspaceItems","Pages","PageCanvasItems","PageCanvasSettings","WidgetInstances","WidgetChartSettings","WidgetListSettings","Assets","AssetReferences","RichTextDocuments","RichTextReferences"];
        var snapshots = new Dictionary<string,string>(); foreach (var table in tables) snapshots[table] = await TableRows(legacy, table);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        foreach (var table in tables) Assert.Equal(snapshots[table], await TableRows(legacy, table));
        Assert.Equal("Keep", await File.ReadAllTextAsync(physical)); Assert.Equal(17L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(15L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Null(await Sql("PRAGMA foreign_key_check;", legacy));
    }
    [Theory]
    [InlineData(WidgetType.RichText)][InlineData(WidgetType.Image)][InlineData(WidgetType.Attachment)]
    public async Task ContentWidgetsHaveNullSourceStableIdentityAndPreserveEmptyHost(WidgetType type)
    {
        var page = await CanvasPage(); var container = await Block(page, kind: CanvasKind.Container); var host = await Block(page, 8, 8, container.Id);
        var widget = await ContentWidget(page, type, host.Id);
        Assert.Null(widget.SourceItemId); Assert.NotEqual(widget.Id, widget.CanvasItemId);
        Assert.Equal(host, (await Layout(page)).Items.Single(i => i.Id == host.Id));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(widget, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets));
        if (type == WidgetType.RichText) Assert.Equal("", (await WidgetDocument(page, widget)).Document.Content);
        else { Assert.Equal(ImageFitMode.Fit, widget.Asset!.Fit); Assert.True(File.Exists(AssetPath((await WidgetAsset(page, widget)).Asset))); }
        await WEdit(page, new RemoveWidget(widget.Id)); Assert.Equal(host, (await Layout(page)).Items.Single(i => i.Id == host.Id));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM AssetReferences;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM RichTextDocuments;")); Assert.Empty(StoredFiles());
    }
    [Fact]
    public async Task RichWidgetDuplicateClonesCanonicalDocumentAndEditsIndependentlyWhileLocked()
    {
        var page = await CanvasPage(); var widget = await ContentWidget(page); var first = await WidgetDocument(page, widget);
        var text = $"# Weekly Review\n\nThis was **productive** and *focused*.\n\n- Training\n- Work\n- Study\n\n[Goals](personalworkspace://item/{page.Item.Id})";
        await Rich().UpdateAsync(Key(first.Reference.Id), text);
        var copy = (await WEdit(page, new DuplicateWidget(widget.Id))).Widgets.Single(w => w.Id != widget.Id);
        var second = await WidgetDocument(page, copy);
        Assert.NotEqual(widget.CanvasItemId, copy.CanvasItemId); Assert.NotEqual(first.Reference.Id, second.Reference.Id); Assert.NotEqual(first.Document.Id, second.Document.Id); Assert.Equal(text, second.Document.Content);
        await Edit(page, new LockCanvas(true)); var vm = WidgetModel(); await vm.OpenAsync(PRef(page));
        await vm.SaveRichTextAsync(vm.Rows.Single(r => r.Widget.Id == copy.Id), "# Independent");
        Assert.Equal(text, (await Rich().GetAsync(Key(first.Document.Id))).Content); Assert.Equal("# Independent", (await Rich().GetAsync(Key(second.Document.Id))).Content);
        Assert.Equal(widget, (await Widgets().GetAsync(PRef(page))).Widgets.Single(w => w.Id == widget.Id));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WEdit(page, new RemoveWidget(widget.Id)));
    }
    [Theory]
    [InlineData(WidgetType.Image)][InlineData(WidgetType.Attachment)]
    public async Task AssetWidgetDuplicatesShareBytesWithIndependentReferencesAndConfiguration(WidgetType type)
    {
        var page = await CanvasPage(); var widget = await ContentWidget(page, type); var original = await WidgetAsset(page, widget);
        var copy = (await WEdit(page, new DuplicateWidget(widget.Id))).Widgets.Single(w => w.Id != widget.Id); var second = await WidgetAsset(page, copy);
        Assert.Equal(original.Asset, second.Asset); Assert.NotEqual(original.Reference.Id, second.Reference.Id); Assert.Single(StoredFiles());
        if (type == WidgetType.Image)
        {
            await WEdit(page, new ConfigureImageWidget(copy.Id, ImageFitMode.Fill, "A useful description"));
            var saved = (await Widgets().GetAsync(PRef(page))).Widgets.Single(w => w.Id == copy.Id);
            Assert.Equal(ImageFitMode.Fill, saved.Asset!.Fit); Assert.Equal("A useful description", saved.Asset.AltText); Assert.True(saved.UpdatedAtUtc > copy.UpdatedAtUtc);
            Assert.Equal(ImageFitMode.Fit, widget.Asset!.Fit); Assert.Equal(original.Asset, (await WidgetAsset(page, saved)).Asset);
        }
        await WEdit(page, new RemoveWidget(widget.Id, true)); Assert.True(File.Exists(AssetPath(original.Asset)));
        await WEdit(page, new RemoveWidget(copy.Id, true)); Assert.Empty(StoredFiles()); Assert.Empty((await Layout(page)).Items); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Theory]
    [InlineData("WidgetInstances")][InlineData("WidgetAssetSettings")][InlineData("PageCanvasItems")]
    public async Task AssetWidgetCreationFailureCompensatesFilesMetadataAndGeometry(string table)
    {
        var page = await CanvasPage(); var source = await SourceFile("photo.png", PngBytes);
        await Sql($"CREATE TRIGGER FailContentWidget BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ContentOperationException>(() => Widgets().ImportAsync(PRef(page), new(WidgetType.Image,null,WidgetMode.Card), source));
        Assert.Empty((await Layout(page)).Items); Assert.Empty(StoredFiles());
        foreach (var name in new[] {"WidgetInstances","Assets","AssetReferences","AssetImports"}) Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM {name};"));
    }
    [Theory]
    [InlineData("RichTextDocuments")][InlineData("RichTextReferences")][InlineData("WidgetRichTextSettings")]
    public async Task RichWidgetFailureRollsBackAllNewIdentities(string table)
    {
        var page = await CanvasPage(); var host = await Block(page);
        await Sql($"CREATE TRIGGER FailContentWidget BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<WidgetOperationException>(() => ContentWidget(page, host: host.Id));
        Assert.Equal(host, Assert.Single((await Layout(page)).Items));
        foreach (var name in new[] {"WidgetInstances","RichTextDocuments","RichTextReferences"}) Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM {name};"));
    }
    [Fact]
    public async Task ContentWidgetLifecycleAndPageDeletionKeepExternallySharedContent()
    {
        var page = await CanvasPage(); var other = await Page("Other"); var rich = await ContentWidget(page); var image = await ContentWidget(page, WidgetType.Image);
        var doc = await WidgetDocument(page, rich); var asset = await WidgetAsset(page, image);
        var externalDoc = await Rich().AddReferenceAsync(PRef(other), Key(doc.Document.Id)); var externalAsset = await Assets().AddReferenceAsync(PRef(other), Key(asset.Asset.Id));
        foreach (var action in new[] {PageAction.Archive, PageAction.RestoreArchive, PageAction.Trash, PageAction.RestoreTrash})
        {
            await Pages().ApplyAsync(PRef(page), action); Assert.Equal(2,(await Widgets().GetAsync(PRef(page))).Widgets.Count);
            Assert.Equal(doc, await WidgetDocument(page, rich)); Assert.True(File.Exists(AssetPath(asset.Asset)));
        }
        var empty = await Pages().DuplicateAsync(PRef(page)); Assert.Empty((await Widgets().GetAsync(PRef(empty))).Widgets);
        await Pages().ApplyAsync(PRef(page), PageAction.Trash); await Pages().DeleteAsync(PRef(page)); await Assets().CleanupAsync(profile);
        Assert.Equal(doc.Document, await Rich().GetAsync(Key(doc.Document.Id))); Assert.True(File.Exists(AssetPath(asset.Asset))); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WidgetInstances;"));
        await Rich().RemoveReferenceAsync(Key(externalDoc.Id)); await Assets().RemoveReferenceAsync(Key(externalAsset.Id));
        Assert.Empty(StoredFiles()); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM RichTextDocuments;")); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task ContentWidgetDatabaseRejectsWrongOwnerSourceTypeAndSharedReferenceOwnership()
    {
        var page = await CanvasPage(); var rich = await ContentWidget(page); var image = await ContentWidget(page, WidgetType.Image); var other = await CanvasPage(); var otherRich = await ContentWidget(other);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE WidgetInstances SET SourceItemId='{page.Item.Id}' WHERE Id='{rich.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE WidgetAssetSettings SET ReferenceId='{Guid.NewGuid()}' WHERE WidgetId='{image.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"DELETE FROM RichTextReferences WHERE Id='{rich.RichText!.ReferenceId}';"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"DELETE FROM WidgetRichTextSettings WHERE WidgetId='{rich.Id}';"));
        // Use otherwise valid, unclaimed references so ownership/type guards are tested,
        // rather than merely hitting the UNIQUE constraint on an existing Widget reference.
        var foreignDocument = await Rich().CreateAsync(PRef(other), "Other Page");
        var foreignAsset = await Assets().ImportAsync(PRef(other), await SourceFile("foreign.png", PngBytes));
        var genericAsset = await Assets().ImportAsync(PRef(page), await SourceFile("generic.txt"));
        foreach (var (type, reference, table) in new[] {
            (WidgetType.RichText, foreignDocument.Reference.Id, "WidgetRichTextSettings"),
            (WidgetType.Image, foreignAsset.Reference.Id, "WidgetAssetSettings"),
            (WidgetType.Attachment, foreignAsset.Reference.Id, "WidgetAssetSettings"),
            (WidgetType.Image, genericAsset.Reference.Id, "WidgetAssetSettings") })
        {
            var host = await Block(page, y: 1200); var id = Guid.NewGuid();
            await Assert.ThrowsAsync<SqliteException>(() => Sql($"BEGIN; INSERT INTO WidgetInstances VALUES('{id}','{host.Id}',{(int)type},NULL,0,'test','test'); INSERT INTO {table}(WidgetId,ReferenceId) VALUES('{id}','{reference}'); COMMIT;"));
            Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM WidgetInstances WHERE Id='{id}';"));
            await Edit(page, new DeleteCanvasItems([host.Id]));
        }
        Assert.Equal(3L,await Sql("SELECT COUNT(*) FROM WidgetInstances;")); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task ContainerDeletionPreservesContentAndExplicitBlockDeletionReleasesOnlyOwnedReferences()
    {
        var page = await CanvasPage(); var other = await Page("Shared asset owner");
        var container = await Block(page, kind: CanvasKind.Container); await Edit(page, new ResizeCanvasItem(container.Id, 1200, 1200));
        await ContentWidget(page, parent: container.Id);
        var image = await ContentWidget(page, WidgetType.Image, parent: container.Id);
        await ContentWidget(page, WidgetType.Attachment, parent: container.Id);
        var asset = await WidgetAsset(page, image);
        var shared = await Assets().AddReferenceAsync(PRef(other), Key(asset.Asset.Id));
        var canvas = new CanvasService(new SqliteCanvasRepository(), current, gate, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<CanvasService>.Instance, Assets());
        var before = await Widgets().GetAsync(PRef(page));
        await canvas.EditAsync(PRef(page), new DeleteCanvasItems([container.Id]));
        // Phase 13 Container deletion detaches children; it must never delete their content.
        var detached = await Widgets().GetAsync(PRef(page));
        Assert.Equal(before.Widgets, detached.Widgets);
        Assert.All(detached.Canvas.Items, item => Assert.Null(item.ParentContainerId));
        Assert.Equal(2, StoredFiles().Length);
        await canvas.EditAsync(PRef(page), new DeleteCanvasItems(detached.Canvas.Items.Select(item => item.Id).ToArray()));
        Assert.Empty((await Widgets().GetAsync(PRef(page))).Widgets); Assert.Empty((await Layout(page)).Items);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM RichTextDocuments;"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WidgetAssetSettings;"));
        Assert.Single(StoredFiles()); Assert.True(File.Exists(AssetPath(asset.Asset)));
        await Assets().RemoveReferenceAsync(Key(shared.Id)); Assert.Empty(StoredFiles());
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task ContentWidgetInvalidAndCancelledImportsPreserveEmptyBlock()
    {
        var page = await CanvasPage(); var host = await Block(page);
        var invalid = await SourceFile("photo.jpg", [1,2,3]);
        await Assert.ThrowsAsync<ContentValidationException>(() => Widgets().ImportAsync(PRef(page), new(WidgetType.Image,null,WidgetMode.Card,host.Id), invalid));
        var generic = await SourceFile();
        await Assert.ThrowsAsync<ContentOperationException>(() => Widgets().ImportAsync(PRef(page), new(WidgetType.Image,null,WidgetMode.Card,host.Id), generic));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Widgets().ImportAsync(PRef(page), new(WidgetType.Attachment,null,WidgetMode.Card,host.Id), generic, cancelled.Token));
        Assert.Equal(host,Assert.Single((await Layout(page)).Items)); Assert.Empty(StoredFiles()); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));
    }
    [Fact]
    public async Task ContentWidgetProfileAndPageSwitchClearStalePresentationAndRejectActions()
    {
        var page = await CanvasPage(); var rich = await ContentWidget(page); var image = await ContentWidget(page, WidgetType.Image); var vm = WidgetModel();
        await vm.OpenAsync(PRef(page)); var old = vm.Rows.Single(r => r.Widget.Id == rich.Id);
        var otherPage = await CanvasPage(); await vm.OpenAsync(PRef(otherPage)); Assert.Empty(vm.Rows); await vm.SaveRichTextAsync(old,"wrong");
        Assert.Equal("",(await WidgetDocument(page,rich)).Document.Content);
        var other = await profiles.CreateAsync("Other content Widgets"); Assert.Empty(vm.Rows);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Widgets().GetAsync(PRef(page)));
        var stored = (await new SqliteContentRepository().ListAsync(new(profile,paths.WorkspaceDatabase(profile)),page.Item.Id,default)).Single();
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Assets().GetAsync(Key(stored.Asset.Id)));
        await profiles.SwitchAsync(profile); await vm.OpenAsync(PRef(page)); Assert.Equal(2,vm.Rows.Count); Assert.NotNull(vm.Rows.Single(r=>r.Widget.Id==image.Id).Asset);
    }
    [Fact]
    public async Task ContentWidgetMovementChangesOnlyLayoutAndDoesNotReloadContent()
    {
        var page = await CanvasPage(); var container = await Block(page,kind:CanvasKind.Container); await Edit(page,new ResizeCanvasItem(container.Id,1200,1200));
        var rich = await ContentWidget(page,parent:container.Id); var image = await ContentWidget(page,WidgetType.Image,parent:container.Id); var file = await ContentWidget(page,WidgetType.Attachment,parent:container.Id);
        var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var rows = vm.Rows.ToArray(); var before = await Widgets().GetAsync(PRef(page)); var doc = await WidgetDocument(page,rich); var asset = await WidgetAsset(page,image);
        var changed = await Edit(page,new MoveCanvasItems([container.Id],8,8));
        for(var i=0;i<100;i++) vm.SetCanvas(changed);
        Assert.All(rows,r=>Assert.Contains(vm.Rows,currentRow=>ReferenceEquals(currentRow,r)));
        Assert.Equal(before.Widgets,(await Widgets().GetAsync(PRef(page))).Widgets);
        Assert.Equal(doc,await WidgetDocument(page,rich)); Assert.Equal(asset,await WidgetAsset(page,image));
    }
    [Fact]
    public async Task ValidatedFileConsumerRejectsMissingAndCrossProfileAssetsWithoutLaunching()
    {
        var page=await CanvasPage();var widget=await ContentWidget(page,WidgetType.Attachment);var asset=await WidgetAsset(page,widget);var consumer=new RecordingFileConsumer();
        await Assets().UseFileAsync(Key(asset.Asset.Id),consumer);Assert.Equal(AssetPath(asset.Asset),consumer.Path);Assert.Equal(asset.Asset,consumer.Asset);
        File.Delete(AssetPath(asset.Asset));consumer.Path=null;await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().UseFileAsync(Key(asset.Asset.Id),consumer));Assert.Null(consumer.Path);
        await profiles.CreateAsync("Other file consumer");await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Assets().UseFileAsync(Key(asset.Asset.Id),consumer));Assert.Null(consumer.Path);
    }
    private sealed class RecordingFileConsumer : IAssetFileConsumer
    {
        public string? Path {get;set;} public Asset? Asset {get;private set;}
        public Task UseAsync(string validatedPath,Asset asset,CancellationToken token){Path=validatedPath;Asset=asset;return Task.CompletedTask;}
    }
    [Theory]
    [InlineData("# Heading")][InlineData("paragraph\n\nnext")][InlineData("**bold** and *italic*")][InlineData("- a\n- b")][InlineData("1. first\n2. second")][InlineData("[Web](https://example.com)")][InlineData("[Internal](personalworkspace://item/11111111-1111-1111-1111-111111111111)")]
    public void RichNativeProjectionSupportsCanonicalConstructs(string content)
    {
        var doc=new RichTextDocument(Guid.NewGuid(),content,RichTextFormat.Name,1,clock.GetUtcNow(),clock.GetUtcNow());var view=RichTextPresentation.Preview(doc);
        Assert.False(view.Fallback);Assert.NotEmpty(view.Paragraphs);
    }
    [Theory]
    [InlineData("<script>alert(1)</script>")][InlineData("[run](javascript:alert)")][InlineData("![image](https://example.com/image.png)")]
    public void RichNativeProjectionFallsBackToInertText(string content)
    {
        var doc=new RichTextDocument(Guid.NewGuid(),content,RichTextFormat.Name,1,clock.GetUtcNow(),clock.GetUtcNow());var view=RichTextPresentation.Preview(doc);
        Assert.True(view.Fallback);Assert.Equal(content,Assert.Single(Assert.Single(view.Paragraphs).Inlines).Text);
    }
    [Theory]
    [InlineData(RichFormatAction.Bold,"**word**")][InlineData(RichFormatAction.Italic,"*word*")][InlineData(RichFormatAction.Heading,"# word")][InlineData(RichFormatAction.BulletedList,"- word")][InlineData(RichFormatAction.NumberedList,"1. word")][InlineData(RichFormatAction.Link,"[word](https://example.com)")]
    public void RichToolbarProducesValidCanonicalFormatting(RichFormatAction action,string expected)
    {
        var result=RichTextEditing.Apply("word",0,4,action);Assert.Equal(expected,result.Text);Assert.Equal(expected,RichTextFormat.Validate(result.Text));
    }
}
