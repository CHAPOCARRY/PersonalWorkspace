using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private SearchService SearchService() => new(new SqliteSearchRepository(), current, gate);
    private Task<SearchResponse> Search(string text, bool archived = false) => SearchService().SearchAsync(profile, text, archived);
    [Fact]
    public async Task SearchListNavigationDoesNotReplayDeletedChildOnRefresh()
    {
        var list = await List(); var item = await Lists().AddAsync(LRef(list), new("Passport"));
        var navigation = new NavigationService(current);
        navigation.Navigate(new("Lists", list.Item.Id.ToString(), new(profile, item.Id)));
        var model = new ListWorkspaceViewModel(Lists(), organization, current, navigation, NullLogger<ListWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); Assert.True(Assert.Single(model.Items).SearchMatch);
        model.Search = ""; model.Filter = ListFilter.Unchecked;
        await model.ReloadAsync(); Assert.Equal("", model.Search); Assert.Equal(ListFilter.Unchecked, model.Filter);
        await model.DeleteItemAsync(Assert.Single(model.Items));
        Assert.Null(model.Error); Assert.True(model.HasDetail); Assert.Empty(model.Items);
    }
    [Fact]
    public async Task SearchPageNavigationDoesNotReplayRemovedWidgetOnRefresh()
    {
        var page = await CanvasPage(); var widget = await ContentWidget(page);
        var navigation = new NavigationService(current);
        navigation.Navigate(new("Pages", page.Item.Id.ToString(), new(profile, widget.Id)));
        var canvas = new CanvasWorkspaceViewModel(Canvas(), current, NullLogger<CanvasWorkspaceViewModel>.Instance, WidgetModel());
        var model = new PageWorkspaceViewModel(Pages(), organization, current, navigation, NullLogger<PageWorkspaceViewModel>.Instance, canvas);
        await model.ReloadAsync(); Assert.Contains(widget.CanvasItemId, canvas.Selection);
        await model.ReloadAsync(); Assert.Contains(widget.CanvasItemId, canvas.Selection);
        await WEdit(page, new RemoveWidget(widget.Id, true));
        await model.ReloadAsync(); Assert.Null(model.Error); Assert.Equal(page.Item.Id, model.Detail?.Item.Id);
    }
    [Fact]
    public async Task SearchProjectionBatchUsesCanonicalIdentityLookups()
    {
        using var c=await Connection();using var command=c.CreateCommand();
        command.CommandText="EXPLAIN QUERY PLAN WITH batch AS MATERIALIZED (SELECT Kind,SourceId FROM SearchDirtyDocuments ORDER BY Kind,SourceId LIMIT 64) SELECT s.* FROM SearchSources s WHERE s.SourceId IN (SELECT SourceId FROM batch) AND (s.Kind,s.SourceId) IN (SELECT Kind,SourceId FROM batch) ORDER BY s.Kind,s.SourceId";
        using var reader=await command.ExecuteReaderAsync();var plan=new List<string>();while(await reader.ReadAsync())plan.Add(reader.GetString(3));
        Assert.True(!plan.Any(p=>p=="SCAN w"||p=="SCAN e"||p=="SCAN ListItems"||p=="SCAN Templates"),string.Join("\n",plan));
    }
    [Fact]
    public async Task SearchConcurrentCanonicalWriterCannotCommitInsideProjectionTransaction()
    {
        var task=await JournalTasks().CreateAsync(profile,new("Concurrent original"));
        var repository=new SqliteSearchRepository();var context=new WorkspaceContext(profile,paths.WorkspaceDatabase(profile));
        var attempted=false;
        await repository.SearchAsync(context,"Concurrent",false,p=>
        {
            if(!attempted)
            {
                attempted=true;
                using var other=new SqliteConnection($"Data Source={context.DatabasePath};Pooling=False;Foreign Keys=True");other.Open();
                using var write=other.CreateCommand();write.CommandTimeout=1;write.CommandText="UPDATE WorkspaceItems SET Title='Concurrent replacement' WHERE Id=$id";write.Parameters.AddWithValue("$id",task.Item.Id.ToString("D"));
                Assert.Equal(5,Assert.Throws<SqliteException>(()=>write.ExecuteNonQuery()).SqliteErrorCode);
            }
            return p.Body;
        },default);
        Assert.True(attempted);
        await Sql($"UPDATE WorkspaceItems SET Title='Concurrent replacement' WHERE Id='{task.Item.Id}'");
        Assert.Empty((await Search("original")).Results);Assert.Single((await Search("replacement")).Results);
    }
    [Fact]
    public async Task SearchNewInvalidationBetweenCommittedBatchesIsNeverLost()
    {
        for(var i=0;i<65;i++)await JournalTasks().CreateAsync(profile,new("Queued "+i));
        var repository=new SqliteSearchRepository();var context=new WorkspaceContext(profile,paths.WorkspaceDatabase(profile));var seen=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.SearchAsync(context,"Queued",false,p=>++seen==65?throw new InvalidOperationException():p.Body,default));
        var first=(string)(await Sql("SELECT SourceId FROM SearchDocuments ORDER BY SourceId LIMIT 1"))!;
        await Sql($"UPDATE WorkspaceItems SET Title='Later committed' WHERE Id='{first}'");
        Assert.Equal(2L,await Sql("SELECT count(*) FROM SearchDirtyDocuments"));
        Assert.Single((await Search("Later committed")).Results);Assert.Equal(65L,await Sql("SELECT count(*) FROM SearchDocuments"));
        Assert.Equal(0L,await Sql("SELECT Rebuilding FROM SearchIndexState"));
    }
    [Fact]
    public void SearchAccessibleResultUsesReadableDateAndLifecycleWithoutGuidNoise()
    {
        var row=new SearchResult(new(profile,SearchKind.JournalEntry,Guid.NewGuid(),Guid.NewGuid(),Day),"Journal entry","Reflection","Reflection","Thoughts",true);
        Assert.Contains("Archived",row.Detail);Assert.Contains(Day.ToString("d",System.Globalization.CultureInfo.CurrentCulture),row.Detail);
        Assert.DoesNotContain(profile.ToString(),row.ToString());Assert.Contains("Thoughts",row.ToString());
    }
    [Fact]
    public async Task SearchMigrationPreservesPopulatedPhaseTwentyOneAndRollsBackFailure()
    {
        var legacy = await PopulatedPhaseTwenty();
        var migration19 = PersonalWorkspace.Data.Migrations.PageTemplateMigration.Definition;
        await Sql(migration19.Sql + "INSERT INTO SchemaMigrations VALUES(19,'Create page templates','original');",legacy);
        var source=(await Pages().GetAsync(profile)).Pages.Values.First(p=>p.Item.Title=="Training");
        var template=await Capture(source.Item,TemplateType.Page,"Legacy dashboard");
        using(var c=await Connection(legacy))
        {
            using(var cmd=c.CreateCommand()){cmd.CommandText="ATTACH DATABASE $path AS fixture";cmd.Parameters.AddWithValue("$path",paths.WorkspaceDatabase(profile));await cmd.ExecuteNonQueryAsync();}
            var tables=new List<string>();
            using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND (name LIKE 'PageTemplate%' OR name='TemplateAssetReferences') ORDER BY rowid";using var r=await cmd.ExecuteReaderAsync();while(await r.ReadAsync())tables.Add(r.GetString(0));}
            using(var cmd=c.CreateCommand()){cmd.CommandText=$"INSERT INTO Templates SELECT * FROM fixture.Templates WHERE Id='{template.Metadata.Id}'";await cmd.ExecuteNonQueryAsync();}
            foreach(var table in tables){using var cmd=c.CreateCommand();cmd.CommandText=$"INSERT INTO main.{table} SELECT * FROM fixture.{table}";await cmd.ExecuteNonQueryAsync();}
        }
        var before=await PageTemplateDatabase(legacy);
        await Sql("CREATE TABLE SearchIndexState(Conflict INTEGER)",legacy);
        await Assert.ThrowsAnyAsync<Exception>(()=>initializer.InitializeAsync(legacy,false));
        Assert.Equal(19L,await Sql("SELECT count(*) FROM SchemaMigrations",legacy));
        await Sql("DROP TABLE SearchIndexState",legacy);
        await initializer.InitializeAsync(legacy,false);await initializer.InitializeAsync(legacy,false);
        foreach(var (table,rows) in before.Where(p=>p.Key!="SchemaMigrations"))Assert.Equal(rows,await TableRows(legacy,table));
        Assert.Null(await Sql("PRAGMA foreign_key_check",legacy));
        var response=await new SqliteSearchRepository().SearchAsync(new(legacy,paths.WorkspaceDatabase(legacy)),"Legacy dashboard",false,p=>p.Format is null?p.Body:RichTextFormat.Extract(new(p.SourceId,p.Body,p.Format,p.FormatVersion,default,default)),default);
        Assert.Equal(template.Metadata.Id,Assert.Single(response.Results).Target.SourceId);
    }
    [Fact]
    public async Task SearchExactArchivedPageAndListRoutesIgnorePreviousFilters()
    {
        var nav=new NavigationService();var page=await Page("Archived page");await Page("Other page");await Pages().ApplyAsync(PRef(page),PageAction.Archive);
        var pages=PageModel(nav);nav.Navigate(await SearchService().ResolveAsync(Assert.Single((await Search("Archived page",true)).Results).Target,true));await pages.ReloadAsync();
        Assert.Equal(page.Item.Id,pages.Detail!.Item.Id);
        var list=await List("Archived list");var item=await ListAdd(list,"Passport");await List("Other list");await Lists().ApplyAsync(LRef(list),ListAction.Archive);
        var lists=ListModel(nav);lists.Filter=ListFilter.Checked;lists.Search="hidden";
        nav.Navigate(await SearchService().ResolveAsync(Assert.Single((await Search("Passport",true)).Results).Target,true));await lists.ReloadAsync();
        Assert.Equal(list.Item.Id,lists.DetailRow!.Definition.Item.Id);Assert.True(Assert.Single(lists.Items).SearchMatch);
        await Lists().ApplyAsync(LRef(list),ListAction.Trash);await lists.ReloadAsync();Assert.Null(lists.DetailRow);Assert.NotNull(lists.Error);
    }
    [Fact]
    public void SearchMigrationNineteenHashIsFrozen()
    {
        var sql = PersonalWorkspace.Data.Migrations.PageTemplateMigration.Definition.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));
        Assert.Equal("AD8F0C203C57A7B44753260E2B057498E21590B532CA89FAD84352B890404BA2", hash);
    }
    [Theory]
    [InlineData(OrganizationKind.Tag)][InlineData(OrganizationKind.Space)]
    public async Task SearchOrganizationAssignmentRenameAndRemovalFanout(OrganizationKind kind)
    {
        var task = await JournalTasks().CreateAsync(profile, new("Parent"));
        var child = await JournalTasks().CreateSubtaskAsync(new(profile, task.Item.Id), "Child");
        var id = await organization.SaveAsync(profile, kind, null, new("Research"));
        foreach (var item in new[] { task, child }) await organization.AssignAsync(new(profile,item.Item.Id),kind,id,true);
        Assert.Equal(2,(await Search("Research")).Results.Count);
        await organization.SaveAsync(profile,kind,id,new("Exploration"));
        Assert.Empty((await Search("Research")).Results); Assert.Equal(2,(await Search("Exploration")).Results.Count);
        await organization.AssignAsync(new(profile,child.Item.Id),kind,id,false);
        Assert.Equal(task.Item.Id,Assert.Single((await Search("Exploration")).Results).Target.SourceId);
        if(kind==OrganizationKind.Tag) { await organization.DeleteTagAsync(profile,id); Assert.Empty((await Search("Exploration")).Results); }
    }
    [Fact]
    public async Task SearchTracksEventTrackerAndIndependentTemplateCatalog()
    {
        var tracker = await Create(new("Weight",new(TrackerValueType.CustomUnit,"stones"),new()));
        Assert.Equal(tracker.Item.Id, Assert.Single((await Search("stones")).Results).Target.SourceId);
        var ev = await WidgetEvents().CreateAsync(profile,new("Conference",true,Day,null,Day,null));
        Assert.Equal("Event",(await SearchService().ResolveAsync(Assert.Single((await Search("Conference")).Results).Target,false)).Destination);
        var template = await Capture((await List("Packing")).Item,TemplateType.List,"Reusable passport");
        var row = Assert.Single((await Search("Reusable")).Results); Assert.Equal(SearchKind.Template,row.Target.Kind);
        Assert.Equal(template.Metadata.Id.ToString("D"),(await SearchService().ResolveAsync(row.Target,false)).EntityId);
        await Templates().RenameAsync(TRef(template),"Blueprint","Journey");
        Assert.Empty((await Search("Reusable")).Results); Assert.Single((await Search("Journey")).Results);
        await Templates().DeleteAsync(TRef(template)); Assert.Empty((await Search("Journey")).Results);
    }
    [Fact]
    public async Task SearchSharedRichTextFanoutAndContentCascade()
    {
        var a = await CanvasPage(); var b = await CanvasPage(); var widget = await ContentWidget(a); var doc = await WidgetDocument(a,widget);
        var shared = await Rich().AddReferenceAsync(PRef(b),Key(doc.Document.Id)); var host = await Block(b); var second = Guid.NewGuid();
        await Sql($"INSERT INTO WidgetInstances VALUES('{second}','{host.Id}',7,NULL,0,'2026-10-21','2026-10-21'); INSERT INTO WidgetRichTextSettings VALUES('{second}','{shared.Id}');");
        await Rich().UpdateAsync(Key(doc.Reference.Id),"# Shared\n\n**Expedition** [notes](https://example.org)");
        var hits=(await Search("Expedition")).Results; Assert.Equal(2,hits.Count); Assert.Equal(new[]{a.Item.Id,b.Item.Id}.Order(),hits.Select(r=>r.Target.OwnerId!.Value).Order());
        foreach(var row in hits) { var route=await SearchService().ResolveAsync(row.Target,false);Assert.Equal("Pages",route.Destination);Assert.Equal(row.Target.SourceId,route.Context!.ChildId); }
        await Rich().UpdateAsync(Key(doc.Reference.Id),"Changed content"); Assert.Empty((await Search("Expedition")).Results);Assert.Equal(2,(await Search("Changed")).Results.Count);
        await WEdit(a,new RemoveWidget(widget.Id)); Assert.Equal(second,Assert.Single((await Search("Changed")).Results).Target.SourceId);
        await Pages().ApplyAsync(PRef(b),PageAction.Trash);await Pages().DeleteAsync(PRef(b));Assert.Empty((await Search("Changed")).Results);
        Assert.Null(await Sql("PRAGMA foreign_key_check"));
    }
    [Fact]
    public async Task SearchSharedAssetFilenamesDoNotRetainOwnership()
    {
        var a=await CanvasPage();var b=await CanvasPage();var widget=await ContentWidget(a,WidgetType.Attachment);var asset=await WidgetAsset(a,widget);
        var reference=await Assets().AddReferenceAsync(PRef(b),Key(asset.Asset.Id));var host=await Block(b);var second=Guid.NewGuid();
        await Sql($"INSERT INTO WidgetInstances VALUES('{second}','{host.Id}',9,NULL,0,'2026-10-21','2026-10-21'); INSERT INTO WidgetAssetSettings VALUES('{second}','{reference.Id}',0,'');");
        Assert.Equal(2,(await Search("notes")).Results.Count);
        await WEdit(a,new RemoveWidget(widget.Id));Assert.Single((await Search("notes")).Results);Assert.True(File.Exists(AssetPath(asset.Asset)));
        await WEdit(b,new RemoveWidget(second));Assert.Empty((await Search("notes")).Results);
        await Assets().CleanupAsync(profile);Assert.False(File.Exists(AssetPath(asset.Asset)));
        Assert.Equal(0L,await Sql("SELECT count(*) FROM Assets"));
    }
    [Fact]
    public async Task SearchInterruptedBatchAndExternalFtsDamageRecover()
    {
        for(var i=0;i<70;i++)await JournalTasks().CreateAsync(profile,new("Batch "+i));
        var repo=new SqliteSearchRepository();var context=new WorkspaceContext(profile,paths.WorkspaceDatabase(profile));var seen=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SearchAsync(context,"batch",false,p=>++seen==65?throw new InvalidOperationException():p.Body,default));
        Assert.Equal(64L,await Sql("SELECT count(*) FROM SearchDocuments"));Assert.Equal(6L,await Sql("SELECT count(*) FROM SearchDirtyDocuments"));Assert.Equal(1L,await Sql("SELECT Rebuilding FROM SearchIndexState"));
        Assert.True((await Search("batch")).HasMore);Assert.Equal(70L,await Sql("SELECT count(*) FROM SearchDocuments"));
        await Sql("INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rowid,Title,Metadata,Body) SELECT 'delete',Id,Title,Metadata,Body FROM SearchDocuments LIMIT 1;");
        Assert.True((await Search("batch")).HasMore);
        Assert.Null(await Sql("INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rank) VALUES('integrity-check',1)"));
    }
    [Fact]
    public async Task SearchTitlesBodiesAccentsRankingAndCanonicalReadOnly()
    {
        var a = await JournalTasks().CreateAsync(profile, new("Café", "Tea"));
        var b = await JournalTasks().CreateAsync(profile, new("Other", "cafe"));
        var before = await TableRows(profile, "WorkspaceItems");
        var result = await Search("CAFE");
        Assert.Equal(new[] { a.Item.Id, b.Item.Id }, result.Results.Select(r => r.Target.SourceId));
        Assert.Equal(before, await TableRows(profile, "WorkspaceItems"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM TaskOccurrences"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM JournalEntries"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM SearchDirtyDocuments"));
        Assert.Null(await Sql("INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rank) VALUES('integrity-check',1)"));
    }
    [Theory]
    [InlineData("'")][InlineData("\"")][InlineData("*")][InlineData("NEAR(x)")][InlineData("x' OR 1=1 --")][InlineData("Title:*")][InlineData("100%_")][InlineData("")]
    public async Task SearchTreatsInputAsText(string input)
    {
        await JournalTasks().CreateAsync(profile, new("Unrelated"));
        Assert.Empty((await Search(input)).Results);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM Tasks"));
    }
    [Fact]
    public async Task SearchBoundsAndOrderingAreDeterministic()
    {
        for (var i = 0; i < 56; i++) await JournalTasks().CreateAsync(profile, new("Bounded"));
        var a = await Search("bound"); var b = await Search("bound");
        Assert.True(a.HasMore); Assert.Equal(50, a.Results.Count); Assert.Equal(a.Results, b.Results);
    }
    [Fact]
    public async Task SearchTaskEditsLifecycleAndDeletionSynchronize()
    {
        var task = await JournalTasks().CreateAsync(profile, new("Original")); var reference = new TaskReference(profile, task.Item.Id);
        Assert.Single((await Search("Original")).Results);
        await JournalTasks().UpdateAsync(reference, new("Replacement"));
        Assert.Empty((await Search("Original")).Results); Assert.Single((await Search("Replacement")).Results);
        await JournalTasks().ApplyAsync(reference, TaskAction.Archive);
        Assert.Empty((await Search("Replacement")).Results); Assert.Single((await Search("Replacement", true)).Results);
        await JournalTasks().ApplyAsync(reference, TaskAction.Trash);
        Assert.Empty((await Search("Replacement", true)).Results);
        await JournalTasks().ApplyAsync(reference, TaskAction.RestoreTrash);
        await JournalTasks().ApplyAsync(reference, TaskAction.RestoreArchive);
        var row = Assert.Single((await Search("Replacement")).Results);
        await JournalTasks().ApplyAsync(reference, TaskAction.Trash); await JournalTasks().PermanentlyDeleteAsync(reference);
        await Assert.ThrowsAsync<SearchException>(() => SearchService().ResolveAsync(row.Target, false));
        Assert.Empty((await Search("Replacement")).Results);
    }
    [Fact]
    public async Task SearchListItemKeepsExactParentAndChildAndNotesLinks()
    {
        var list = await Lists().CreateAsync(profile, new("Travel"));
        var item = await Lists().AddAsync(new(profile, list.Item.Id), new("Passport", Note: "Embassy", Link: "https://example.org/visa"));
        foreach (var query in new[] { "passport", "embassy", "visa" })
        {
            var row = Assert.Single((await Search(query)).Results);
            Assert.Equal(item.Id, row.Target.SourceId); Assert.Equal(list.Item.Id, row.Target.OwnerId);
            var route = await SearchService().ResolveAsync(row.Target, false);
            Assert.Equal("Lists", route.Destination); Assert.Equal(list.Item.Id.ToString("D"), route.EntityId); Assert.Equal(item.Id, route.Context!.ChildId);
        }
        await Lists().UpdateItemAsync(new(profile, list.Item.Id, item.Id), new("Tickets"));
        Assert.Empty((await Search("Embassy")).Results);
        await Lists().DeleteItemAsync(new(profile, list.Item.Id, item.Id)); Assert.Empty((await Search("Tickets")).Results);
    }
    [Fact]
    public async Task SearchHistoricalJournalCorrectionFieldAndOptionFanout()
    {
        var journal = await Journal("Reflection"); var text = await Field(journal, JournalFieldType.LongText, "Thoughts");
        var entry = await JSave(journal, text, new(Text: "Passport renewal"), Day.AddDays(-100));
        var row = Assert.Single((await Search("renewal")).Results); Assert.Equal(entry!.Id, row.Target.SourceId);
        Assert.Equal(Day.AddDays(-100), row.Target.EntryDate);
        Assert.Equal($"{journal.Item.Id:D}|{Day.AddDays(-100):yyyy-MM-dd}", (await SearchService().ResolveAsync(row.Target, false)).EntityId);
        await JSave(journal, text, new(Text: "Corrected"), Day.AddDays(-100)); Assert.Empty((await Search("renewal")).Results);
        await Journals().SaveFieldAsync(JRef(journal), text.Id, new("Observations", JournalFieldType.LongText));
        Assert.Contains((await Search("Observations")).Results, r => r.Target.Kind == SearchKind.JournalEntry);
        var choice = await Field(journal, JournalFieldType.Select, "Feeling");
        await JSave(journal, choice, new(OptionIds: [choice.OrderedOptions[0].Id]), Day);
        await JSave(journal, choice, new(OptionIds: [choice.OrderedOptions[0].Id]), Day.AddDays(-1));
        await Search("Low");
        await Journals().SaveFieldAsync(JRef(journal), choice.Id, new("Feeling", JournalFieldType.Select, Options: choice.OrderedOptions.Select((o,i) => o with { Label = i == 0 ? "Serene" : o.Label }).ToArray()));
        Assert.Equal(2, (await Search("Serene")).Results.Count(r => r.Target.Kind == SearchKind.JournalEntry));
        Assert.Empty((await Search("Low")).Results);
        await Journals().DeleteFieldAsync(JRef(journal), choice.Id, true);
        Assert.Empty((await Search("Serene")).Results);
    }
    [Fact]
    public async Task SearchRollbackAndFailedProjectionRetainWork()
    {
        var task = await JournalTasks().CreateAsync(profile, new("Original")); await Search("Original");
        var generation = await Sql("SELECT Generation FROM SearchIndexState");
        await Sql($"BEGIN; UPDATE WorkspaceItems SET Title='Rolledback' WHERE Id='{task.Item.Id}'; ROLLBACK;");
        Assert.Equal(generation, await Sql("SELECT Generation FROM SearchIndexState"));
        Assert.Empty((await Search("Rolledback")).Results);
        await Sql($"UPDATE WorkspaceItems SET Title='Committed' WHERE Id='{task.Item.Id}'");
        var repo = new SqliteSearchRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SearchAsync(new(profile,paths.WorkspaceDatabase(profile)), "Committed", false, _ => throw new InvalidOperationException("Injected"), default));
        Assert.True(Convert.ToInt64(await Sql("SELECT count(*) FROM SearchDirtyDocuments")) > 0);
        Assert.Single((await Search("Committed")).Results);
    }
    [Fact]
    public async Task SearchRebuildRestartVersionAndWritesDuringRebuild()
    {
        await JournalTasks().CreateAsync(profile, new("Before")); await Search("Before");
        await SearchService().RebuildAsync(profile);
        Assert.Equal(1L, await Sql("SELECT Rebuilding FROM SearchIndexState"));
        await JournalTasks().CreateAsync(profile, new("After"));
        Assert.Single((await SearchService().SearchAsync(profile, "After", false)).Results);
        Assert.Single((await Search("Before")).Results);
        await Sql("UPDATE SearchIndexState SET Version=0; DELETE FROM SearchDocuments;");
        Assert.Single((await Search("Before")).Results);
        Assert.Equal(0L, await Sql("SELECT Rebuilding FROM SearchIndexState"));
        Assert.Null(await Sql("PRAGMA foreign_key_check"));
    }
    [Fact]
    public async Task SearchIsProfileScopedAndRejectsForeignTargets()
    {
        await JournalTasks().CreateAsync(profile, new("Secret")); var row = Assert.Single((await Search("Secret")).Results);
        var other = await profiles.CreateAsync("Other");
        Assert.Empty((await SearchService().SearchAsync(other.Id, "Secret", false)).Results);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => SearchService().ResolveAsync(row.Target, false));
        await profiles.SwitchAsync(profile); Assert.Single((await Search("Secret")).Results);
    }
}
