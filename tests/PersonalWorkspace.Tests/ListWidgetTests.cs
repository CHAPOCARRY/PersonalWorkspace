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
    private async Task<WidgetInstance> ListWidget(PageItem page, ListDefinition list, WidgetMode mode = WidgetMode.Checklist, ListFilter filter = ListFilter.All, Guid? host = null, Guid? parent = null)
    {
        var before = (await Widgets().GetAsync(PRef(page))).Widgets.Select(w => w.Id).ToHashSet();
        return (await WEdit(page, new AddWidget(WidgetType.List, list.Item.Id, mode, host, parent, List: new(filter)))).Widgets.Single(w => !before.Contains(w.Id));
    }
    [Fact]
    public async Task ListWidgetMigrationPreservesPopulatedPhaseFifteenExactly()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(13)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var page = Guid.NewGuid(); var container = Guid.NewGuid(); var stamp = "2026-10-08T00:00:00+00:00";
        await Sql($"INSERT INTO WorkspaceItems VALUES('{page}',5,'Existing','{stamp}','{stamp}',NULL,NULL); INSERT INTO Pages VALUES('{page}',NULL,0,NULL); INSERT INTO PageCanvasSettings VALUES('{page}',0,125); INSERT INTO PageCanvasItems VALUES('{container}','{page}',NULL,1,24,24,1200,1200,'Group','{stamp}','{stamp}');", legacy);
        int[] types = [1,3,3,2,4,5]; int[] modes = [1,3,5,0,6,0];
        for (var i = 0; i < types.Length; i++)
        {
            var source = Guid.NewGuid(); var host = Guid.NewGuid(); var widget = Guid.NewGuid();
            await Sql($"INSERT INTO WorkspaceItems VALUES('{source}',{types[i]},'Source {i}','{stamp}','{stamp}',NULL,NULL); INSERT INTO PageCanvasItems VALUES('{host}','{page}','{container}',0,24,{24+i*160},320,128,'','{stamp}','{stamp}');", legacy);
            if (i == 2) await Sql($"INSERT INTO Trackers(ItemId,ValueType,Frequency,StartDate,Interval,Weekdays,EntryMode,Aggregation) VALUES('{source}',0,0,'2026-10-08',1,0,0,0);", legacy);
            await Sql($"INSERT INTO WidgetInstances VALUES('{widget}','{host}',{i},'{source}',{modes[i]},'{stamp}','{stamp}');", legacy);
            if (i == 2) await Sql($"INSERT INTO WidgetChartSettings VALUES('{widget}',2,1);", legacy);
        }
        // Include a historical missing reference, with its original identity and geometry.
        await Sql("UPDATE WidgetInstances SET SourceItemId=NULL WHERE WidgetType=0;", legacy);
        string[] tables = ["WorkspaceItems", "PageCanvasItems", "PageCanvasSettings", "WidgetInstances", "WidgetChartSettings", "Lists", "ListItems"];
        var before = new Dictionary<string, string>();
        foreach (var table in tables) before[table] = await TableRows(legacy, table);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        foreach (var table in tables) Assert.Equal(before[table], await TableRows(legacy, table));
        Assert.Equal(21L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(13L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Null(await Sql("PRAGMA foreign_key_check;", legacy));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WidgetListSettings;", legacy));
    }
    private async Task<string> TableRows(Guid id, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(id)};Pooling=False"); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
        using var reader = await command.ExecuteReaderAsync(); var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
        return string.Join("\n", rows);
    }
    [Theory]
    [InlineData(WidgetMode.Checklist)][InlineData(WidgetMode.Shopping)][InlineData(WidgetMode.Compact)]
    public async Task ListWidgetCreationKeepsGenericStableIdentityAndDefaults(WidgetMode mode)
    {
        var page = await CanvasPage(); var list = await List(); var widget = await WAdd(page, list.Item, WidgetType.List, mode);
        Assert.Equal(new WidgetListSettings(ListFilter.All), widget.List); Assert.Equal(list.Item.Id, widget.SourceItemId);
        Assert.NotEqual(widget.Id, widget.CanvasItemId); Assert.NotEqual(widget.Id, widget.SourceItemId);
        Assert.Equal(widget, Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets));
        var host = Assert.Single((await Layout(page)).Items); Assert.Equal(400, host.Rect.Width); Assert.Equal(320, host.Rect.Height);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Lists;")); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM WidgetListSettings;"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM pragma_table_info('WidgetInstances') WHERE name='ListId';"));
    }
    [Fact]
    public async Task ListWidgetDatabaseRequiresTypedOneToOneSettingsAndListSources()
    {
        var page = await CanvasPage(); var list = await List(); var widget = await ListWidget(page, list); var task = await JournalTasks().CreateAsync(profile, new("Task")); var other = await WAdd(page, task.Item);
        foreach (var sql in new[] {
            $"UPDATE WidgetInstances SET SourceItemId='{task.Item.Id}' WHERE Id='{widget.Id}';",
            $"UPDATE WidgetInstances SET PresentationMode=0 WHERE Id='{widget.Id}';",
            $"UPDATE WidgetListSettings SET ItemFilter=3 WHERE WidgetId='{widget.Id}';",
            $"DELETE FROM WidgetListSettings WHERE WidgetId='{widget.Id}';",
            $"INSERT INTO WidgetListSettings VALUES('{widget.Id}',0);",
            $"INSERT INTO WidgetListSettings VALUES('{other.Id}',0);",
            $"UPDATE WidgetListSettings SET WidgetId='{other.Id}' WHERE WidgetId='{widget.Id}';" })
            await Assert.ThrowsAsync<SqliteException>(() => Sql(sql));
        await Assert.ThrowsAsync<WidgetValidationException>(() => WAdd(page, task.Item, WidgetType.List, WidgetMode.Checklist));
        await WEdit(page, new RemoveWidget(widget.Id)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WidgetListSettings;")); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Theory]
    [InlineData(ListFilter.All,3)][InlineData(ListFilter.Unchecked,2)][InlineData(ListFilter.Checked,1)]
    public async Task ListWidgetFiltersKeepCanonicalOrderAndWholeListExactTotals(ListFilter filter, int count)
    {
        var page = await CanvasPage(); var list = await List(); var milk = await ListAdd(list, "Milk", null, 0.1m); await ListAdd(list, "Bread", 3, 0.2m); await ListAdd(list, "Coffee"); await Lists().SetCheckedAsync(LIRef(milk), true);
        await ListWidget(page, list, WidgetMode.Shopping, filter); var vm = WidgetModel(); await vm.OpenAsync(PRef(page)); var row = Assert.Single(vm.Rows);
        Assert.Equal(count, row.ListItems.Count); Assert.Equal(new ListTotals(0.7m,0.6m), row.ListData!.Totals); Assert.Equal((await ListRead(list)).Totals, row.ListData.Totals);
        Assert.Equal((await ListRead(list)).Items.Where(i => ListRules.Matches(i,filter, "")), row.ListData.Items);
        Assert.Equal(3, row.ListData.Count); Assert.Equal(1,row.ListData.CheckedCount); Assert.Equal(filter,row.Widget.List!.Filter);
        Assert.Equal(3, (await ListRead(list)).Items.Count);
    }
    [Fact]
    public async Task ListWidgetLockedActionsRefreshRepeatedSourcesAndRejectLayoutChanges()
    {
        var page = await CanvasPage(); var list = await List(); await ListAdd(list); var widget = await ListWidget(page,list,WidgetMode.Shopping); await ListWidget(page,list,WidgetMode.Compact);
        await Edit(page,new LockCanvas(true)); var vm=WidgetModel(); await vm.OpenAsync(PRef(page)); var row=vm.Rows.Single(r=>r.Widget.Id==widget.Id);
        Assert.True(vm.CanInteract); Assert.False(vm.CanConfigure); await vm.CheckListItemAsync(row,Assert.Single(row.ListItems));
        Assert.True(Assert.Single((await ListRead(list)).Items).IsChecked); Assert.All(vm.Rows,r=>Assert.Equal(1,r.ListData!.CheckedCount));
        row=vm.Rows.Single(r=>r.Widget.Id==widget.Id); row.Input="Eggs"; await vm.QuickAddAsync(row);
        Assert.Equal(new[]{"Milk","Eggs"},(await ListRead(list)).Items.Select(i=>i.Title)); Assert.All(vm.Rows,r=>Assert.Equal(2,r.ListData!.Count));
        foreach (var edit in new WidgetEdit[]{new ConfigureWidget(widget.Id,WidgetMode.Compact),new RemoveWidget(widget.Id),new ReplaceWidgetSource(widget.Id,list.Item.Id),new DuplicateWidget(widget.Id)}) await Assert.ThrowsAsync<WidgetValidationException>(()=>WEdit(page,edit));
        await Assert.ThrowsAsync<CanvasValidationException>(()=>Edit(page,new MoveCanvasItems([widget.CanvasItemId],8,8)));
        Assert.Empty(vm.Rows.Single(r=>r.Widget.Mode==WidgetMode.Compact).ListItems);
    }
    [Fact]
    public async Task ListWidgetReorderRefreshAndOtherPageUseCanonicalState()
    {
        var a=await CanvasPage(); var b=await CanvasPage(); var list=await List(); await ListAdd(list,"First"); var last=await ListAdd(list,"Last"); await ListWidget(a,list); await ListWidget(b,list,WidgetMode.Compact);
        var vm=WidgetModel(); await vm.OpenAsync(PRef(a)); var stale=Assert.Single(vm.Rows);
        await Lists().ReorderAsync(LIRef(last),-1); await vm.RefreshAsync(); Assert.Equal("Last",vm.Rows[0].ListItems[0].Title);
        await vm.OpenAsync(PRef(b)); stale.Input="Stale"; await vm.QuickAddAsync(stale); Assert.Equal(2,(await ListRead(list)).Items.Count);
        await Lists().SetCheckedAsync(LIRef(last),true); await vm.RefreshAsync(); Assert.Equal(1,vm.Rows[0].ListData!.CheckedCount);
        await vm.OpenAsync(PRef(a)); Assert.True(vm.Rows[0].ListItems[0].IsChecked);
    }
    [Fact]
    public async Task ListWidgetShoppingCompatibilityAndReplacementFallbackPreserveGeometry()
    {
        var page=await CanvasPage(); var priced=await List(); var plain=await List("Movies",false); var widget=await ListWidget(page,priced,WidgetMode.Shopping,ListFilter.Unchecked); var host=Assert.Single((await Layout(page)).Items);
        await Assert.ThrowsAsync<WidgetValidationException>(()=>ListWidget(page,plain,WidgetMode.Shopping));
        Assert.DoesNotContain(WidgetMode.Shopping,WidgetRules.ListModes(false));
        await Lists().UpdateAsync(LRef(priced),new("Shopping",ShowPrices:false)); var vm=WidgetModel(); await vm.OpenAsync(PRef(page));
        Assert.Equal(WidgetMode.Checklist,Assert.Single(vm.Rows).ListMode); Assert.Equal(widget,Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets));
        await Assert.ThrowsAsync<WidgetValidationException>(()=>WEdit(page,new ConfigureWidget(widget.Id,WidgetMode.Shopping)));
        var replaced=Assert.Single((await WEdit(page,new ReplaceWidgetSource(widget.Id,plain.Item.Id))).Widgets);
        Assert.Equal(widget.Id,replaced.Id); Assert.Equal(widget.CanvasItemId,replaced.CanvasItemId); Assert.Equal(widget.List,replaced.List); Assert.Equal(WidgetMode.Checklist,replaced.Mode); Assert.Equal(host,Assert.Single((await Layout(page)).Items));
    }
    [Fact]
    public async Task ListWidgetLifecycleMissingAndReplacementKeepHistoryAndGeometry()
    {
        var page=await CanvasPage(); var list=await List(); await ListAdd(list); var widget=await ListWidget(page,list); var host=Assert.Single((await Layout(page)).Items); var vm=WidgetModel();
        foreach(var action in new[]{ListAction.Archive,ListAction.RestoreArchive,ListAction.Trash,ListAction.RestoreTrash})
        {
            await Lists().ApplyAsync(LRef(list),action); await vm.OpenAsync(PRef(page)); var row=Assert.Single(vm.Rows);
            Assert.Equal(action is ListAction.RestoreArchive or ListAction.RestoreTrash,row.QuickAdd); Assert.Equal(widget,row.Widget);
        }
        await Lists().ApplyAsync(LRef(list),ListAction.Trash); await Lists().DeleteAsync(LRef(list)); await vm.RefreshAsync();
        Assert.Equal("Missing reference",Assert.Single(vm.Rows).Lifecycle); Assert.Null(vm.Rows[0].Widget.SourceItemId); Assert.Equal(host,Assert.Single((await Layout(page)).Items));
        var replacement=await List("Replacement"); await WEdit(page,new ReplaceWidgetSource(widget.Id,replacement.Item.Id)); Assert.Equal(widget.Id,Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets).Id);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ListWidgetRemovalAndPageDeletionCascadeSettingsButLeaveList(bool removeBlock)
    {
        var page=await CanvasPage(); var list=await List(); var item=await ListAdd(list); var host=await Block(page); var widget=await ListWidget(page,list,host:host.Id);
        Assert.Equal(host,Assert.Single((await Layout(page)).Items)); await WEdit(page,new RemoveWidget(widget.Id,removeBlock));
        Assert.Equal(removeBlock?0:1,(await Layout(page)).Items.Count); Assert.Equal(item,Assert.Single((await ListRead(list)).Items)); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WidgetListSettings;"));
        await ListWidget(page,list); await Pages().ApplyAsync(PRef(page),PageAction.Trash); await Pages().DeleteAsync(PRef(page));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WidgetListSettings;")); Assert.Equal(item,Assert.Single((await ListRead(list)).Items)); Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task ListWidgetDuplicateAndContainerMovementDoNotDuplicateOrRetargetSource()
    {
        var page=await CanvasPage(); var list=await List(); await ListAdd(list); var container=await Block(page,0,0,kind:CanvasKind.Container); var widget=await ListWidget(page,list,WidgetMode.Shopping,ListFilter.Checked,parent:container.Id);
        await Edit(page,new ResizeCanvasItem(container.Id,1000,800)); var duplicate=(await WEdit(page,new DuplicateWidget(widget.Id))).Widgets.Single(w=>w.Id!=widget.Id);
        Assert.NotEqual(widget.CanvasItemId,duplicate.CanvasItemId); Assert.Equal(widget.List,duplicate.List); Assert.Equal(widget.SourceItemId,duplicate.SourceItemId); Assert.Equal(widget.Mode,duplicate.Mode);
        var before=await ListRead(list); await Edit(page,new MoveCanvasItems([container.Id],16,16)); Assert.Equal(before.Items,(await ListRead(list)).Items);
        Assert.Equal(widget,(await Widgets().GetAsync(PRef(page))).Widgets.Single(w=>w.Id==widget.Id)); Assert.Single(await Lists().GetDefinitionsAsync(profile));
        await Lists().DuplicateAsync(LRef(list)); Assert.All((await Widgets().GetAsync(PRef(page))).Widgets,w=>Assert.Equal(list.Item.Id,w.SourceItemId)); CanvasLayout.Validate(new(await Layout(page)));
        await Edit(page,new DeleteCanvasItems([container.Id])); Assert.All((await Layout(page)).Items,i=>Assert.Null(i.ParentContainerId)); Assert.Equal(2L,await Sql("SELECT COUNT(*) FROM WidgetListSettings;")); await Edit(page,new DeleteCanvasItems([widget.CanvasItemId,duplicate.CanvasItemId])); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WidgetListSettings;")); Assert.Equal(before.Items,(await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListWidgetBatchReadBoundsItemsReusesRequestsAndKeepsGlobalCounts()
    {
        var page=await CanvasPage(); var list=await List();
        await Sql($"WITH RECURSIVE n(v) AS(SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<600) INSERT INTO ListItems SELECT lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(6))),'{list.Item.Id}','Item '||v,v%2,NULL,'0.1',NULL,NULL,v,'2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00' FROM n;");
        await ListWidget(page,list,WidgetMode.Shopping); await ListWidget(page,list,WidgetMode.Shopping); await ListWidget(page,list,WidgetMode.Compact);
        var spy=WidgetSpy<IListService>.Wrap(Lists()); var vm=new WidgetWorkspaceViewModel(Widgets(),JournalTasks(),WidgetRecurrence(),service,Analytics(),WidgetEvents(),Journals(),Pages(),current,new NavigationService(),clock,NullLogger<WidgetWorkspaceViewModel>.Instance,spy.Service);
        await vm.OpenAsync(PRef(page)); Assert.Equal(1,Assert.Single(spy.Spy.Calls).Value); Assert.Equal("ReadViewsAsync",Assert.Single(spy.Spy.Calls).Key);
        var shopping=vm.Rows.Where(r=>r.Widget.Mode==WidgetMode.Shopping).ToArray(); Assert.Same(shopping[0].ListData,shopping[1].ListData); Assert.All(shopping,r=>Assert.Equal(50,r.ListItems.Count));
        Assert.All(vm.Rows,r=>{Assert.Equal(600,r.ListData!.Count);Assert.Equal(300,r.ListData.CheckedCount);Assert.Equal(new ListTotals(60m,30m),r.ListData.Totals);}); Assert.Empty(vm.Rows.Single(r=>r.Widget.Mode==WidgetMode.Compact).ListData!.Items);
    }
    [Fact]
    public async Task ListWidgetProfileIsolationAndInvalidQuickAddKeepCanonicalDataSafe()
    {
        var page=await CanvasPage(); var list=await List(); var widget=await ListWidget(page,list); var vm=WidgetModel(); await vm.OpenAsync(PRef(page)); var stale=Assert.Single(vm.Rows);
        stale.Input="   "; await vm.QuickAddAsync(stale); Assert.NotNull(stale.Error); Assert.Equal("   ",stale.Input); Assert.Empty((await ListRead(list)).Items);
        var other=await profiles.CreateAsync("Other List Widgets"); var otherList=await Lists().CreateAsync(other.Id,new("Private"));
        stale.Input="Wrong profile"; await vm.QuickAddAsync(stale); Assert.Empty((await Lists().ReadAsync(new(other.Id,otherList.Item.Id))).Items);
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().ReadViewsAsync(other.Id,[new(list.Item.Id)]));
        await profiles.SwitchAsync(profile); await Assert.ThrowsAsync<WidgetValidationException>(()=>WEdit(page,new ReplaceWidgetSource(widget.Id,otherList.Item.Id))); Assert.Empty((await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListWidgetSettingsFailureRollsBackCreationAndConfiguration()
    {
        var page=await CanvasPage(); var list=await List(); var widget=await ListWidget(page,list); var host=Assert.Single((await Layout(page)).Items);
        await Sql("CREATE TRIGGER FailListSettings BEFORE UPDATE ON WidgetListSettings BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<WidgetOperationException>(()=>WEdit(page,new ConfigureWidget(widget.Id,WidgetMode.Compact,List:new(ListFilter.Checked))));
        await Assert.ThrowsAsync<WidgetOperationException>(()=>ListWidget(page,list));
        Assert.Equal(widget,Assert.Single((await Widgets().GetAsync(PRef(page))).Widgets)); Assert.Equal(host,Assert.Single((await Layout(page)).Items)); Assert.Equal(1L,await Sql("SELECT COUNT(*) FROM WidgetListSettings;"));
    }
    [Fact]
    public async Task ListWidgetPageLifecycleAndReadOnlyRefreshPreserveConfiguration()
    {
        var page=await CanvasPage(); var list=await List(); await ListAdd(list); var widget=await ListWidget(page,list,WidgetMode.Shopping,ListFilter.Checked); var vm=WidgetModel();
        var bytes=await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)); await vm.OpenAsync(PRef(page)); await vm.RefreshAsync();
        Assert.Equal(bytes,await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile)));
        foreach(var action in new[]{PageAction.Archive,PageAction.RestoreArchive,PageAction.Trash,PageAction.RestoreTrash})
        {
            await Pages().ApplyAsync(PRef(page),action); await vm.OpenAsync(PRef(page)); Assert.Equal(widget,Assert.Single(vm.Rows).Widget);
            Assert.Equal(action is PageAction.RestoreArchive or PageAction.RestoreTrash,vm.CanInteract); Assert.Single((await ListRead(list)).Items);
        }
    }
    [Fact]
    public async Task ListWidgetBoundedReadIgnoresUnrelatedListsAndRejectsInvalidRequests()
    {
        var list=await List(); await ListAdd(list); var unrelated=await List("Unrelated"); var bad=await ListAdd(unrelated);
        await Sql($"UPDATE ListItems SET UnitPrice='malformed' WHERE Id='{bad.Id}';");
        var views=await Lists().ReadViewsAsync(profile,[new(list.Item.Id),new(list.Item.Id),new(list.Item.Id,ListFilter.Checked),new(list.Item.Id,ListFilter.All,0)]);
        Assert.Equal(3,views.Count); Assert.Single(views[0].Items); Assert.Empty(views[1].Items); Assert.Empty(views[2].Items);
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().ReadViewsAsync(profile,[new(list.Item.Id,(ListFilter)9)]));
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().ReadViewsAsync(profile,[new(list.Item.Id,Limit:1001)]));
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().ReadViewsAsync(profile,[new(list.Item.Id,Limit:-1)]));
    }
}
