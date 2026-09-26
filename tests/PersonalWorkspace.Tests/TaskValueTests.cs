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

public sealed class TaskValueTests : IAsyncLifetime
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
    public TaskValueTests()
    {
        paths = new(root); current = new(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        tasks = new(new SqliteTaskRepository(), current, gate, TimeProvider.System, NullLogger<TaskService>.Instance);
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

    [Fact]
    public async Task PhaseFiveUpgradePreservesCheckboxTasksAndRelationships()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(4))
            await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES ({migration.Version},'{migration.Name}','original');", legacy);
        var context = new WorkspaceContext(legacy, paths.WorkspaceDatabase(legacy)); var now = DateTimeOffset.UtcNow;
        var parent = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Parent", now, now, null, null), "", TaskStatus.Doing, TaskPriority.High, new(2026,9,26));
        var child = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Child", now, now, null, null), "", TaskStatus.ToDo, TaskPriority.None, null, parent.Item.Id);
        var repo = new SqliteTaskRepository(); await repo.CreateAsync(context, parent, default); await repo.CreateAsync(context, child, default);
        await Sql($"INSERT INTO TaskDependencies VALUES ('{parent.Item.Id:D}','{child.Item.Id:D}','{now:O}');", legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(5L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(parent, await repo.FindAsync(context, parent.Item.Id, default));
        Assert.Equal(child, await repo.FindAsync(context, child.Item.Id, default));
        Assert.Equal(TaskValueType.Checkbox, (await repo.FindAsync(context, child.Item.Id, default))!.ValueType);
        Assert.Single((await repo.GetGraphAsync(context, default)).Dependencies);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TaskValues;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync();
        using var command = global.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='TaskValues';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidTargetsAreRejected(int target) => await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Number, target)));

    [Fact]
    public async Task NumberNullBelowEqualAboveAndClearingActualPreserveResultsAndReopen()
    {
        var task = await Value(new(TaskValueType.Number, 20));
        Assert.Null(task.Value!.Actual); Assert.Null(task.Value.Ratio); Assert.Equal(0m, task.Value.VisualPercent);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(task));
        var below = await Actual(task, 18); Assert.Equal(.9m, below.Value!.Ratio); Assert.Equal(TaskStatus.ToDo, below.Status);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(task));
        var reached = await Actual(task, 20); Assert.Equal(TaskStatus.Done, reached.Status);
        Assert.True(reached.Item.UpdatedAtUtc > below.Item.UpdatedAtUtc);
        var reopened = await Actual(task, 18); Assert.Equal(TaskStatus.ToDo, reopened.Status);
        var surplus = await Actual(task, 25); Assert.Equal(25m, surplus.Value!.Actual); Assert.Equal(1.25m, surplus.Value.Ratio);
        Assert.Equal(125m, surplus.Value.Percent); Assert.Equal(100m, surplus.Value.VisualPercent); Assert.Equal(TaskStatus.Done, surplus.Status);
        Assert.Equal(surplus, await Read(task)); Assert.Equal(surplus, await Actual(task, 25)); // No-op timestamp.
        var cleared = await Actual(task, null); Assert.Null(cleared.Value!.Actual); Assert.Equal(TaskStatus.ToDo, cleared.Status);
        Assert.Null(await Sql($"SELECT Actual FROM TaskValues WHERE ItemId='{task.Item.Id:D}';") as string);
        await Assert.ThrowsAsync<TaskValidationException>(() => Actual(task, -1));
    }

    [Fact]
    public async Task PercentageIsRelativeToTargetAndBoundsAreEnforced()
    {
        var task = await Value(new(TaskValueType.Percentage, 80, 60));
        Assert.Equal(75m, task.Value!.Percent); Assert.Equal(TaskStatus.ToDo, task.Status);
        Assert.Equal(TaskStatus.Done, (await Actual(task, 100)).Status);
        await Assert.ThrowsAsync<TaskValidationException>(() => Actual(task, 101));
        await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Percentage, 101)));
        var editor = new TaskValueEditor { Type = TaskValueType.Percentage }; Assert.Equal(100m, editor.Build()!.Target);
    }

    [Fact]
    public async Task CurrencyUsesExactDecimalsAndCanonicalTextWithoutConversion()
    {
        var target = 12345678901234567890.123456789m;
        var task = await Value(new(TaskValueType.Currency, target, .1m + .2m, " eur ", "stale"));
        Assert.Equal(target, (await Read(task))!.Value!.Target); Assert.Equal(.3m, (await Read(task))!.Value!.Actual);
        Assert.Equal("EUR", task.Value!.CurrencyCode); Assert.Null(task.Value.Unit);
        Assert.Equal("text", await Sql($"SELECT typeof(Actual) FROM TaskValues WHERE ItemId='{task.Item.Id:D}';"));
        Assert.Equal("0.3", await Sql($"SELECT Actual FROM TaskValues WHERE ItemId='{task.Item.Id:D}';"));
        var changed = await Configure(task, task.Value with { CurrencyCode = "USD" });
        Assert.Equal(target, changed.Value!.Target); Assert.Equal(.3m, changed.Value.Actual); Assert.Equal("USD", changed.Value.CurrencyCode);
        Assert.True(changed.Item.UpdatedAtUtc > task.Item.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public async Task InvalidCurrencyCodesAreRejected(string? code) => await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Currency, 1, CurrencyCode: code)));

    [Theory]
    [InlineData("30:00", 1800)]
    [InlineData("13:04", 784)]
    [InlineData("1:15:30", 4530)]
    [InlineData("0:00", 0)]
    public void DurationParsingAndDisplayAreExact(string text, long seconds)
    {
        Assert.Equal(seconds, TaskValuePresentation.Parse(text, TaskValueType.Duration));
        Assert.Equal(text, TaskValuePresentation.Duration(seconds));
    }

    [Theory]
    [InlineData("30")]
    [InlineData("-1:00")]
    [InlineData("1:60")]
    [InlineData("1:60:00")]
    [InlineData("1:00.5")]
    [InlineData("99999999999999999999999:00")]
    public void InvalidOrAmbiguousDurationsAreRejected(string text) => Assert.Throws<TaskValidationException>(() => TaskValuePresentation.Parse(text, TaskValueType.Duration));

    [Fact]
    public async Task DurationStoresIntegerSecondsAndSupportsSurplus()
    {
        var task = await Value(new(TaskValueType.Duration, 1800, 784));
        Assert.Equal(task, await Read(task)); Assert.Equal(TaskStatus.ToDo, task.Status);
        Assert.Equal("integer", await Sql($"SELECT typeof(ActualSeconds) FROM TaskValues WHERE ItemId='{task.Item.Id:D}';"));
        Assert.Equal(784L, await Sql($"SELECT ActualSeconds FROM TaskValues WHERE ItemId='{task.Item.Id:D}';"));
        Assert.Equal(TaskStatus.Done, (await Actual(task, 1800)).Status);
        Assert.Equal(3601m, (await Actual(task, 3601)).Value!.Actual);
        Assert.Equal(TaskStatus.ToDo, (await Actual(task, 0)).Status);
        foreach(var invalid in new[] { 0m, -1m, .5m, TaskValue.MaximumDurationSeconds + 1m })
            await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Duration, invalid)));
        await Assert.ThrowsAsync<TaskValidationException>(() => Actual(task, 1.5m));
    }

    [Fact]
    public async Task CustomUnitSupportsDecimalValuesAndRequiresShortTrimmedLabel()
    {
        var task = await Value(new(TaskValueType.CustomUnit, 2.5m, 1.25m, "EUR", " km "));
        Assert.Equal("km", task.Value!.Unit); Assert.Null(task.Value.CurrencyCode); Assert.Equal(50m, task.Value.Percent);
        foreach(var unit in new[] { "", " ", new string('x',33), "bad\nunit" })
            await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.CustomUnit, 1, Unit: unit)));
    }

    [Fact]
    public async Task ManualDoneGuardAndTargetChangesCannotPersistContradictoryValues()
    {
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.CreateAsync(profile, new("Invalid", Status: TaskStatus.Done, Value: new(TaskValueType.Number, 20))));
        var task = await Value(new(TaskValueType.Number,20,20)); Assert.Equal(TaskStatus.Done, task.Status);
        var raised = await Configure(task, task.Value! with { Target = 30 }); Assert.Equal(TaskStatus.ToDo, raised.Status);
        var lowered = await Configure(task, raised.Value! with { Target = 10 }); Assert.Equal(TaskStatus.Done, lowered.Status);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(task, TaskStatus.ToDo));
        await Actual(task, null);
        await Assert.ThrowsAsync<TaskValidationException>(() => tasks.UpdateAsync(Ref(task), new("Still invalid", Status: TaskStatus.Done, Value: new(TaskValueType.Number,10))));
    }

    [Fact]
    public async Task NumericalParentRequiresOwnTargetAndEveryChildAndReopensAncestors()
    {
        var rootTask = await Create(); var parent = await Child(rootTask);
        await Configure(parent, new(TaskValueType.CustomUnit,10, Unit:"km")); var child = await Child(parent);
        await Actual(parent,10); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Status(child); Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status); Assert.Equal(TaskStatus.Done,(await Read(rootTask))!.Status);
        await Actual(parent,7); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status); Assert.Equal(TaskStatus.ToDo,(await Read(rootTask))!.Status);
        Assert.Equal(new TaskProgress(1,1),(await Graph()).Progress()[parent.Item.Id]);
        await Configure(child,new(TaskValueType.Number,20));
        await Actual(parent,10); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Actual(child,20); Assert.Equal(TaskStatus.Done,(await Read(rootTask))!.Status);
        await Actual(child,18); Assert.Equal(TaskStatus.ToDo,(await Read(child))!.Status); Assert.Equal(TaskStatus.ToDo,(await Read(rootTask))!.Status);
    }

    [Fact]
    public async Task CompleteChildrenDoNotCompleteParentBelowItsOwnTarget()
    {
        var parent = await Value(new(TaskValueType.Number,20,18)); var child = await Child(parent);
        await Status(child); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(parent));
        await Actual(parent,20); Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status);
    }

    [Fact]
    public async Task DependencyChangeDoesNotAutoCompleteNumericalLeafButExplicitUpdateCan()
    {
        var blocker = await Create(); var task = await Value(new(TaskValueType.Number,20));
        await tasks.AddDependencyAsync(Ref(task),blocker.Item.Id);
        await Actual(task,20); Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status);
        await Assert.ThrowsAsync<TaskValidationException>(() => Status(task));
        await Status(blocker); Assert.Equal(TaskStatus.ToDo,(await Read(task))!.Status);
        Assert.Equal(TaskStatus.Done,(await Actual(task,20)).Status);
        await Actual(task,18); await Assert.ThrowsAsync<TaskValidationException>(() => Status(task));
    }

    [Theory]
    [InlineData(TaskValueType.Number)]
    [InlineData(TaskValueType.Percentage)]
    [InlineData(TaskValueType.Currency)]
    [InlineData(TaskValueType.Duration)]
    [InlineData(TaskValueType.CustomUnit)]
    public async Task DuplicateCopiesDefinitionOnly(TaskValueType type)
    {
        var original = await Value(new(type,20,25,"EUR","pages")); var copy = await tasks.DuplicateAsync(Ref(original));
        Assert.Equal(original.Value! with { Actual = null }, copy.Value); Assert.Equal(TaskStatus.ToDo,copy.Status);
        Assert.Null(copy.ParentTaskId); Assert.NotEqual(original.Item.Id,copy.Item.Id);
    }

    [Fact]
    public async Task TypeChangesReplaceMetadataAndCheckboxDeletesValueRow()
    {
        var task = await Create(); await Configure(task,new(TaskValueType.Number,30,10));
        await Configure(task,new(TaskValueType.Currency,100,40,"EUR"));
        var duration = await Configure(task,new(TaskValueType.Duration,1800)); Assert.Null(duration.Value!.Actual); Assert.Null(duration.Value.CurrencyCode);
        Assert.Equal(1L,await Sql($"SELECT COUNT(*) FROM TaskValues WHERE ItemId='{task.Item.Id:D}' AND Target IS NULL AND Actual IS NULL AND CurrencyCode IS NULL AND Unit IS NULL;"));
        var checkbox = await Configure(task,null); Assert.Equal(TaskValueType.Checkbox,checkbox.ValueType); Assert.Null(checkbox.Value);
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskValues;")); await Status(task);
    }

    [Fact]
    public void EditorTypeSwitchNeverReinterpretsOrResurrectsPreviousValues()
    {
        var editor = new TaskValueEditor(); editor.Load(new(TaskValueType.Currency,30,10,"EUR"));
        editor.Type=TaskValueType.Duration; Assert.Equal("",editor.Target); Assert.Equal("",editor.Actual); Assert.Equal("",editor.Currency);
        Assert.Throws<TaskValidationException>(() => editor.Build()); editor.Target="30:00"; Assert.Equal(1800m,editor.Build()!.Target);
        editor.Type=TaskValueType.Checkbox; Assert.Null(editor.Build()); editor.Type=TaskValueType.Currency;
        Assert.Equal("",editor.Target); Assert.Equal("",editor.Actual);
    }

    [Theory]
    [InlineData(TaskAction.Archive,TaskAction.RestoreArchive)]
    [InlineData(TaskAction.Trash,TaskAction.RestoreTrash)]
    public async Task LifecyclePreservesDefinitionAndActual(TaskAction action,TaskAction restore)
    {
        var task=await Value(new(TaskValueType.CustomUnit,300,125,Unit:"pages"));
        Assert.Equal(task.Value,(await tasks.ApplyAsync(Ref(task),action)).Value);
        Assert.Equal(task.Value,(await tasks.ApplyAsync(Ref(task),restore)).Value);
        await tasks.ApplyAsync(Ref(task),TaskAction.Trash);
        await Assert.ThrowsAsync<TaskValidationException>(() => Actual(task,250));
        await tasks.PermanentlyDeleteAsync(Ref(task)); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM TaskValues;"));
    }

    [Fact]
    public async Task ActualAndParentStatusRollbackTogetherOnWriteFailure()
    {
        var parent=await Create(); var child=await Child(parent); await Configure(child,new(TaskValueType.Number,20,20));
        var original=(await Read(child))!; Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status);
        await Sql($"CREATE TRIGGER FailValue BEFORE UPDATE ON TaskValues WHEN NEW.ItemId='{child.Item.Id:D}' BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Actual(child,18));
        Assert.Equal(original,await Read(child)); Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status);
        await Sql("DROP TRIGGER FailValue;");
        await Sql($"CREATE TRIGGER FailParent BEFORE UPDATE ON Tasks WHEN NEW.ItemId='{parent.Item.Id:D}' BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Actual(child,18)); Assert.Equal(original,await Read(child));
    }

    [Fact]
    public async Task FailedValueInsertAndTypeConversionLeaveNoPartialChanges()
    {
        await Sql("CREATE TRIGGER FailValueInsert BEFORE INSERT ON TaskValues BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Value(new(TaskValueType.Number,20)));
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Tasks;")); Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
        await Sql("DROP TRIGGER FailValueInsert;");
        var task=await Value(new(TaskValueType.Currency,100,50,"EUR"));
        await Sql("CREATE TRIGGER FailValueDelete BEFORE DELETE ON TaskValues BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<TaskOperationException>(() => Configure(task,null)); Assert.Equal(task,await Read(task));
    }

    [Fact]
    public async Task NumericalParentRequiresValueChildrenAndDependenciesTogether()
    {
        var parent=await Value(new(TaskValueType.Number,10)); var child=await Child(parent); var blocker=await Create();
        await tasks.AddDependencyAsync(Ref(parent),blocker.Item.Id); await Status(child); await Status(blocker);
        Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Actual(parent,10); Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status);
        await Status(blocker,TaskStatus.ToDo); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Actual(parent,9); await Status(blocker); Assert.Equal(TaskStatus.ToDo,(await Read(parent))!.Status);
        await Actual(parent,10); Assert.Equal(TaskStatus.Done,(await Read(parent))!.Status);
    }

    [Fact]
    public async Task ExtremeDecimalValuesRemainExactAndUnrepresentableProgressFailsClearly()
    {
        var task=await Value(new(TaskValueType.Number,decimal.MaxValue,decimal.MaxValue));
        Assert.Equal(task,await Read(task)); Assert.Equal(1m,task.Value!.Ratio);
        var error=await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Number,.0000000000000000000000000001m,decimal.MaxValue)));
        Assert.Contains("progress",error.Message);
        await Assert.ThrowsAsync<TaskValidationException>(() => Value(new((TaskValueType)99,1)));
        await Assert.ThrowsAsync<TaskValidationException>(() => Value(new(TaskValueType.Checkbox,1)));
    }

    [Fact]
    public async Task EmptyProfileFiltersTolerateNativeSelectionContainerProbes()
    {
        var model = new TaskWorkspaceViewModel(tasks, current, new NavigationService(), NullLogger<TaskWorkspaceViewModel>.Instance, organization);
        void ProbeFilters()
        {
            // WinUI may search with a UI container rather than an OrganizationFilter.
            Assert.Equal(-1, ((System.Collections.IList)model.TagFilters).IndexOf(new object()));
            Assert.Equal(-1, ((System.Collections.IList)model.SpaceFilters).IndexOf(new object()));
        }
        ProbeFilters();
        await model.ReloadAsync();
        ProbeFilters();
        await profiles.CreateAsync("Empty profile");
        await model.ReloadAsync();
        ProbeFilters();
        await profiles.SwitchAsync(profile);
        await model.ReloadAsync();
        ProbeFilters();
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task ProfileSwitchClearsValuesAndRejectsStaleActualWrites()
    {
        var task=await Value(new(TaskValueType.Number,20,18)); var navigation=new NavigationService(); navigation.Navigate(new("Task",task.Item.Id.ToString("D")));
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization); await model.ReloadAsync();
        Assert.Equal(18m,model.ValueEditor.Build()!.Actual);
        var other=await profiles.CreateAsync("Other"); await model.ReloadAsync(); Assert.Null(model.ValueEditor.Build());
        Assert.Empty(await tasks.GetAsync(other.Id,TaskCollection.Active)); await Assert.ThrowsAsync<WorkspaceChangedException>(() => Actual(task,20));
        await profiles.SwitchAsync(profile); await profiles.RestoreAsync(); Assert.Equal(task,await Read(task));
    }

    [Fact]
    public async Task TodayCalendarAndOrganizationUseSameValueTaskAndQuickActualPreservesDraft()
    {
        var task=await Value(new(TaskValueType.Number,20)); var today=DateOnly.FromDateTime(DateTime.Now); await tasks.ScheduleAsync(Ref(task),today);
        var tag=await organization.SaveAsync(profile,OrganizationKind.Tag,null,new("exercise")); await organization.AssignAsync(new(profile,task.Item.Id),OrganizationKind.Tag,tag,true);
        var navigation=new NavigationService(); navigation.Navigate(new("Task",task.Item.Id.ToString("D")));
        var model=new TaskWorkspaceViewModel(tasks,current,navigation,NullLogger<TaskWorkspaceViewModel>.Instance,organization); await model.ReloadAsync();
        model.EditorTitle="Unsaved title"; model.ValueEditor.Actual="25"; await model.RecordActualCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal("Unsaved title",model.EditorTitle); Assert.Equal("Value task",(await Read(task))!.Item.Title);
        Assert.Equal(TaskStatus.Done,model.EditorStatus); Assert.Contains("125%",model.ValueEditor.Progress);
        Assert.Equal(25m,Assert.Single(await tasks.GetAsync(profile,TaskCollection.Today)).Value!.Actual);
        Assert.Equal(25m,Assert.Single(await tasks.GetScheduledAsync(profile,today,today)).Value!.Actual);
        Assert.Single((await organization.GetAsync(profile)).ItemTags);
        model.ValueEditor.Target="30"; model.ValueEditor.Actual="28"; await model.RecordActualCommand.ExecuteAsync(null);
        Assert.Contains("definition",model.Error); Assert.Equal(25m,(await Read(task))!.Value!.Actual);
    }

    [Theory]
    [InlineData("pt-PT","2,5")]
    [InlineData("en-US","2.5")]
    public void DecimalInputsAndDisplayRespectWindowsCulture(string culture,string text)
    {
        var previous=System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo(culture);
            Assert.Equal(2.5m,TaskValuePresentation.Parse(text,TaskValueType.Number));
            Assert.Equal(text,TaskValuePresentation.Input(2.5m,TaskValueType.Number));
            Assert.Contains("42%",TaskValuePresentation.Progress(new(TaskValueType.CustomUnit,300,125,Unit:"pages")));
        }
        finally {System.Globalization.CultureInfo.CurrentCulture=previous;}
    }

    public Task DisposeAsync()
    {
        profiles.Dispose(); gate.Dispose();
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
