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
    private CanvasService Canvas(ICanvasRepository? repository = null) => new(repository ?? new SqliteCanvasRepository(), current, gate, clock, NullLogger<CanvasService>.Instance);
    private Task<CanvasSnapshot> Layout(PageItem page) => Canvas().GetAsync(PRef(page));
    private Task<CanvasSnapshot> Edit(PageItem page, CanvasEdit edit) => Canvas().EditAsync(PRef(page), edit);
    private async Task<PageItem> CanvasPage() { var page = await Page(); await Edit(page, new LockCanvas(false)); return page; }
    private async Task<CanvasItem> Block(PageItem page, int x = 24, int y = 24, Guid? parent = null, CanvasKind kind = CanvasKind.Block)
    {
        var before = (await Layout(page)).Items.Select(i => i.Id).ToHashSet(); return (await Edit(page, new CreateCanvasItem(kind, parent, x, y))).Items.Single(i => !before.Contains(i.Id));
    }
    [Fact]
    public async Task CanvasMigrationUpgradesPhaseTwelveAndPreservesPageHierarchy()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy); await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(10)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await Sql($"INSERT INTO WorkspaceItems VALUES('{a}',5,'Training','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00',NULL,NULL);INSERT INTO WorkspaceItems VALUES('{b}',5,'Goals','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00',NULL,NULL);INSERT INTO Pages VALUES('{a}',NULL,0,NULL);INSERT INTO Pages VALUES('{b}','{a}',0,2);", legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(11L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy)); Assert.Equal(10L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy)); Assert.Equal(a.ToString(), await Sql($"SELECT ParentPageId FROM Pages WHERE ItemId='{b}';", legacy)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasItems;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync(); using var command = global.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'PageCanvas%';"; Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task CanvasOpeningEmptyPageIsReadOnlyAndSettingsAreLazy()
    {
        var page = await Page(); var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); var snapshot = await Layout(page);
        Assert.Empty(snapshot.Items); Assert.Equal(new CanvasSettings(), snapshot.Settings); Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
        await Edit(page, new LockCanvas(true)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasSettings;"));
        await Edit(page, new LockCanvas(false)); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM PageCanvasSettings;")); await Edit(page, new LockCanvas(true)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasSettings;"));
    }
    [Theory]
    [InlineData(3, 0)][InlineData(4, 8)][InlineData(12, 16)][InlineData(-4, -8)][InlineData(65535, 65536)]
    public void CanvasGridSnapsDeterministically(double value, int expected) => Assert.Equal(expected, CanvasLayout.Snap(value));
    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(1000000)]
    public void CanvasGridRejectsUnsupportedValues(double value) => Assert.Throws<CanvasValidationException>(() => CanvasLayout.Snap(value));
    [Fact]
    public async Task CanvasCreationFindsDeterministicFreeGridPositionsAndKeepsIdentity()
    {
        var page = await CanvasPage(); var a = await Block(page, 25, 25); var b = await Block(page, 25, 25); var c = await Block(page, 25, 25);
        Assert.Equal(new CanvasRect(24, 24, 192, 128), a.Rect); Assert.False(a.Rect.Overlaps(b.Rect)); Assert.False(c.Rect.Overlaps(b.Rect)); Assert.False(c.Rect.Overlaps(a.Rect)); Assert.Equal(a, (await Layout(page)).Items.Single(i => i.Id == a.Id)); Assert.Equal(3, (await Layout(page)).Items.Select(i => i.Id).Distinct().Count());
        var state = new CanvasState(await Layout(page)); Assert.Equal(CanvasLayout.FreePosition(state, null, a.Rect), CanvasLayout.FreePosition(state, null, a.Rect));
    }
    [Theory]
    [InlineData(-32, 0)][InlineData(65536, 0)][InlineData(0, -32)][InlineData(0, 65536)]
    public async Task CanvasMoveRejectsOutOfBounds(int x, int y)
    {
        var page = await CanvasPage(); var block = await Block(page); await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([block.Id], x, y))); Assert.Equal(block, Assert.Single((await Layout(page)).Items));
    }
    [Fact]
    public async Task CanvasMoveResizeAcceptsTouchingEdgesAndRejectsOverlap()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0); var b = await Block(page, 256, 0);
        await Edit(page, new MoveCanvasItems([a.Id], 64, 0)); var touching = (await Layout(page)).Items.Single(i => i.Id == a.Id); Assert.Equal(b.Rect.X, touching.Rect.Right);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([a.Id], 8, 0))); await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ResizeCanvasItem(a.Id, 200, 128)));
        var resized = (await Edit(page, new ResizeCanvasItem(a.Id, 129, 65))).Items.Single(i => i.Id == a.Id); Assert.Equal(128, resized.Rect.Width); Assert.Equal(64, resized.Rect.Height); Assert.Equal(a.CreatedAtUtc, resized.CreatedAtUtc); Assert.True(resized.UpdatedAtUtc > a.UpdatedAtUtc);
        Assert.Equal(page, (await Pages().GetAsync(profile)).Find(page.Item.Id));
    }
    [Theory]
    [InlineData(56, 128)][InlineData(192, 40)][InlineData(65536, 128)][InlineData(192, 65536)]
    public async Task CanvasResizeRejectsMinimumAndBoundaryViolations(int width, int height)
    {
        var page = await CanvasPage(); var block = await Block(page); await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ResizeCanvasItem(block.Id, width, height))); Assert.Equal(block, Assert.Single((await Layout(page)).Items));
    }
    [Fact]
    public async Task CanvasGroupMovePreservesOffsetsAndRejectsCollisionAtomically()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0); var b = await Block(page, 256, 0); await Block(page, 0, 512);
        await Edit(page, new MoveCanvasItems([a.Id, b.Id], 8, 8)); var before = await Layout(page); Assert.Equal(256, before.Items.Single(i => i.Id == b.Id).Rect.X - before.Items.Single(i => i.Id == a.Id).Rect.X);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([a.Id, b.Id], 0, 504))); Assert.Equal(before.Items, (await Layout(page)).Items);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([a.Id, b.Id], -16, 0))); Assert.Equal(before.Items, (await Layout(page)).Items);
    }
    [Fact]
    public async Task CanvasGroupStorageFailureRollsBackAllRows()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0); var b = await Block(page, 256, 0); var before = await Layout(page);
        await Sql($"CREATE TRIGGER FailCanvas BEFORE UPDATE ON PageCanvasItems WHEN OLD.Id='{b.Id}' BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<CanvasOperationException>(() => Edit(page, new MoveCanvasItems([a.Id, b.Id], 8, 8))); Assert.Equal(before.Items, (await Layout(page)).Items);
    }
    [Fact]
    public async Task CanvasContainerUsesRelativeCoordinatesAndMovesWithoutRewritingChildren()
    {
        var page = await CanvasPage(); var container = await Block(page, 512, 512, kind: CanvasKind.Container); var a = await Block(page, 8, 8, container.Id); var b = await Block(page, 208, 8, container.Id);
        await Edit(page, new MoveCanvasItems([container.Id], 80, 40)); var layout = await Layout(page); Assert.Equal(a, layout.Items.Single(i => i.Id == a.Id)); Assert.Equal(b, layout.Items.Single(i => i.Id == b.Id));
        Assert.Equal(new CanvasRect(608, 600, 192, 128), CanvasLayout.Absolute(new(layout), a));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([a.Id], 200, 0)));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([a.Id], -16, 0)));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ResizeCanvasItem(container.Id, 256, 128)));
        await Edit(page, new ResizeCanvasItem(container.Id, 600, 400));
    }
    [Fact]
    public async Task CanvasRejectsNestedContainersAndMixedCoordinateGroups()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var child = await Block(page, 8, 8, container.Id); var root = await Block(page, 600, 0);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new CreateCanvasItem(CanvasKind.Container, container.Id)));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ReparentCanvasItem(container.Id, root.Id)));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new MoveCanvasItems([child.Id, root.Id], 8, 8)));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ReparentCanvasItem(root.Id, child.Id)));
    }
    [Fact]
    public async Task CanvasSeparateContainersHaveIndependentCollisionSpaces()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0, kind: CanvasKind.Container); var b = await Block(page, 600, 0, kind: CanvasKind.Container);
        var first = await Block(page, 0, 0, a.Id); var second = await Block(page, 0, 0, b.Id); Assert.Equal(first.Rect, second.Rect);
        CanvasLayout.Validate(new(await Layout(page)));
    }
    [Fact]
    public async Task CanvasMembershipFindsFreeSpaceAndDeletingContainerPreservesAbsoluteChildren()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var block = await Block(page, 600, 0);
        var inside = (await Edit(page, new ReparentCanvasItem(block.Id, container.Id))).Items.Single(i => i.Id == block.Id); Assert.Equal(container.Id, inside.ParentContainerId); Assert.Equal(block.Id, inside.Id);
        var before = await Layout(page); var absolute = CanvasLayout.Absolute(new(before), inside);
        var after = await Edit(page, new DeleteCanvasItems([container.Id])); var survivor = Assert.Single(after.Items); Assert.Null(survivor.ParentContainerId); Assert.Equal(absolute, survivor.Rect); Assert.Equal(block.CreatedAtUtc, survivor.CreatedAtUtc);
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task CanvasRemovingChildFindsNearbyFreeRootPosition()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var block = await Block(page, 8, 8, container.Id);
        var detached = (await Edit(page, new ReparentCanvasItem(block.Id, null))).Items.Single(i => i.Id == block.Id); Assert.Null(detached.ParentContainerId); Assert.False(detached.Rect.Overlaps(container.Rect)); CanvasLayout.Validate(new(await Layout(page)));
    }
    [Fact]
    public async Task CanvasMembershipFailureAndContainerDeleteRollbackPreserveChildren()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var child = await Block(page, 8, 8, container.Id); var before = await Layout(page);
        await Sql("CREATE TRIGGER FailCanvas BEFORE UPDATE ON PageCanvasItems BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<CanvasOperationException>(() => Edit(page, new ReparentCanvasItem(child.Id, null))); await Assert.ThrowsAsync<CanvasOperationException>(() => Edit(page, new DeleteCanvasItems([container.Id]))); Assert.Equal(before.Items, (await Layout(page)).Items);
    }
    [Fact]
    public async Task CanvasContainerDeleteRejectsCollisionsInProposedDetachedState()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var child = await Block(page, 8, 8, container.Id); var sibling = await Block(page, 600, 0); var snapshot = await Layout(page);
        var malformed = snapshot with { Items = snapshot.Items.Select(i => i.Id == sibling.Id ? i with { Rect = CanvasLayout.Absolute(new(snapshot), child) } : i).ToArray() };
        Assert.Throws<CanvasValidationException>(() => CanvasLayout.Apply(malformed, new DeleteCanvasItems([container.Id]), clock.GetUtcNow()));
    }
    [Fact]
    public async Task CanvasLockGuardsAllLayoutEditsAndZoomDoesNotChangeGeometry()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); var child = await Block(page, 8, 8, container.Id); await Edit(page, new LockCanvas(true)); var before = await Layout(page);
        CanvasEdit[] edits = [new CreateCanvasItem(CanvasKind.Block), new MoveCanvasItems([child.Id], 8, 8), new ResizeCanvasItem(child.Id, 200, 128), new DeleteCanvasItems([container.Id]), new ReparentCanvasItem(child.Id, null), new RenameCanvasContainer(container.Id, "Goals")];
        foreach (var edit in edits) await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, edit));
        await Edit(page, new ZoomCanvas(25)); await Edit(page, new ZoomCanvas(200)); Assert.Equal(before.Items, (await Layout(page)).Items); Assert.Equal(200, (await Layout(page)).Settings.ZoomPercent);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ZoomCanvas(24))); await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ZoomCanvas(201)));
        await Edit(page, new LockCanvas(false)); await Edit(page, new RenameCanvasContainer(container.Id, " Goals ")); Assert.Equal("Goals", (await Layout(page)).Items.Single(i => i.Id == container.Id).Title);
    }
    [Fact]
    public async Task CanvasPageLifecyclePreservesLayoutAndDuplicateStaysEmpty()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container); await Block(page, 8, 8, container.Id); await Edit(page, new ZoomCanvas(125)); var before = await Layout(page);
        foreach (var action in new[] { PageAction.Archive, PageAction.RestoreArchive, PageAction.Trash, PageAction.RestoreTrash })
        {
            await Pages().ApplyAsync(PRef(page), action); var after = await Layout(page); Assert.Equal(before.Items, after.Items); Assert.Equal(before.Settings, after.Settings);
            if (!after.PageActive) await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new CreateCanvasItem(CanvasKind.Block)));
        }
        var copy = await Pages().DuplicateAsync(PRef(page)); Assert.Empty((await Layout(copy)).Items); Assert.Equal(new CanvasSettings(), (await Layout(copy)).Settings);
        await Pages().ApplyAsync(PRef(page), PageAction.Trash); await Pages().DeleteAsync(PRef(page)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasItems;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM PageCanvasSettings;"));
    }
    [Fact]
    public async Task CanvasPageAndProfileIsolationRejectsForeignIds()
    {
        var a = await CanvasPage(); var item = await Block(a); var b = await CanvasPage(); Assert.Empty((await Layout(b)).Items);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(b, new MoveCanvasItems([item.Id], 8, 8)));
        var other = await profiles.CreateAsync("Canvas other"); await Assert.ThrowsAsync<WorkspaceChangedException>(() => Layout(a)); await Assert.ThrowsAsync<CanvasValidationException>(() => Canvas().GetAsync(new(other.Id, a.Item.Id)));
        await profiles.SwitchAsync(profile); Assert.Equal(item, Assert.Single((await Layout(a)).Items));
    }
    [Fact]
    public async Task CanvasPresentationPreviewsDoNotReadOrWriteAndInvalidDropRestores()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0); await Block(page, 256, 0); var spy = new CountingCanvasRepository(new SqliteCanvasRepository()); var model = new CanvasWorkspaceViewModel(Canvas(spy), current, NullLogger<CanvasWorkspaceViewModel>.Instance);
        await model.OpenAsync(PRef(page)); Assert.Equal(1, spy.Reads); model.Select(a.Id); Assert.True(model.BeginGesture(a.Id));
        for (var i = 0; i < 100; i++) model.PreviewMove(0, i); Assert.Equal(0, spy.Writes); Assert.Equal(1, spy.Reads);
        model.PreviewMove(256, 0); Assert.False(model.PreviewValid); await model.EndGestureAsync(); Assert.Equal(0, spy.Writes); Assert.Equal(a.Rect, model.DisplayItems.Single(i => i.Id == a.Id).Rect);
        model.BeginGesture(a.Id); model.PreviewMove(0, 160); await model.EndGestureAsync(); Assert.Equal(1, spy.Writes); Assert.Equal(160, model.Snapshot!.Items.Single(i => i.Id == a.Id).Rect.Y);
    }
    [Fact]
    public async Task CanvasPresentationSelectionSwitchingCancelAndZoomRestoration()
    {
        var a = await CanvasPage(); var container = await Block(a, 0, 0, kind: CanvasKind.Container); var child = await Block(a, 8, 8, container.Id); var root = await Block(a, 600, 0); await Edit(a, new ZoomCanvas(150)); var b = await Page("Other");
        var model = new CanvasWorkspaceViewModel(Canvas(), current, NullLogger<CanvasWorkspaceViewModel>.Instance); await model.OpenAsync(PRef(a)); model.Select(root.Id); model.Select(child.Id, true); Assert.Equal(child.Id, Assert.Single(model.Selection));
        model.BeginGesture(child.Id); model.PreviewMove(8, 8); model.CancelGesture(); Assert.Equal(child.Rect, model.DisplayItems.Single(i => i.Id == child.Id).Rect);
        await model.OpenAsync(PRef(b)); Assert.Empty(model.Selection); Assert.Empty(model.DisplayItems); Assert.Equal(100, model.Zoom); Assert.True(model.Locked);
        await model.OpenAsync(PRef(a)); Assert.Equal(150, model.Zoom); Assert.Equal(3, model.DisplayItems.Count);
        await profiles.CreateAsync("Canvas presentation other"); Assert.Null(model.Reference); Assert.Null(model.Snapshot); Assert.Empty(model.Selection); Assert.Empty(model.DisplayItems);
    }
    [Fact]
    public async Task CanvasAlignmentSnapKeepsGridAndProvidesGuides()
    {
        var page = await CanvasPage(); var a = await Block(page, 0, 0); var b = await Block(page, 256, 256); var snap = CanvasSnapping.Move(await Layout(page), new HashSet<Guid> { a.Id }, 60, 256);
        Assert.Equal(64, snap.DeltaX); Assert.Equal(256, snap.GuideX); Assert.Equal(256, snap.GuideY); Assert.Equal(0, snap.DeltaX % 8);
    }
    [Fact]
    public async Task CanvasLateLoadsAndSavesCannotReplaceAnotherPagesLayout()
    {
        var a = await CanvasPage(); var block = await Block(a); var b = await Page("Other canvas");
        var delayed = new DeferredCanvasService(Canvas());
        var model = new CanvasWorkspaceViewModel(delayed, current, NullLogger<CanvasWorkspaceViewModel>.Instance);
        delayed.DelayRead = true;
        var oldRead = model.OpenAsync(PRef(a));
        await delayed.ReadStarted.Task;
        await model.OpenAsync(PRef(b));
        delayed.ReadRelease.SetResult(); await oldRead;
        Assert.Equal(PRef(b), model.Reference); Assert.Empty(model.DisplayItems);
        await model.OpenAsync(PRef(a)); model.Select(block.Id);
        delayed.DelayEdit = true;
        var oldSave = model.EditAsync(new MoveCanvasItems([block.Id], 80, 0));
        await delayed.EditStarted.Task;
        await model.OpenAsync(PRef(b));
        delayed.EditRelease.SetResult(); await oldSave;
        Assert.Equal(PRef(b), model.Reference); Assert.Empty(model.DisplayItems); Assert.Empty(model.Selection); Assert.False(model.IsBusy);
        Assert.Equal(block.Rect.X + 80, Assert.Single((await Layout(a)).Items).Rect.X);
    }
    [Fact]
    public async Task CanvasLateProfileReadCannotRepopulateClearedPresentation()
    {
        var page = await CanvasPage(); await Block(page);
        var delayed = new DeferredCanvasService(Canvas()) { DelayRead = true };
        var model = new CanvasWorkspaceViewModel(delayed, current, NullLogger<CanvasWorkspaceViewModel>.Instance);
        var pending = model.OpenAsync(PRef(page)); await delayed.ReadStarted.Task;
        await profiles.CreateAsync("Canvas late response");
        delayed.ReadRelease.SetResult(); await pending;
        Assert.Null(model.Reference); Assert.Null(model.Snapshot); Assert.Empty(model.DisplayItems); Assert.False(model.IsBusy);
    }
    [Fact]
    public async Task CanvasFullContainerRejectsCreationAndMembershipWithoutLosingItems()
    {
        var page = await CanvasPage(); var container = await Block(page, 0, 0, kind: CanvasKind.Container);
        await Edit(page, new ResizeCanvasItem(container.Id, 208, 176));
        await Block(page, 0, 0, container.Id); var outside = await Block(page, 600, 0); var before = await Layout(page);
        await Assert.ThrowsAsync<CanvasValidationException>(() => Block(page, 0, 0, container.Id));
        await Assert.ThrowsAsync<CanvasValidationException>(() => Edit(page, new ReparentCanvasItem(outside.Id, container.Id)));
        Assert.Equal(before.Items, (await Layout(page)).Items);
    }
    [Fact]
    public void CanvasHundredsOfItemsUseTheSameInMemoryValidationAndPlacement()
    {
        var page = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, 500).Select(i => new CanvasItem(Guid.NewGuid(), page, null, CanvasKind.Block,
            new((i % 25) * 256, (i / 25) * 256, 192, 128), "", now, now)).ToArray();
        var snapshot = new CanvasSnapshot(page, true, new(false), items);
        CanvasLayout.Validate(new(snapshot));
        for (var i = 0; i < 100; i++)
        {
            var snap = CanvasSnapping.Move(snapshot, new HashSet<Guid> { items[0].Id }, 0, i);
            var preview = CanvasLayout.Apply(snapshot, new MoveCanvasItems([items[0].Id], snap.DeltaX, snap.DeltaY), now);
            Assert.Equal(500, preview.Items.Count);
        }
        var created = CanvasLayout.Apply(snapshot, new CreateCanvasItem(CanvasKind.Block, X: 0, Y: 0), now);
        Assert.Equal(501, created.Items.Count); CanvasLayout.Validate(new(created));
    }
    [Fact]
    public async Task CanvasStationaryClickDoesNotAlignmentSnapOrSave()
    {
        var page = await CanvasPage(); var a = await Block(page, 24, 384); await Edit(page, new ResizeCanvasItem(a.Id, 304, 192));
        var b = await Block(page, 224, 24);
        Assert.Equal(new CanvasSnap(0, 0), CanvasSnapping.Move(await Layout(page), new HashSet<Guid> { b.Id }, 0, 0));
        var spy = new CountingCanvasRepository(new SqliteCanvasRepository());
        var model = new CanvasWorkspaceViewModel(Canvas(spy), current, NullLogger<CanvasWorkspaceViewModel>.Instance);
        await model.OpenAsync(PRef(page)); model.Select(b.Id); model.BeginGesture(b.Id); model.PreviewMove(0, 0); await model.EndGestureAsync();
        Assert.Equal(0, spy.Writes); Assert.Equal(1, spy.Reads); Assert.Equal(b.Rect, model.DisplayItems.Single(i => i.Id == b.Id).Rect);
    }
    private sealed class DeferredCanvasService(ICanvasService inner) : ICanvasService
    {
        public bool DelayRead, DelayEdit;
        public TaskCompletionSource ReadStarted { get; } = new();
        public TaskCompletionSource ReadRelease { get; } = new();
        public TaskCompletionSource EditStarted { get; } = new();
        public TaskCompletionSource EditRelease { get; } = new();
        public async Task<CanvasSnapshot> GetAsync(WorkspaceItemReference page, CancellationToken token = default)
        {
            var result = await inner.GetAsync(page, token);
            if (DelayRead) { DelayRead = false; ReadStarted.SetResult(); await ReadRelease.Task; }
            return result;
        }
        public async Task<CanvasSnapshot> EditAsync(WorkspaceItemReference page, CanvasEdit edit, CancellationToken token = default)
        {
            var result = await inner.EditAsync(page, edit, token);
            if (DelayEdit) { DelayEdit = false; EditStarted.SetResult(); await EditRelease.Task; }
            return result;
        }
    }
    private sealed class CountingCanvasRepository(ICanvasRepository inner) : ICanvasRepository
    {
        public int Reads { get; private set; } public int Writes { get; private set; }
        public Task<CanvasSnapshot> GetAsync(WorkspaceContext workspace, Guid pageId, CancellationToken token) { Reads++; return inner.GetAsync(workspace, pageId, token); }
        public Task<CanvasSnapshot> EditAsync(WorkspaceContext workspace, Guid pageId, Func<CanvasSnapshot, CanvasSnapshot> edit, CancellationToken token) { Writes++; return inner.EditAsync(workspace, pageId, edit, token); }
    }
}
