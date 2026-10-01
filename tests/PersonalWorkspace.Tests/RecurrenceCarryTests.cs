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

public sealed partial class RecurrenceTests
{
    private async Task<TaskItem> CarrySeries(bool deficit=true, bool surplus=false, TaskValue? value=null)
    {
        var task=await Value(value ?? new(TaskValueType.Number,20)); await Set(task);
        await recurrence.SetCarrySettingsAsync(Ref(task),deficit,surplus); return task;
    }
    private Task<OccurrenceItem> ReadOccurrence(OccurrenceItem item)=>recurrence.FindAsync(ORef(item));
    private static OccurrenceCalculation Calc(OccurrenceItem item)=>Assert.IsType<OccurrenceCalculation>(item.Occurrence.Calculation);

    [Fact]
    public async Task CarryMigrationBackfillsPhaseSevenWithoutChangingExecution()
    {
        var legacy=Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);",legacy);
        foreach(var migration in WorkspaceMigrationCatalog.All.Take(6))
            await Sql(migration.Sql+$"INSERT INTO SchemaMigrations VALUES ({migration.Version},'{migration.Name}','original');",legacy);
        var context=new WorkspaceContext(legacy,paths.WorkspaceDatabase(legacy)); var repository=new SqliteTaskRepository();
        var identities=new List<(Guid Task,Guid Occurrence)>();
        foreach(var type in Enum.GetValues<TaskValueType>())
        {
            var task=new TaskItem(new(Guid.NewGuid(),WorkspaceItemType.Task,type.ToString(),clock.GetUtcNow(),clock.GetUtcNow(),null,null),"",TaskStatus.ToDo,TaskPriority.None,null,
                Value:type==TaskValueType.Checkbox?null:new TaskValue(type,20,CurrencyCode:"EUR",Unit:"pages").Validate());
            await repository.CreateAsync(context,task,default); var segment=Guid.NewGuid(); var occurrence=Guid.NewGuid(); identities.Add((task.Item.Id,occurrence));
            await Sql($"INSERT INTO TaskRecurrenceRules VALUES ('{segment}','{task.Item.Id}',0,'2026-10-01',1,0,1,1,1,NULL,'2026-10-01',NULL,1,'2026-10-01T00:00:00+00:00');"+
                $"INSERT INTO TaskOccurrences VALUES ('{occurrence}','{task.Item.Id}','{segment}','2026-10-01','2026-10-02',1,{(type==TaskValueType.Checkbox?"NULL":"'18'")},0,1,0,'2026-10-01T00:00:00+00:00','2026-10-01T01:00:00+00:00');",legacy);
        }
        await initializer.InitializeAsync(legacy,false); await initializer.InitializeAsync(legacy,false);
        Assert.Equal(9L,await Sql("SELECT COUNT(*) FROM SchemaMigrations;",legacy)); Assert.Equal(6L,await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';",legacy));
        Assert.Equal(5L,await Sql("SELECT COUNT(*) FROM TaskOccurrenceCalculations WHERE BaseTarget='20' AND CarryIn='0' AND EffectiveTarget='20' AND CarryOut='0';",legacy));
        Assert.Equal(4L,await Sql("SELECT COUNT(*) FROM TaskCarrySettings WHERE CarryDeficit=0 AND CarrySurplus=0 AND HistoryLocked=1;",legacy));
        foreach(var (task,id) in identities)
        {
            Assert.Equal(1L,await Sql($"SELECT COUNT(*) FROM TaskOccurrences WHERE Id='{id}' AND TaskId='{task}' AND Status=1 AND OccurrenceDate='2026-10-02' AND UpdatedAtUtc='2026-10-01T01:00:00+00:00';",legacy));
        }
        Assert.Equal(5L,await Sql("SELECT COUNT(*) FROM TaskOccurrences WHERE Actual='18';",legacy));
        await using var global=await new SqliteConnectionFactory(paths).OpenAsync(); using var command=global.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TaskCarrySettings','TaskOccurrenceCalculations');"; Assert.Equal(0L,await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false,false,18,20)] [InlineData(true,false,18,22)] [InlineData(false,true,25,15)] [InlineData(true,true,25,15)]
    [InlineData(false,true,18,20)] [InlineData(true,false,25,20)]
    public async Task CarryPoliciesAreIndependent(bool deficit,bool surplus,int actual,int next)
    {
        await CarrySeries(deficit,surplus); var items=await Range(); await Edit(items[0],actual:actual);
        Assert.Equal(next,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        Assert.Equal(actual,(await ReadOccurrence(items[0])).Occurrence.Actual);
    }

    [Fact]
    public async Task CarryRetroactiveFourDayChainCompletesAndReopensWithoutChangingActualOrIdentity()
    {
        var task=await CarrySeries(); var items=await Range();
        await Edit(items[0],actual:18); await Edit(items[1],actual:19); await Edit(items[2],actual:21);
        var initial=await Range(); Assert.Equal(new[]{20m,22m,23m,22m},initial.Take(4).Select(i=>Calc(i).EffectiveTarget));
        Assert.Equal(new[]{2m,3m,2m,0m},initial.Take(4).Select(i=>Calc(i).CarryOut));
        await Edit(items[0],actual:20); var corrected=await Range();
        Assert.Equal(new[]{20m,20m,21m,20m},corrected.Take(4).Select(i=>Calc(i).EffectiveTarget));
        Assert.Equal(TaskStatus.Done,corrected[2].Occurrence.Status); Assert.Equal(21m,corrected[2].Occurrence.Actual);
        await Edit(items[0],actual:18); Assert.Equal(TaskStatus.ToDo,(await ReadOccurrence(items[2])).Occurrence.Status);
        await Edit(items[0],actual:null); Assert.Equal(20m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        Assert.Equal(items.Select(i=>i.Occurrence.Id),(await Range()).Select(i=>i.Occurrence.Id));
        Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status); Assert.Null((await Read(task))!.Value!.Actual);
    }

    [Fact]
    public async Task CarryPrimaryCorrectionUpdatesAllStoredDownstreamResults()
    {
        await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:20);
        Assert.Equal(22m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
        await Edit(items[0],actual:20);
        Assert.Equal(new(20,0,20,0),Calc(await ReadOccurrence(items[1]))); Assert.Equal(new(20,0,20,0),Calc(await ReadOccurrence(items[2])));
        Assert.Equal(TaskStatus.Done,(await ReadOccurrence(items[1])).Occurrence.Status); Assert.Equal(20m,(await ReadOccurrence(items[1])).Occurrence.Actual);
        await Edit(items[0],actual:18); Assert.Equal(TaskStatus.ToDo,(await ReadOccurrence(items[1])).Occurrence.Status);
        await Edit(items[1],actual:22); Assert.Equal(20m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
    }

    [Fact]
    public async Task CarrySurplusUsesEffectiveTargetAndDiscardsExcessAfterOneExecution()
    {
        await CarrySeries(true,true); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:25);
        Assert.Equal(new(20,2,22,-3),Calc(await ReadOccurrence(items[1]))); Assert.Equal(17m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
        await Edit(items[0],actual:50); await Edit(items[1],actual:null);
        var covered=await ReadOccurrence(items[1]); Assert.Equal(new(20,-20,0,0),Calc(covered)); Assert.Null(covered.Occurrence.Actual);
        Assert.Equal(TaskStatus.Done,covered.Occurrence.Status); Assert.Equal(100m,covered.Value!.Percent); Assert.Equal(20m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
        await Edit(covered,actual:5); Assert.Equal(15m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
    }

    [Fact]
    public async Task CarryNullIsNotZeroAndStopsUntilAResultIsRecorded()
    {
        await CarrySeries(); var items=await Range(); Assert.Equal(20m,Calc(items[1]).EffectiveTarget);
        await Edit(items[0],actual:18); Assert.Equal(22m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        Assert.Equal(20m,Calc(await ReadOccurrence(items[2])).EffectiveTarget);
        await Edit(items[0],actual:0); Assert.Equal(40m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        await Edit(items[0],actual:null); Assert.Equal(20m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
    }

    [Theory]
    [InlineData(TaskValueType.Number)] [InlineData(TaskValueType.Currency)] [InlineData(TaskValueType.CustomUnit)] [InlineData(TaskValueType.Duration)]
    public async Task CarryExactValuesAndWholeSecondsRoundTrip(TaskValueType type)
    {
        var basis=type==TaskValueType.Duration?1800m:1.234567890123456789012345678m;
        var actual=type==TaskValueType.Duration?784m:.000000000000000000000000001m;
        await CarrySeries(value:new(type,basis,CurrencyCode:"EUR",Unit:"km")); var items=await Range(); await Edit(items[0],actual:actual);
        var expected=ExactDecimal.Subtract(basis,actual); var next=Calc(await ReadOccurrence(items[1])); Assert.Equal(expected,next.CarryIn);
        Assert.Equal(ExactDecimal.Add(basis,expected),next.EffectiveTarget);
        Assert.Equal("text",await Sql($"SELECT typeof(EffectiveTarget) FROM TaskOccurrenceCalculations WHERE OccurrenceId='{items[1].Occurrence.Id}';"));
        if(type==TaskValueType.Duration)await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(items[0],actual:.5m));
    }

    [Fact]
    public async Task CarryEligibilityDefaultsAndPolicyHistoryLockAreEnforced()
    {
        var normal=await Value(new(TaskValueType.Number,20)); await Assert.ThrowsAsync<TaskValidationException>(()=>recurrence.SetCarrySettingsAsync(Ref(normal),true,false));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskCarrySettings;"));
        var checkbox=await Create(); await Set(checkbox); var percentage=await Value(new(TaskValueType.Percentage,80)); await Set(percentage);
        foreach(var task in new[]{checkbox,percentage})await Assert.ThrowsAsync<TaskValidationException>(()=>recurrence.SetCarrySettingsAsync(Ref(task),true,true));
        await Set(normal); Assert.Equal(new(),await recurrence.GetCarrySettingsAsync(Ref(normal)));
        await recurrence.SetCarrySettingsAsync(Ref(normal),false,true); await recurrence.SetCarrySettingsAsync(Ref(normal),true,false);
        var first=(await Range()).Single(i=>i.Definition.Item.Id==normal.Item.Id && i.Occurrence.SlotDate==Start);
        await Edit(first,actual:18); await Edit(first,actual:null);
        Assert.True((await recurrence.GetCarrySettingsAsync(Ref(normal))).Locked);
        await Assert.ThrowsAsync<TaskValidationException>(()=>recurrence.SetCarrySettingsAsync(Ref(normal),true,true));
        await recurrence.SetCarrySettingsAsync(Ref(normal),true,false); // Re-saving the same policy is harmless.
        await Assert.ThrowsAsync<TaskValidationException>(()=>Configure(normal,new(TaskValueType.Number,30)));
    }

    [Fact]
    public async Task CarrySkipsPassThroughAndUnskipRecalculatesTheChain()
    {
        await CarrySeries(true,true); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:50,skip:true); await Edit(items[2],skip:true);
        Assert.Equal(new(20,0,0,0),Calc(await ReadOccurrence(items[1]))); Assert.Equal(22m,Calc(await ReadOccurrence(items[3])).EffectiveTarget);
        await Edit(items[1],actual:null,skip:false); Assert.Equal(20m,Calc(await ReadOccurrence(items[3])).EffectiveTarget);
        await Edit(items[1],actual:20,skip:false); Assert.Equal(22m,Calc(await ReadOccurrence(items[3])).EffectiveTarget);
        await profiles.RestoreAsync(); Assert.True((await ReadOccurrence(items[2])).Occurrence.IsSkipped); Assert.Equal(6,(await Range()).Count);
    }

    [Fact]
    public async Task CarryMovesDoNotReorderNodesAndMovedCorrectionsStillPropagate()
    {
        await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18); var before=Calc(await ReadOccurrence(items[1]));
        await recurrence.MoveAsync(ORef(items[0]),Start.AddDays(4)); Assert.Equal(before,Calc(await ReadOccurrence(items[1])));
        var moved=await ReadOccurrence(items[0]); Assert.Equal(Start,moved.Occurrence.SlotDate); Assert.Equal(Start.AddDays(4),moved.Occurrence.OccurrenceDate);
        await Edit(moved,actual:20); Assert.Equal(20m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        Assert.Equal(7L,await Sql("SELECT COUNT(*) FROM TaskOccurrenceCalculations;"));
    }

    [Fact]
    public async Task CarryCrossesSplitAndEntireSeriesReconciliationWithoutLosingHistory()
    {
        var task=await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18);
        await Set(task,new(RecurrencePattern.Daily,Start.AddDays(1),2),RecurrenceScope.ThisAndFuture,items[1].Occurrence.Id);
        Assert.Equal(22m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
        await Edit(items[1],actual:20); Assert.Equal(22m,Calc(await ReadOccurrence(items[3])).EffectiveTarget);
        var previous=(await ReadOccurrence(items[0])).Occurrence;
        clock.Now=Start.AddDays(1); await Set(task,new(RecurrencePattern.Weekly,Start,Weekdays:2));
        var after=await Range(); Assert.Equal(previous,(await ReadOccurrence(items[0])).Occurrence);
        Assert.Equal(22m,Calc(after.Single(i=>i.Occurrence.SlotDate==new DateOnly(2026,10,5))).EffectiveTarget);
        Assert.Equal(20m,(await ReadOccurrence(items[1])).Occurrence.Actual);
    }

    [Fact]
    public async Task CarryMaterializationUsesRelevantPredecessorAndUnmaterializedGapsResetIt()
    {
        await CarrySeries(); var first=Assert.Single(await Range(Start,Start)); await Edit(first,actual:18);
        var third=Assert.Single(await Range(Start.AddDays(2),Start.AddDays(2))); Assert.Equal(20m,Calc(third).EffectiveTarget);
        var second=Assert.Single(await Range(Start.AddDays(1),Start.AddDays(1))); Assert.Equal(22m,Calc(second).EffectiveTarget);
        await Edit(second,actual:20); Assert.Equal(22m,Calc(await ReadOccurrence(third)).EffectiveTarget);
        var far=Assert.Single(await Range(new(2099,1,1),new(2099,1,1))); Assert.Equal(20m,Calc(far).EffectiveTarget);
        Assert.Equal(4L,await Sql("SELECT COUNT(*) FROM TaskOccurrences;"));
        await Edit(first,actual:20); Assert.Equal(20m,Calc(await ReadOccurrence(third)).EffectiveTarget);
        Assert.Equal(far.Occurrence,(await ReadOccurrence(far)).Occurrence); // Propagation stops before an unaffected far suffix.
    }

    [Fact]
    public async Task CarryGuardsAffectCompletionButNeverTheArithmetic()
    {
        var task=await CarrySeries(true,true); var blocker=await Create(); var child=await Child(task); await tasks.AddDependencyAsync(Ref(task),blocker.Item.Id);
        var items=await Range(); await Edit(items[0],actual:18); Assert.Equal(2m,Calc(await ReadOccurrence(items[0])).CarryOut);
        await Edit(items[0],actual:50); var covered=await ReadOccurrence(items[1]); Assert.Equal(0m,Calc(covered).EffectiveTarget); Assert.Equal(TaskStatus.ToDo,covered.Occurrence.Status);
        await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(covered,TaskStatus.Done));
        await Status(blocker); await Status(child); await Edit(covered,actual:null);
        Assert.Equal(TaskStatus.Done,(await ReadOccurrence(covered)).Occurrence.Status); Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status);
    }

    [Theory]
    [InlineData(TaskAction.Archive,TaskAction.RestoreArchive)] [InlineData(TaskAction.Trash,TaskAction.RestoreTrash)]
    public async Task CarryLifecycleRemovalDuplicationAndCascadePreserveHistory(TaskAction hide,TaskAction restore)
    {
        var task=await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:20);
        var before=(await Range()).Select(i=>i.Occurrence).ToArray(); await tasks.ApplyAsync(Ref(task),hide); Assert.Empty(await Range());
        await tasks.ApplyAsync(Ref(task),restore); Assert.Equal(before,(await Range()).Select(i=>i.Occurrence));
        var duplicate=await tasks.DuplicateAsync(Ref(task)); Assert.False(duplicate.IsRecurring); Assert.False(duplicate.HasOccurrences);
        Assert.Equal(new(),await recurrence.GetCarrySettingsAsync(Ref(duplicate)));
        await recurrence.RemoveAsync(Ref(task)); Assert.Empty(await Range());
        Assert.Equal(before[0].Calculation,Calc(await ReadOccurrence(items[0]))); Assert.Equal(before[1].Calculation,Calc(await ReadOccurrence(items[1])));
        Assert.Null((await Read(task))!.Value!.Actual);
        await tasks.ApplyAsync(Ref(task),TaskAction.Trash); await tasks.PermanentlyDeleteAsync(Ref(task));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskOccurrenceCalculations;")); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskCarrySettings;"));
    }

    [Fact]
    public async Task CarryRollbackKeepsActualAndAllDownstreamStateAtomic()
    {
        await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:20); var before=await Range();
        await Sql($"CREATE TRIGGER FailCarry BEFORE UPDATE ON TaskOccurrenceCalculations WHEN NEW.OccurrenceId='{items[2].Occurrence.Id}' BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(()=>Edit(items[0],actual:20)); Assert.Equal(before,await Range());
    }

    [Fact]
    public async Task CarryDisabledDirectionDoesNotCalculateAnUnrepresentableUnusedDifference()
    {
        await CarrySeries(false,true,new(TaskValueType.Number,decimal.MaxValue)); var first=Assert.Single(await Range(Start,Start));
        await Edit(first,actual:.1m); Assert.Equal(0m,Calc(await ReadOccurrence(first)).CarryOut);
        Assert.Equal(.1m,(await ReadOccurrence(first)).Occurrence.Actual);
    }
    [Fact]
    public async Task CarryOverflowOrPrecisionLossRejectsTheWholeEdit()
    {
        Assert.Throws<TaskValidationException>(()=>ExactDecimal.Add(decimal.MaxValue,.1m));
        Assert.Throws<TaskValidationException>(()=>ExactDecimal.Add(decimal.MaxValue,1m));
        await CarrySeries(value:new(TaskValueType.Duration,TaskValue.MaximumDurationSeconds)); var items=await Range();
        await Assert.ThrowsAsync<TaskValidationException>(()=>Edit(items[0],actual:0)); Assert.Equal(items,await Range());
    }

    [Fact]
    public async Task CarryProfileIsolationRestartAndPresentationUseEffectiveTargets()
    {
        var task=await CarrySeries(); var items=await Range(); await Edit(items[0],actual:18); await Edit(items[1],actual:18); clock.Now=Start.AddDays(1);
        var navigation=new NavigationService(); navigation.Navigate(new("Today"));
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization,recurrence,clock);
        await model.ReloadAsync(); Assert.Contains("82%",Assert.Single(model.Rows).ValueText); Assert.Contains("22",model.Rows.Single().ValueText);
        navigation.Navigate(new("Occurrence",items[1].Occurrence.Id.ToString("D"))); await model.ReloadAsync();
        Assert.Equal("20",model.OccurrenceBaseTarget); Assert.Equal("22",model.OccurrenceEffectiveTarget); Assert.Contains("2",model.OccurrenceIncoming);
        var other=await profiles.CreateAsync("Carry other"); await model.ReloadAsync(); Assert.Empty(model.OccurrenceEffectiveTarget);
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskCarrySettings;",other.Id)); Assert.Empty(await recurrence.GetRangeAsync(other.Id,Start,Start.AddDays(4)));
        await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Edit(items[0],actual:20)); await profiles.SwitchAsync(profile); await profiles.RestoreAsync();
        Assert.True((await recurrence.GetCarrySettingsAsync(Ref(task))).Deficit); Assert.Equal(22m,Calc(await ReadOccurrence(items[1])).EffectiveTarget);
    }

    [Fact]
    public async Task CarryMaterializationDoesNotReevaluateUnchangedHistoricalCompletion()
    {
        var task=await CarrySeries(); var blocker=await Create(); await Status(blocker); await tasks.AddDependencyAsync(Ref(task),blocker.Item.Id);
        await Range(Start,Start); var third=Assert.Single(await Range(Start.AddDays(2),Start.AddDays(2))); await Edit(third,actual:20);
        await Status(blocker,TaskStatus.ToDo);
        await Range(Start,Start.AddDays(4));
        Assert.Equal(TaskStatus.Done,(await ReadOccurrence(third)).Occurrence.Status);
    }
    [Theory]
    [InlineData("move")] [InlineData("skip")] [InlineData("status")]
    public async Task CarryPolicyLocksAfterManualHistoryEvenWithoutActual(string action)
    {
        var task=await CarrySeries(false,false); var first=(await Range())[0];
        if(action=="move")await recurrence.MoveAsync(ORef(first),Start.AddDays(1));
        else await Edit(first,action=="status"?TaskStatus.Doing:TaskStatus.ToDo,skip:action=="skip");
        await Assert.ThrowsAsync<TaskValidationException>(()=>recurrence.SetCarrySettingsAsync(Ref(task),true,false));
        Assert.True((await recurrence.GetCarrySettingsAsync(Ref(task))).Locked);
    }
    [Fact]
    public void CarryConceptualSlotLookupMatchesBoundedGenerationAcrossPatterns()
    {
        var rules=new[]{new RecurrenceRule(RecurrencePattern.Daily,Start,3),new(RecurrencePattern.Weekly,Start,2,18),
            new(RecurrencePattern.MonthlyDay,Start,MonthDay:31),new(RecurrencePattern.MonthlyWeekday,Start,Ordinal:-1,Weekday:DayOfWeek.Friday),
            new(RecurrencePattern.MonthlyWeekday,Start,Ordinal:5,Weekday:DayOfWeek.Monday),new(RecurrencePattern.MonthlyDay,new(2024,2,1),12,MonthDay:29)};
        foreach(var rule in rules)
            for(var i=0;i<24;i++)
            {
                var from=Start.AddMonths(i).AddDays(7); var through=from.AddDays(90); var segment=new RecurrenceSegment(Guid.NewGuid(),Guid.NewGuid(),rule,rule.StartDate,null,true,clock.GetUtcNow());
                Assert.Equal(rule.Dates(from,through).Select(d=>(DateOnly?)d).FirstOrDefault(),RecurrenceSchedule.Next(segment,from,through));
            }
    }
}
