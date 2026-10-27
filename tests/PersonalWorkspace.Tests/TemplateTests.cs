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
    private TemplateService Templates(ITemplateRepository? repository = null) => new(repository ?? new SqliteTemplateRepository(), JournalTasks(), Lists(), Journals(), current, gate, clock, NullLogger<TemplateService>.Instance,
        new PageTemplateService(new SqliteTemplateRepository(), AssetFiles(), clock), Assets());
    private TemplateReference TRef(TemplateSnapshot snapshot) => new(profile, snapshot.Metadata.Id);
    private Task<TemplateSnapshot> Capture(WorkspaceItem item, TemplateType type, string name = "Reusable") => Templates().CaptureAsync(new(profile, item.Id), type, name);

    [Fact]
    public async Task TemplateMigrationPreservesEveryPopulatedPhaseNineteenTableAndIsIdempotent()
    {
        var page=await CanvasPage();var container=await Block(page,512,512,kind:CanvasKind.Container);var host=await Block(page,8,8,container.Id);
        await WAdd(page,(await JournalTasks().CreateAsync(profile,new("Keep Task"))).Item,host:host.Id);
        await ListWidget(page,await List(),filter:ListFilter.Checked);
        await WAdd(page,(await Create()).Item,WidgetType.TrackerChart,WidgetMode.Chart,chart:new(WidgetChartKind.Bar,AnalyticsPreset.NinetyDays));
        await ContentWidget(page);await ContentWidget(page,WidgetType.Image);await Shape(page);await Connector(page,host);
        await DEdit(page,new CreateDecoration(DecorationKind.Stroke,new(),Points:[new(10,20),new(30,40),new(50,10)]));
        await Edit(page,new ZoomCanvas(75));
        var legacy=Guid.NewGuid();new ProfileFiles(paths).Create(legacy);await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);",legacy);
        foreach(var m in WorkspaceMigrationCatalog.All.Take(17))await Sql(m.Sql+$"INSERT INTO SchemaMigrations VALUES({m.Version},'{m.Name}','original');",legacy);
        var tables=new List<string>();
        await using(var c=await Connection(legacy))
        {
            using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name<>'SchemaMigrations' ORDER BY rowid";using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync())tables.Add(r.GetString(0));}
            using var attach=c.CreateCommand();attach.CommandText="ATTACH DATABASE $path AS fixture";attach.Parameters.AddWithValue("$path",paths.WorkspaceDatabase(profile));await attach.ExecuteNonQueryAsync();
            foreach(var table in tables){using var cmd=c.CreateCommand();cmd.CommandText=$"INSERT INTO main.\"{table}\" SELECT * FROM fixture.\"{table}\" WHERE true"+(table=="WidgetListSettings"?" ON CONFLICT(WidgetId) DO UPDATE SET ItemFilter=excluded.ItemFilter":"");await cmd.ExecuteNonQueryAsync();}
        }
        var before=new Dictionary<string,string>();foreach(var table in tables)before[table]=await TableRows(legacy,table);
        await initializer.InitializeAsync(legacy,false);await initializer.InitializeAsync(legacy,false);
        foreach(var table in tables)Assert.Equal(before[table],await TableRows(legacy,table));
        Assert.Equal(21L,await Sql("SELECT COUNT(*) FROM SchemaMigrations;",legacy));Assert.Equal(17L,await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';",legacy));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Templates;",legacy));Assert.Null(await Sql("PRAGMA foreign_key_check;",legacy));Assert.Equal("ok",await Sql("PRAGMA integrity_check;",legacy));
        await using var global=await new SqliteConnectionFactory(paths).OpenAsync();using var check=global.CreateCommand();check.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%Template%'";Assert.Equal(0L,await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TemplateSchemaEnforcesTypeOwnershipVersionAndCascadesAllSnapshotRows()
    {
        var list=await List();await ListAdd(list);var template=await Capture(list.Item,TemplateType.List);var id=template.Metadata.Id;
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"INSERT INTO JournalTemplateDefinitions(TemplateId,Title,Description) VALUES('{id}','Wrong','');"));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE Templates SET TemplateType=1 WHERE Id='{id}';"));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE Templates SET FormatVersion=99 WHERE Id='{id}';"));
        await Templates().DeleteAsync(TRef(template));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM ListTemplateItems;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM ListTemplateDefinitions;"));
        var journal=await Journals().CreateDefinitionAsync(profile,"Options","",[FieldDraft(JournalFieldType.Select)]);var jt=await Capture(journal.Item,TemplateType.Journal);
        await Assert.ThrowsAsync<SqliteException>(()=>Sql("UPDATE JournalTemplateFields SET FieldType=1;"));
        await Templates().DeleteAsync(TRef(jt));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM JournalTemplateFields;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM JournalTemplateOptions;"));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task TemplateReplacementRejectsUnsupportedTaskWithoutChangingSnapshot()
    {
        var simple=await JournalTasks().CreateAsync(profile,new("Simple"));var template=await Capture(simple.Item,TemplateType.Task);
        await JournalTasks().CreateSubtaskAsync(new(profile,simple.Item.Id),"Child");
        await Assert.ThrowsAsync<TaskValidationException>(()=>Templates().ReplaceAsync(TRef(template),new(profile,simple.Item.Id)));
        Assert.Equal(template,await Templates().ReadAsync(TRef(template)));
    }
    [Fact]
    public async Task TemplateBatchValidationAndCancellationNeverLeavePartialEntities()
    {
        var before=await TableRows(profile,"WorkspaceItems");
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().CreateDefinitionAsync(profile,new("Bad"),[new("First"),new("Bad quantity",-1)]));
        await Assert.ThrowsAsync<ListValidationException>(()=>Lists().CreateDefinitionAsync(profile,new("Overflow"),[new("Bad product",decimal.MaxValue,2)]));
        await Assert.ThrowsAsync<JournalValidationException>(()=>Journals().CreateDefinitionAsync(profile,"Bad","",[FieldDraft(JournalFieldType.ShortText),new("Bad scale",JournalFieldType.Scale,ScaleMin:5,ScaleMax:1)]));
        Assert.Equal(before,await TableRows(profile,"WorkspaceItems"));
        var source=await List();var template=await Capture(source.Item,TemplateType.List);before=await TableRows(profile,"WorkspaceItems");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Templates().InstantiateAsync(TRef(template),token:cancellation.Token));Assert.Equal(before,await TableRows(profile,"WorkspaceItems"));
    }
    [Fact]
    public async Task SearchTemplateNavigationIsConsumedBeforeCatalogFilteringOrDeletion()
    {
        var source = await List(); var template = await Capture(source.Item, TemplateType.List);
        var navigation = new NavigationService(current);
        navigation.Navigate(new("Templates", template.Metadata.Id.ToString(), new(profile)));
        var model = new TemplateWorkspaceViewModel(Templates(), current, navigation, NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync();
        Assert.Equal(template.Metadata.Id, model.Snapshot?.Metadata.Id);

        model.Filter = "Task"; await model.ReloadAsync();
        Assert.Empty(model.Rows); Assert.Null(model.Selected); Assert.Null(model.Error);
        model.Filter = "All"; await model.ReloadAsync();
        Assert.Null(model.Selected); Assert.Null(model.Snapshot); Assert.Null(model.Error);
        navigation.Navigate(navigation.Current); await model.ReloadAsync();
        Assert.Equal(template.Metadata.Id, model.Snapshot?.Metadata.Id);
        await model.DeleteAsync(TRef(template));
        Assert.Empty(model.Rows); Assert.Null(model.Snapshot); Assert.Null(model.Error);
        Assert.Equal("Template deleted. Existing instances are unchanged.", model.Notice);
    }
    [Fact]
    public async Task TemplateModelClearsSelectionDraftsSourcesAndPreviewOnProfileChange()
    {
        var source=await List();var template=await Capture(source.Item,TemplateType.List);var navigation=new NavigationService();navigation.Navigate(new("Templates"));
        var model=new TemplateWorkspaceViewModel(Templates(),current,navigation,NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync();model.Selected=model.Rows.Single();await model.SelectAsync(model.Selected);Assert.NotNull(model.Snapshot);
        model.Name="Draft";model.CaptureName="Capture draft";model.CaptureDescription="Description draft";
        var foreign=await profiles.CreateAsync("New template Profile");await model.ReloadAsync();
        Assert.Empty(model.Rows);Assert.Empty(model.Sources);Assert.Null(model.Selected);Assert.Null(model.Snapshot);Assert.Null(model.Source);Assert.Equal("",model.Name);Assert.Equal("",model.CaptureName);Assert.Equal("",model.CaptureDescription);Assert.False(model.CanUse);Assert.DoesNotContain("Shopping",model.Preview);
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WorkspaceItems;",foreign.Id));
    }
    [Fact]
    public async Task TemplateModelDiscardsLatePreviewAfterProfileSwitch()
    {
        var source=await List();var template=await Capture(source.Item,TemplateType.List);var repository=new DelayedTemplateRepository(new SqliteTemplateRepository());
        var navigation=new NavigationService();navigation.Navigate(new("Templates"));var model=new TemplateWorkspaceViewModel(Templates(repository),current,navigation,NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync();var pending=model.SelectAsync(model.Rows.Single());await repository.Started.Task;
        // Profile publication can happen after the repository read but before the presentation continuation.
        var switching=profiles.CreateAsync("Late template Profile");repository.Release.SetResult();await switching;await pending;await model.ReloadAsync();
        Assert.Null(model.Snapshot);Assert.Empty(model.Rows);Assert.False(model.CanUse);
    }
    [Fact]
    public async Task TemplateModelDiscardsLatePreviewAfterNavigation()
    {
        var source=await List();await Capture(source.Item,TemplateType.List);var repository=new DelayedTemplateRepository(new SqliteTemplateRepository());
        var navigation=new NavigationService();navigation.Navigate(new("Templates"));var model=new TemplateWorkspaceViewModel(Templates(repository),current,navigation,NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync();var pending=model.SelectAsync(model.Rows.Single());await repository.Started.Task;
        navigation.Navigate(new("Today"));repository.Release.SetResult();await pending;
        Assert.Null(model.Snapshot);Assert.Null(model.Selected);Assert.Empty(model.Sources);Assert.False(model.CanUse);
    }
    [Fact]
    public async Task TemplateModelReplacementRefreshesDefaultTitleAndDoubleCreationIsGuarded()
    {
        var a=await List("A");var b=await List("B");var template=await Capture(a.Item,TemplateType.List);
        var navigation=new NavigationService();navigation.Navigate(new("Templates"));var model=new TemplateWorkspaceViewModel(Templates(),current,navigation,NullLogger<TemplateWorkspaceViewModel>.Instance);
        await model.ReloadAsync();model.Selected=model.Rows.Single();await model.SelectAsync(model.Selected);
        await model.ReplaceAsync(TRef(template),LRef(b));Assert.Equal("B",model.NewTitle);Assert.Contains("B",model.Preview);
        using(var lease=await gate.EnterAsync())
        {
            var first=model.InstantiateCommand.ExecuteAsync(null);await model.InstantiateCommand.ExecuteAsync(null);
            Assert.True(model.IsBusy);
            lease.Dispose();await first;
        }
        Assert.Equal(3L,await Sql("SELECT COUNT(*) FROM Lists;"));
    }
    private sealed class DelayedTemplateRepository(ITemplateRepository inner) : ITemplateRepository
    {
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TemplateSnapshot> ReadAsync(WorkspaceContext w,Guid id,CancellationToken token){var value=await inner.ReadAsync(w,id,token);Started.TrySetResult();await Release.Task;return value;}
        public Task<IReadOnlyList<TemplateMetadata>> CatalogAsync(WorkspaceContext w,TemplateType? type,CancellationToken t)=>inner.CatalogAsync(w,type,t);
        public Task<IReadOnlyList<TemplateSource>> SourcesAsync(WorkspaceContext w,TemplateType type,CancellationToken t)=>inner.SourcesAsync(w,type,t);
        public Task SaveAsync(WorkspaceContext w,TemplateSnapshot s,bool create,CancellationToken t)=>inner.SaveAsync(w,s,create,t);
        public Task RenameAsync(WorkspaceContext w,Guid id,string name,string? description,DateTimeOffset now,CancellationToken t)=>inner.RenameAsync(w,id,name,description,now,t);
        public Task DeleteAsync(WorkspaceContext w,Guid id,CancellationToken t)=>inner.DeleteAsync(w,id,t);
    }

    [Theory]
    [InlineData(TaskValueType.Checkbox)][InlineData(TaskValueType.Number)][InlineData(TaskValueType.Percentage)]
    [InlineData(TaskValueType.Currency)][InlineData(TaskValueType.Duration)][InlineData(TaskValueType.CustomUnit)]
    public async Task TemplateTaskRetainsExactDefinitionAndCreatesFreshIndependentExecutions(TaskValueType type)
    {
        var target = type == TaskValueType.Duration ? 4530m : 20.123456789012345678901234567m;
        TaskValue? value = type == TaskValueType.Checkbox ? null : new(type, target, target, "EUR", "pages");
        var source = await JournalTasks().CreateAsync(profile, new("Read 20 pages", "Definition", TaskStatus.Done, TaskPriority.Normal, Day, value));
        var before = await TableRows(profile, "Tasks"); var values = await TableRows(profile, "TaskValues");
        var template = await Capture(source.Item, TemplateType.Task); var reference = TRef(template);
        Assert.Equal(before, await TableRows(profile, "Tasks")); Assert.Equal(values, await TableRows(profile, "TaskValues"));
        var a = await Templates().InstantiateAsync(reference); var b = await Templates().InstantiateAsync(reference, "Second");
        var task = (await JournalTasks().FindAsync(new(profile, a.Id)))!;
        Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(source.Item.Id, a.Id); Assert.NotEqual(reference.Id, a.Id);
        Assert.Equal(TaskStatus.ToDo, task.Status); Assert.Null(task.ScheduledDate); Assert.Null(task.Value?.Actual); Assert.Null(task.ParentTaskId);
        Assert.False(task.HasOccurrences); Assert.False(task.IsRecurring); Assert.Null(task.Item.ArchivedAtUtc); Assert.Null(task.Item.DeletedAtUtc);
        Assert.Equal(TaskPriority.Normal, task.Priority); Assert.Equal("Definition", task.Description);
        Assert.Equal(source.Value is { } v ? v with { Actual = null } : null, task.Value);
        await JournalTasks().UpdateAsync(new(profile, a.Id), new("Only first changed"));
        Assert.Equal("Second", (await JournalTasks().FindAsync(new(profile, b.Id)))!.Item.Title);
        Assert.Equal("Read 20 pages", (await Templates().ReadAsync(reference)).Blueprint.Title);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ItemTags;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ItemSpaces;"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TaskOccurrences;"));
        Assert.Equal(type == TaskValueType.Duration ? "integer" : type == TaskValueType.Checkbox ? "null" : "text", await Sql("SELECT typeof(COALESCE(Target,TargetSeconds)) FROM TaskTemplateDefinitions;"));
    }
    [Theory]
    [InlineData("parent")][InlineData("child")][InlineData("dependency")][InlineData("blocker")][InlineData("recurrence")][InlineData("history")]
    public async Task TemplateTaskRejectsUnsupportedGraphWithoutSaving(string scenario)
    {
        var task = await JournalTasks().CreateAsync(profile, new("Complex")); var reference = new TaskReference(profile, task.Item.Id);
        if (scenario is "parent" or "child") { var child = await JournalTasks().CreateSubtaskAsync(reference, "Child"); if (scenario == "child") task = child; }
        if (scenario is "dependency" or "blocker")
        {
            var other = await JournalTasks().CreateAsync(profile, new("Other"));
            await JournalTasks().AddDependencyAsync(reference, other.Item.Id); if (scenario == "blocker") task = other;
        }
        if (scenario is "recurrence" or "history")
        {
            var recurrence = new RecurrenceService(new SqliteTaskRepository(),current,gate,clock,NullLogger<RecurrenceService>.Instance);
            await recurrence.SetRuleAsync(reference,new(RecurrencePattern.Daily,Day));
            if (scenario == "history") { await recurrence.GetRangeAsync(profile,Day,Day); await recurrence.RemoveAsync(reference); }
        }
        var error = await Assert.ThrowsAsync<TaskValidationException>(() => Capture(task.Item, TemplateType.Task));
        Assert.Contains("single Tasks", error.Message); Assert.Empty(await Templates().CatalogAsync(profile));
    }
    [Fact]
    public async Task ListTemplateCopiesExactOrderedDefinitionsResetsChecksAndSurvivesSourceDeletion()
    {
        var source = await List("Travel Packing"); var a = await ListAdd(source, "Passport", 0.3m, 0.10m); var b = await Lists().AddAsync(LRef(source), new("Charger", null, 1.20m, "note", "https://example.com"));
        await Lists().SetCheckedAsync(LIRef(a), true); await Lists().ReorderAsync(LIRef(b), -1);
        var original = await ListRead(source); var template = await Capture(source.Item, TemplateType.List);
        Assert.Equal(original.Items, (await ListRead(source)).Items);
        await Lists().UpdateAsync(LRef(source), new("Renamed")); await Lists().DeleteItemAsync(LIRef(a));
        await Lists().ApplyAsync(LRef(source), ListAction.Archive); await Lists().ApplyAsync(LRef(source), ListAction.Trash); await Lists().DeleteAsync(LRef(source));
        var first = await Templates().InstantiateAsync(TRef(template)); var second = await Templates().InstantiateAsync(TRef(template));
        var copy = await Lists().ReadAsync(new(profile, first.Id)); var copy2 = await Lists().ReadAsync(new(profile, second.Id));
        Assert.Equal("Travel Packing", first.Title); Assert.Equal("EUR", copy.Definition.CurrencyCode); Assert.True(copy.Definition.ShowPrices); Assert.True(copy.Definition.ShowQuantity);
        Assert.Equal(original.Items.Select(i => i.Title), copy.Items.Select(i => i.Title)); Assert.All(copy.Items, i => Assert.False(i.IsChecked));
        Assert.Empty(copy.Items.Select(i => i.Id).Intersect(original.Items.Select(i => i.Id))); Assert.Empty(copy.Items.Select(i => i.Id).Intersect(copy2.Items.Select(i => i.Id)));
        Assert.Equal(new ListTotals(1.23m,1.23m),copy.Totals); Assert.Equal(0.3m,copy.Items[1].Quantity); Assert.Equal(0.1m,copy.Items[1].UnitPrice);
        Assert.Equal("note", copy.Items[0].Note); Assert.Equal("https://example.com", copy.Items[0].Link);
        await Lists().DeleteItemAsync(new(profile,first.Id,copy.Items[0].Id)); Assert.Equal(2,(await Lists().ReadAsync(new(profile,second.Id))).Items.Count);
        Assert.Equal(2,Assert.IsType<ListBlueprint>((await Templates().ReadAsync(TRef(template))).Blueprint).Items.Count);
    }
    [Theory]
    [InlineData(JournalFieldType.ShortText)][InlineData(JournalFieldType.LongText)][InlineData(JournalFieldType.Number)][InlineData(JournalFieldType.Percentage)]
    [InlineData(JournalFieldType.Currency)][InlineData(JournalFieldType.Duration)][InlineData(JournalFieldType.Checkbox)][InlineData(JournalFieldType.Scale)]
    [InlineData(JournalFieldType.Select)][InlineData(JournalFieldType.MultiSelect)][InlineData(JournalFieldType.Date)][InlineData(JournalFieldType.Link)]
    [InlineData(JournalFieldType.TaskReference)][InlineData(JournalFieldType.TrackerReference)]
    public async Task JournalTemplateCopiesAllFieldTypesWithFreshOptionsAndLazyDays(JournalFieldType type)
    {
        var source = await Journal(); var field = await Field(source,type); var second = await Field(source,JournalFieldType.LongText,"Second");
        await JSave(source,second,new(Text:"Historical note"));
        if (type is JournalFieldType.Select or JournalFieldType.MultiSelect) await JSave(source,field,new(OptionIds:[field.OrderedOptions[0].Id]));
        await Journals().MoveFieldAsync(JRef(source),second.Id,-1);
        var history = await TableRows(profile,"JournalValues"); var selections = await TableRows(profile,"JournalSelections");
        var template = await Capture(source.Item,TemplateType.Journal); var instance = await Templates().InstantiateAsync(TRef(template));
        Assert.Equal(history,await TableRows(profile,"JournalValues")); Assert.Equal(selections,await TableRows(profile,"JournalSelections"));
        var copy = await Journals().ReadDefinitionAsync(new(profile,instance.Id)); Assert.Equal(new[]{"Second","Field"},copy.Fields.Select(f=>f.Name));
        var copied = copy.Fields[1]; Assert.Equal(type,copied.Type); Assert.NotEqual(field.Id,copied.Id); Assert.False(copied.HistoryLocked); Assert.False(copied.HasValues);
        Assert.Equal(field.CurrencyCode,copied.CurrencyCode); Assert.Equal(field.ScaleMin,copied.ScaleMin); Assert.Equal(field.ScaleMax,copied.ScaleMax);
        Assert.Equal(field.OrderedOptions.Select(o=>o.Label),copied.OrderedOptions.Select(o=>o.Label)); Assert.Empty(field.OrderedOptions.Select(o=>o.Id).Intersect(copied.OrderedOptions.Select(o=>o.Id)));
        Assert.Empty((await Journals().GetRangeAsync(new(profile,instance.Id),Day,Day)).Entries); Assert.Equal(0L,await Sql($"SELECT COUNT(*) FROM JournalEntries WHERE JournalId='{instance.Id}';"));
        await Journals().ApplyAsync(JRef(source),JournalAction.Archive); await Journals().ApplyAsync(JRef(source),JournalAction.Trash); await Journals().PermanentlyDeleteAsync(JRef(source));
        var another = await Templates().InstantiateAsync(TRef(template)); Assert.NotEqual(instance.Id,another.Id); Assert.Equal(2,(await Journals().ReadDefinitionAsync(new(profile,another.Id))).Fields.Count);
    }
    [Fact]
    public async Task TemplateCatalogIdentityMetadataFilterDeletionAndTaskSourceIndependence()
    {
        var source = await JournalTasks().CreateAsync(profile,new("Original")); var a=await Capture(source.Item,TemplateType.Task," Same "); var b=await Capture(source.Item,TemplateType.Task,"Same");
        Assert.NotEqual(a.Metadata.Id,b.Metadata.Id); Assert.Equal("Same",a.Metadata.Name); Assert.Equal(1,a.Metadata.FormatVersion); Assert.Equal(TimeSpan.Zero,a.Metadata.CreatedAtUtc.Offset);
        await JournalTasks().UpdateAsync(new(profile,source.Item.Id),new("Edited")); await JournalTasks().ApplyAsync(new(profile,source.Item.Id),TaskAction.Archive); await JournalTasks().ApplyAsync(new(profile,source.Item.Id),TaskAction.Trash); await JournalTasks().PermanentlyDeleteAsync(new(profile,source.Item.Id));
        var copy=await Templates().InstantiateAsync(TRef(a)); Assert.Equal("Original",copy.Title);
        await Templates().RenameAsync(TRef(a),"Renamed"," Description "); var renamed=await Templates().ReadAsync(TRef(a));
        Assert.Equal(a.Metadata.Id,renamed.Metadata.Id);Assert.Equal(a.Metadata.CreatedAtUtc,renamed.Metadata.CreatedAtUtc);Assert.True(renamed.Metadata.UpdatedAtUtc>a.Metadata.UpdatedAtUtc);Assert.Equal("Description",renamed.Metadata.Description);
        await Templates().RenameAsync(TRef(a),"Renamed","Description");Assert.Equal(renamed.Metadata,(await Templates().ReadAsync(TRef(a))).Metadata);
        Assert.Equal(2,(await Templates().CatalogAsync(profile,TemplateType.Task)).Count); Assert.Empty(await Templates().CatalogAsync(profile,TemplateType.List));
        await Templates().DeleteAsync(TRef(a)); Assert.NotNull(await JournalTasks().FindAsync(new(profile,copy.Id))); Assert.Single(await Templates().CatalogAsync(profile));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Theory]
    [InlineData(TemplateType.Task)][InlineData(TemplateType.List)][InlineData(TemplateType.Journal)]
    public async Task TemplateReplacementIsAtomicAndLeavesExistingInstancesUnchanged(TemplateType type)
    {
        async Task<WorkspaceItem> Source(string title) => type switch
        {
            TemplateType.Task => (await JournalTasks().CreateAsync(profile,new(title))).Item,
            TemplateType.List => (await Lists().CreateDefinitionAsync(profile,new(title),[new("Item")])).Item,
            _ => (await Journals().CreateDefinitionAsync(profile,title,"",[FieldDraft(JournalFieldType.Select)])).Item
        };
        var original=await Source("Original");var replacement=await Source("Replacement");var template=await Capture(original,type);var copy=await Templates().InstantiateAsync(TRef(template));
        await Templates().ReplaceAsync(TRef(template),new(profile,replacement.Id));Assert.Equal("Original",await Sql($"SELECT Title FROM WorkspaceItems WHERE Id='{copy.Id}';"));Assert.Equal("Replacement",(await Templates().InstantiateAsync(TRef(template))).Title);
        var table=type switch{TemplateType.Task=>"TaskTemplateDefinitions",TemplateType.List=>"ListTemplateItems",_=>"JournalTemplateOptions"};
        var before=await TableRows(profile,table);var metadata=(await Templates().ReadAsync(TRef(template))).Metadata;
        await Sql($"CREATE TRIGGER RejectTemplate BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TemplateOperationException>(()=>Templates().ReplaceAsync(TRef(template),new(profile,original.Id)));
        Assert.Equal(before,await TableRows(profile,table));Assert.Equal(metadata,(await Templates().ReadAsync(TRef(template))).Metadata);Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Theory]
    [InlineData(TemplateType.Task)][InlineData(TemplateType.List)][InlineData(TemplateType.Journal)]
    public async Task TemplateInstantiationFailureRollsBackEveryCanonicalRow(TemplateType type)
    {
        var source=type switch
        {
            TemplateType.Task=>(await JournalTasks().CreateAsync(profile,new("Source",Value:new(TaskValueType.Number,20)))).Item,
            TemplateType.List=>(await Lists().CreateDefinitionAsync(profile,new("Source"),[new("First"),new("Second")])).Item,
            _=>(await Journals().CreateDefinitionAsync(profile,"Source","",[FieldDraft(JournalFieldType.Select)])).Item
        };
        var template=await Capture(source,type);var before=await TableRows(profile,"WorkspaceItems");
        var table=type switch{TemplateType.Task=>"TaskValues",TemplateType.List=>"ListItems",_=>"JournalFieldOptions"};var children=await TableRows(profile,table);
        await Sql($"CREATE TRIGGER RejectInstance BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAnyAsync<Exception>(()=>Templates().InstantiateAsync(TRef(template)));
        Assert.Equal(before,await TableRows(profile,"WorkspaceItems"));Assert.Equal(children,await TableRows(profile,table));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Theory]
    [InlineData(TemplateType.Task)][InlineData(TemplateType.List)][InlineData(TemplateType.Journal)]
    public async Task TemplateCaptureFailureRollsBackCatalogAndChildren(TemplateType type)
    {
        var source=type switch
        {
            TemplateType.Task=>(await JournalTasks().CreateAsync(profile,new("Source"))).Item,
            TemplateType.List=>(await Lists().CreateDefinitionAsync(profile,new("Source"),[new("First"),new("Second")])).Item,
            _=>(await Journals().CreateDefinitionAsync(profile,"Source","",[FieldDraft(JournalFieldType.Select)])).Item
        };
        var table=type switch{TemplateType.Task=>"TaskTemplateDefinitions",TemplateType.List=>"ListTemplateItems",_=>"JournalTemplateOptions"};
        await Sql($"CREATE TRIGGER RejectCapture BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TemplateOperationException>(()=>Capture(source,type));Assert.Empty(await Templates().CatalogAsync(profile));
        foreach(var name in new[]{"TaskTemplateDefinitions","ListTemplateDefinitions","ListTemplateItems","JournalTemplateDefinitions","JournalTemplateFields","JournalTemplateOptions"})Assert.Equal(0L,await Sql($"SELECT COUNT(*) FROM {name};"));
        Assert.Equal(1L,await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task TemplateLargeListUsesOneCanonicalBatchAndCatalogDoesNotDeserializeOtherSnapshots()
    {
        var items=Enumerable.Range(0,300).Select(i=>new ListItemDraft("Item "+i,1.25m,0.20m)).ToArray();
        var source=await Lists().CreateDefinitionAsync(profile,new("Large",ShowPrices:true,CurrencyCode:"EUR"),items);var template=await Capture(source.Item,TemplateType.List);
        var item=await Templates().InstantiateAsync(TRef(template));var copy=await Lists().ReadAsync(new(profile,item.Id));
        Assert.Equal(items.Select(i=>i.Title),copy.Items.Select(i=>i.Title));Assert.Equal(75m,copy.Totals.Total);Assert.All(copy.Items,i=>Assert.False(i.IsChecked));
        await Sql("UPDATE ListTemplateItems SET UnitPrice='invalid' WHERE SortOrder=250;");Assert.Single(await Templates().CatalogAsync(profile));
    }
    [Fact]
    public async Task TemplatesAreProfileLocalAndSourceReadsDoNotLoadUnrelatedBlueprints()
    {
        var source=await List();await ListAdd(source);var template=await Capture(source.Item,TemplateType.List);var reference=TRef(template);
        await Sql("UPDATE ListTemplateItems SET Quantity='corrupt';"); Assert.Single(await Templates().CatalogAsync(profile));
        var task=await JournalTasks().CreateAsync(profile,new("Task"));var taskTemplate=await Capture(task.Item,TemplateType.Task);Assert.Equal("Task",(await Templates().ReadAsync(TRef(taskTemplate))).Blueprint.Title);
        var foreign=await profiles.CreateAsync("Foreign templates");Assert.Empty(await Templates().CatalogAsync(foreign.Id));
        await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Templates().ReadAsync(reference));await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Templates().InstantiateAsync(reference));
        await Assert.ThrowsAsync<TemplateValidationException>(()=>Templates().ReadAsync(new(foreign.Id,reference.Id)));
        await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Templates().ReplaceAsync(new(foreign.Id,reference.Id),new(profile,source.Item.Id)));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WorkspaceItems;",foreign.Id));
    }
}
