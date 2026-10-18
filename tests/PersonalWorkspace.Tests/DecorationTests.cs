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
    private DecorationService Decorations(IDecorationRepository? repository = null) => new(repository ?? new SqliteDecorationRepository(), current, gate, clock, NullLogger<DecorationService>.Instance);
    private Task<DecorationSnapshot> DRead(PageItem page) => Decorations().GetAsync(PRef(page));
    private Task<DecorationSnapshot> DEdit(PageItem page, DecorationEdit edit) => Decorations().EditAsync(PRef(page), edit);
    private async Task<CanvasDecoration> Shape(PageItem page, DecorationKind kind = DecorationKind.Rectangle) => (await DEdit(page, new CreateDecoration(kind, new(), new(24, 24, 320, 200)))).Items.Last();
    private async Task<CanvasDecoration> Connector(PageItem page, CanvasItem target) => (await DEdit(page, new CreateDecoration(DecorationKind.Connector, new(), Start: new(new(0, 0), target.Id, DecorationAnchor.Right), End: new(new(600, 300))))).Items.Last();

    [Fact]
    public async Task DecorationMigrationPreservesPopulatedPhaseEighteenWorkspaceAndAssets()
    {
        var page=await CanvasPage();var container=await Block(page,512,512,kind:CanvasKind.Container);var host=await Block(page,8,8,container.Id);
        await WAdd(page,(await JournalTasks().CreateAsync(profile,new("Keep task"))).Item,host:host.Id);
        var tracker=await Create();await WAdd(page,tracker.Item,WidgetType.TrackerChart,WidgetMode.Chart,chart:new(WidgetChartKind.Area,AnalyticsPreset.NinetyDays));
        await ListWidget(page,await List(),filter:ListFilter.Unchecked);
        var rich=await ContentWidget(page);var doc=await WidgetDocument(page,rich);await Rich().UpdateAsync(Key(doc.Reference.Id),"**Keep rich content**");
        await ContentWidget(page,WidgetType.Image);await ContentWidget(page,WidgetType.Attachment);await Edit(page,new ZoomCanvas(125));
        var legacy=Guid.NewGuid();new ProfileFiles(paths).Create(legacy);await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);",legacy);
        foreach(var m in WorkspaceMigrationCatalog.All.Take(16))await Sql(m.Sql+$"INSERT INTO SchemaMigrations VALUES({m.Version},'{m.Name}','original');",legacy);
        var tables=new List<string>();
        await using(var c=await Connection(legacy))
        {
            using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name<>'SchemaMigrations' ORDER BY rowid";using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())tables.Add(reader.GetString(0));}
            using var attach=c.CreateCommand();attach.CommandText="ATTACH DATABASE $path AS fixture";attach.Parameters.AddWithValue("$path",paths.WorkspaceDatabase(profile));await attach.ExecuteNonQueryAsync();
            foreach(var table in tables){using var cmd=c.CreateCommand();cmd.CommandText=$"INSERT INTO main.\"{table}\" SELECT * FROM fixture.\"{table}\" WHERE true"+(table=="WidgetListSettings"?" ON CONFLICT(WidgetId) DO UPDATE SET ItemFilter=excluded.ItemFilter":"");await cmd.ExecuteNonQueryAsync();}
        }
        var snapshots=new Dictionary<string,string>();foreach(var table in tables){snapshots[table]=await TableRows(legacy,table);Assert.Equal(await TableRows(profile,table),snapshots[table]);}
        var files=new Dictionary<string,byte[]>();foreach(var source in StoredFiles()){var relative=Path.GetRelativePath(paths.ProfileAttachments(profile),source);var target=Path.Combine(paths.ProfileAttachments(legacy),relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target);files[target]=await File.ReadAllBytesAsync(source);}
        await initializer.InitializeAsync(legacy,false);await initializer.InitializeAsync(legacy,false);
        foreach(var table in tables)Assert.Equal(snapshots[table],await TableRows(legacy,table));foreach(var (file,bytes) in files)Assert.Equal(bytes,await File.ReadAllBytesAsync(file));
        Assert.Equal(17L,await Sql("SELECT COUNT(*) FROM SchemaMigrations;",legacy));Assert.Equal(16L,await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';",legacy));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM CanvasDecorations;",legacy));Assert.Null(await Sql("PRAGMA foreign_key_check;",legacy));
    }

    [Theory]
    [InlineData(DecorationKind.Rectangle)][InlineData(DecorationKind.RoundedRectangle)][InlineData(DecorationKind.Ellipse)]
    public async Task DecorationsOverlapWithoutChangingNormalCanvasAndRoundTrip(DecorationKind kind)
    {
        var page = await CanvasPage(); var block = await Block(page); var normal = await Layout(page);
        var a = await Shape(page, kind); var b = await Shape(page, kind);
        Assert.Equal(a.Bounds,b.Bounds); Assert.NotEqual(a.Id,b.Id); Assert.Equal(normal.Items,(await Layout(page)).Items);
        await DEdit(page,new MoveDecoration(a.Id,17,9)); await DEdit(page,new ResizeDecoration(a.Id,new(40,32,400,240)));
        var style = new DecorationStyle(DecorationColor.Danger,5,true,true); await DEdit(page,new ChangeDecorationStyle(a.Id,style));
        var loaded=(await DRead(page)).Items.Single(i=>i.Id==a.Id); Assert.Equal(style,loaded.Style);Assert.Equal(new CanvasRect(40,32,400,240),loaded.Bounds);Assert.Equal(a.CreatedAtUtc,loaded.CreatedAtUtc);
        var duplicate=(await DEdit(page,new DuplicateDecoration(a.Id))).Items.Last();Assert.NotEqual(a.Id,duplicate.Id);Assert.Equal(style,duplicate.Style);Assert.Equal(new CanvasRect(56,48,400,240),duplicate.Bounds);
        await DEdit(page,new ReorderDecoration(a.Id,true));Assert.Equal(a.Id,(await DRead(page)).Items.Last().Id);
        await DEdit(page,new ReorderDecoration(a.Id,false));Assert.Equal(a.Id,(await DRead(page)).Items.First().Id);
        var other=await Block(page,400,24);await Assert.ThrowsAsync<CanvasValidationException>(()=>Edit(page,new MoveCanvasItems([other.Id],-376,0)));
        await DEdit(page,new DeleteDecoration(b.Id));Assert.Equal(0L,await Sql($"SELECT COUNT(*) FROM CanvasShapeSettings WHERE DecorationId='{b.Id}';"));
    }
    [Theory]
    [InlineData(DecorationKind.Line)][InlineData(DecorationKind.Arrow)][InlineData(DecorationKind.Connector)]
    public async Task DecorationFreeEndpointsMoveEditAndRemainStable(DecorationKind kind)
    {
        var page=await CanvasPage();var line=Assert.Single((await DEdit(page,new CreateDecoration(kind,new(),Start:new(new(16,24)),End:new(new(200,300))))).Items);
        await DEdit(page,new MoveDecoration(line.Id,16,8));await DEdit(page,new ChangeDecorationEndpoint(line.Id,false,new(new(400,400))));
        var loaded=Assert.Single((await DRead(page)).Items);Assert.Equal(new DecorationPoint(32,32),loaded.Start!.Point);Assert.Equal(new DecorationPoint(400,400),loaded.End!.Point);Assert.Equal(line.Id,loaded.Id);
        Assert.True(DecorationRules.Hit(loaded,(await Layout(page)),new(216,219),7));Assert.False(DecorationRules.Hit(loaded,(await Layout(page)),new(600,30),7));
    }
    [Theory]
    [InlineData(DecorationAnchor.Left,24,88)][InlineData(DecorationAnchor.Right,216,88)][InlineData(DecorationAnchor.Top,120,24)][InlineData(DecorationAnchor.Bottom,120,152)][InlineData(DecorationAnchor.Center,120,88)]
    public async Task ConnectorAnchorsResolveFromLogicalTargetGeometry(DecorationAnchor anchor,int x,int y)
    {
        var page=await CanvasPage();var target=await Block(page);var canvas=await Layout(page);
        Assert.Equal(new DecorationPoint(x,y),DecorationRules.Resolve(new(new(0,0),target.Id,anchor),canvas));
        await Edit(page,new MoveCanvasItems([target.Id],80,40));Assert.Equal(new DecorationPoint(x+80,y+40),DecorationRules.Resolve(new(new(0,0),target.Id,anchor),await Layout(page)));
    }
    [Fact]
    public async Task ConnectorFollowsContainerAndResizeAndTargetDeletionFreezesEndpoint()
    {
        var page=await CanvasPage();var container=await Block(page,512,512,kind:CanvasKind.Container);var target=await Block(page,8,8,container.Id);var connector=await Connector(page,target);
        await Edit(page,new MoveCanvasItems([container.Id],80,40));await Edit(page,new ResizeCanvasItem(target.Id,240,128));
        var snapshot=await DRead(page);var position=DecorationRules.Resolve(snapshot.Items.Single().Start!,snapshot.Canvas);Assert.Equal(new DecorationPoint(848,664),position);
        Assert.Equal(connector,(await DRead(page)).Items.Single());
        await Edit(page,new DeleteCanvasItems([target.Id]));var detached=Assert.Single((await DRead(page)).Items);Assert.Null(detached.Start!.Target);Assert.Equal(position,detached.Start.Point);Assert.Equal(connector.Id,detached.Id);Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task DeletingContainerKeepsChildAttachmentAndFreezesContainerAttachment()
    {
        var page=await CanvasPage();var container=await Block(page,512,512,kind:CanvasKind.Container);var child=await Block(page,8,8,container.Id);
        var a=await Connector(page,container);var b=await Connector(page,child);var before=await DRead(page);
        await Edit(page,new DeleteCanvasItems([container.Id]));var after=await DRead(page);
        Assert.Null(after.Items.Single(i=>i.Id==a.Id).Start!.Target);Assert.Equal(child.Id,after.Items.Single(i=>i.Id==b.Id).Start!.Target);
        foreach(var old in before.Items)Assert.Equal(DecorationRules.Resolve(old.Start!,before.Canvas),DecorationRules.Resolve(after.Items.Single(i=>i.Id==old.Id).Start!,after.Canvas));
    }
    [Fact]
    public async Task RemovingWidgetHostDetachesConnectorAndPreservesShape()
    {
        var page=await CanvasPage();var widget=await ContentWidget(page);var host=(await Layout(page)).Items.Single();var c=await Connector(page,host);var shape=await Shape(page);
        await WEdit(page,new RemoveWidget(widget.Id,true));var loaded=await DRead(page);Assert.Null(loaded.Items.Single(i=>i.Id==c.Id).Start!.Target);Assert.Contains(shape,loaded.Items);
    }
    [Fact]
    public async Task StrokeIsReducedVectorDataAndCommitsAtomically()
    {
        var page=await CanvasPage();DecorationPoint[] points=[new(10,10),new(10.2,10.2),new(11,11),new(11.1,11.2)];
        var stroke=Assert.Single((await DEdit(page,new CreateDecoration(DecorationKind.Stroke,new(DecorationColor.Success,3),Points:points))).Items);
        Assert.Equal(new[]{points[0],points[2],points[3]},stroke.Points);Assert.Equal(stroke.Points,(await DRead(page)).Items.Single().Points);
        await DEdit(page,new MoveDecoration(stroke.Id,8,16));Assert.Equal(new DecorationPoint(18,26),(await DRead(page)).Items.Single().Points![0]);
        await Sql("CREATE TRIGGER FailStroke BEFORE INSERT ON CanvasStrokePoints WHEN NEW.PointIndex=1 BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<DecorationOperationException>(()=>DEdit(page,new MoveDecoration(stroke.Id,8,0)));Assert.Equal(new DecorationPoint(18,26),(await DRead(page)).Items.Single().Points![0]);
        await DEdit(page,new DeleteDecoration(stroke.Id));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM CanvasStrokePoints;"));
    }
    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(-1)][InlineData(65537)]
    public async Task InvalidDecorationCoordinatesAreRejectedWithoutWrites(double x)
    {
        var page=await CanvasPage();await Assert.ThrowsAsync<DecorationValidationException>(()=>DEdit(page,new CreateDecoration(DecorationKind.Arrow,new(),Start:new(new(x,0)),End:new(new(20,20)))));Assert.Empty((await DRead(page)).Items);
    }
    [Fact]
    public async Task DecorationsValidateTypesBoundsPointLimitsAndCrossPageTargets()
    {
        var page=await CanvasPage();var other=await CanvasPage();var foreign=await Block(other);var shape=await Shape(page);
        DecorationEdit[] invalid=[new ResizeDecoration(shape.Id,new(0,0,8,24)),new ResizeDecoration(shape.Id,new(65528,0,32,32)),new ChangeDecorationStyle(shape.Id,new(Width:17)),new CreateDecoration(DecorationKind.Line,new(),Start:new(new(0,0),foreign.Id),End:new(new(0,0))),new CreateDecoration(DecorationKind.Connector,new(),Start:new(new(0,0),foreign.Id),End:new(new(0,0))),new CreateDecoration(DecorationKind.Stroke,new(),Points:[new(0,0)]),new CreateDecoration(DecorationKind.Stroke,new(),Points:Enumerable.Range(0,8193).Select(i=>new DecorationPoint(i,0)).ToArray())];
        foreach(var edit in invalid)await Assert.ThrowsAsync<DecorationValidationException>(()=>DEdit(page,edit));Assert.Equal(shape,Assert.Single((await DRead(page)).Items));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"INSERT INTO CanvasDecorationEndpoints VALUES('{shape.Id}',0,0,0,NULL,4);"));
        var own=await Block(page);var connector=await Connector(page,own);
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE CanvasDecorationEndpoints SET TargetCanvasItemId='{foreign.Id}' WHERE DecorationId='{connector.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE CanvasShapeSettings SET X=65536 WHERE DecorationId='{shape.Id}';"));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE CanvasDecorations SET DecorationType=6 WHERE Id='{shape.Id}';"));
    }
    [Fact]
    public async Task LockBlocksEveryDecorationMutationAndLifecyclePreservesUntilPageDeletion()
    {
        var page=await CanvasPage();var shape=await Shape(page);await Edit(page,new LockCanvas(true));
        DecorationEdit[] edits=[new CreateDecoration(DecorationKind.Rectangle,new(),new(0,0,32,32)),new MoveDecoration(shape.Id,8,8),new ResizeDecoration(shape.Id,new(0,0,64,64)),new ChangeDecorationStyle(shape.Id,new(Width:4)),new DeleteDecoration(shape.Id),new DuplicateDecoration(shape.Id),new ReorderDecoration(shape.Id,true),new ChangeDecorationEndpoint(shape.Id,true,new(new(0,0)))];
        foreach(var edit in edits)await Assert.ThrowsAsync<DecorationValidationException>(()=>DEdit(page,edit));Assert.Equal(shape,Assert.Single((await DRead(page)).Items));
        foreach(var action in new[]{PageAction.Archive,PageAction.RestoreArchive,PageAction.Trash,PageAction.RestoreTrash}){await Pages().ApplyAsync(PRef(page),action);Assert.Equal(shape,Assert.Single((await DRead(page)).Items));}
        var copy=await Pages().DuplicateAsync(PRef(page));Assert.Empty((await DRead(copy)).Items);
        await Pages().ApplyAsync(PRef(page),PageAction.Trash);await Pages().DeleteAsync(PRef(page));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM CanvasDecorations;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM CanvasShapeSettings;"));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task DecorationGesturesPreviewWithoutWritesCommitOnceAndCancelOnPageProfileChange()
    {
        var page=await CanvasPage();var spy=new DecorationSpy(Decorations());var vm=new DecorationWorkspaceViewModel(spy,current,NullLogger<DecorationWorkspaceViewModel>.Instance);await vm.OpenAsync(PRef(page));vm.SetTool(DecorationTool.Rectangle);Assert.True(vm.BeginGesture());
        for(var i=0;i<100;i++)vm.Preview(new CreateDecoration(DecorationKind.Rectangle,new(),new(i*8,0,64,64)));
        Assert.Equal(0,spy.Writes);Assert.Empty((await DRead(page)).Items);await vm.EndGestureAsync();Assert.Equal(1,spy.Writes);Assert.NotNull(vm.Selected);
        var id=vm.Selected!.Value;vm.BeginGesture();vm.Preview(new MoveDecoration(id,80,80));vm.CancelGesture();Assert.Equal(1,spy.Writes);Assert.Equal(new CanvasRect(792,0,64,64),vm.DisplayItems.Single().Bounds);
        vm.BeginGesture();vm.Preview(new MoveDecoration(id,8,8));await vm.OpenAsync(PRef(await CanvasPage()));Assert.False(vm.IsPreview);Assert.Null(vm.Selected);Assert.Empty(vm.DisplayItems);Assert.Equal(DecorationTool.Layout,vm.Tool);
        await vm.OpenAsync(PRef(page));await profiles.CreateAsync("Decoration isolation");Assert.Null(vm.Reference);Assert.Empty(vm.DisplayItems);await Assert.ThrowsAsync<WorkspaceChangedException>(()=>DRead(page));
    }
    [Fact]
    public async Task DecorationViewModelDetachesMissingTargetBeforeAsynchronousRefresh()
    {
        var page=await CanvasPage();var target=await Block(page);await Connector(page,target);var vm=new DecorationWorkspaceViewModel(Decorations(),current,NullLogger<DecorationWorkspaceViewModel>.Instance);await vm.OpenAsync(PRef(page));
        var before=DecorationRules.Resolve(vm.DisplayItems.Single().Start!,vm.Snapshot!.Canvas);var canvas=await Edit(page,new DeleteCanvasItems([target.Id]));vm.SetCanvas(canvas);
        Assert.Equal(before,DecorationRules.Resolve(vm.DisplayItems.Single().Start!,canvas));await vm.RefreshAsync();Assert.Equal(before,vm.DisplayItems.Single().Start!.Point);
    }
    [Fact]
    public async Task CanvasDeletionDetachesDecorationsBeforeWidgetNotificationsCanRender()
    {
        var page=await CanvasPage();var widget=await ContentWidget(page);var target=(await Layout(page)).Items.Single();await Connector(page,target);
        var drawing=new DecorationWorkspaceViewModel(Decorations(),current,NullLogger<DecorationWorkspaceViewModel>.Instance);
        var canvas=new CanvasWorkspaceViewModel(Canvas(),current,NullLogger<CanvasWorkspaceViewModel>.Instance,WidgetModel(),drawing);await canvas.OpenAsync(PRef(page));
        canvas.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(canvas.DisplayItems) && canvas.Snapshot is {} snapshot)foreach(var item in drawing.DisplayItems){if(item.Start is {} start)DecorationRules.Resolve(start,snapshot);if(item.End is {} end)DecorationRules.Resolve(end,snapshot);}};
        await canvas.EditAsync(new DeleteCanvasItems([target.Id]));Assert.Null(canvas.Error);Assert.Null(drawing.Error);Assert.Null(drawing.DisplayItems.Single().Start!.Target);Assert.False(canvas.IsBusy);Assert.True(drawing.CanEdit);
    }
    private sealed class DecorationSpy(IDecorationService inner):IDecorationService
    {
        public int Writes{get;private set;}
        public Task<DecorationSnapshot> GetAsync(WorkspaceItemReference page,CancellationToken token=default)=>inner.GetAsync(page,token);
        public Task<DecorationSnapshot> EditAsync(WorkspaceItemReference page,DecorationEdit edit,CancellationToken token=default){Writes++;return inner.EditAsync(page,edit,token);}
    }

    [Fact]
    public async Task DecorationLateReadAndSaveCannotRepopulateClosedPage()
    {
        var page=await CanvasPage();var snapshot=await DRead(page);var delayed=new DelayedDecorationService();var vm=new DecorationWorkspaceViewModel(delayed,current,NullLogger<DecorationWorkspaceViewModel>.Instance);
        var opening=vm.OpenAsync(PRef(page));vm.Clear();delayed.Read.SetResult(snapshot);await opening;Assert.Null(vm.Snapshot);Assert.Empty(vm.DisplayItems);
        await vm.OpenAsync(PRef(page));var saving=vm.EditAsync(new CreateDecoration(DecorationKind.Rectangle,new(),new(0,0,32,32)));vm.Clear();
        delayed.Save.SetResult(DecorationRules.Apply(snapshot,new CreateDecoration(DecorationKind.Rectangle,new(),new(0,0,32,32)),clock.GetUtcNow()));await saving;Assert.Null(vm.Snapshot);Assert.Empty(vm.DisplayItems);Assert.False(vm.IsBusy);
    }
    private sealed class DelayedDecorationService:IDecorationService
    {
        public TaskCompletionSource<DecorationSnapshot> Read {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DecorationSnapshot> Save {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DecorationSnapshot> GetAsync(WorkspaceItemReference page,CancellationToken token=default)=>Read.Task;
        public Task<DecorationSnapshot> EditAsync(WorkspaceItemReference page,DecorationEdit edit,CancellationToken token=default)=>Save.Task;
    }
}
