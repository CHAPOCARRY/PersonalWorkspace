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

public sealed partial class RecurrenceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly WorkspaceOperationGate gate = new();
    private readonly CurrentProfile current;
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly TaskService tasks;
    private readonly OrganizationService organization;
    private Guid profile;
    private readonly RecurrenceService recurrence;
    private readonly RecurrenceClock clock = new();
    public RecurrenceTests()
    {
        paths = new(root); current = new(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        tasks = new(new SqliteTaskRepository(), current, gate, clock, NullLogger<TaskService>.Instance);
        recurrence = new(new SqliteTaskRepository(),current,gate,clock,NullLogger<RecurrenceService>.Instance);
        organization = new(new SqliteOrganizationRepository(), current, gate, TimeProvider.System, NullLogger<OrganizationService>.Instance);
    }
    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profile = (await profiles.CreateAsync("Personal")).Id;
    }
    private TaskReference Ref(TaskItem item) => new(profile, item.Item.Id);
    private Task<TaskItem> Create(string title = "Task") => tasks.CreateAsync(profile, new(title));
    private Task<TaskItem> Child(TaskItem parent, string title = "Child") => tasks.CreateSubtaskAsync(Ref(parent), title);
    private Task<TaskItem> Status(TaskItem task, TaskStatus status = TaskStatus.Done) => tasks.ChangeStatusAsync(Ref(task), status);
    private Task<TaskItem?> Read(TaskItem task) => tasks.FindAsync(Ref(task));
    private async Task<TaskGraph> Graph() => await tasks.GetGraphAsync(profile);
    private async Task<object?> Sql(string sql, Guid? workspace = null)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(workspace ?? profile)};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync(); connection.CreateCollation("WORKSPACE_NAME", (a,b) => StringComparer.OrdinalIgnoreCase.Compare(a,b));
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private Task<TaskItem> Value(TaskValue value, string title = "Value task") => tasks.CreateAsync(profile, new(title, Value: value));
    private Task<TaskItem> Actual(TaskItem task, decimal? value) => tasks.RecordActualAsync(Ref(task), value);
    private async Task<TaskItem> Configure(TaskItem task, TaskValue? value)
    {
        var currentTask = (await Read(task))!;
        return await tasks.UpdateAsync(Ref(task), new(currentTask.Item.Title, currentTask.Description, currentTask.Status, currentTask.Priority, currentTask.ScheduledDate, value));
    }

    private static readonly DateOnly Start = new(2026,10,1);
    private Task Set(TaskItem task, RecurrenceRule? rule = null, RecurrenceScope scope = RecurrenceScope.EntireSeries, Guid? from = null) => recurrence.SetRuleAsync(Ref(task), rule ?? new(RecurrencePattern.Daily,Start),scope,from);
    private Task<IReadOnlyList<OccurrenceItem>> Range(DateOnly? from = null, DateOnly? through = null) => recurrence.GetRangeAsync(profile,from ?? Start,through ?? Start.AddDays(6));
    private OccurrenceReference ORef(OccurrenceItem item) => new(profile,item.Occurrence.Id);
    private Task<OccurrenceItem> Edit(OccurrenceItem item, TaskStatus status = TaskStatus.ToDo, decimal? actual = null, DateOnly? date = null, bool skip = false) => recurrence.UpdateAsync(ORef(item),date ?? item.Occurrence.OccurrenceDate,status,actual,skip);

    [Theory]
    [InlineData(1,7)] [InlineData(2,4)] [InlineData(3,3)]
    public async Task DailyIntervalsAreBoundedIdempotentAndStable(int interval,int count)
    {
        var task=await Create(); await Set(task,new(RecurrencePattern.Daily,Start,interval));
        var first=await Range(); Assert.Equal(count,first.Count); Assert.Equal(first,await Range());
        Assert.Single(await tasks.GetAsync(profile,TaskCollection.Active));
        Assert.Empty(await tasks.GetScheduledAsync(profile,Start,Start.AddDays(6)));
        Assert.Empty(await tasks.GetUnscheduledAsync(profile));
        var far=await Range(new(2099,1,1),new(2099,1,7)); Assert.InRange(far.Count,2,7);
        Assert.Equal(count+far.Count,(long)(await Sql("SELECT COUNT(*) FROM TaskOccurrences;"))!);
        Assert.All(first,item=>Assert.Equal(TimeSpan.Zero,item.Occurrence.CreatedAtUtc.Offset));
    }
    [Fact]
    public void WeeklyAnchorAndLowerBoundAreDeterministic()
    {
        var rule=new RecurrenceRule(RecurrencePattern.Weekly,new(2026,9,29),2,(1<<1)|(1<<4)); // Tuesday; Monday/Thursday.
        Assert.Equal(new[]{new DateOnly(2026,10,1),new(2026,10,12),new(2026,10,15)},rule.Dates(new(2026,9,28),new(2026,10,18)));
        var weekdays=rule with { Interval=1,Weekdays=(1<<1)|(1<<3)|(1<<5) };
        Assert.Equal(new[]{new DateOnly(2026,9,30),new(2026,10,2),new(2026,10,5)},weekdays.Dates(new(2026,9,29),new(2026,10,5)));
    }
    [Theory]
    [InlineData(15,2027,2,15)] [InlineData(31,2027,3,31)] [InlineData(29,2028,2,29)]
    public void MonthlyDaysSkipMissingDates(int day,int year,int month,int expectedDay)
    {
        var from=new DateOnly(year,2,1); var rule=new RecurrenceRule(RecurrencePattern.MonthlyDay,from,MonthDay:day);
        Assert.Equal(new DateOnly(year,month,expectedDay),rule.Dates(from,new(year,3,31)).First());
        Assert.Equal(new DateOnly(year+1,1,day),rule.Dates(new(year,12,1),new(year+1,1,31)).Last());
    }
    [Theory]
    [InlineData(1,DayOfWeek.Saturday,3)] [InlineData(2,DayOfWeek.Monday,12)] [InlineData(-1,DayOfWeek.Friday,30)]
    public void OrdinalWeekdaysFollowMonthShape(int ordinal,DayOfWeek weekday,int expected)
    {
        var rule=new RecurrenceRule(RecurrencePattern.MonthlyWeekday,Start,Ordinal:ordinal,Weekday:weekday);
        Assert.Equal(new DateOnly(2026,10,expected),Assert.Single(rule.Dates(Start,new(2026,10,31))));
        Assert.Single(rule.Dates(new(2027,2,1),new(2027,2,28)));
    }
    [Fact]
    public async Task EndDateAndBoundaryValidation()
    {
        var task=await Create(); await Set(task,new(RecurrencePattern.Daily,Start,EndDate:Start.AddDays(2)));
        Assert.Equal(3,(await Range()).Count);
        await Assert.ThrowsAsync<TaskValidationException>(()=>Set(task,new(RecurrencePattern.Weekly,Start)));
        await Assert.ThrowsAsync<TaskValidationException>(()=>Set(task,new(RecurrencePattern.Daily,Start,0)));
        await Assert.ThrowsAsync<TaskValidationException>(()=>Set(task,new(RecurrencePattern.Daily,Start,EndDate:Start.AddDays(-1))));
        await Assert.ThrowsAsync<TaskValidationException>(()=>Range(Start,Start.AddDays(366)));
        var last=new RecurrenceRule(RecurrencePattern.Daily,DateOnly.MaxValue); Assert.Single(last.Dates(DateOnly.MaxValue,DateOnly.MaxValue));
    }
    [Fact]
    public async Task IndependentCompletionActualSurplusAndReopeningNeverCarry()
    {
        var task=await Value(new(TaskValueType.Number,20)); await Set(task); var items=await Range();
        var first=await Edit(items[0],actual:18); Assert.Equal(TaskStatus.ToDo,first.Occurrence.Status);
        await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(first,TaskStatus.Done,18));
        first=await Edit(first,actual:25); Assert.Equal(TaskStatus.Done,first.Occurrence.Status); Assert.Equal(125m,first.Value!.Percent);
        var tomorrow=(await recurrence.FindAsync(ORef(items[1]))); Assert.Null(tomorrow.Value!.Actual); Assert.Equal(20m,tomorrow.Value.Target);
        first=await Edit(first,TaskStatus.Done,18); Assert.Equal(TaskStatus.ToDo,first.Occurrence.Status);
        Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status); Assert.Null((await Read(task))!.Value!.Actual);
        await Assert.ThrowsAsync<TaskValidationException>(()=>tasks.RecordActualAsync(Ref(task),20));
        await Assert.ThrowsAsync<TaskValidationException>(()=>tasks.ChangeStatusAsync(Ref(task),TaskStatus.Done));
        await Assert.ThrowsAsync<TaskValidationException>(()=>tasks.ScheduleAsync(Ref(task),Start));
        await Assert.ThrowsAsync<TaskValidationException>(()=>Configure(task,new(TaskValueType.Number,30)));
    }
    [Fact]
    public async Task MoveCollisionAndSkipKeepOriginalSlotsAcrossRestart()
    {
        var task=await Create(); await Set(task); var items=await Range(); var id=items[0].Occurrence.Id;
        await recurrence.MoveAsync(ORef(items[0]),Start.AddDays(1));
        var moved=await Range(); Assert.Equal(2,moved.Count(o=>o.Occurrence.OccurrenceDate==Start.AddDays(1)));
        Assert.DoesNotContain(moved,o=>o.Occurrence.OccurrenceDate==Start);
        Assert.Equal(Start,(await recurrence.FindAsync(new(profile,id))).Occurrence.SlotDate);
        await Edit(items[2],skip:true); Assert.Equal(6,(await Range()).Count);
        await profiles.RestoreAsync(); Assert.Equal(6,(await Range()).Count);
        Assert.True((await recurrence.FindAsync(ORef(items[2]))).Occurrence.IsSkipped);
        Assert.Equal(7L,await Sql("SELECT COUNT(*) FROM TaskOccurrences;"));
    }
    [Fact]
    public async Task ThisAndFutureSplitsPreservesPastAndReconcilesOnlyUnusedFuture()
    {
        var task=await Create(); await Set(task); var items=await Range(Start,Start.AddDays(20));
        var done=await Edit(items[0],TaskStatus.Done); var boundary=items[9];
        await Set(task,new(RecurrencePattern.Weekly,boundary.Occurrence.SlotDate,Weekdays:62),RecurrenceScope.ThisAndFuture,boundary.Occurrence.Id);
        var after=await Range(Start,Start.AddDays(20));
        Assert.Equal(done.Occurrence,(await recurrence.FindAsync(ORef(done))).Occurrence);
        Assert.Equal(items.Take(9).Select(x=>x.Occurrence.Id),after.Take(9).Select(x=>x.Occurrence.Id));
        Assert.All(after.Skip(9),o=>Assert.DoesNotContain(o.Occurrence.SlotDate.DayOfWeek,new[]{DayOfWeek.Saturday,DayOfWeek.Sunday}));
        Assert.Equal(after.Count,after.Select(o=>o.Occurrence.SlotDate).Distinct().Count());
        var rules=await recurrence.GetSegmentsAsync(Ref(task)); Assert.Equal(2,rules.Count); Assert.Equal(boundary.Occurrence.SlotDate,rules[0].Until);
    }
    [Fact]
    public async Task EntireSeriesPreservesPastAndExplicitFutureResultsAndRestoresUnusedIdentity()
    {
        var task=await Create(); await Set(task); var items=await Range(); await Edit(items[6],TaskStatus.Doing);
        clock.Now=Start.AddDays(2);
        await Set(task,new(RecurrencePattern.Daily,Start,3)); var changed=await Range();
        Assert.Contains(changed,o=>o.Occurrence.Id==items[0].Occurrence.Id); Assert.Contains(changed,o=>o.Occurrence.Id==items[1].Occurrence.Id);
        Assert.Contains(changed,o=>o.Occurrence.Id==items[6].Occurrence.Id);
        Assert.DoesNotContain(changed,o=>o.Occurrence.Id==items[4].Occurrence.Id);
        await Set(task,new(RecurrencePattern.Daily,Start));
        Assert.Contains(await Range(),o=>o.Occurrence.Id==items[4].Occurrence.Id);
        await Assert.ThrowsAsync<TaskValidationException>(()=>Set(task,new(RecurrencePattern.Daily,Start),RecurrenceScope.ThisAndFuture,items[0].Occurrence.Id));
    }
    [Fact]
    public async Task RemoveRecurrenceRetainsHistoryAndDoesNotMergeExecution()
    {
        var task=await Value(new(TaskValueType.Currency,1500,CurrencyCode:"EUR")); await Set(task); var item=(await Range())[0]; await Edit(item,actual:625.25m);
        await recurrence.RemoveAsync(Ref(task)); Assert.Empty(await Range()); var normal=(await Read(task))!;
        Assert.False(normal.IsRecurring); Assert.Null(normal.ScheduledDate); Assert.Null(normal.Value!.Actual); Assert.Equal(TaskStatus.ToDo,normal.Status);
        Assert.Equal(625.25m,Assert.Single(await recurrence.GetHistoryAsync(Ref(task),Start,Start.AddDays(6))).Value!.Actual);
        await Set(task); Assert.Equal(item.Occurrence.Id,(await Range())[0].Occurrence.Id);
    }
    [Theory]
    [InlineData(TaskAction.Archive,TaskAction.RestoreArchive)] [InlineData(TaskAction.Trash,TaskAction.RestoreTrash)]
    public async Task LifecycleHidesOccurrencesPreservesAndCascades(TaskAction hide,TaskAction restore)
    {
        var task=await Create(); await Set(task); var before=await Range(); await tasks.ApplyAsync(Ref(task),hide);
        Assert.Empty(await Range()); Assert.Equal(7L,await Sql("SELECT COUNT(*) FROM TaskOccurrences;"));
        await tasks.ApplyAsync(Ref(task),restore); Assert.Equal(before.Select(i=>i.Occurrence), (await Range()).Select(i=>i.Occurrence));
        await tasks.ApplyAsync(Ref(task),TaskAction.Trash); await tasks.PermanentlyDeleteAsync(Ref(task));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskOccurrences;")); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskRecurrenceRules;"));
    }
    [Fact]
    public async Task DefinitionChildrenAndDependenciesGuardOccurrencesWithoutTrees()
    {
        var parent=await Value(new(TaskValueType.Number,20)); var child=await Child(parent); var blocker=await Create();
        await tasks.AddDependencyAsync(Ref(parent),blocker.Item.Id); await Set(parent); var occurrence=(await Range())[0];
        Assert.Equal(TaskStatus.ToDo,(await Edit(occurrence,actual:20)).Occurrence.Status);
        await Status(child); await Status(blocker); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        Assert.Equal(TaskStatus.ToDo,(await recurrence.FindAsync(ORef(occurrence))).Occurrence.Status);
        Assert.Equal(TaskStatus.Done,(await Edit(occurrence,actual:20)).Occurrence.Status);
        Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status); Assert.All(await Range(),o=>Assert.Equal(parent.Item.Id,o.Definition.Item.Id));
    }
    [Fact]
    public async Task SplitAndOccurrenceFailureRollBackAtomically()
    {
        var task=await Create(); await Set(task); var items=await Range(); var segments=await recurrence.GetSegmentsAsync(Ref(task));
        await Sql("CREATE TRIGGER FailRule BEFORE INSERT ON TaskRecurrenceRules BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(()=>Set(task,new(RecurrencePattern.Daily,Start,2)));
        Assert.Equal(segments,await recurrence.GetSegmentsAsync(Ref(task))); Assert.Equal(items,await Range());
        await Sql("CREATE TRIGGER FailOccurrence BEFORE UPDATE ON TaskOccurrences BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(()=>Edit(items[0],TaskStatus.Done));
        Assert.Equal(items[0],await recurrence.FindAsync(ORef(items[0])));
    }
    [Fact]
    public async Task ProfileIsolationAndUniquenessRemainEnforced()
    {
        var task=await Create(); await Set(task); var items=await Range();
        await Assert.ThrowsAsync<SqliteException>(()=>Sql("INSERT INTO TaskOccurrences SELECT 'duplicate',TaskId,SegmentId,SlotDate,OccurrenceDate,Status,Actual,IsSkipped,IsOverride,IsSuppressed,CreatedAtUtc,UpdatedAtUtc FROM TaskOccurrences LIMIT 1;"));
        var other=await profiles.CreateAsync("Other"); Assert.Empty(await recurrence.GetRangeAsync(other.Id,Start,Start.AddDays(6)));
        await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Edit(items[0],TaskStatus.Done));
        await profiles.SwitchAsync(profile); Assert.Equal(items,await Range());
    }
    [Fact]
    public async Task PhaseSixUpgradePreservesDefinitionActualAndAddsNoHistoricalOccurrences()
    {
        var legacy=Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);",legacy);
        foreach(var migration in WorkspaceMigrationCatalog.All.Take(5))
            await Sql(migration.Sql+$"INSERT INTO SchemaMigrations VALUES ({migration.Version},'{migration.Name}','original');",legacy);
        var now=clock.GetUtcNow(); var task=new TaskItem(new(Guid.NewGuid(),WorkspaceItemType.Task,"Existing",now,now,null,null),"Details",TaskStatus.Done,TaskPriority.High,Start,Value:new(TaskValueType.Number,20,25));
        var context=new WorkspaceContext(legacy,paths.WorkspaceDatabase(legacy)); var repo=new SqliteTaskRepository();
        await repo.CreateAsync(context,task,default); await initializer.InitializeAsync(legacy,false); await initializer.InitializeAsync(legacy,false);
        Assert.Equal(task,await repo.FindAsync(context,task.Item.Id,default)); Assert.Equal(12L,await Sql("SELECT COUNT(*) FROM SchemaMigrations;",legacy));
        Assert.Equal(5L,await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';",legacy));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskOccurrences;",legacy)); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskRecurrenceRules;",legacy));
        await using var global=await new SqliteConnectionFactory(paths).OpenAsync(); using var command=global.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TaskOccurrences','TaskRecurrenceRules');";
        Assert.Equal(0L,await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task OccurrenceMaterializationDoesNotWriteTaskDefinitionOrTimestamp()
    {
        var task=await Create(); await Set(task); var before=(await Read(task))!;
        await Sql("CREATE TRIGGER NoDefinitionWrite BEFORE UPDATE ON Tasks BEGIN SELECT RAISE(ABORT,'unexpected'); END;");
        await Range(); var after=(await Read(task))!;
        Assert.Equal(before with { HasOccurrences=true },after);
    }
    [Fact]
    public async Task ReopeningCheckboxOccurrenceLeavesNeighborsAndDefinitionIndependent()
    {
        var task=await Create(); await Set(task); var items=await Range();
        var done=await Edit(items[0],TaskStatus.Done); var reopened=await Edit(done,TaskStatus.Doing);
        Assert.Equal(TaskStatus.Doing,reopened.Occurrence.Status);
        Assert.Equal(items[1],await recurrence.FindAsync(ORef(items[1]))); Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status);
    }
    [Theory]
    [InlineData(TaskValueType.Currency)] [InlineData(TaskValueType.Duration)] [InlineData(TaskValueType.CustomUnit)] [InlineData(TaskValueType.Percentage)]
    public async Task OtherValueTypesKeepExactIndependentOccurrenceActual(TaskValueType type)
    {
        var task=await Value(new(type,100,CurrencyCode:"EUR",Unit:"pages")); await Set(task); var items=await Range();
        var actual=type==TaskValueType.Duration?65m:65.12345678901234567890123456m;
        await Edit(items[0],actual:actual); Assert.Equal(actual,(await recurrence.FindAsync(ORef(items[0]))).Value!.Actual);
        Assert.Null((await recurrence.FindAsync(ORef(items[1]))).Value!.Actual);
        await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(items[0],actual:-1));
        if(type==TaskValueType.Percentage)await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(items[0],actual:101));
        if(type==TaskValueType.Duration)await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(items[0],actual:.5m));
    }
    [Fact]
    public async Task EnablingRequiresExplicitResultClearAndDuplicateHasNoRecurrenceOrHistory()
    {
        var task=await Value(new(TaskValueType.Number,20,18));
        await Assert.ThrowsAsync<TaskValidationException>(()=>Set(task)); Assert.Equal(18m,(await Read(task))!.Value!.Actual);
        await Actual(task,null); await Set(task); await Range(); var duplicate=await tasks.DuplicateAsync(Ref(task));
        Assert.False(duplicate.IsRecurring); Assert.False(duplicate.HasOccurrences); Assert.Null(duplicate.Value!.Actual);
        Assert.Empty(await recurrence.GetSegmentsAsync(Ref(duplicate)));
    }
    [Fact]
    public async Task FutureEditedMovedAndSkippedSlotsSurviveEntireSeriesChanges()
    {
        var task=await Create(); await Set(task); var items=await Range();
        await Edit(items[1],skip:true); await recurrence.MoveAsync(ORef(items[2]),Start.AddDays(10)); await Edit(items[3],TaskStatus.Done);
        await Set(task,new(RecurrencePattern.MonthlyDay,Start,MonthDay:31));
        Assert.True((await recurrence.FindAsync(ORef(items[1]))).Occurrence.IsSkipped);
        Assert.Equal(Start.AddDays(10),(await recurrence.FindAsync(ORef(items[2]))).Occurrence.OccurrenceDate);
        Assert.Equal(TaskStatus.Done,(await recurrence.FindAsync(ORef(items[3]))).Occurrence.Status);
        Assert.Single(await Range(Start.AddDays(10),Start.AddDays(10)));
        Assert.Empty(await Range(Start.AddDays(1),Start.AddDays(1)));
    }
    [Fact]
    public async Task UnmaterializedPastStillUsesOldSegmentAfterEntireSeriesEdit()
    {
        var task=await Create(); await Set(task); clock.Now=Start.AddDays(10);
        await Set(task,new(RecurrencePattern.MonthlyDay,Start,MonthDay:31));
        Assert.Equal(10,(await Range(Start,Start.AddDays(9))).Count);
        Assert.Single(await Range(Start.AddDays(10),new(2026,10,31)));
    }
    [Fact]
    public async Task ConcurrentMaterializationAndCancellationRemainAtomic()
    {
        var task=await Create(); await Set(task);
        var results=await Task.WhenAll(Range(),Range()); Assert.Equal(results[0],results[1]);
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>recurrence.SetRuleAsync(Ref(task),new(RecurrencePattern.Daily,Start,2),cancellationToken:cancellation.Token));
        Assert.Equal(results[0],await Range());
    }
    [Fact]
    public async Task CalendarTodayAndOccurrenceEditorUseLocalDateAndClearProfileState()
    {
        var task=await Value(new(TaskValueType.Number,20)); await Set(task);
        var scheduled=await tasks.CreateAsync(profile,new("One-off",ScheduledDate:Start));
        var eventService=new EventService(new SqliteEventRepository(),current,gate,clock,NullLogger<EventService>.Instance);
        await eventService.CreateAsync(profile,new("Event",true,Start,null,Start,null));
        var navigation=new NavigationService(); navigation.Navigate(new("Calendar"));
        var calendar=new CalendarViewModel(eventService,tasks,current,navigation,clock,NullLogger<CalendarViewModel>.Instance,recurrence) { Mode=CalendarMode.Day };
        await calendar.ReloadAsync(); var day=Assert.Single(calendar.Days); Assert.Equal(2,day.Tasks.Count()); Assert.Single(day.AllDayEvents);
        var entry=day.Tasks.Single(e=>e.Occurrence is not null); calendar.OpenCommand.Execute(entry);
        Assert.Equal("Occurrence",navigation.Current.Destination);
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization,recurrence,clock);
        await model.ReloadAsync(); Assert.True(model.IsOccurrence); model.OccurrenceValue.Actual="20"; await model.SaveOccurrenceCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(TaskStatus.Done,model.OccurrenceStatus);
        navigation.Navigate(new("Today")); await model.ReloadAsync(); Assert.Equal(2,model.Rows.Count());
        Assert.Equal(TaskStatus.Done,model.Rows.Single(r=>r.Occurrence is not null).ExecutionStatus);
        navigation.Navigate(new("Calendar")); await calendar.ReloadAsync();
        await calendar.MoveAsync(calendar.Days.Single().Tasks.Single(e=>e.Occurrence is not null),Start.AddDays(1));
        Assert.Single(calendar.Days.Single().Tasks); Assert.Equal(scheduled.Item.Id,calendar.Days.Single().Tasks.Single().Id);
        navigation.Navigate(new("Task",task.Item.Id.ToString("D"))); await model.ReloadAsync(); Assert.True(model.IsSeries); Assert.NotEmpty(model.OccurrenceRows);
        var other=await profiles.CreateAsync("Other"); await model.ReloadAsync(); Assert.False(model.IsOccurrence); Assert.Empty(model.OccurrenceRows);
        navigation.Navigate(new("Calendar")); await calendar.ReloadAsync(); Assert.Empty(calendar.Days.SelectMany(d=>d.Entries));
        await calendar.MoveAsync(entry,Start); Assert.NotNull(calendar.Error);
    }
    [Fact]
    public async Task LocalDayBoundaryControlsTodayCalendarAndHistoryProtection()
    {
        clock.Zone=TimeZoneInfo.CreateCustomTimeZone("Recurrence acceptance UTC+14",TimeSpan.FromHours(14),"UTC+14","UTC+14");
        var localToday=Start.AddDays(1); // The fixture's UTC clock is still noon on Start.
        var task=await Create(); await Set(task); var occurrences=await Range(Start,localToday);
        var navigation=new NavigationService(); navigation.Navigate(new("Today"));
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization,recurrence,clock);
        await model.ReloadAsync();
        Assert.Equal(occurrences[1].Occurrence.Id,Assert.Single(model.Rows).Occurrence!.Id);
        var events=new EventService(new SqliteEventRepository(),current,gate,clock,NullLogger<EventService>.Instance);
        navigation.Navigate(new("Calendar"));
        var calendar=new CalendarViewModel(events,tasks,current,navigation,clock,NullLogger<CalendarViewModel>.Instance,recurrence) { Mode=CalendarMode.Day };
        await calendar.ReloadAsync();
        Assert.Equal(localToday,Assert.Single(calendar.Days).Date);
        Assert.Equal(occurrences[1].Occurrence.Id,Assert.Single(calendar.Days.Single().Tasks).Id);
        await Set(task,new(RecurrencePattern.MonthlyDay,Start,MonthDay:31));
        Assert.Equal(occurrences[0].Occurrence,(await recurrence.FindAsync(ORef(occurrences[0]))).Occurrence);
        Assert.Empty(await Range(localToday,localToday));
    }
    [Fact]
    public async Task FailedOccurrenceNavigationClearsPreviousExecutionEditor()
    {
        var task=await Value(new(TaskValueType.Number,20)); await Set(task);
        var item=await Edit((await Range())[0],actual:18);
        var navigation=new NavigationService(); navigation.Navigate(new("Occurrence",item.Occurrence.Id.ToString("D")));
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization,recurrence,clock);
        await model.ReloadAsync(); Assert.Equal("Value task",model.OccurrenceTitle); Assert.Equal("18",model.OccurrenceValue.Actual);
        navigation.Navigate(new("Occurrence",Guid.NewGuid().ToString("D"))); await model.ReloadAsync();
        Assert.NotNull(model.Error); Assert.Empty(model.OccurrenceTitle); Assert.Empty(model.OccurrenceContext); Assert.Empty(model.OccurrenceValue.Actual);
        await model.SaveOccurrenceCommand.ExecuteAsync(null);
        Assert.Equal(item,await recurrence.FindAsync(ORef(item)));
    }
    [Fact]
    public void RecurrenceEditorRequiresExplicitStartAndUsesCurrentCulture()
    {
        var editor=new RecurrenceEditor(); Assert.Throws<TaskValidationException>(()=>editor.Build());
        editor.Load(new(RecurrencePattern.Weekly,Start,2,42,EndDate:Start.AddMonths(1)));
        Assert.Equal(new RecurrenceRule(RecurrencePattern.Weekly,Start,2,42,EndDate:Start.AddMonths(1)),editor.Build());
        Assert.Contains("Monday",RecurrenceEditor.Summary(editor.Build()));
    }
    public Task DisposeAsync()
    {
        profiles.Dispose(); gate.Dispose();
        var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"PersonalWorkspace.Tests"))+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(root).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if(Directory.Exists(root)) Directory.Delete(root,true); return Task.CompletedTask;
    }
    private sealed class RecurrenceClock : TimeProvider
    {
        public DateOnly Now {get;set;}=Start;
        public TimeZoneInfo Zone {get;set;}=TimeZoneInfo.Utc;
        public override TimeZoneInfo LocalTimeZone=>Zone;
        public override DateTimeOffset GetUtcNow()=>new(Now.ToDateTime(new(12,0)),TimeSpan.Zero);
    }
}
