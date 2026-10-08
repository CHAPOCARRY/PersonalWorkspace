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
    private PageService Pages(IPageRepository? repository = null) => new(repository ?? new SqlitePageRepository(), current, gate, clock, NullLogger<PageService>.Instance);
    private WorkspaceItemReference PRef(PageItem page) => new(profile, page.Item.Id);
    private Task<PageItem> Page(string title = "Training", PageItem? parent = null) => Pages().CreateAsync(profile, title, parent?.Item.Id);
    private PageWorkspaceViewModel PageModel(NavigationService? navigation = null)
    {
        navigation ??= new(); navigation.Navigate(new("Pages")); return new(Pages(), organization, current, navigation, NullLogger<PageWorkspaceViewModel>.Instance);
    }
    [Fact]
    public async Task PagesMigrationUpgradesPhaseElevenWithoutChangingJournalData()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(9)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var journal = Guid.NewGuid(); var entry = Guid.NewGuid();
        await Sql($"INSERT INTO WorkspaceItems VALUES('{journal}',4,'Daily','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00',NULL,NULL); INSERT INTO Journals VALUES('{journal}','Reflection'); INSERT INTO JournalEntries VALUES('{entry}','{journal}','2026-10-01','2026-10-01T00:00:00+00:00','2026-10-01T00:00:00+00:00');", legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(13L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy)); Assert.Equal(9L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(entry.ToString(), await Sql("SELECT Id FROM JournalEntries;", legacy)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Pages;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync(); using var command = global.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Pages';"; Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task PagesCreateRootChildAndDuplicateTitlesWithWorkspaceIdentity()
    {
        var root = await Page("  Training  "); var child = await Page("Training", root);
        Assert.Equal("Training", root.Item.Title); Assert.Null(root.ParentPageId); Assert.Equal(root.Item.Id, child.ParentPageId); Assert.NotEqual(root.Item.Id, child.Item.Id); Assert.Equal(WorkspaceItemType.Page, root.Item.ItemType);
        Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM WorkspaceItems WHERE ItemType=5;")); Assert.Equal(PageIcon.None, root.Icon);
    }
    [Theory]
    [InlineData("")][InlineData("   ")][InlineData("bad\ntitle")]
    public async Task PagesRejectInvalidTitles(string title) => await Assert.ThrowsAsync<PageValidationException>(() => Page(title));
    [Fact]
    public async Task PagesRejectInvalidIconAndUnknownParent()
    {
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().CreateAsync(profile, "Page", icon: (PageIcon)99));
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().CreateAsync(profile, "Page", Guid.NewGuid())); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Pages;"));
    }
    [Fact]
    public async Task PagesAncestorsChildrenAndStableIdentityAfterRenameAndMove()
    {
        var a = await Page(); var b = await Page("Goals", a); var c = await Page("Strength", b); var d = await Page("Muscle-up", c);
        var graph = await Pages().GetAsync(profile); Assert.Equal(new[] { a.Item.Id, b.Item.Id, c.Item.Id }, graph.Ancestors(d.Item.Id).Select(p => p.Item.Id)); Assert.Equal(d, Assert.Single(graph.Children(c.Item.Id)));
        var renamed = await Pages().UpdateAsync(PRef(d), "Muscle-up Goal", PageIcon.Star); Assert.Equal(d.Item.Id, renamed.Item.Id); Assert.Equal(d.Item.CreatedAtUtc, renamed.Item.CreatedAtUtc); Assert.True(renamed.Item.UpdatedAtUtc > d.Item.UpdatedAtUtc);
        var moved = await Pages().MoveAsync(PRef(d), a.Item.Id); Assert.Equal(d.Item.Id, moved.Item.Id); Assert.Equal(a.Item.Id, moved.ParentPageId);
        Assert.Null((await Pages().MoveAsync(PRef(d), null)).ParentPageId);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(4)]
    public async Task PagesRejectSelfDirectAndDeepCycles(int depth)
    {
        var root = await Page(); var leaf = root; for (var i = 0; i < depth; i++) leaf = await Page("Child", leaf);
        var exception = await Assert.ThrowsAsync<PageValidationException>(() => Pages().MoveAsync(PRef(root), leaf.Item.Id)); Assert.Contains("cycle", exception.Message);
        Assert.Null((await Pages().GetAsync(profile)).Find(root.Item.Id).ParentPageId);
    }
    [Fact]
    public async Task PagesDeepGraphUsesIterativeTraversal()
    {
        var pages = new List<PageItem>(); Guid? parent = null;
        for (var i = 0; i < 5000; i++) { var id = Guid.NewGuid(); pages.Add(new(new(id, WorkspaceItemType.Page, "Page", clock.GetUtcNow(), clock.GetUtcNow(), null, null), parent, 0)); parent = id; }
        var graph = new PageGraph(pages); Assert.Equal(4999, graph.Ancestors(parent!.Value).Count); Assert.Equal(4999, graph.Descendants(pages[0].Item.Id).Count);
    }
    [Fact]
    public async Task PagesMoveAndReorderNormalizeOnlyAffectedSiblingGroups()
    {
        var a = await Page("A"); var b = await Page("B"); var c = await Page("C"); var child = await Page("Child", a); var unrelated = await Page("Other", b);
        await Pages().MoveAsync(PRef(c), a.Item.Id); var graph = await Pages().GetAsync(profile);
        Assert.Equal(new[] { child.Item.Id, c.Item.Id }, graph.Children(a.Item.Id).Select(p => p.Item.Id));
        await Pages().ReorderAsync(PRef(c), -1); graph = await Pages().GetAsync(profile); Assert.Equal(new[] { c.Item.Id, child.Item.Id }, graph.Children(a.Item.Id).Select(p => p.Item.Id)); Assert.Equal(unrelated, graph.Find(unrelated.Item.Id));
        await Pages().MoveAsync(PRef(child), b.Item.Id); await Pages().MoveAsync(PRef(c), null); graph = await Pages().GetAsync(profile);
        foreach (var group in graph.Pages.Values.GroupBy(p => p.ParentPageId)) Assert.Equal(Enumerable.Range(0, group.Count()), group.OrderBy(p => p.SortOrder).Select(p => p.SortOrder));
    }
    [Theory]
    [InlineData(PageAction.Archive, PageAction.RestoreArchive)][InlineData(PageAction.Trash, PageAction.RestoreTrash)]
    public async Task PagesHiddenParentPreservesDiscoverableChildrenAndRestoresHierarchy(PageAction hide, PageAction restore)
    {
        var parent = await Page(); var child = await Page("Goals", parent); var model = PageModel(); await model.ReloadAsync();
        await Pages().ApplyAsync(PRef(parent), hide); await model.ReloadAsync(); var row = Assert.Single(model.Rows); Assert.Equal(child.Item.Id, row.Page.Item.Id); Assert.Equal(0, row.Depth); Assert.Contains("Parent: Training", row.Context);
        Assert.Equal(child, (await Pages().GetAsync(profile)).Find(child.Item.Id));
        await Pages().ApplyAsync(PRef(parent), restore); await model.ReloadAsync(); Assert.Equal(1, model.Rows.Single(r => r.Page.Item.Id == child.Item.Id).Depth);
    }
    [Fact]
    public async Task PagesPermanentDeleteDetachesOnlyDirectChildrenAndNormalizesRoots()
    {
        var parent = await Page(); var child = await Page("Goals", parent); var grandchild = await Page("Muscle-up", child); var other = await Page("Other");
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().DeleteAsync(PRef(parent)));
        await Pages().ApplyAsync(PRef(parent), PageAction.Trash); await Pages().DeleteAsync(PRef(parent)); var graph = await Pages().GetAsync(profile);
        Assert.Equal(3, graph.Pages.Count); Assert.Null(graph.Find(child.Item.Id).ParentPageId); Assert.Equal(child.Item.Id, graph.Find(grandchild.Item.Id).ParentPageId); Assert.Equal(new[] { 0, 1 }, graph.Children(null).Select(p => p.SortOrder)); Assert.True(graph.Find(child.Item.Id).Item.UpdatedAtUtc > child.Item.UpdatedAtUtc);
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task PagesDatabaseForeignKeyAlsoPreservesChildren()
    {
        var parent = await Page(); var child = await Page("Child", parent); await Sql($"DELETE FROM WorkspaceItems WHERE Id='{parent.Item.Id}';");
        Assert.Null((await Pages().GetAsync(profile)).Find(child.Item.Id).ParentPageId);
    }
    [Fact]
    public async Task PagesDuplicateCopiesOnlyOneDefinitionAndNoAssignments()
    {
        var parent = await Page(); var source = await Page("Goals", parent); await Page("Child", source); source = await Pages().UpdateAsync(PRef(source), "Goals", PageIcon.Flag);
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("planning")); await organization.AssignAsync(PRef(source), OrganizationKind.Tag, tag, true);
        await Pages().ApplyAsync(PRef(source), PageAction.Archive); var copy = await Pages().DuplicateAsync(PRef(source)); var graph = await Pages().GetAsync(profile);
        Assert.NotEqual(source.Item.Id, copy.Item.Id); Assert.Equal(source.Item.Title, copy.Item.Title); Assert.Equal(PageIcon.Flag, copy.Icon); Assert.Equal(parent.Item.Id, copy.ParentPageId); Assert.True(copy.IsActive); Assert.Empty(graph.Children(copy.Item.Id)); Assert.Equal(1, copy.SortOrder);
        Assert.DoesNotContain((await organization.GetAsync(profile)).ItemTags, t => t.ItemId == copy.Item.Id);
    }
    [Fact]
    public async Task PagesTagsAndSpacesDoNotChangeHierarchyOrContentTimestamp()
    {
        var root = await Page(); var child = await Page("Child", root);
        foreach (var kind in new[] { OrganizationKind.Tag, OrganizationKind.Space }) foreach (var name in new[] { "Personal", "Training" }) { var id = await organization.SaveAsync(profile, kind, null, new(name)); await organization.AssignAsync(PRef(child), kind, id, true); }
        var catalog = await organization.GetAsync(profile); Assert.Equal(2, catalog.ItemTags.Count); Assert.Equal(2, catalog.ItemSpaces.Count); Assert.Equal(child, (await Pages().GetAsync(profile)).Find(child.Item.Id));
        await Pages().ApplyAsync(PRef(child), PageAction.Trash); await Pages().DeleteAsync(PRef(child)); catalog = await organization.GetAsync(profile); Assert.Empty(catalog.ItemTags); Assert.Empty(catalog.ItemSpaces); Assert.Equal(2, catalog.Tags.Count);
    }
    [Fact]
    public async Task PagesProfileIsolationRejectsStaleActionsAndForeignParent()
    {
        var original = profile; var a = await Page(); var other = await profiles.CreateAsync("Pages other");
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Pages().UpdateAsync(PRef(a), "Bad", PageIcon.None));
        Assert.Empty((await Pages().GetAsync(other.Id)).Pages);
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().CreateAsync(other.Id, "Bad", a.Item.Id));
        var b = await Pages().CreateAsync(other.Id, "B"); await profiles.SwitchAsync(original);
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().MoveAsync(PRef(a), b.Item.Id)); Assert.Single((await Pages().GetAsync(original)).Pages);
    }
    [Fact]
    public async Task PagesReadsAndNoOpChangesPreserveDatabaseAndTimestamps()
    {
        var page = await Page(); var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); await Pages().GetAsync(profile);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
        Assert.Equal(page, await Pages().UpdateAsync(PRef(page), page.Item.Title, PageIcon.None)); Assert.Equal(page, await Pages().MoveAsync(PRef(page), null)); await Pages().ReorderAsync(PRef(page), -1); Assert.Equal(page, (await Pages().GetAsync(profile)).Find(page.Item.Id));
    }
    [Theory]
    [InlineData(PageAction.Archive)][InlineData(PageAction.Trash)]
    public async Task PagesRejectInactiveMoveAndCreateTargets(PageAction action)
    {
        var parent = await Page(); var child = await Page("Other"); await Pages().ApplyAsync(PRef(parent), action);
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().MoveAsync(PRef(child), parent.Item.Id)); await Assert.ThrowsAsync<PageValidationException>(() => Page("New", parent));
    }
    [Fact]
    public async Task PagesCreateAndDuplicateFailureRollBackWorkspaceIdentity()
    {
        var page = await Page(); await Sql("CREATE TRIGGER FailPage BEFORE INSERT ON Pages BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<PageOperationException>(() => Page("Broken")); await Assert.ThrowsAsync<PageOperationException>(() => Pages().DuplicateAsync(PRef(page)));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
    }
    [Fact]
    public async Task PagesMoveReorderAndDeleteFailuresRollBackAllChanges()
    {
        var a = await Page(); var b = await Page("B"); var child = await Page("Child", a); await Pages().ApplyAsync(PRef(a), PageAction.Trash); var before = await Pages().GetAsync(profile);
        await Sql("CREATE TRIGGER FailPageUpdate BEFORE UPDATE ON Pages BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<PageOperationException>(() => Pages().MoveAsync(PRef(child), b.Item.Id)); await Assert.ThrowsAsync<PageOperationException>(() => Pages().DeleteAsync(PRef(a)));
        Assert.Equal(before.Pages.OrderBy(p => p.Key), (await Pages().GetAsync(profile)).Pages.OrderBy(p => p.Key));
    }
    [Fact]
    public async Task PagesPresentationBreadcrumbsCollapseAndLifecycleFallback()
    {
        var a = await Page(); var b = await Page("Goals", a); var c = await Page("Muscle-up", b); var model = PageModel(); await model.ReloadAsync();
        model.Toggle(model.Rows[0]); Assert.Single(model.Rows); model.Toggle(model.Rows[0]); Assert.Equal(3, model.Rows.Count);
        model.OpenCommand.Execute(model.Rows.Single(r => r.Page.Item.Id == c.Item.Id)); await model.ReloadAsync(); Assert.Equal(new[] { "Training", "Goals", "Muscle-up" }, model.Breadcrumbs.Select(r => r.Title));
        Assert.DoesNotContain(model.ParentChoices, p => p.Id == c.Item.Id);
        await model.ApplyAsync(model.DetailRow!, PageAction.Archive); Assert.Equal(b.Item.Id, model.Detail!.Item.Id);
        await model.ApplyAsync(model.DetailRow!, PageAction.Trash); Assert.Equal(a.Item.Id, model.Detail!.Item.Id);
        model.Collection = PageCollection.Trash; await model.ReloadAsync(); await model.DeleteAsync(model.DetailRow!); Assert.Null(model.Detail); Assert.Empty(model.Rows); Assert.Null(model.Error);
    }
    [Fact]
    public async Task PagesProfileEventClearsSelectionAndRestoresTree()
    {
        await Page(); var model = PageModel(); await model.ReloadAsync(); Assert.NotNull(model.Detail);
        await profiles.CreateAsync("Page presentation other"); for (var i = 0; i < 200 && model.IsBusy; i++) await Task.Delay(10);
        Assert.Empty(model.Rows); Assert.Null(model.Detail); Assert.Empty(model.Breadcrumbs); Assert.Empty(model.Assignments);
        await profiles.SwitchAsync(profile); for (var i = 0; i < 200 && (model.IsBusy || model.Rows.Count == 0); i++) await Task.Delay(10); Assert.Single(model.Rows);
    }
    [Fact]
    public async Task PagesCancelledMutationWritesNothing()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pages().CreateAsync(profile, "Cancelled", token: cancellation.Token)); Assert.Empty((await Pages().GetAsync(profile)).Pages);
    }
    [Fact]
    public async Task PagesMetadataBatchSupportsThousandsOfNodesWithoutPerNodeReads()
    {
        var repository = new SqlitePageRepository(); var context = new WorkspaceContext(profile, paths.WorkspaceDatabase(profile));
        await repository.TransactAsync(context, graph =>
        {
            Guid? parent = null;
            for (var i = 0; i < 2000; i++) { var id = Guid.NewGuid(); graph.Pages.Add(id, new(new(id, WorkspaceItemType.Page, "Page " + i, clock.GetUtcNow(), clock.GetUtcNow(), null, null), parent, 0)); parent = id; }
            return true;
        }, default);
        var counting = new CountingPageRepository(repository); var navigation = new NavigationService(); navigation.Navigate(new("Pages"));
        var model = new PageWorkspaceViewModel(Pages(counting), organization, current, navigation, NullLogger<PageWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); Assert.Equal(2000, model.Rows.Count); Assert.Equal(1, counting.Reads);
        var bytes = await File.ReadAllBytesAsync(context.DatabasePath); model.Toggle(model.Rows[0]); Assert.Single(model.Rows); model.Toggle(model.Rows[0]); Assert.Equal(2000, model.Rows.Count);
        Assert.Equal(1, counting.Reads); Assert.Equal(bytes, await File.ReadAllBytesAsync(context.DatabasePath));
    }
    [Fact]
    public async Task PagesReorderRootPersistsAndFailureRollsBackBothSiblings()
    {
        var a = await Page("A"); var b = await Page("B"); await Pages().ReorderAsync(PRef(b), -1);
        Assert.Equal(new[] { b.Item.Id, a.Item.Id }, (await Pages().GetAsync(profile)).Children(null).Select(p => p.Item.Id));
        await Sql($"CREATE TRIGGER FailSecondPage BEFORE UPDATE ON Pages WHEN OLD.ItemId='{a.Item.Id}' BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<PageOperationException>(() => Pages().ReorderAsync(PRef(b), 1)); Assert.Equal(new[] { b.Item.Id, a.Item.Id }, (await Pages().GetAsync(profile)).Children(null).Select(p => p.Item.Id));
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().ReorderAsync(PRef(b), 2));
    }
    [Fact]
    public async Task PagesTrashBlocksEditsAndDuplicateButRestoreClearsLifecycle()
    {
        var page = await Page(); await Pages().ApplyAsync(PRef(page), PageAction.Archive); await Pages().ApplyAsync(PRef(page), PageAction.Trash);
        await Assert.ThrowsAsync<PageValidationException>(() => Pages().UpdateAsync(PRef(page), "Bad", PageIcon.None)); await Assert.ThrowsAsync<PageValidationException>(() => Pages().DuplicateAsync(PRef(page)));
        Assert.True((await Pages().ApplyAsync(PRef(page), PageAction.RestoreTrash)).IsActive);
    }
    [Fact]
    public async Task PagesRejectOtherWorkspaceItemTypeAsParent()
    {
        var tracker = await Create(); await Assert.ThrowsAsync<PageValidationException>(() => Pages().CreateAsync(profile, "Page", tracker.Item.Id));
    }
    [Fact]
    public async Task PagesSelectedOnlyPageDeletionClearsDetailAndDraft()
    {
        var page = await Page(); var model = PageModel(); await model.ReloadAsync();
        await model.ApplyAsync(model.DetailRow!, PageAction.Trash); Assert.Null(model.Detail);
        model.Collection = PageCollection.Trash; await model.ReloadAsync(); Assert.NotNull(model.Detail);
        await model.DeleteAsync(model.DetailRow!); Assert.Null(model.Detail); Assert.False(model.ShowEditor); Assert.Empty(model.Breadcrumbs); Assert.Empty(model.Assignments);
    }
    private sealed class CountingPageRepository(IPageRepository inner) : IPageRepository
    {
        public int Reads { get; private set; }
        public Task<PageGraph> GetAsync(WorkspaceContext workspace, CancellationToken token) { Reads++; return inner.GetAsync(workspace, token); }
        public Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<PageGraph, T> change, CancellationToken token) => inner.TransactAsync(workspace, change, token);
    }
}
