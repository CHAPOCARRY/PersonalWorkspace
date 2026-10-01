using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalWorkspace.Tests", Guid.NewGuid().ToString("N"));
    private readonly ApplicationPaths paths;
    private readonly CurrentProfile current;
    private readonly WorkspaceOperationGate gate = new();
    private readonly ProfileService profiles;
    private readonly WorkspaceInitializer initializer;
    private readonly TrackerService service;
    private readonly OrganizationService organization;
    private readonly Clock clock = new();
    private Guid profile;
    private static readonly DateOnly Day = new(2026, 9, 29);
    public TrackerTests()
    {
        paths = new(root); current = new(paths); var connections = new SqliteConnectionFactory(paths);
        initializer = new(paths, NullLogger<WorkspaceInitializer>.Instance);
        profiles = new(new SqliteProfileRepository(connections), new SqliteSettingsService(connections, NullLogger<SqliteSettingsService>.Instance),
            new ProfileFiles(paths), initializer, current, NullLogger<ProfileService>.Instance, gate);
        service = new(new SqliteTrackerRepository(), current, gate, clock, NullLogger<TrackerService>.Instance);
        organization = new(new SqliteOrganizationRepository(), current, gate, clock, NullLogger<OrganizationService>.Instance);
    }
    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(paths, new SqliteConnectionFactory(paths), MigrationCatalog.All, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        profile = (await profiles.CreateAsync("Tracker tests")).Id;
    }
    private WorkspaceItemReference Ref(TrackerItem item) => new(profile, item.Item.Id);
    private static TrackerDraft Draft(TrackerValueType type = TrackerValueType.Decimal) => new("Measure", type switch
    {
        TrackerValueType.Currency => new(type, CurrencyCode: " eur "), TrackerValueType.Distance => new(type, "km"),
        TrackerValueType.Scale => new(type, ScaleMin: -2, ScaleMax: 8), TrackerValueType.CustomUnit => new(type, " L "), _ => new(type)
    }, new(TrackerFrequency.Daily, Day));
    private Task<TrackerItem> Create(TrackerDraft? draft = null) => service.CreateAsync(profile, draft ?? Draft());
    private Task<TrackerEntry> Save(TrackerItem item, decimal value, DateOnly? date = null, Guid? id = null, string? note = null, int hour = 12) =>
        service.SaveEntryAsync(Ref(item), date ?? Day, new(hour, 0), new(value), note, id);
    private async Task<decimal?> Total(TrackerItem item, DateOnly? date = null) => (await service.GetPeriodAsync(Ref(item), date ?? Day)).Value?.Number;
    private async Task<SqliteConnection> Connection(Guid? id = null)
    {
        var connection = new SqliteConnection($"Data Source={paths.WorkspaceDatabase(id ?? profile)};Pooling=False;Foreign Keys=True");
        connection.CreateCollation("WORKSPACE_NAME", StringComparer.OrdinalIgnoreCase.Compare); await connection.OpenAsync(); return connection;
    }
    private async Task<object?> Sql(string sql, Guid? id = null)
    {
        await using var connection = await Connection(id); using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
    [Fact]
    public async Task PhaseEightUpgradePreservesPopulatedWorkspaceAndLedgerAndIsIdempotent()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(7)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var task = Guid.NewGuid(); var segment = Guid.NewGuid(); var occurrence = Guid.NewGuid();
        await Sql($"""
            INSERT INTO WorkspaceItems VALUES('{task}',1,'Existing','2026-09-29T12:00:00+00:00','2026-09-29T12:00:00+00:00',NULL,NULL);
            INSERT INTO Tasks(ItemId,Description,Status,Priority) VALUES('{task}','',0,0);
            INSERT INTO TaskValues(ItemId,ValueType,Target) VALUES('{task}',1,'20');
            INSERT INTO TaskRecurrenceRules VALUES('{segment}','{task}',0,'2026-09-29',1,0,1,1,1,NULL,'2026-09-29',NULL,1,'2026-09-29T12:00:00+00:00');
            INSERT INTO TaskOccurrences VALUES('{occurrence}','{task}','{segment}','2026-09-29','2026-09-29',0,'18',0,1,0,'2026-09-29T12:00:00+00:00','2026-09-29T12:00:00+00:00');
            INSERT INTO TaskCarrySettings VALUES('{task}',1,1,1);
            INSERT INTO TaskOccurrenceCalculations VALUES('{occurrence}','20','0','20','2');
            """, legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(11L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy));
        Assert.Equal(7L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal("18", await Sql($"SELECT Actual FROM TaskOccurrences WHERE Id='{occurrence}';", legacy));
        Assert.Equal("2", await Sql($"SELECT CarryOut FROM TaskOccurrenceCalculations WHERE OccurrenceId='{occurrence}';", legacy));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Trackers;", legacy));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TrackerEntries;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync(); using var command = global.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Trackers','TrackerEntries');"; Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Theory]
    [InlineData(TrackerValueType.Integer)] [InlineData(TrackerValueType.Decimal)] [InlineData(TrackerValueType.Percentage)]
    [InlineData(TrackerValueType.Currency)] [InlineData(TrackerValueType.Duration)] [InlineData(TrackerValueType.Distance)]
    [InlineData(TrackerValueType.Boolean)] [InlineData(TrackerValueType.Scale)] [InlineData(TrackerValueType.CustomUnit)]
    public async Task EveryTypeRoundTripsWithIndependentWorkspaceIdentity(TrackerValueType type)
    {
        var value = type == TrackerValueType.Boolean ? new TrackerValue(Boolean: true) : new(4);
        var item = await Create(Draft(type) with { Target = value, Title = "  Measure  " });
        Assert.Equal("Measure", item.Item.Title); Assert.True(item.IsActive); Assert.False(item.HasEntries);
        Assert.Equal(WorkspaceItemType.Tracker, item.Item.ItemType); Assert.Equal(item, await service.FindAsync(Ref(item)));
        var entry = await service.SaveEntryAsync(Ref(item), Day, new(9, 30), value, "Note");
        Assert.Equal(entry, Assert.Single(await service.GetEntriesAsync(Ref(item), new(Recent: 50))));
        Assert.Equal(value, (await service.GetPeriodAsync(Ref(item), Day)).Value);
        Assert.Equal(item.Item, (await service.FindAsync(Ref(item)))!.Item);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Tasks;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TaskOccurrences;"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(TimeSpan.Zero, entry.CreatedAtUtc.Offset);
    }
    [Theory]
    [InlineData(TrackerValueType.Decimal)] [InlineData(TrackerValueType.Currency)] [InlineData(TrackerValueType.Distance)] [InlineData(TrackerValueType.CustomUnit)]
    public async Task ExactDecimalsUseTextAndOptionalTargetsPersist(TrackerValueType type)
    {
        const decimal number = 12345678901234567890.123456789m;
        var item = await Create(Draft(type)); Assert.Null(item.Target);
        await Save(item, number); Assert.Equal(number, await Total(item));
        Assert.Equal("text", await Sql("SELECT typeof(Value) FROM TrackerEntries;"));
        item = await service.UpdateAsync(Ref(item), item.Draft with { Target = new(number) });
        Assert.Equal(number, (await service.FindAsync(Ref(item)))!.Target!.Number);
        Assert.Equal("text", await Sql("SELECT typeof(Target) FROM Trackers;"));
    }
    [Theory]
    [InlineData(TrackerValueType.Integer, "9223372036854775807")]
    [InlineData(TrackerValueType.Integer, "-9223372036854775808")]
    [InlineData(TrackerValueType.Duration, "4530")]
    [InlineData(TrackerValueType.Scale, "-2")]
    public async Task WholeValuesUseIntegerStorage(TrackerValueType type, string text)
    {
        var number = decimal.Parse(text, CultureInfo.InvariantCulture); var item = await Create(Draft(type)); await Save(item, number);
        Assert.Equal(number, await Total(item)); Assert.Equal("integer", await Sql("SELECT typeof(IntegerValue) FROM TrackerEntries;"));
    }
    [Theory]
    [InlineData("0")] [InlineData("100")] [InlineData("72.125")]
    public async Task PercentageAcceptsBoundaries(string text)
    {
        var item = await Create(Draft(TrackerValueType.Percentage)); var value = decimal.Parse(text, CultureInfo.InvariantCulture); await Save(item, value); Assert.Equal(value, await Total(item));
    }
    [Theory]
    [InlineData(TrackerValueType.Percentage, "-0.01")] [InlineData(TrackerValueType.Percentage, "100.01")]
    [InlineData(TrackerValueType.Integer, "1.5")] [InlineData(TrackerValueType.Integer, "9223372036854775808")]
    [InlineData(TrackerValueType.Duration, "0.5")] [InlineData(TrackerValueType.Duration, "-1")] [InlineData(TrackerValueType.Duration, "922337203686")]
    [InlineData(TrackerValueType.Scale, "9")] [InlineData(TrackerValueType.Scale, "-3")] [InlineData(TrackerValueType.Scale, "1.1")]
    [InlineData(TrackerValueType.Distance, "-1")]
    public async Task InvalidEntriesAndTargetsAreRejected(TrackerValueType type, string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture); var item = await Create(Draft(type));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Save(item, value));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Create(Draft(type) with { Target = new(value) }));
        Assert.Empty(await service.GetEntriesAsync(Ref(item), new(Recent: 50)));
    }
    [Fact]
    public async Task InvalidDefinitionsRejectWithoutPartialWorkspaceItems()
    {
        var invalid = new[]
        {
            Draft() with { Title = " " }, Draft() with { Settings = new((TrackerValueType)999) },
            Draft(TrackerValueType.Currency) with { Settings = new(TrackerValueType.Currency, CurrencyCode: "EU") },
            Draft(TrackerValueType.Currency) with { Settings = new(TrackerValueType.Currency, CurrencyCode: "E1R") },
            Draft(TrackerValueType.CustomUnit) with { Settings = new(TrackerValueType.CustomUnit, " ") },
            Draft(TrackerValueType.CustomUnit) with { Settings = new(TrackerValueType.CustomUnit, new string('x', 33)) },
            Draft(TrackerValueType.Distance) with { Settings = new(TrackerValueType.Distance, "yards") },
            Draft(TrackerValueType.Scale) with { Settings = new(TrackerValueType.Scale, ScaleMin: 5, ScaleMax: 5) },
            Draft() with { Schedule = new(TrackerFrequency.Daily) }, Draft() with { Schedule = new(TrackerFrequency.Daily, Day, Day.AddDays(-1)) },
            Draft() with { Schedule = new(TrackerFrequency.EveryXDays, Day, Interval: 0) }, Draft() with { Schedule = new(TrackerFrequency.SelectedWeekdays, Day) },
            Draft(TrackerValueType.Boolean) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum },
            Draft(TrackerValueType.Scale) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum }
        };
        foreach (var draft in invalid) await Assert.ThrowsAsync<TrackerValidationException>(() => Create(draft));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
    }
    [Fact]
    public async Task BooleanFalseIsAnEntryAndDoesNotRepresentMissingOrTaskCompletion()
    {
        var item = await Create(Draft(TrackerValueType.Boolean) with { Target = new(Boolean: true) });
        await service.SaveEntryAsync(Ref(item), Day, new(10, 0), new(Boolean: false));
        var period = await service.GetPeriodAsync(Ref(item), Day); Assert.False(period.Pending); Assert.False(period.Value!.Boolean);
        await Assert.ThrowsAsync<TrackerValidationException>(() => Save(item, 1));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Tasks;"));
    }
    [Fact]
    public async Task SingleSavesUpsertIdentityAndDoNotRewriteOrderingOrDefinitionTimestamp()
    {
        var item = await Create(); var entry = await Save(item, 78.4m, note: " Before ", hour: 8);
        var same = await Save(item, 78.4m, note: "Before", hour: 9); Assert.Equal(entry, same);
        var correction = await Save(item, 78.2m, note: "After", hour: 10);
        Assert.Equal(entry.Id, correction.Id); Assert.Equal(entry.CreatedAtUtc, correction.CreatedAtUtc); Assert.Equal(entry.LocalTime, correction.LocalTime);
        Assert.True(correction.UpdatedAtUtc > entry.UpdatedAtUtc); Assert.Equal(78.2m, await Total(item));
        Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day)));
        Assert.Equal(item.Item.UpdatedAtUtc, (await service.FindAsync(Ref(item)))!.Item.UpdatedAtUtc);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO TrackerEntries SELECT '{Guid.NewGuid()}',TrackerId,ValueType,EntryMode,PeriodDate,LocalDate,LocalTime,Value,IntegerValue,Note,CreatedAtUtc,UpdatedAtUtc FROM TrackerEntries;"));
    }
    [Theory]
    [InlineData(TrackerAggregation.Sum, "1.6")] [InlineData(TrackerAggregation.Average, "0.5333333333333333333333333333")]
    [InlineData(TrackerAggregation.Min, "0.4")] [InlineData(TrackerAggregation.Max, "0.7")] [InlineData(TrackerAggregation.Last, "0.4")]
    public async Task MultipleEntriesAggregateCanonically(TrackerAggregation aggregation, string expected)
    {
        var item = await Create(Draft(TrackerValueType.CustomUnit) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = aggregation });
        var a = await Save(item, 0.5m, hour: 9); var b = await Save(item, 0.7m, hour: 10); var c = await Save(item, 0.4m, hour: 11);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), await Total(item));
        var entries = await service.GetEntriesAsync(Ref(item), new(Period: Day)); Assert.Equal(new[] { c.Id, b.Id, a.Id }, entries.Select(e => e.Id));
        var corrected = await Save(item, 0.3m, id: a.Id, hour: 23); Assert.Equal(a.Id, corrected.Id); Assert.Equal(a.LocalTime, corrected.LocalTime);
        if (aggregation == TrackerAggregation.Last) Assert.Equal(0.4m, await Total(item));
        await service.DeleteEntryAsync(Ref(item), b.Id); Assert.Equal(2, (await service.GetEntriesAsync(Ref(item), new(Period: Day))).Count);
        if (aggregation == TrackerAggregation.Sum) Assert.Equal(0.7m, await Total(item));
        Assert.Equal(item.Item, (await service.FindAsync(Ref(item)))!.Item);
    }
    [Fact]
    public async Task HistoricalCorrectionAndDeletionRecomputeWaterTotal()
    {
        var item = await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-10)), EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Save(item, 0.5m, Day.AddDays(-1)); var remove = await Save(item, 0.7m, Day.AddDays(-1)); await Save(item, 0.4m, Day.AddDays(-1));
        Assert.Equal(1.6m, await Total(item, Day.AddDays(-1))); await service.DeleteEntryAsync(Ref(item), remove.Id); Assert.Equal(0.9m, await Total(item, Day.AddDays(-1)));
        await Save(item, 9m); Assert.Equal(9m, await Total(item));
    }
    [Fact]
    public async Task ExactAggregationRejectsPrecisionLossAtomicallyAndAverageAvoidsIntermediateOverflow()
    {
        var item = await Create(Draft() with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Save(item, decimal.MaxValue);
        await Assert.ThrowsAsync<TrackerValidationException>(() => Save(item, 0.1m));
        Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day)));
        var average = await Create(Draft() with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Average });
        await Save(average, decimal.MaxValue); await Save(average, decimal.MaxValue); Assert.Equal(decimal.MaxValue, await Total(average));
    }
    [Fact]
    public void FrequencyAnchorsAndBoundariesAreDeterministic()
    {
        foreach (var frequency in new[] { TrackerFrequency.Daily, TrackerFrequency.Weekly, TrackerFrequency.EveryXDays, TrackerFrequency.SelectedWeekdays, TrackerFrequency.Monthly })
        {
            var schedule = new TrackerSchedule(frequency, Day, Day.AddYears(1), 3, (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday));
            Assert.Null(schedule.PeriodOn(Day.AddDays(-1))); Assert.Null(schedule.PeriodOn(Day.AddYears(1).AddDays(1)));
        }
        Assert.Equal(Day, new TrackerSchedule(TrackerFrequency.Weekly, Day).PeriodOn(Day.AddDays(6)));
        Assert.Equal(Day.AddDays(7), new TrackerSchedule(TrackerFrequency.Weekly, Day).PeriodOn(Day.AddDays(7)));
        Assert.Equal(Day.AddDays(3), new TrackerSchedule(TrackerFrequency.EveryXDays, Day, Interval: 3).PeriodOn(Day.AddDays(5)));
        var weekdays = new TrackerSchedule(TrackerFrequency.SelectedWeekdays, Day, Weekdays: 10);
        Assert.Null(weekdays.PeriodOn(Day)); Assert.Equal(Day.AddDays(1), weekdays.PeriodOn(Day.AddDays(1)));
        var monthly = new TrackerSchedule(TrackerFrequency.Monthly, new(2026, 1, 31));
        Assert.Null(monthly.PeriodOn(new(2026, 2, 28))); Assert.Equal(new DateOnly(2026, 3, 31), monthly.PeriodOn(new(2026, 3, 31)));
        Assert.Null(monthly.PeriodOn(new(2026, 3, 30)));
        Assert.Equal(DateOnly.MaxValue, new TrackerSchedule(TrackerFrequency.Daily, DateOnly.MaxValue).PeriodOn(DateOnly.MaxValue));
    }
    [Fact]
    public async Task WeeklySingleUsesOneAnchoredPeriodAcrossLocalDates()
    {
        var item = await Create(Draft() with { Schedule = new(TrackerFrequency.Weekly, Day) });
        var first = await Save(item, 1); var second = await Save(item, 2, Day.AddDays(6));
        Assert.Equal(first.Id, second.Id); Assert.Equal(Day, second.PeriodDate); Assert.Equal(2m, await Total(item));
        await Save(item, 3, Day.AddDays(7)); Assert.Equal(2, (await service.GetEntriesAsync(Ref(item), new(Recent: 10))).Count);
    }
    [Fact]
    public async Task TodayDerivesPendingAndExcludesUnscheduledNotStartedEndedArchivedAndTrashWithoutCreatingReplacement()
    {
        var active = await Create(); var multiple = await Create(Draft() with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Create(Draft() with { Schedule = new() });
        await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(1)) });
        await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-10), Day.AddDays(-1)) });
        var archived = await Create(); await service.ApplyAsync(Ref(archived), TrackerAction.Archive);
        var trashed = await Create(); await service.ApplyAsync(Ref(trashed), TrackerAction.Trash);
        var ending = await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day, Day.AddDays(1)) });
        Assert.Equal(3, (await service.GetAsync(profile, todayOnly: true)).Count); Assert.All(await service.GetAsync(profile, todayOnly: true), p => Assert.True(p.Pending));
        await Save(active, 0); await Save(multiple, 0);
        Assert.False((await service.GetAsync(profile, todayOnly: true)).Single(p => p.Tracker.Item.Id == multiple.Item.Id).Pending);
        clock.Now = Day.AddDays(2); Assert.DoesNotContain(await service.GetAsync(profile, todayOnly: true), p => p.Tracker.Item.Id == ending.Item.Id);
        Assert.NotNull(await service.FindAsync(Ref(ending))); Assert.Equal(8L, await Sql("SELECT COUNT(*) FROM Trackers;"));
    }
    [Fact]
    public async Task DefinitionChangesAreNoOpsOrAdvanceOnlyDefinitionAndHistoryShapeIsProtected()
    {
        var item = await Create(); Assert.Equal(item, await service.UpdateAsync(Ref(item), item.Draft));
        var entry = await Save(item, 2);
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.UpdateAsync(Ref(item), item.Draft with { Settings = new(TrackerValueType.Integer) }));
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.UpdateAsync(Ref(item), item.Draft with { Schedule = new(TrackerFrequency.Weekly, Day) }));
        var changed = await service.UpdateAsync(Ref(item), item.Draft with { Title = "Changed", Target = new(5) });
        Assert.True(changed.Item.UpdatedAtUtc > item.Item.UpdatedAtUtc);
        Assert.Equal(entry, Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day))));
        await service.GetAsync(profile); await service.GetPeriodAsync(Ref(item), Day);
        Assert.Equal(changed, await service.FindAsync(Ref(item)));
    }
    [Fact]
    public async Task LifecyclePreservesEntriesAndPermanentDeleteCascadesGenericAssignments()
    {
        var item = await Create(); var entry = await Save(item, 3);
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("health"));
        var space = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Personal"));
        var second = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Training"));
        await organization.AssignAsync(Ref(item), OrganizationKind.Tag, tag, true);
        await organization.AssignAsync(Ref(item), OrganizationKind.Space, space, true); await organization.AssignAsync(Ref(item), OrganizationKind.Space, second, true);
        Assert.Equal(2, (await organization.GetAsync(profile)).ItemSpaces.Count);
        await service.ApplyAsync(Ref(item), TrackerAction.Archive); Assert.Empty(await service.GetAsync(profile));
        Assert.Single(await service.GetAsync(profile, TrackerCollection.Archived));
        Assert.Equal(entry, Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day))));
        await service.ApplyAsync(Ref(item), TrackerAction.RestoreArchive); Assert.Single(await service.GetAsync(profile));
        await service.ApplyAsync(Ref(item), TrackerAction.Trash); Assert.Single(await service.GetAsync(profile, TrackerCollection.Trash));
        await Assert.ThrowsAsync<TrackerValidationException>(() => Save(item, 4));
        await service.ApplyAsync(Ref(item), TrackerAction.RestoreTrash); Assert.Single(await service.GetAsync(profile));
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.PermanentlyDeleteAsync(Ref(item)));
        await service.ApplyAsync(Ref(item), TrackerAction.Trash); await service.PermanentlyDeleteAsync(Ref(item));
        Assert.Null(await service.FindAsync(Ref(item))); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TrackerEntries;"));
        var catalog = await organization.GetAsync(profile); Assert.Single(catalog.Tags); Assert.Equal(2, catalog.Spaces.Count); Assert.Empty(catalog.ItemSpaces); Assert.Empty(catalog.ItemTags);
    }
    [Fact]
    public async Task DuplicateCopiesConfigurationWithoutHistoryOrAssignments()
    {
        var item = await Create(Draft(TrackerValueType.CustomUnit) with { Target = new(2.5m), EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Save(item, 0.7m); var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("Tag")); await organization.AssignAsync(Ref(item), OrganizationKind.Tag, tag, true);
        var copy = await service.DuplicateAsync(Ref(item)); Assert.NotEqual(item.Item.Id, copy.Item.Id); Assert.Equal(item.Draft, copy.Draft);
        Assert.False(copy.HasEntries); Assert.Empty(await service.GetEntriesAsync(Ref(copy), new(Recent: 50)));
        Assert.DoesNotContain((await organization.GetAsync(profile)).ItemTags, a => a.ItemId == copy.Item.Id);
    }
    [Fact]
    public async Task RangePeriodAndRecentQueriesAreBoundedAndProfileSwitchRejectsStaleActions()
    {
        var item = await Create(Draft() with { Schedule = new() });
        for (var offset = 0; offset < 12; offset++) await Save(item, offset, Day.AddDays(offset));
        Assert.Equal(3, (await service.GetEntriesAsync(Ref(item), new(Day.AddDays(3), Day.AddDays(5)))).Count);
        Assert.Equal(2, (await service.GetEntriesAsync(Ref(item), new(Recent: 2))).Count);
        Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day.AddDays(4))));
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.GetEntriesAsync(Ref(item), new()));
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.GetEntriesAsync(Ref(item), new(Recent: 501)));
        var other = await profiles.CreateAsync("Other"); Assert.Empty(await service.GetAsync(other.Id));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => Save(item, 999));
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => service.GetEntriesAsync(Ref(item), new(Recent: 1)));
        await profiles.SwitchAsync(profile); Assert.Equal(12, (await service.GetEntriesAsync(Ref(item), new(Recent: 50))).Count);
        var restarted = new TrackerService(new SqliteTrackerRepository(), current, gate, clock, NullLogger<TrackerService>.Instance);
        Assert.Equal(item.Draft, (await restarted.FindAsync(Ref(item)))!.Draft);
    }
    [Fact]
    public async Task InsertUpdateEntryAndDeleteFailuresRollbackAtomically()
    {
        await Sql("CREATE TRIGGER FailTracker BEFORE INSERT ON Trackers BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TrackerOperationException>(() => Create()); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
        await Sql("DROP TRIGGER FailTracker;"); var item = await Create(); var entry = await Save(item, 3);
        await Sql("CREATE TRIGGER FailEntry BEFORE UPDATE ON TrackerEntries BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TrackerOperationException>(() => Save(item, 4)); Assert.Equal(3m, await Total(item));
        await Sql("CREATE TRIGGER FailDefinition BEFORE UPDATE ON Trackers BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TrackerOperationException>(() => service.UpdateAsync(Ref(item), item.Draft with { Title = "Wrong" }));
        Assert.Equal(item.Item, (await service.FindAsync(Ref(item)))!.Item);
        await Sql("DROP TRIGGER FailDefinition;"); await service.ApplyAsync(Ref(item), TrackerAction.Trash);
        await Sql("CREATE TRIGGER FailDelete BEFORE DELETE ON TrackerEntries BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<TrackerOperationException>(() => service.PermanentlyDeleteAsync(Ref(item)));
        Assert.NotNull(await service.FindAsync(Ref(item))); Assert.Equal(entry, Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day))));
    }
    [Fact]
    public async Task ViewModelUsesLocalTodayPreservesDraftDuringEntryAndClearsProfileState()
    {
        clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Tracker UTC+14", TimeSpan.FromHours(14), "UTC+14", "UTC+14");
        var item = await Create(); var navigation = new NavigationService(); navigation.Navigate(new("Today"));
        var model = new TrackerWorkspaceViewModel(service, organization, current, navigation, clock, NullLogger<TrackerWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); Assert.Equal(Day.AddDays(1), Assert.Single(model.Rows).Period.PeriodDate);
        model.OpenCommand.Execute(model.Rows.Single()); await model.ReloadAsync(); model.Editor.Title = "Unsaved name";
        model.EntryValue = "4"; await model.SaveEntryCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.Equal("Unsaved name", model.Editor.Title);
        Assert.Single(model.Entries); Assert.True(model.Editor.Locked);
        await profiles.CreateAsync("Other"); await model.ReloadAsync(); Assert.Empty(model.Rows); Assert.Empty(model.Entries); Assert.Empty(model.EntryValue); Assert.Empty(model.Editor.Title);
        Assert.Equal("Trackers", navigation.Current.Destination); Assert.False(model.IsDetail);
        await profiles.SwitchAsync(profile); await model.ReloadAsync(); Assert.Single(model.Rows);
    }
    [Theory]
    [InlineData("pt-PT", "78,4")] [InlineData("en-US", "78.4")]
    public void PresentationUsesCultureAndSharedWholeSecondDuration(string culture, string number)
    {
        var previous = CultureInfo.CurrentCulture; try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(78.4m, TrackerPresentation.Parse(number, TrackerValueType.Decimal).Number);
            Assert.Equal(number, TrackerPresentation.Input(new(78.4m), TrackerValueType.Decimal));
            Assert.Equal(4530m, TrackerPresentation.Parse("1:15:30", TrackerValueType.Duration).Number);
            Assert.Equal("1:15:30", TrackerPresentation.Input(new(4530), TrackerValueType.Duration));
            Assert.Throws<TrackerValidationException>(() => TrackerPresentation.Parse("75", TrackerValueType.Duration));
            Assert.Throws<TrackerValidationException>(() => TrackerPresentation.Parse("1:60", TrackerValueType.Duration));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void NumericInputRejectsSilentPrecisionLossButAcceptsExactTrailingZeros()
    {
        var before = CultureInfo.CurrentCulture; try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Throws<TrackerValidationException>(() => TrackerPresentation.Parse("1.00000000000000000000000000001", TrackerValueType.Decimal));
            Assert.Equal(1m, TrackerPresentation.Parse("1.00000000000000000000000000000", TrackerValueType.Decimal).Number);
            Assert.Equal(-78.4m, TrackerPresentation.Parse("-78.4", TrackerValueType.Decimal).Number);
            Assert.Throws<TrackerValidationException>(() => TrackerPresentation.Parse("1,234.5", TrackerValueType.Decimal));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
    [Fact]
    public async Task DurationAverageRoundsOnlySummaryAndOverflowRejectsWrite()
    {
        var item = await Create(Draft(TrackerValueType.Duration) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Average });
        await Save(item, 1); await Save(item, 2); Assert.Equal(2m, await Total(item));
        Assert.Equal(new[] { 1m, 2m }, (await service.GetEntriesAsync(Ref(item), new(Period: Day))).Select(e => e.Value.Number!.Value).Order());
        var sum = await Create(Draft(TrackerValueType.Duration) with { EntryMode = TrackerEntryMode.Multiple, Aggregation = TrackerAggregation.Sum });
        await Save(sum, TrackerRules.MaximumDurationSeconds); await Assert.ThrowsAsync<TrackerValidationException>(() => Save(sum, 1));
        Assert.Single(await service.GetEntriesAsync(Ref(sum), new(Period: Day)));
    }
    [Fact]
    public async Task LastUsesStableLocalTimeThenCreationAndGuidTies()
    {
        var item = await Create(Draft() with { EntryMode = TrackerEntryMode.Multiple });
        var first = await Save(item, 1, hour: 10); var second = await Save(item, 2, hour: 10);
        var expected = new[] { first, second }.OrderBy(e => e.Id).Last(); Assert.Equal(expected.Value.Number, await Total(item));
        var late = await Save(item, 3, hour: 11); Assert.Equal(3m, await Total(item));
        await Save(item, 5, id: first.Id, hour: 23); Assert.Equal(3m, await Total(item));
        Assert.Equal(late.Id, (await service.GetEntriesAsync(Ref(item), new(Recent: 1))).Single().Id);
    }
    [Fact]
    public async Task HistoricalEditorKeepsSelectedPeriodAfterCorrection()
    {
        var item = await Create(Draft() with { Schedule = new(TrackerFrequency.Daily, Day.AddDays(-100)) });
        for (var offset = -60; offset <= 0; offset++) await Save(item, 1, Day.AddDays(offset));
        var navigation = new NavigationService(); navigation.Navigate(new("Tracker", item.Item.Id.ToString("D")));
        var model = new TrackerWorkspaceViewModel(service, organization, current, navigation, clock, NullLogger<TrackerWorkspaceViewModel>.Instance);
        await model.ReloadAsync(); Assert.Equal(50, model.Entries.Count);
        model.HistoryDate = TrackerEditor.Picker(Day.AddDays(-60)); await model.LoadPeriodCommand.ExecuteAsync(null);
        var row = Assert.Single(model.Entries); model.EditEntryCommand.Execute(row); model.EntryValue = "9"; await model.SaveEntryCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(row.Entry.Id, Assert.Single(model.Entries).Entry.Id); Assert.Equal(9m, Assert.Single(model.Entries).Entry.Value.Number);
        Assert.Equal(9m, await Total(item, Day.AddDays(-60)));
        navigation.Navigate(new("Tracker", Guid.NewGuid().ToString("D"))); await model.ReloadAsync(); Assert.False(model.IsDetail); Assert.Empty(model.Entries); Assert.NotNull(model.Error);
    }
    [Fact]
    public async Task ForeignKeysIdentityAndOwnershipRejectCrossEntityEntries()
    {
        var item = await Create(); var other = await Create(); var entry = await Save(item, 1);
        await Assert.ThrowsAsync<TrackerValidationException>(() => Save(other, 2, id: entry.Id));
        await Assert.ThrowsAsync<TrackerValidationException>(() => service.DeleteEntryAsync(Ref(other), entry.Id));
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"UPDATE TrackerEntries SET TrackerId='{Guid.NewGuid()}' WHERE Id='{entry.Id}';"));
        var id = Guid.NewGuid(); await Sql($"INSERT INTO WorkspaceItems VALUES('{id}',1,'Task identity','2026-09-29','2026-09-29',NULL,NULL);");
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO Trackers SELECT '{id}',ValueType,Unit,CurrencyCode,ScaleMin,ScaleMax,Target,TargetInteger,Frequency,StartDate,EndDate,Interval,Weekdays,EntryMode,Aggregation FROM Trackers WHERE ItemId='{item.Item.Id}';"));
        Assert.Equal(1m, await Total(item));
    }
    [Fact]
    public async Task ConcurrentSingleSavesAndCancellationKeepOneCanonicalEntry()
    {
        var item = await Create(); var entries = await Task.WhenAll(Save(item, 1), Save(item, 2)); Assert.Equal(entries[0].Id, entries[1].Id);
        Assert.Single(await service.GetEntriesAsync(Ref(item), new(Period: Day)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveEntryAsync(Ref(item), Day, new(12, 0), new(3), cancellationToken: cancellation.Token));
        Assert.Equal(2m, await Total(item));
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
        public DateOnly Now { get; set; } = Day;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override TimeZoneInfo LocalTimeZone => Zone;
        public override DateTimeOffset GetUtcNow() => new(Now.ToDateTime(new(12, 0)), TimeSpan.Zero);
    }
}
