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

public sealed class EventCalendarTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly WorkspaceOperationGate gate = new();
    private readonly CurrentProfile current;
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly EventService events;
    private readonly TaskService tasks;
    private readonly Clock clock = new();
    private Guid profile;
    private static readonly DateOnly Date = new(2026, 9, 26);
    private static EventDraft Timed(string title = "Concert") => new(title, false, Date, new(21, 0), Date, new(23, 30));
    private static EventDraft AllDay(string title = "Holiday") => new(title, true, Date, null, Date.AddDays(3), null);
    public EventCalendarTests()
    {
        paths = new(root); current = new(paths);
        var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        tasks = new(new SqliteTaskRepository(), current, gate, clock, NullLogger<TaskService>.Instance);
        events = new(new SqliteEventRepository(), current, gate, clock, NullLogger<EventService>.Instance);
    }
    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profile = (await profiles.CreateAsync("Personal")).Id;
    }
    private WorkspaceItemReference Ref(EventItem item) => new(profile, item.Item.Id);
    private Task<EventItem> Create(EventDraft? draft = null) => events.CreateAsync(profile, draft ?? Timed());
    private CalendarViewModel Model(NavigationService navigation, IEventService? service = null) => new(service ?? events, tasks, current, navigation, clock, NullLogger<CalendarViewModel>.Instance);

    [Theory]
    [InlineData(1, "Create task core", "57468374C91973E1E1CF5A094EF74CD898BF862B36CC9237520D4D01136F10D7")]
    [InlineData(2, "Create tags and spaces", "A1E3592D5C9FA77FBD8B548235AEE87AAD3337E75D9C62D931326D9231381139")]
    [InlineData(3, "Create calendar events", "A95B832879F2FCA0358FB77EC38514B8FB541FB739ABFC5BBB5F529D12F66E64")]
    [InlineData(4, "Create subtasks and dependencies", "596860A3D358F51062BAECC81398A0AE8A7F42B9D212B0F3206FAD9A7BA02CB2")]
    public void ShippedWorkspaceMigrationsRemainUnchanged(int version, string name, string expectedHash)
    {
        var migration = WorkspaceMigrationCatalog.All.Single(migration => migration.Version == version);
        Assert.Equal(name, migration.Name);
        var sql = System.Text.Encoding.UTF8.GetBytes(migration.Sql.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(expectedHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sql)));
    }

    [Fact]
    public async Task PhaseThreeUpgradePreservesOriginalLedgerTasksAndOrganization()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await using (var connection = await Connection(legacy))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
            foreach (var migration in WorkspaceMigrationCatalog.All.Take(2))
            {
                command.CommandText = migration.Sql + "INSERT INTO SchemaMigrations VALUES ($version,$name,'original');";
                command.Parameters.Clear(); command.Parameters.AddWithValue("$version", migration.Version); command.Parameters.AddWithValue("$name", migration.Name);
                await command.ExecuteNonQueryAsync();
            }
        }
        var context = new WorkspaceContext(legacy, paths.WorkspaceDatabase(legacy));
        var item = new TaskItem(new(Guid.NewGuid(), WorkspaceItemType.Task, "Existing task", clock.GetUtcNow(), clock.GetUtcNow(), null, null), "Details", TaskStatus.Doing, TaskPriority.High, Date);
        var taskRepository = new SqliteTaskRepository();
        await taskRepository.CreateAsync(context, item, default);
        var tag = Guid.NewGuid(); var org = new SqliteOrganizationRepository();
        await org.SaveAsync(context, OrganizationKind.Tag, tag, new("health"), true, clock.GetUtcNow(), default);
        await org.AssignAsync(context, item.Item.Id, OrganizationKind.Tag, tag, true, default);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(5L, await Scalar("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='Events';", legacy));
        Assert.Equal(item, await taskRepository.FindAsync(context, item.Item.Id, default));
        Assert.Single((await org.GetAsync(context, default)).ItemTags);
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync();
        using var check = global.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='Events';";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }
    [Fact]
    public async Task TimedAllDayAndMultiDayEventsRoundTripWithoutTaskRows()
    {
        var timed = await Create(Timed(" Concert "));
        var duplicate = await Create();
        var single = await Create(AllDay("Birthday") with { EndDate = Date, StartTime = new(9, 0), EndTime = new(10, 0) });
        var multiple = await Create(AllDay());
        var overnight = await Create(Timed("Overnight") with { EndDate = Date.AddDays(1), EndTime = new(2, 0) });
        Assert.Equal("Concert", timed.Item.Title); Assert.NotEqual(timed.Item.Id, duplicate.Item.Id);
        Assert.Null(single.StartTime); Assert.Null(single.EndTime);
        foreach (var item in new[] { timed, duplicate, single, multiple, overnight })
        {
            Assert.Equal(item, await events.FindAsync(Ref(item)));
            Assert.Equal(WorkspaceItemType.Event, item.Item.ItemType);
            Assert.Equal(TimeSpan.Zero, item.Item.CreatedAtUtc.Offset);
        }
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
        Assert.Equal(5L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public async Task BlankTitleRejected(string title) => await Assert.ThrowsAsync<EventValidationException>(() => Create(Timed(title)));
    [Fact]
    public async Task InvalidDatesAndTimedRangesRejected()
    {
        foreach (var draft in new[] { Timed() with { EndDate = Date.AddDays(-1) }, Timed() with { StartTime = null }, Timed() with { EndTime = null },
            Timed() with { EndTime = new(21, 0) }, Timed() with { EndTime = new(20, 0) }, AllDay() with { EndDate = Date.AddDays(-1) } })
            await Assert.ThrowsAsync<EventValidationException>(() => Create(draft));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
    }
    [Fact]
    public async Task EditsAndAllDayTransitionsUpdateOnlyMeaningfulTimestamps()
    {
        var item = await Create();
        Assert.Equal(item, await events.UpdateAsync(Ref(item), Timed()));
        clock.Advance();
        var changed = await events.UpdateAsync(Ref(item), AllDay("Changed"));
        Assert.Equal(item.Item.Id, changed.Item.Id); Assert.Equal(item.Item.CreatedAtUtc, changed.Item.CreatedAtUtc);
        Assert.True(changed.Item.UpdatedAtUtc > item.Item.UpdatedAtUtc); Assert.Null(changed.StartTime); Assert.Null(changed.EndTime);
        var timed = await events.UpdateAsync(Ref(item), Timed("Changed again") with { StartDate = Date.AddDays(1), EndDate = Date.AddDays(2) });
        Assert.False(timed.AllDay); Assert.Equal(new TimeOnly(21, 0), timed.StartTime);
        Assert.Equal(Date.AddDays(2), timed.EndDate); Assert.True(timed.Item.UpdatedAtUtc > changed.Item.UpdatedAtUtc);
    }
    [Fact]
    public async Task RangeQueriesIncludeOverlapsAndExcludeLifecycleAndExclusiveMidnight()
    {
        var inside = await Create();
        var spanning = await Create(AllDay() with { StartDate = Date.AddDays(-2), EndDate = Date.AddDays(2) });
        var endAtMidnight = await Create(Timed() with { StartDate = Date.AddDays(-1), EndTime = TimeOnly.MinValue });
        await Create(Timed() with { StartDate = Date.AddDays(1), EndDate = Date.AddDays(1) });
        var archived = await Create(); await events.ApplyAsync(Ref(archived), EventAction.Archive);
        var deleted = await Create(); await events.ApplyAsync(Ref(deleted), EventAction.Trash);
        var result = await events.GetRangeAsync(profile, Date, Date);
        Assert.Equal(2, result.Count); Assert.Contains(inside, result); Assert.Contains(spanning, result);
        Assert.DoesNotContain(endAtMidnight, result); Assert.False(endAtMidnight.OccursOn(Date));
        Assert.True(spanning.OccursOn(Date.AddDays(2))); Assert.False(spanning.OccursOn(Date.AddDays(3)));
        await Assert.ThrowsAsync<EventValidationException>(() => events.GetRangeAsync(profile, Date, Date.AddDays(-1)));
    }
    [Fact]
    public async Task PastStateIsDerivedAtEndWithoutDatabaseMutation()
    {
        var timed = await Create(); var allDay = await Create(AllDay() with { EndDate = Date });
        Assert.False(timed.IsPast(Date.ToDateTime(new(23, 29)))); Assert.True(timed.IsPast(Date.ToDateTime(new(23, 30))));
        Assert.False(allDay.IsPast(Date.ToDateTime(TimeOnly.MaxValue))); Assert.True(allDay.IsPast(Date.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        Assert.Equal(timed, await events.FindAsync(Ref(timed))); Assert.Equal(allDay, await events.FindAsync(Ref(allDay)));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM pragma_table_info('Events') WHERE name='Status';"));
    }
    [Fact]
    public async Task MovingPreservesWallTimeDurationAndIdentity()
    {
        foreach (var draft in new[] { Timed(), AllDay(), Timed() with { EndDate = Date.AddDays(2) } })
        {
            var item = await Create(draft); var moved = await events.MoveAsync(Ref(item), Date.AddDays(10));
            Assert.Equal(item.Item.Id, moved.Item.Id); Assert.Equal(item.EndDate.DayNumber - item.StartDate.DayNumber, moved.EndDate.DayNumber - moved.StartDate.DayNumber);
            Assert.Equal(item.StartTime, moved.StartTime); Assert.Equal(item.EndTime, moved.EndTime);
        }
        var multi = await Create(AllDay());
        await Assert.ThrowsAsync<EventValidationException>(() => events.MoveAsync(Ref(multi), DateOnly.MaxValue));
        Assert.Equal(multi, await events.FindAsync(Ref(multi)));
    }
    [Fact]
    public async Task EventLifecycleAndOrganizationCascadesPreserveTasks()
    {
        var item = await Create(); var task = await tasks.CreateAsync(profile, new("Keep task"));
        var org = new OrganizationService(new SqliteOrganizationRepository(), current, gate, clock, NullLogger<OrganizationService>.Instance);
        var tag = await org.SaveAsync(profile, OrganizationKind.Tag, null, new("urgent"));
        var space = await org.SaveAsync(profile, OrganizationKind.Space, null, new("Personal"));
        await org.AssignAsync(Ref(item), OrganizationKind.Tag, tag, true); await org.AssignAsync(Ref(item), OrganizationKind.Space, space, true);
        await Assert.ThrowsAsync<EventValidationException>(() => events.PermanentlyDeleteAsync(Ref(item)));
        await events.ApplyAsync(Ref(item), EventAction.Archive);
        Assert.Empty(await events.GetRangeAsync(profile, Date, Date)); Assert.Single(await events.GetCollectionAsync(profile, EventCollection.Archived));
        await events.ApplyAsync(Ref(item), EventAction.RestoreArchive);
        Assert.Single(await events.GetRangeAsync(profile, Date, Date));
        await events.ApplyAsync(Ref(item), EventAction.Archive); await events.ApplyAsync(Ref(item), EventAction.Trash);
        Assert.Empty(await events.GetCollectionAsync(profile, EventCollection.Archived)); Assert.Single(await events.GetCollectionAsync(profile, EventCollection.Trash));
        await Assert.ThrowsAsync<EventValidationException>(() => events.UpdateAsync(Ref(item), Timed("Blocked")));
        await events.ApplyAsync(Ref(item), EventAction.RestoreTrash);
        Assert.Single(await events.GetRangeAsync(profile, Date, Date));
        await events.ApplyAsync(Ref(item), EventAction.Trash); await events.PermanentlyDeleteAsync(Ref(item));
        Assert.Null(await events.FindAsync(Ref(item))); Assert.Equal(task, await tasks.FindAsync(new(profile, task.Item.Id)));
        var remaining = await org.GetAsync(profile); Assert.Empty(remaining.ItemTags); Assert.Empty(remaining.ItemSpaces); Assert.Single(remaining.Tags); Assert.Single(remaining.Spaces);
    }
    [Fact]
    public async Task FailedInsertAndUpdateAreAtomic()
    {
        await Scalar("CREATE TRIGGER FailEvent BEFORE INSERT ON Events BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<EventOperationException>(() => Create()); Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM WorkspaceItems;"));
        await Scalar("DROP TRIGGER FailEvent;"); var item = await Create();
        await Scalar("CREATE TRIGGER FailEvent BEFORE UPDATE ON Events BEGIN SELECT RAISE(ABORT,'simulated'); END;");
        await Assert.ThrowsAsync<EventOperationException>(() => events.UpdateAsync(Ref(item), Timed("Changed")));
        Assert.Equal(item, await events.FindAsync(Ref(item)));
    }
    [Fact]
    public async Task CalendarTasksUseRangeQueriesAndReschedulingNeverDuplicates()
    {
        var task = await tasks.CreateAsync(profile, new("Scheduled", ScheduledDate: Date));
        var unscheduled = await tasks.CreateAsync(profile, new("Unscheduled"));
        await tasks.CreateAsync(profile, new("Old", ScheduledDate: Date.AddYears(-2)));
        var archived = await tasks.CreateAsync(profile, new("Archived", ScheduledDate: Date)); await tasks.ApplyAsync(new(profile, archived.Item.Id), TaskAction.Archive);
        var deleted = await tasks.CreateAsync(profile, new("Deleted", ScheduledDate: Date)); await tasks.ApplyAsync(new(profile, deleted.Item.Id), TaskAction.Trash);
        Assert.Equal(task, Assert.Single(await tasks.GetScheduledAsync(profile, Date, Date)));
        Assert.Equal(unscheduled, Assert.Single(await tasks.GetUnscheduledAsync(profile)));
        var nav = new NavigationService(); nav.Navigate(new("Calendar")); var model = Model(nav); model.Mode = CalendarMode.Day;
        await model.ReloadAsync();
        await model.MoveAsync(Assert.Single(Assert.Single(model.Days).Tasks), Date.AddDays(1));
        Assert.Empty(await tasks.GetScheduledAsync(profile, Date, Date));
        Assert.Equal(task.Item.Id, Assert.Single(await tasks.GetScheduledAsync(profile, Date.AddDays(1), Date.AddDays(1))).Item.Id);
        await model.ScheduleCommand.ExecuteAsync(Assert.Single(model.Unscheduled));
        Assert.Equal(unscheduled.Item.Id, Assert.Single(await tasks.GetScheduledAsync(profile, Date, Date)).Item.Id);
        Assert.Equal(5L, await Scalar("SELECT COUNT(*) FROM Tasks;"));
    }
    [Fact]
    public async Task TodayUsesLocalDateAndCalendarRefreshReflectsTaskStatus()
    {
        Assert.NotEqual(DateOnly.FromDateTime(clock.GetUtcNow().DateTime), Date);
        var today = await Create(); await Create(Timed("Tomorrow") with { StartDate = Date.AddDays(1), EndDate = Date.AddDays(1) });
        var task = await tasks.CreateAsync(profile, new("Today's task", ScheduledDate: Date));
        Assert.Single(await tasks.GetAsync(profile, TaskCollection.Today));
        var nav = new NavigationService(); var model = Model(nav); await model.ReloadAsync();
        Assert.Equal(today.Item.Id, Assert.Single(model.EventRows).Id);
        nav.Navigate(new("Calendar")); model.Mode = CalendarMode.Day; await model.ReloadAsync();
        await tasks.ChangeStatusAsync(new(profile, task.Item.Id), TaskStatus.Done);
        await model.RefreshClockAsync(); Assert.Equal(TaskStatus.Done, Assert.Single(Assert.Single(model.Days).Tasks).Task!.Status);
    }
    [Fact]
    public async Task ProfileSwitchClearsCalendarEditorAndRejectsStaleActions()
    {
        var item = await Create(); await tasks.CreateAsync(profile, new("A", ScheduledDate: Date));
        var nav = new NavigationService(); nav.Navigate(new("Calendar")); var model = Model(nav); model.Mode = CalendarMode.Day; await model.ReloadAsync();
        var stale = Assert.Single(Assert.Single(model.Days).TimedEvents); model.OpenCommand.Execute(stale); await model.ReloadAsync(); model.EditorTitle = "Unsaved A";
        var other = await profiles.CreateAsync("Other"); await model.ReloadAsync();
        Assert.True(model.IsCalendar); Assert.Empty(model.Days.SelectMany(day => day.Entries)); Assert.Equal("", model.EditorTitle);
        await events.CreateAsync(other.Id, Timed("B")); await tasks.CreateAsync(other.Id, new("B", ScheduledDate: Date)); await model.ReloadAsync();
        Assert.All(model.Days.SelectMany(day => day.Entries), entry => Assert.Equal(other.Id, entry.ProfileId));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => events.MoveAsync(Ref(item), Date.AddDays(1)));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => events.UpdateAsync(Ref(item), Timed("Stale")));
        await model.MoveAsync(stale, Date.AddDays(1)); Assert.NotNull(model.Error);
        await profiles.SwitchAsync(profile); await model.ReloadAsync();
        Assert.Contains(model.Days.SelectMany(day => day.Entries), entry => entry.Id == item.Item.Id);
        Assert.DoesNotContain(model.Days.SelectMany(day => day.Entries), entry => entry.Title == "B");
    }
    [Theory]
    [InlineData(CalendarMode.Month, DayOfWeek.Monday, 42)]
    [InlineData(CalendarMode.Month, DayOfWeek.Sunday, 42)]
    [InlineData(CalendarMode.Week, DayOfWeek.Monday, 7)]
    [InlineData(CalendarMode.Day, DayOfWeek.Sunday, 1)]
    public void CalendarRangesRespectModeAndCulture(CalendarMode mode, DayOfWeek firstDay, int length)
    {
        var range = CalendarPeriods.Range(new(2028, 2, 29), mode, firstDay);
        Assert.Equal(length, range.Through.DayNumber - range.From.DayNumber + 1);
        if (mode != CalendarMode.Day) Assert.Equal(firstDay, range.From.DayOfWeek);
        Assert.True(range.From <= new DateOnly(2028, 2, 29) && range.Through >= new DateOnly(2028, 2, 29));
    }
    [Fact]
    public async Task CreatingFromCalendarPrefillsDateAndMultiDayEntriesRepeatCorrectly()
    {
        var item = await Create(AllDay()); var nav = new NavigationService(); nav.Navigate(new("Calendar")); var model = Model(nav); await model.ReloadAsync();
        Assert.Equal(4, model.Days.Count(day => day.Entries.Any(entry => entry.Id == item.Item.Id)));
        model.SelectedDate = new DateTimeOffset(Date.AddDays(2).ToDateTime(TimeOnly.MinValue));
        await model.ReloadAsync(); model.NewEventCommand.Execute(null); await model.ReloadAsync();
        Assert.Equal(Date.AddDays(2), DateOnly.FromDateTime(model.StartDate!.Value.DateTime));
        Assert.Equal(model.StartDate, model.EndDate);
        nav.Navigate(new("Calendar")); await model.ReloadAsync(); model.NewTaskCommand.Execute(null);
        var org = new OrganizationService(new SqliteOrganizationRepository(), current, gate, clock, NullLogger<OrganizationService>.Instance);
        var taskModel = new TaskWorkspaceViewModel(tasks, current, nav, NullLogger<TaskWorkspaceViewModel>.Instance, org); await taskModel.ReloadAsync();
        Assert.True(taskModel.IsEditor); Assert.Equal(Date.AddDays(2), DateOnly.FromDateTime(taskModel.EditorScheduledDate!.Value.DateTime));
    }
    private async Task<SqliteConnection> Connection(Guid id)
    {
        var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(id)};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync(); connection.CreateCollation("WORKSPACE_NAME", (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b)); return connection;
    }
    [Fact]
    public async Task WallTimesAcrossDstRemainLocalAndUnscheduledPanelIsBounded()
    {
        var date = new DateOnly(2026, 3, 29);
        var item = await Create(new("DST wall time", false, date, new(1, 30), date, new(3, 30)));
        Assert.Equal(item, await events.FindAsync(Ref(item)));
        Assert.Equal("2026-03-29|01:30:00.0000000|2026-03-29|03:30:00.0000000", await Scalar("SELECT StartDate || '|' || StartTime || '|' || EndDate || '|' || EndTime FROM Events;"));
        for (var i = 0; i < 105; i++) await tasks.CreateAsync(profile, new($"Unscheduled {i}"));
        Assert.Equal(100, (await tasks.GetUnscheduledAsync(profile)).Count);
        Assert.Empty(await tasks.GetScheduledAsync(profile, date, date));
    }
    [Fact]
    public async Task NoProfileRejectsEventAccess()
    {
        var service = new EventService(new SqliteEventRepository(), new CurrentProfile(paths), gate, clock, NullLogger<EventService>.Instance);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => service.GetRangeAsync(profile, Date, Date));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => service.CreateAsync(profile, Timed()));
    }
    [Fact]
    public async Task DelayedOldCalendarReadCannotRepopulateAnotherProfile()
    {
        await Create(); var delayed = new DelayedEvents(events);
        var navigation = new NavigationService(); navigation.Navigate(new("Calendar")); var model = Model(navigation, delayed);
        var oldRead = model.ReloadAsync(); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await profiles.CreateAsync("Other"); await model.ReloadAsync();
        Assert.Empty(model.Days.SelectMany(day => day.Entries));
        delayed.Continue.SetResult(); await oldRead;
        Assert.Empty(model.Days.SelectMany(day => day.Entries)); Assert.Empty(model.EventRows); Assert.Empty(model.Unscheduled);
    }
    [Fact]
    public async Task ProfileSwitchWaitsForEventWrite()
    {
        var other = await profiles.CreateAsync("Other"); await profiles.SwitchAsync(profile);
        var delayed = new DelayedEventRepository(new SqliteEventRepository());
        var service = new EventService(delayed, current, gate, clock, NullLogger<EventService>.Instance);
        var creating = service.CreateAsync(profile, Timed()); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = profiles.SwitchAsync(other.Id); Assert.False(switching.IsCompleted);
        delayed.Continue.SetResult(); var item = await creating; await switching;
        Assert.Empty(await events.GetRangeAsync(other.Id, Date, Date));
        await profiles.SwitchAsync(profile); Assert.Equal(item, Assert.Single(await events.GetRangeAsync(profile, Date, Date)));
    }
    private sealed class DelayedEvents(IEventService inner) : IEventService
    {
        private int delay = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<EventItem>> GetRangeAsync(Guid profileId, DateOnly from, DateOnly through, CancellationToken cancellationToken = default)
        {
            var items = await inner.GetRangeAsync(profileId, from, through, cancellationToken);
            if (Interlocked.Exchange(ref delay, 0) == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return items;
        }
        public Task<IReadOnlyList<EventItem>> GetCollectionAsync(Guid profileId, EventCollection collection, CancellationToken cancellationToken = default) => inner.GetCollectionAsync(profileId, collection, cancellationToken);
        public Task<EventItem?> FindAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) => inner.FindAsync(reference, cancellationToken);
        public Task<EventItem> CreateAsync(Guid profileId, EventDraft draft, CancellationToken cancellationToken = default) => inner.CreateAsync(profileId, draft, cancellationToken);
        public Task<EventItem> UpdateAsync(WorkspaceItemReference reference, EventDraft draft, CancellationToken cancellationToken = default) => inner.UpdateAsync(reference, draft, cancellationToken);
        public Task<EventItem> MoveAsync(WorkspaceItemReference reference, DateOnly date, CancellationToken cancellationToken = default) => inner.MoveAsync(reference, date, cancellationToken);
        public Task<EventItem> ApplyAsync(WorkspaceItemReference reference, EventAction action, CancellationToken cancellationToken = default) => inner.ApplyAsync(reference, action, cancellationToken);
        public Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default) => inner.PermanentlyDeleteAsync(reference, cancellationToken);
    }
    private sealed class DelayedEventRepository(IEventRepository inner) : IEventRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CreateAsync(WorkspaceContext workspace, EventItem item, CancellationToken cancellationToken)
        { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); await inner.CreateAsync(workspace, item, cancellationToken); }
        public Task<IReadOnlyList<EventItem>> GetRangeAsync(WorkspaceContext workspace, DateOnly from, DateOnly through, CancellationToken cancellationToken) => inner.GetRangeAsync(workspace, from, through, cancellationToken);
        public Task<IReadOnlyList<EventItem>> GetCollectionAsync(WorkspaceContext workspace, EventCollection collection, CancellationToken cancellationToken) => inner.GetCollectionAsync(workspace, collection, cancellationToken);
        public Task<EventItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken) => inner.FindAsync(workspace, id, cancellationToken);
        public Task<EventItem> UpdateAsync(WorkspaceContext workspace, Guid id, Func<EventItem, EventItem> update, CancellationToken cancellationToken) => inner.UpdateAsync(workspace, id, update, cancellationToken);
        public Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken) => inner.PermanentlyDeleteAsync(workspace, id, cancellationToken);
    }
    private async Task<object?> Scalar(string sql, Guid? id = null)
    {
        await using var connection = await Connection(id ?? profile); using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
    public Task DisposeAsync()
    {
        profiles.Dispose(); gate.Dispose();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 25, 23, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("TestLocal", TimeSpan.FromHours(2), "TestLocal", "TestLocal");
        public void Advance() => now = now.AddMinutes(1);
    }
}
