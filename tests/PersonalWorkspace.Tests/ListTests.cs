using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private ListService Lists(IListRepository? repository = null) => new(repository ?? new SqliteListRepository(), JournalTasks(), current, gate, clock, NullLogger<ListService>.Instance);
    private WorkspaceItemReference LRef(ListDefinition list) => new(profile, list.Item.Id);
    private ListItemReference LIRef(ListItem item) => new(profile, item.ListId, item.Id);
    private Task<ListDefinition> List(string title = "Shopping", bool prices = true) => Lists().CreateAsync(profile, new(title, "Description", prices, prices, prices ? "EUR" : null));
    private Task<ListItem> ListAdd(ListDefinition list, string title = "Milk", decimal? quantity = null, decimal? price = null) => Lists().AddAsync(LRef(list), new(title, quantity, price));
    private Task<ListSnapshot> ListRead(ListDefinition list) => Lists().ReadAsync(LRef(list));
    private ListWorkspaceViewModel ListModel(NavigationService? navigation = null, IListRepository? repository = null)
    {
        navigation ??= new(); navigation.Navigate(new("Lists")); return new(Lists(repository), organization, current, navigation, NullLogger<ListWorkspaceViewModel>.Instance);
    }
    [Fact]
    public async Task ListsMigrationUpgradesPopulatedPhaseFourteenAndKeepsGlobalDatabaseSeparate()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(12)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var page = Guid.NewGuid(); var source = Guid.NewGuid(); var block = Guid.NewGuid(); var widget = Guid.NewGuid();
        await Sql($"""
            INSERT INTO WorkspaceItems VALUES('{page}',5,'Existing page','2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00',NULL,NULL);
            INSERT INTO WorkspaceItems VALUES('{source}',1,'Existing Task','2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00',NULL,NULL);
            INSERT INTO Pages VALUES('{page}',NULL,0,NULL);
            INSERT INTO Tasks(ItemId,Description,Status,Priority) VALUES('{source}','Keep',0,0);
            INSERT INTO PageCanvasItems VALUES('{block}','{page}',NULL,0,0,0,320,240,'','2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00');
            INSERT INTO WidgetInstances VALUES('{widget}','{block}',0,'{source}',0,'2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00');
            """, legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(13L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy)); Assert.Equal(12L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(widget.ToString(), await Sql("SELECT Id FROM WidgetInstances;", legacy)); Assert.Equal("Keep", await Sql("SELECT Description FROM Tasks;", legacy));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Lists;", legacy)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ListItems;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync(); using var command = global.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Lists','ListItems');"; Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task ListsDefinitionIdentityDefaultsTrimAndDuplicateTitles()
    {
        var first = await Lists().CreateAsync(profile, new("  Movies  ")); var second = await List("Movies", false);
        Assert.Equal("Movies", first.Item.Title); Assert.NotEqual(first.Item.Id, second.Item.Id); Assert.Equal(WorkspaceItemType.List, first.Item.ItemType);
        Assert.False(first.ShowPrices); Assert.False(first.ShowQuantity); Assert.Null(first.CurrencyCode); Assert.Empty(first.Description);
        Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM WorkspaceItems WHERE ItemType=6;"));
    }
    [Theory]
    [InlineData("")][InlineData("   ")][InlineData("bad\ntitle")]
    public async Task ListsRejectInvalidDefinitionAndItemTitles(string title)
    {
        await Assert.ThrowsAsync<ListValidationException>(() => List(title)); var list = await List();
        await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list, title)); Assert.Empty((await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListsCreateRollsBackWorkspaceIdentityOnStorageFailure()
    {
        await Sql("CREATE TRIGGER FailList BEFORE INSERT ON Lists BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ListOperationException>(() => List()); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems WHERE ItemType=6;"));
    }
    [Fact]
    public async Task ListsItemStableIdentityCheckPersistenceAndSeparateTimestamps()
    {
        var list = await List(); var item = await ListAdd(list, "  Milk  "); Assert.False(item.IsChecked); Assert.Equal("Milk", item.Title); Assert.Equal(0, item.SortOrder);
        await Lists().SetCheckedAsync(LIRef(item), true); var saved = Assert.Single((await ListRead(list)).Items);
        Assert.Equal(item.Id, saved.Id); Assert.Equal(item.CreatedAtUtc, saved.CreatedAtUtc); Assert.True(saved.UpdatedAtUtc > item.UpdatedAtUtc); Assert.True(saved.IsChecked);
        await Lists().SetCheckedAsync(LIRef(item), true); Assert.Equal(saved, Assert.Single((await ListRead(list)).Items));
        await Lists().SetCheckedAsync(LIRef(item), false); Assert.False(Assert.Single((await ListRead(list)).Items).IsChecked); Assert.Equal(list, (await ListRead(list)).Definition);
        Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM WorkspaceItems WHERE Id='{item.Id}';"));
    }
    [Fact]
    public async Task ListsDefinitionAndItemNoOpEditsKeepTimestampsAndMeaningfulEditsAdvance()
    {
        var list = await List(); var item = await ListAdd(list);
        Assert.Equal(list, await Lists().UpdateAsync(LRef(list), new("Shopping", "Description", true, true, " eur ")));
        Assert.Equal(item, await Lists().UpdateItemAsync(LIRef(item), new("Milk")));
        var changed = await Lists().UpdateAsync(LRef(list), new("Renamed", "Changed", false, false, "EUR")); Assert.True(changed.Item.UpdatedAtUtc > list.Item.UpdatedAtUtc); Assert.Equal(list.Item.Id, changed.Item.Id);
        Assert.Equal(item, Assert.Single((await ListRead(list)).Items));
        var changedItem = await Lists().UpdateItemAsync(LIRef(item), new("Bread")); Assert.Equal(item.Id, changedItem.Id); Assert.True(changedItem.UpdatedAtUtc > item.UpdatedAtUtc);
    }
    [Theory]
    [InlineData("0", "1")][InlineData("-0.1", "1")][InlineData("1", "-0.01")]
    public async Task ListsRejectInvalidQuantityAndPrice(string quantity, string price)
    {
        var list = await List(); await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list, quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), price: decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture))); Assert.Empty((await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListsExactDecimalStorageNullQuantityZeroPriceAndDerivedTotals()
    {
        var list = await List(); var milk = await ListAdd(list, "Milk", 2, 1.20m); var bread = await ListAdd(list, "Bread", null, 1.80m); var coffee = await ListAdd(list, "Coffee", 1, 4.50m);
        await ListAdd(list, "Unpriced", 5); await ListAdd(list, "Free", null, 0); var snapshot = await ListRead(list);
        Assert.Equal(new ListTotals(8.70m, 8.70m), snapshot.Totals); Assert.Equal(2.40m, ListRules.Subtotal(milk)); Assert.Equal(1.80m, ListRules.Subtotal(bread));
        Assert.Equal(DBNull.Value, await Sql($"SELECT Quantity FROM ListItems WHERE Id='{bread.Id}';")); Assert.Equal("text", await Sql($"SELECT typeof(UnitPrice) FROM ListItems WHERE Id='{milk.Id}';"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM pragma_table_info('Lists') WHERE name LIKE '%Total%';")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM pragma_table_info('ListItems') WHERE name LIKE '%Total%';"));
        await Lists().SetCheckedAsync(LIRef(coffee), true); Assert.Equal(new ListTotals(8.70m, 4.20m), (await ListRead(list)).Totals);
        await Lists().SetCheckedAsync(LIRef(coffee), false); Assert.Equal(8.70m, (await ListRead(list)).Totals.Remaining);
        await Lists().UpdateItemAsync(LIRef(milk), new("Milk", 3, 1.20m)); Assert.Equal(9.90m, (await ListRead(list)).Totals.Total);
        Assert.Equal(0.03m, ListRules.Subtotal(await ListAdd(list, "Exact", 0.3m, 0.10m)));
    }
    [Fact]
    public async Task ListsRejectProductPrecisionLossAndAggregateOverflowAtomically()
    {
        var list = await List();
        await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list, quantity: 0.0000000000000000000000000001m, price: 0.1m));
        await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list, quantity: decimal.MaxValue, price: 2)); Assert.Empty((await ListRead(list)).Items);
        var item = await ListAdd(list, price: decimal.MaxValue);
        await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list, price: 0.1m)); Assert.Single((await ListRead(list)).Items);
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().UpdateItemAsync(LIRef(item), new("Milk", 2, decimal.MaxValue))); Assert.Equal(item, Assert.Single((await ListRead(list)).Items));
    }
    [Fact]
    public async Task ListsHiddenOptionalColumnsPreserveDataAndCurrency()
    {
        var list = await List(); var item = await ListAdd(list, quantity: 1.5m, price: 2.75m);
        var hidden = await Lists().UpdateAsync(LRef(list), new("Shopping", ShowQuantity: false, ShowPrices: false, CurrencyCode: " eur "));
        Assert.Equal("EUR", hidden.CurrencyCode); Assert.Equal(item, Assert.Single((await ListRead(list)).Items));
        var restored = await Lists().UpdateAsync(LRef(list), new("Shopping", ShowQuantity: true, ShowPrices: true)); Assert.Null(restored.CurrencyCode); Assert.Equal(4.125m, (await ListRead(list)).Totals.Total);
    }
    [Theory]
    [InlineData("EU")][InlineData("EURO")][InlineData("€UR")][InlineData("E1R")]
    public async Task ListsRejectMalformedCurrency(string currency) => await Assert.ThrowsAsync<ListValidationException>(() => Lists().CreateAsync(profile, new("Shopping", ShowPrices: true, CurrencyCode: currency)));
    [Theory]
    [InlineData("https://example.com/a?q=one")][InlineData("http://example.com")][InlineData("mailto:friend@example.com")]
    public async Task ListsNotesAndLinksPersist(string link)
    {
        var list = await List(); var item = await Lists().AddAsync(LRef(list), new("Movie", Note: "  Recommended by a friend\nSecond line  ", Link: link));
        Assert.Equal("Recommended by a friend\nSecond line", item.Note); Assert.Equal(link, item.Link); Assert.Equal(item, Assert.Single((await ListRead(list)).Items));
    }
    [Theory]
    [InlineData("relative/path")][InlineData("javascript:alert(1)")][InlineData("file:///C:/secret")][InlineData("https://example.com/a b")]
    public async Task ListsRejectUnsafeOrMalformedLinks(string link)
    {
        var list = await List(); await Assert.ThrowsAsync<ListValidationException>(() => Lists().AddAsync(LRef(list), new("Item", Link: link))); Assert.Empty((await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListsReorderDeleteAndAppendKeepUniqueContiguousOrder()
    {
        var list = await List(); var a = await ListAdd(list, "A"); var b = await ListAdd(list, "B"); var c = await ListAdd(list, "C");
        await Lists().ReorderAsync(LIRef(c), -1); Assert.Equal(new[] { a.Id, c.Id, b.Id }, (await ListRead(list)).Items.Select(i => i.Id));
        await Lists().ReorderAsync(LIRef(a), 1); Assert.Equal(new[] { c.Id, a.Id, b.Id }, (await ListRead(list)).Items.Select(i => i.Id));
        await Lists().ReorderAsync(LIRef(c), -1); await Lists().DeleteItemAsync(LIRef(a)); var d = await ListAdd(list, "D");
        var items = (await ListRead(list)).Items; Assert.Equal(new[] { c.Id, b.Id, d.Id }, items.Select(i => i.Id)); Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i.SortOrder));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE ListItems SET SortOrder=0 WHERE Id='{d.Id}';"));
    }
    [Fact]
    public async Task ListsReorderAndClearCheckedRollBackOnStorageFailure()
    {
        var list = await List(); var a = await ListAdd(list, "A"); var b = await ListAdd(list, "B"); await Lists().SetCheckedAsync(LIRef(a), true); await Lists().SetCheckedAsync(LIRef(b), true);
        var before = (await ListRead(list)).Items;
        await Sql($"CREATE TRIGGER FailOrder BEFORE UPDATE ON ListItems WHEN NEW.Id='{a.Id}' AND NEW.SortOrder=1 BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ListOperationException>(() => Lists().ReorderAsync(LIRef(a), 1)); Assert.Equal(before, (await ListRead(list)).Items);
        await Sql($"CREATE TRIGGER FailDelete BEFORE DELETE ON ListItems WHEN OLD.Id='{b.Id}' BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ListOperationException>(() => Lists().ClearCheckedAsync(LRef(list))); Assert.Equal(before, (await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListsClearCheckedAndUncheckAllPreserveDefinitionAndUncheckedItems()
    {
        var list = await List(); var a = await ListAdd(list, "A"); var b = await ListAdd(list, "B"); await Lists().SetCheckedAsync(LIRef(a), true);
        await Lists().UncheckAllAsync(LRef(list)); Assert.All((await ListRead(list)).Items, i => Assert.False(i.IsChecked));
        await Lists().SetCheckedAsync(LIRef(a), true); await Lists().ClearCheckedAsync(LRef(list)); var remaining = Assert.Single((await ListRead(list)).Items);
        Assert.Equal(b.Id, remaining.Id); Assert.Equal(0, remaining.SortOrder); Assert.Equal(list, (await ListRead(list)).Definition);
    }
    [Theory]
    [InlineData(ListFilter.All, 2)][InlineData(ListFilter.Checked, 1)][InlineData(ListFilter.Unchecked, 1)]
    public async Task ListsBoundedFiltersMatchPresentation(ListFilter filter, int count)
    {
        var list = await List(); var a = await ListAdd(list, "Café"); await ListAdd(list, "Bread"); await Lists().SetCheckedAsync(LIRef(a), true);
        var model = ListModel(); await model.ReloadAsync(); model.Filter = filter; Assert.Equal(count, model.Items.Count);
        Assert.Equal(model.Items.Select(i => i.Item), await Lists().GetItemsAsync(LRef(list), new(filter)));
        model.Filter = ListFilter.All; model.Search = "CAFÉ"; Assert.Single(model.Items); Assert.Single(await Lists().GetItemsAsync(LRef(list), new(Title: "CAFÉ")));
        Assert.Single(await Lists().GetItemsAsync(LRef(list), new(Offset: 1, Limit: 1)));
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().GetItemsAsync(LRef(list), new(Limit: 1001)));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ListsCreateIndependentTaskUsingCanonicalDefaults(bool checkedItem)
    {
        var list = await List(); var item = await Lists().AddAsync(LRef(list), new("The Matrix", 2, 3, "Recommended by a friend")); await Lists().SetCheckedAsync(LIRef(item), checkedItem);
        var before = (await ListRead(list)).Items; var task = await Lists().CreateTaskAsync(LIRef(item)); var another = await Lists().CreateTaskAsync(LIRef(item));
        Assert.NotEqual(item.Id, task.Item.Id); Assert.NotEqual(task.Item.Id, another.Item.Id); Assert.Equal("The Matrix", task.Item.Title); Assert.Equal("Recommended by a friend", task.Description);
        Assert.Equal(TaskStatus.ToDo, task.Status); Assert.Equal(TaskPriority.None, task.Priority); Assert.Null(task.ScheduledDate); Assert.Null(task.Value); Assert.Null(task.ParentTaskId);
        Assert.Equal(before, (await ListRead(list)).Items);
        await JournalTasks().ChangeStatusAsync(new(profile, task.Item.Id), TaskStatus.Done); Assert.Equal(before, (await ListRead(list)).Items);
        await Lists().UpdateItemAsync(LIRef(item), new("Changed item")); Assert.Equal("The Matrix", (await JournalTasks().FindAsync(new(profile, task.Item.Id)))!.Item.Title);
    }
    [Fact]
    public async Task ListsDuplicateCopiesSettingsDetailsOrderWithFreshUncheckedIdentities()
    {
        var list = await List(); var a = await Lists().AddAsync(LRef(list), new("A", 0.75m, 1.2m, "note", "https://example.com")); var b = await ListAdd(list, "B"); await Lists().SetCheckedAsync(LIRef(a), true); await Lists().ReorderAsync(LIRef(b), -1);
        var before = await ListRead(list); var copy = await Lists().DuplicateAsync(LRef(list)); var after = await ListRead(copy);
        Assert.NotEqual(list.Item.Id, copy.Item.Id); Assert.Equal(list.Description, copy.Description); Assert.Equal(list.CurrencyCode, copy.CurrencyCode); Assert.True(copy.ShowQuantity && copy.ShowPrices && copy.IsActive);
        Assert.Equal(before.Items.Select(i => (i.Title, i.Quantity, i.UnitPrice, i.Note, i.Link, i.SortOrder)), after.Items.Select(i => (i.Title, i.Quantity, i.UnitPrice, i.Note, i.Link, i.SortOrder)));
        Assert.All(after.Items, i => { Assert.False(i.IsChecked); Assert.DoesNotContain(i.Id, before.Items.Select(x => x.Id)); Assert.Equal(copy.Item.Id, i.ListId); });
        Assert.Equal(before.Items, (await ListRead(list)).Items); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Tasks;"));
    }
    [Fact]
    public async Task ListsDuplicateRollsBackDefinitionAndPartialItems()
    {
        var list = await List(); await ListAdd(list, "A"); await ListAdd(list, "B");
        await Sql($"CREATE TRIGGER FailCopy BEFORE INSERT ON ListItems WHEN NEW.ListId<>'{list.Item.Id}' AND NEW.SortOrder=1 BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ListOperationException>(() => Lists().DuplicateAsync(LRef(list))); Assert.Single(await Lists().GetDefinitionsAsync(profile)); Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM ListItems;"));
    }
    [Theory]
    [InlineData(ListAction.Archive, ListAction.RestoreArchive)][InlineData(ListAction.Trash, ListAction.RestoreTrash)]
    public async Task ListsLifecyclePreservesItemsAndRejectsInactiveEdits(ListAction hide, ListAction restore)
    {
        var list = await List(); var item = await ListAdd(list); await Lists().SetCheckedAsync(LIRef(item), true); var before = (await ListRead(list)).Items;
        await Lists().ApplyAsync(LRef(list), hide); Assert.False((await ListRead(list)).Definition.IsActive); Assert.Equal(before, (await ListRead(list)).Items);
        await Assert.ThrowsAsync<ListValidationException>(() => ListAdd(list)); await Assert.ThrowsAsync<ListValidationException>(() => Lists().SetCheckedAsync(LIRef(item), false));
        await Lists().ApplyAsync(LRef(list), restore); Assert.True((await ListRead(list)).Definition.IsActive); Assert.Equal(before, (await ListRead(list)).Items);
    }
    [Fact]
    public async Task ListsPermanentDeletionCascadesOwnedDataAndAssignmentsButTasksSurvive()
    {
        var list = await List(); var item = await ListAdd(list); var task = await Lists().CreateTaskAsync(LIRef(item));
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("shopping")); var space = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Personal"));
        await organization.AssignAsync(LRef(list), OrganizationKind.Tag, tag, true); await organization.AssignAsync(LRef(list), OrganizationKind.Space, space, true);
        var catalog = await organization.GetAsync(profile); Assert.Contains(new(list.Item.Id, tag), catalog.ItemTags); Assert.Contains(new(list.Item.Id, space), catalog.ItemSpaces); Assert.DoesNotContain(catalog.ItemTags, x => x.ItemId == item.Id);
        var copy = await Lists().DuplicateAsync(LRef(list)); Assert.DoesNotContain((await organization.GetAsync(profile)).ItemTags, x => x.ItemId == copy.Item.Id);
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().DeleteAsync(LRef(list)));
        await Lists().ApplyAsync(LRef(list), ListAction.Trash); await Lists().DeleteAsync(LRef(list));
        Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM ListItems WHERE ListId='{list.Item.Id}';")); Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM WorkspaceItems WHERE Id='{list.Item.Id}';"));
        Assert.NotNull(await JournalTasks().FindAsync(new(profile, task.Item.Id))); Assert.Empty((await organization.GetAsync(profile)).ItemTags); Assert.Single((await organization.GetAsync(profile)).Tags);
        Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task ListsProfileIsolationClearsSelectionItemsFiltersAndDrafts()
    {
        var list = await List(); var item = await ListAdd(list); var model = ListModel(); await model.ReloadAsync(); model.Filter = ListFilter.Checked; model.Search = "Milk"; model.QuickTitle = "Draft";
        var other = await profiles.CreateAsync("List isolation"); await model.ReloadAsync(); Assert.Empty(model.Rows); Assert.Empty(model.Items); Assert.Null(model.DetailRow); Assert.Equal(ListFilter.All, model.Filter); Assert.Empty(model.Search); Assert.Empty(model.QuickTitle); Assert.False(model.HasItemEditor);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => ListRead(list)); await Assert.ThrowsAsync<WorkspaceChangedException>(() => Lists().SetCheckedAsync(LIRef(item), true));
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().ReadAsync(new(other.Id, list.Item.Id)));
        await profiles.SwitchAsync(profile); await model.ReloadAsync(); Assert.Single(model.Rows); Assert.Equal(item.Id, Assert.Single(model.Items).Item.Id);
    }
    [Fact]
    public async Task ListsRejectWrongListItemAndNonListIdentity()
    {
        var a = await List(); var b = await List(); var item = await ListAdd(a); var task = await JournalTasks().CreateAsync(profile, new("Task"));
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().SetCheckedAsync(new(profile, b.Item.Id, item.Id), true));
        await Assert.ThrowsAsync<ListValidationException>(() => Lists().ReadAsync(new(profile, task.Item.Id))); Assert.False(Assert.Single((await ListRead(a)).Items).IsChecked);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO Lists VALUES('{task.Item.Id}','',0,0,NULL);"));
    }
    [Fact]
    public async Task ListsLibraryDoesNotReadItemsAndSelectedReadsStayBounded()
    {
        var list = await List(); var other = await List("Other");
        await Sql($"WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<1999) INSERT INTO ListItems SELECT lower(hex(randomblob(16))),'{list.Item.Id}','Item '||x,0,NULL,NULL,NULL,NULL,x,'2026-10-08T00:00:00+00:00','2026-10-08T00:00:00+00:00' FROM n;");
        // A malformed decimal in a different List proves metadata and selected-item reads do not deserialize it.
        var foreign = await ListAdd(other); await Sql($"UPDATE ListItems SET Quantity='invalid' WHERE Id='{foreign.Id}';");
        Assert.Equal(2, (await Lists().GetDefinitionsAsync(profile)).Count); Assert.Equal(2000, (await ListRead(list)).Items.Count);
        var page = await Lists().GetItemsAsync(LRef(list), new(Offset: 1200, Limit: 25)); Assert.Equal(25, page.Count); Assert.Equal(1200, page[0].SortOrder);
    }
    [Fact]
    public async Task ListsPresentationRejectsLateNavigationLoadAndCanReload()
    {
        await List(); var repository = new DelayedListRepository(); var navigation = new NavigationService(); var model = ListModel(navigation, repository);
        var pending = model.ReloadAsync(); await repository.Started.Task; navigation.Navigate(new("Tasks")); repository.Release.SetResult(); await pending;
        Assert.False(model.HasDetail); Assert.Empty(model.Items); Assert.False(model.IsBusy);
        navigation.Navigate(new("Lists")); await model.ReloadAsync(); Assert.Single(model.Rows);
    }
    [Fact]
    public async Task ListsPresentationPreventsConcurrentTaskCreationAndUsesReadableAccessibleRows()
    {
        var list = await List(); await ListAdd(list); var model = ListModel(); await model.ReloadAsync(); var row = Assert.Single(model.Items);
        Assert.Equal("Shopping", Assert.Single(model.Rows).ToString()); Assert.DoesNotContain(profile.ToString(), row.ToString()); Assert.Contains("Milk · Unchecked", row.ToString());
        var lease = await gate.EnterAsync();
        var first = model.CreateTaskAsync(row); Assert.True(model.IsBusy); await model.CreateTaskAsync(row); lease.Dispose(); await first;
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Tasks;")); Assert.NotNull(model.Notice); Assert.False(model.IsBusy);
    }
    [Fact]
    public async Task ListsPermanentDeletionRollsBackChildrenOnFailure()
    {
        var list = await List(); var item = await ListAdd(list); await Lists().ApplyAsync(LRef(list), ListAction.Trash);
        await Sql("CREATE TRIGGER FailListDelete BEFORE DELETE ON Lists BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ListOperationException>(() => Lists().DeleteAsync(LRef(list))); Assert.Equal(item, Assert.Single((await ListRead(list)).Items)); Assert.Single(await Lists().GetDefinitionsAsync(profile));
    }
    [Fact]
    public async Task ListsFailedTaskCreationAndCanceledMutationsLeaveSourceUntouched()
    {
        var list = await List(); var item = await ListAdd(list);
        await Sql("CREATE TRIGGER FailTaskInsert BEFORE INSERT ON Tasks BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Lists().CreateTaskAsync(LIRef(item))); Assert.Equal(item, Assert.Single((await ListRead(list)).Items)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems WHERE ItemType=1;"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lists().SetCheckedAsync(LIRef(item), true, cancellation.Token)); Assert.Equal(item, Assert.Single((await ListRead(list)).Items));
    }
    [Fact]
    public async Task ListsCurrentCultureInputAndPresentationRetainExactValues()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");
            var list = await List(); var item = await ListAdd(list); var model = ListModel(); await model.ReloadAsync(); model.EditItemCommand.Execute(Assert.Single(model.Items));
            model.Quantity = "0,3"; model.UnitPrice = "0,10"; await model.SaveItemCommand.ExecuteAsync(null); Assert.Null(model.Error);
            Assert.Equal(0.03m, (await ListRead(list)).Totals.Total); Assert.Contains("0,03 EUR", model.Totals);
            model.EditItemCommand.Execute(Assert.Single(model.Items)); model.Quantity = "0,00000000000000000000000000001"; await model.SaveItemCommand.ExecuteAsync(null); Assert.NotNull(model.Error); Assert.Equal(0.3m, Assert.Single((await ListRead(list)).Items).Quantity);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }
    private sealed class DelayedListRepository : IListRepository
    {
        private readonly SqliteListRepository inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ListDefinition>> GetDefinitionsAsync(WorkspaceContext workspace, CancellationToken token) { var result = await inner.GetDefinitionsAsync(workspace, token); Started.TrySetResult(); await Release.Task; return result; }
        public Task<ListSnapshot> ReadAsync(WorkspaceContext w, Guid id, CancellationToken t) => inner.ReadAsync(w, id, t);
        public Task<IReadOnlyList<ListItem>> GetItemsAsync(WorkspaceContext w, Guid id, ListItemQuery query, CancellationToken t) => inner.GetItemsAsync(w, id, query, t);
        public Task<T> TransactAsync<T>(WorkspaceContext w, Guid? id, Func<ListState, T> action, CancellationToken t) => inner.TransactAsync(w, id, action, t);
    }
}
