using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    [Theory]
    [InlineData("geometry")][InlineData("chart")][InlineData("list")][InlineData("rich")][InlineData("asset")][InlineData("stroke")]
    public async Task PageTemplateMalformedSnapshotRejectsInstantiationWithoutPartialRows(string part)
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page); var p = (PageBlueprint)template.Blueprint;
        switch (part)
        {
            case "geometry":
                var rootItems = p.Canvas.Items.Where(i => i.ParentContainerId is null && i.Kind == CanvasKind.Block).Take(2).ToArray();
                await Sql($"UPDATE PageTemplateCanvasItems SET X={rootItems[0].Rect.X},Y={rootItems[0].Rect.Y} WHERE Id='{rootItems[1].Id}';"); break;
            case "chart": await Sql("DELETE FROM PageTemplateChartSettings;"); break;
            case "list": await Sql("DELETE FROM PageTemplateListSettings;"); break;
            case "rich": await Sql("DELETE FROM PageTemplateRichTextContent;"); break;
            case "asset": await Sql("DELETE FROM PageTemplateAssetSettings;"); break;
            case "stroke": await Sql("DELETE FROM PageTemplateStrokePoints WHERE PointIndex=1;"); break;
        }
        var before = await PageTemplateDatabase();
        await Assert.ThrowsAsync<TemplateValidationException>(() => PageInstance(template)); SameDatabase(before, await PageTemplateDatabase());
    }
    [Fact]
    public async Task PageTemplateUnavailableFilesAndCancellationLeaveDatabaseAndRetentionUnchanged()
    {
        var page = await CanvasPage(); var widget = await ContentWidget(page, WidgetType.Image); var asset = (await WidgetAsset(page, widget)).Asset;
        var template = await Capture(page.Item, TemplateType.Page); var before = await PageTemplateDatabase(); var bytes = await File.ReadAllBytesAsync(AssetPath(asset));
        File.Delete(AssetPath(asset));
        await Assert.ThrowsAsync<TemplateValidationException>(() => PageInstance(template));
        await Assert.ThrowsAsync<TemplateValidationException>(() => Capture(page.Item, TemplateType.Page));
        SameDatabase(before, await PageTemplateDatabase()); await File.WriteAllBytesAsync(AssetPath(asset), bytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Templates().InstantiatePageAsync(TRef(template), token: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Templates().CaptureAsync(PRef(page), TemplateType.Page, "Cancelled", token: cancellation.Token));
        SameDatabase(before, await PageTemplateDatabase());
    }
    [Fact]
    public async Task PageTemplateProfileAndParentGuardsRejectStaleOrForeignCreation()
    {
        var page = await CanvasPage(); await ContentWidget(page); var template = await Capture(page.Item, TemplateType.Page);
        var before = await PageTemplateDatabase();
        await Assert.ThrowsAsync<TemplateValidationException>(() => PageInstance(template, parent: Guid.NewGuid()));
        await Pages().ApplyAsync(PRef(page), PageAction.Archive);
        await Assert.ThrowsAsync<TemplateValidationException>(() => PageInstance(template, parent: page.Item.Id));
        await Pages().ApplyAsync(PRef(page), PageAction.RestoreArchive);
        var foreign = await profiles.CreateAsync("Page templates isolation");
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Templates().InstantiatePageAsync(TRef(template)));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Capture(page.Item, TemplateType.Page));
        await Assert.ThrowsAsync<TemplateValidationException>(() => Templates().InstantiatePageAsync(new(foreign.Id, template.Metadata.Id)));
        await Assert.ThrowsAsync<TemplateValidationException>(() => Templates().CaptureAsync(new(foreign.Id, page.Item.Id), TemplateType.Page, "Foreign"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;", foreign.Id)); Assert.Empty(await Templates().CatalogAsync(foreign.Id));
        await profiles.SwitchAsync(profile); Assert.Equal(before["PageTemplateCanvasItems"], await TableRows(profile, "PageTemplateCanvasItems"));
        Assert.NotNull(await PageInstance(template));
    }
    [Fact]
    public async Task PageTemplateNativeModelCreatesInSelectedParentAndClearsPageDraftsOnProfileSwitch()
    {
        var source = await CanvasPage(); await ContentWidget(source); var parent = await Page("Destination");
        var template = await Capture(source.Item, TemplateType.Page); var nav = new NavigationService(); nav.Navigate(new("Templates"));
        var model = new TemplateWorkspaceViewModel(Templates(), current, nav, NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); model.Selected = model.Rows.Single(); await model.SelectAsync(model.Selected);
        Assert.True(model.IsPageTemplate); Assert.Contains("1 Widgets", model.Preview);
        model.NewTitle = "From UI"; model.Destination = model.Destinations.Single(d => d.Id == parent.Item.Id);
        await model.InstantiateCommand.ExecuteAsync(null); Assert.Null(model.Error);
        var copy = (await Pages().GetAsync(profile)).Pages.Values.Single(p => p.Item.Title == "From UI"); Assert.Equal(parent.Item.Id, copy.ParentPageId);
        await profiles.CreateAsync("UI isolation"); await model.ReloadAsync();
        Assert.False(model.IsPageTemplate); Assert.Null(model.Snapshot); Assert.Null(model.Source); Assert.Null(model.Destination?.Id);
        Assert.Single(model.Destinations); Assert.Empty(model.Rows); Assert.Equal("", model.NewTitle);
    }
    [Fact]
    public async Task PageTemplatePickerRefreshRestoresIdentityAfterNativeCollectionSelectionReset()
    {
        var page = await CanvasPage(); var parent = await Page("Destination");
        var nav = new NavigationService(); nav.Navigate(new("Templates"));
        var model = new TemplateWorkspaceViewModel(Templates(), current, nav, NullLogger<TemplateWorkspaceViewModel>.Instance);
        model.SourceType = TemplateType.Page; await model.ReloadAsync();
        model.Source = model.Sources.Single(s => s.Reference.ItemId == page.Item.Id);
        model.Destination = model.Destinations.Single(d => d.Id == parent.Item.Id);
        model.CaptureName = "Authored template name";
        // Native ComboBox invalidates its old selection when ItemsSource is replaced.
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.Sources)) model.Source = null;
            if (e.PropertyName == nameof(model.Destinations)) model.Destination = null;
        };
        await model.ReloadAsync();
        Assert.Equal(page.Item.Id, model.Source?.Reference.ItemId);
        Assert.Equal(parent.Item.Id, model.Destination?.Id);
        Assert.Equal("Authored template name", model.CaptureName);
        await Pages().ApplyAsync(PRef(parent), PageAction.Archive); await model.ReloadAsync();
        Assert.NotNull(model.Destination); Assert.Null(model.Destination.Id); Assert.Equal("Root", model.Destination.ToString());
    }
    [Fact]
    public async Task PageTemplateLockPreventsLayoutEditsWhileCanonicalAndRichContentActionsWork()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Canonical")); await WAdd(page, task.Item);
        await ContentWidget(page); await Edit(page, new LockCanvas(true)); var template = await Capture(page.Item, TemplateType.Page); var copy = await PageInstance(template);
        var snapshot = await Widgets().GetAsync(PRef(copy));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(copy, new MoveCanvasItems([snapshot.Canvas.Items[0].Id], 8, 0)));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WEdit(copy, new RemoveWidget(snapshot.Widgets[0].Id)));
        await Assert.ThrowsAsync<DecorationValidationException>(() => Shape(copy));
        await JournalTasks().UpdateAsync(new(profile, task.Item.Id), new("Canonical", Status: PersonalWorkspace.Core.TaskStatus.Done));
        var rich = await WidgetDocument(copy, snapshot.Widgets.Single(w => w.Type == WidgetType.RichText)); await Rich().UpdateAsync(Key(rich.Reference.Id), "Locked content edit");
        Assert.Equal("Locked content edit", (await Rich().GetAsync(Key(rich.Document.Id))).Content);
        Assert.Equal(snapshot.Canvas.Items.ToArray(), (await Layout(copy)).Items.ToArray());
    }
    [Fact]
    public async Task PageTemplateArchivedTrashedSourcesAndShoppingFallbackKeepCanonicalIdentity()
    {
        var page = await CanvasPage(); var list = await Lists().CreateAsync(profile, new("Prices", ShowPrices: true));
        var widget = await ListWidget(page, list, WidgetMode.Shopping, ListFilter.Checked);
        var template = await Capture(page.Item, TemplateType.Page);
        await Lists().UpdateAsync(LRef(list), new("Unpriced", ShowPrices: false));
        await Lists().ApplyAsync(LRef(list), ListAction.Trash);
        var copy = await PageInstance(template); var copied = Assert.Single((await Widgets().GetAsync(PRef(copy))).Widgets);
        Assert.Equal(widget.SourceItemId, copied.SourceItemId); Assert.Equal(WidgetMode.Shopping, copied.Mode); Assert.Equal(ListFilter.Checked, copied.List!.Filter);
        var sources = (await Widgets().GetAsync(PRef(copy))).Sources; Assert.NotNull(Assert.Single(sources).DeletedAtUtc);
        await Lists().ApplyAsync(LRef(list), ListAction.RestoreTrash); Assert.Null(Assert.Single((await Widgets().GetAsync(PRef(copy))).Sources).DeletedAtUtc);
    }
    [Fact]
    public async Task PageTemplateLargeSnapshotIsSelectedOnlyAndCapturesNoUnrelatedHistory()
    {
        var page = await CanvasPage();
        // One canonical transaction supplies a practical 300-item layout without hundreds of service calls.
        await new SqliteCanvasRepository().EditAsync(new(profile, paths.WorkspaceDatabase(profile)), page.Item.Id, s => s with
        { Items = Enumerable.Range(0, 300).Select(i => new CanvasItem(Guid.NewGuid(), page.Item.Id, null, CanvasKind.Block, new((i % 20) * 80, (i / 20) * 64, 64, 48), "", clock.GetUtcNow(), clock.GetUtcNow())).ToArray() }, default);
        var template = await Capture(page.Item, TemplateType.Page); var copy = await PageInstance(template);
        Assert.Equal(300, (await Layout(copy)).Items.Count);
        var unrelated = await CanvasPage(); var text = await ContentWidget(unrelated); var doc = await WidgetDocument(unrelated, text);
        await Sql($"UPDATE RichTextDocuments SET Content='<script>unsupported</script>' WHERE Id='{doc.Document.Id}';");
        Assert.Single(await Templates().CatalogAsync(profile)); Assert.Equal(300, ((PageBlueprint)(await Templates().ReadAsync(TRef(template))).Blueprint).Canvas.Items.Count);
        Assert.NotNull(await PageInstance(template)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TrackerEntries;"));
    }
    [Fact]
    public async Task PageTemplateSnapshotReopenPreservesExactPersistentRelationshipsAndSettings()
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page); await PageInstance(template);
        var before = await PageTemplateDatabase(); await initializer.InitializeAsync(profile, false); await profiles.RestoreAsync();
        var reopened = await Templates().ReadAsync(TRef(template)); Assert.Equal(template.Metadata, reopened.Metadata);
        SameDatabase(before, await PageTemplateDatabase());
        var copy = await PageInstance(reopened); Assert.Equal(7, (await DRead(copy)).Items.Count); Assert.Equal(10, (await Widgets().GetAsync(PRef(copy))).Widgets.Count);
    }
    [Fact]
    public async Task PageTemplateMultipleWidgetsShareOneRetentionButReceiveSeparatePageReferences()
    {
        var page = await CanvasPage(); var first = await ContentWidget(page, WidgetType.Image);
        await WEdit(page, new DuplicateWidget(first.Id)); var template = await Capture(page.Item, TemplateType.Page);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM TemplateAssetReferences;"));
        var copy = await PageInstance(template); var refs = await Assets().ListAsync(PRef(copy));
        Assert.Equal(2, refs.Count); Assert.NotEqual(refs[0].Reference.Id, refs[1].Reference.Id); Assert.Equal(refs[0].Asset.Id, refs[1].Asset.Id);
        Assert.Single(StoredFiles()); await DeletePage(page); await Templates().DeleteAsync(TRef(template));
        var widgets = (await Widgets().GetAsync(PRef(copy))).Widgets;
        await WEdit(copy, new RemoveWidget(widgets[0].Id)); Assert.Single(StoredFiles());
        await WEdit(copy, new RemoveWidget(widgets[1].Id)); Assert.Empty(StoredFiles());
    }
    [Fact]
    public async Task PageTemplateTiedDecorationOrderAndMissingTargetFallbackAreDeterministic()
    {
        var page = await CanvasPage(); var block = await Block(page); await Shape(page); await Shape(page, DecorationKind.Ellipse); await Connector(page, block);
        await Sql("UPDATE CanvasDecorations SET ZOrder=0;");
        var before = (await DRead(page)).Items; var template = await Capture(page.Item, TemplateType.Page); var copy = await PageInstance(template);
        Assert.Equal(before.Select(d => d.Kind), (await DRead(copy)).Items.Select(d => d.Kind));
        var blueprint = (PageBlueprint)template.Blueprint; var captured = blueprint.Decorations.Single(d => d.Kind == DecorationKind.Connector);
        var partial = blueprint with { Canvas = blueprint.Canvas with { Items = [] } };
        var detached = PageTemplateService.Remap(partial, Guid.NewGuid(), clock.GetUtcNow(), true).Decorations.Single(d => d.Kind == DecorationKind.Connector);
        Assert.Null(detached.Start!.Target); Assert.Equal(captured.Start!.Point, detached.Start.Point);
    }
    [Fact]
    public async Task PageTemplateUnsupportedTypesAndCapacityViolationsFailExplicitly()
    {
        var page = await CanvasPage(); await ContentWidget(page); var template = await Capture(page.Item, TemplateType.Page);
        var p = (PageBlueprint)template.Blueprint; var widget = p.Widgets[0];
        Assert.Throws<WidgetValidationException>(() => PageTemplateService.ValidateStructure(p with { Widgets = [widget with { Widget = widget.Widget with { Type = (WidgetType)99 } }] }));
        var stroke = new CanvasDecoration(Guid.NewGuid(), p.Canvas.PageId, DecorationKind.Stroke, new(), 0, default, default,
            Points: Enumerable.Range(0, DecorationRules.MaxPoints + 1).Select(i => new DecorationPoint(i, 0)).ToArray());
        Assert.Throws<DecorationValidationException>(() => PageTemplateService.ValidateStructure(p with { Decorations = [stroke] }));
        var shape = new CanvasDecoration(Guid.NewGuid(), p.Canvas.PageId, DecorationKind.Rectangle, new(), 0, default, default, Bounds: new(0, 0, 16, 16));
        Assert.Throws<TemplateValidationException>(() => PageTemplateService.ValidateStructure(p with { Decorations = Enumerable.Range(0, 2001).Select(_ => shape with { Id = Guid.NewGuid() }).ToArray() }));
    }
    [Fact]
    public async Task PageTemplateCaptureKeepsWorkspaceGateUntilRetentionAndSnapshotCommit()
    {
        var page = await CanvasPage(); await ContentWidget(page, WidgetType.Image);
        var files = new PausedPageTemplateFiles(AssetFiles());
        var templates = new TemplateService(new SqliteTemplateRepository(), JournalTasks(), Lists(), Journals(), current, gate, clock, NullLogger<TemplateService>.Instance,
            new PageTemplateService(new SqliteTemplateRepository(), files, clock), Assets());
        var capture = templates.CaptureAsync(PRef(page), TemplateType.Page, "Consistent"); await files.Started.Task;
        var edit = Pages().UpdateAsync(PRef(page), "Later title", PageIcon.Star);
        Assert.False(edit.IsCompleted); files.Release.SetResult();
        var saved = await capture; await edit;
        Assert.Equal(page.Item.Title, saved.Blueprint.Title); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM TemplateAssetReferences;"));
    }
    private sealed class PausedPageTemplateFiles(IAssetFileStore inner) : IAssetFileStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task UseFileAsync(Guid profile, Asset asset, IAssetFileConsumer consumer, CancellationToken token)
        { Started.TrySetResult(); await Release.Task.WaitAsync(token); await inner.UseFileAsync(profile, asset, consumer, token); }
        public void CheckImportDestination(Guid p, AssetImportJob j) => inner.CheckImportDestination(p, j);
        public Task<AssetFileInfo> ImportAsync(Guid p, AssetImportJob j, string s, CancellationToken t) => inner.ImportAsync(p, j, s, t);
        public void Delete(Guid p, string s) => inner.Delete(p, s);
        public Task CopyToAsync(Guid p, Asset a, Stream s, CancellationToken t) => inner.CopyToAsync(p, a, s, t);
        public Task<string?> CheckIntegrityAsync(Guid p, Asset a, CancellationToken t) => inner.CheckIntegrityAsync(p, a, t);
    }
}
