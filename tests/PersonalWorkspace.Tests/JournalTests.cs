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

public sealed partial class TrackerTests
{
    private JournalService Journals() => new(new SqliteJournalRepository(), current, gate, clock, NullLogger<JournalService>.Instance);
    private WorkspaceItemReference JRef(JournalDefinition journal) => new(profile, journal.Item.Id);
    private Task<JournalDefinition> Journal(string title = "Daily") => Journals().CreateAsync(profile, title, "Daily reflection");
    private async Task<JournalField> Field(JournalDefinition journal, JournalFieldType type = JournalFieldType.ShortText, string name = "Field") =>
        (await Journals().SaveFieldAsync(JRef(journal), null, FieldDraft(type, name))).Fields.Last();
    private static JournalFieldDraft FieldDraft(JournalFieldType type, string name = "Field") => new(name, type,
        type == JournalFieldType.Currency ? " eur " : null, type == JournalFieldType.Scale ? 1 : null, type == JournalFieldType.Scale ? 5 : null,
        type is JournalFieldType.Select or JournalFieldType.MultiSelect ? new[] { new JournalOption(Guid.NewGuid(), "Low", 0), new(Guid.NewGuid(), "High", 1) } : null);
    private Task<JournalEntry?> JSave(JournalDefinition journal, JournalField field, JournalValue? value, DateOnly? date = null) => Journals().SaveEntryAsync(JRef(journal), date ?? Day, new Dictionary<Guid, JournalValue?> { [field.Id] = value });
    private Task<JournalSnapshot> JRead(JournalDefinition journal, DateOnly? date = null) => Journals().GetRangeAsync(JRef(journal), date ?? Day, date ?? Day);
    private TaskService JournalTasks() => new(new SqliteTaskRepository(), current, gate, clock, NullLogger<TaskService>.Instance);

    [Fact]
    public async Task JournalMigrationUpgradesPopulatedPhaseTenAndPreservesEightMigrations()
    {
        var legacy = Guid.NewGuid(); new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);", legacy);
        foreach (var migration in WorkspaceMigrationCatalog.All.Take(8)) await Sql(migration.Sql + $"INSERT INTO SchemaMigrations VALUES({migration.Version},'{migration.Name}','original');", legacy);
        var tracker = Guid.NewGuid(); var entry = Guid.NewGuid();
        await Sql($"""
            INSERT INTO WorkspaceItems VALUES('{tracker}',3,'Weight','2026-09-29T12:00:00+00:00','2026-09-29T12:00:00+00:00',NULL,NULL);
            INSERT INTO Trackers(ItemId,ValueType,Frequency,Interval,Weekdays,EntryMode,Aggregation) VALUES('{tracker}',1,0,1,0,0,4);
            INSERT INTO TrackerEntries(Id,TrackerId,ValueType,EntryMode,PeriodDate,LocalDate,LocalTime,Value,CreatedAtUtc,UpdatedAtUtc)
                VALUES('{entry}','{tracker}',1,0,'2026-09-29','2026-09-29','12:00:00.0000000','78.2','2026-09-29T12:00:00+00:00','2026-09-29T12:00:00+00:00');
            """, legacy);
        await initializer.InitializeAsync(legacy, false); await initializer.InitializeAsync(legacy, false);
        Assert.Equal(10L, await Sql("SELECT COUNT(*) FROM SchemaMigrations;", legacy)); Assert.Equal(8L, await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';", legacy));
        Assert.Equal("78.2", await Sql($"SELECT Value FROM TrackerEntries WHERE Id='{entry}';", legacy)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;", legacy));
        await using var global = await new SqliteConnectionFactory(paths).OpenAsync(); using var command = global.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'Journal%';"; Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task JournalDefinitionsAllowDuplicateNamesAndUseWorkspaceIdentity()
    {
        var first = await Journal("  Daily  "); var second = await Journal();
        Assert.Equal("Daily", first.Item.Title); Assert.NotEqual(first.Item.Id, second.Item.Id); Assert.Equal(WorkspaceItemType.Journal, first.Item.ItemType); Assert.Empty(first.Fields);
        Assert.Equal(2, (await Journals().GetAsync(profile)).Journals.Count); Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Trackers;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Tasks;"));
    }
    [Fact]
    public async Task JournalOpeningBlankDatesAndTodayDoesNotWriteAnything()
    {
        var journal = await Journal(); await Field(journal);
        var bytes = await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile));
        for (var i = -4; i < 5; i++) { Assert.Empty((await JRead(journal, Day.AddDays(i))).Entries); await Journals().GetAsync(profile, date: Day.AddDays(i)); }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(paths.WorkspaceDatabase(profile))); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
    }
    [Theory]
    [InlineData(JournalFieldType.ShortText)] [InlineData(JournalFieldType.LongText)] [InlineData(JournalFieldType.Number)]
    [InlineData(JournalFieldType.Percentage)] [InlineData(JournalFieldType.Currency)] [InlineData(JournalFieldType.Duration)]
    [InlineData(JournalFieldType.Checkbox)] [InlineData(JournalFieldType.Scale)] [InlineData(JournalFieldType.Select)]
    [InlineData(JournalFieldType.MultiSelect)] [InlineData(JournalFieldType.Date)] [InlineData(JournalFieldType.Link)]
    [InlineData(JournalFieldType.TaskReference)] [InlineData(JournalFieldType.TrackerReference)]
    public async Task JournalEveryFieldTypePersistsIndependentlyAndExactly(JournalFieldType type)
    {
        var journal = await Journal(); var field = await Field(journal, type);
        var value = type switch
        {
            JournalFieldType.ShortText => new JournalValue(Text: "Internal  spaces"),
            JournalFieldType.LongText => new(Text: "First line\nSecond  line\nThird line"),
            JournalFieldType.Number or JournalFieldType.Currency => new(Number: 12345678901234567890.123456789m),
            JournalFieldType.Percentage => new(Number: 72.125m), JournalFieldType.Duration => new(Integer: 4530),
            JournalFieldType.Checkbox => new(Integer: 0), JournalFieldType.Scale => new(Integer: 4),
            JournalFieldType.Select => new(OptionIds: [field.OrderedOptions[1].Id]),
            JournalFieldType.MultiSelect => new(OptionIds: field.OrderedOptions.Select(o => o.Id).ToArray()),
            JournalFieldType.Date => new(Date: new(2026, 10, 15)), JournalFieldType.Link => new(Text: "https://example.test/path?q=1"),
            JournalFieldType.TaskReference => new(ReferenceId: (await JournalTasks().CreateAsync(profile, new("Related task"))).Item.Id),
            _ => new(ReferenceId: (await Create()).Item.Id)
        };
        var saved = await JSave(journal, field, value); var loaded = Assert.Single((await JRead(journal)).Entries);
        Assert.Equal(saved!.Id, loaded.Id); Assert.True(JournalRules.Equal(value, loaded.Values[field.Id]));
        Assert.Equal(Day, loaded.Date); Assert.Equal(TimeSpan.Zero, loaded.CreatedAtUtc.Offset);
        Assert.True(Assert.Single((await JRead(journal)).Journals).Fields[0].HistoryLocked);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM JournalValues;"));
    }
    [Fact]
    public async Task JournalCorrectionUsesStableEntryAndSeparateNoOpTimestamps()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.ShortText);
        var definition = (await JRead(journal)).Journals[0]; var first = await JSave(journal, field, new(Text: "First"), Day.AddYears(-2));
        var unchanged = await JSave(journal, field, new(Text: "First"), Day.AddYears(-2)); Assert.Equal(first!.Id, unchanged!.Id); Assert.Equal(first.UpdatedAtUtc, unchanged.UpdatedAtUtc); Assert.True(JournalRules.Equal(first.Values[field.Id], unchanged.Values[field.Id]));
        var changed = await JSave(journal, field, new(Text: "Corrected"), Day.AddYears(-2));
        Assert.Equal(first!.Id, changed!.Id); Assert.Equal(first.CreatedAtUtc, changed.CreatedAtUtc); Assert.True(changed.UpdatedAtUtc > first.UpdatedAtUtc);
        Assert.Equal(definition.Item, (await JRead(journal)).Journals[0].Item); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
        var renamed = await Journals().UpdateAsync(JRef(journal), "Reflection", "Updated description"); Assert.True(renamed.Item.UpdatedAtUtc > definition.Item.UpdatedAtUtc);
        Assert.Equal(changed.UpdatedAtUtc, (await JRead(journal, Day.AddYears(-2))).Entries[0].UpdatedAtUtc);
    }
    [Fact]
    public async Task JournalBlankAndFalseHaveDistinctDeterministicLazySemantics()
    {
        var journal = await Journal(); var text = await Field(journal); var checkbox = await Field(journal, JournalFieldType.Checkbox);
        Assert.Null(await JSave(journal, text, new(Text: " \n "))); Assert.Null(await JSave(journal, checkbox, null));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
        var entry = await JSave(journal, checkbox, new(Integer: 0)); Assert.NotNull(entry); Assert.Equal(0, entry.Values[checkbox.Id].Integer);
        Assert.Null(await JSave(journal, checkbox, null)); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
        Assert.True((await JRead(journal)).Journals[0].Fields.Single(f => f.Id == checkbox.Id).HistoryLocked);
    }
    [Theory]
    [InlineData("0")] [InlineData("100")] [InlineData("12.50")]
    public async Task JournalPercentageAcceptsExactBounds(string text)
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Percentage); var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        Assert.Equal(value, (await JSave(journal, field, new(Number: value)))!.Values[field.Id].Number);
    }
    [Theory]
    [InlineData(JournalFieldType.Percentage, -1)] [InlineData(JournalFieldType.Percentage, 101)]
    [InlineData(JournalFieldType.Scale, 0)] [InlineData(JournalFieldType.Scale, 6)]
    [InlineData(JournalFieldType.Duration, -1)] [InlineData(JournalFieldType.Checkbox, 2)]
    public async Task JournalInvalidBoundsDoNotCreatePhysicalEntry(JournalFieldType type, int number)
    {
        var journal = await Journal(); var field = await Field(journal, type);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, type == JournalFieldType.Percentage ? new(Number: number) : new(Integer: number)));
        Assert.Empty((await JRead(journal)).Entries);
    }
    [Theory]
    [InlineData("relative/path")] [InlineData("javascript:alert(1)")] [InlineData("file:///C:/private")]
    public async Task JournalLinkRejectsUnsupportedUrisWithoutFetching(string uri)
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Link);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, new(Text: uri)));
    }
    [Fact]
    public async Task JournalExactValuesHaveTextStorageAndDurationHasIntegerStorage()
    {
        var journal = await Journal(); var number = await Field(journal, JournalFieldType.Number); var duration = await Field(journal, JournalFieldType.Duration);
        await JSave(journal, number, new(Number: .1234567890123456789012345678m)); await JSave(journal, duration, new(Integer: 2700));
        Assert.Equal("text", await Sql("SELECT typeof(DecimalValue) FROM JournalValues WHERE FieldType=2;")); Assert.Equal("integer", await Sql("SELECT typeof(IntegerValue) FROM JournalValues WHERE FieldType=5;"));
        Assert.Equal("0.1234567890123456789012345678", await Sql("SELECT DecimalValue FROM JournalValues WHERE FieldType=2;"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM TrackerEntries;"));
    }
    [Theory]
    [InlineData(JournalFieldType.Select)] [InlineData(JournalFieldType.MultiSelect)]
    public async Task JournalOptionsKeepStableHistoryAfterRenameAndRejectDeletion(JournalFieldType type)
    {
        var journal = await Journal(); var field = await Field(journal, type); var option = field.OrderedOptions[1];
        var entry = await JSave(journal, field, new(OptionIds: [option.Id]));
        var changed = await Journals().SaveFieldAsync(JRef(journal), field.Id, new("Focus", type, Options: field.OrderedOptions.Select(o => o.Id == option.Id ? o with { Label = "Deep focus" } : o).ToArray()));
        var day = await JRead(journal); Assert.Equal(option.Id, Assert.Single(day.Entries[0].Values[field.Id].OptionIds!)); Assert.Equal(entry!.Id, day.Entries[0].Id);
        Assert.Contains("Deep focus", JournalPresentation.Rows(profile, Day, day)[0].Summary);
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), field.Id, new("Focus", type, Options: [field.OrderedOptions[0]])));
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, new(OptionIds: [Guid.NewGuid()])));
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, new(OptionIds: [option.Id, option.Id])));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM JournalSelections;"));
    }
    [Fact]
    public async Task JournalFieldConfigurationAndOrderingAreHistorySafe()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Scale, "Mood"); var next = await Field(journal, name: "Notes");
        await JSave(journal, field, new(Integer: 4));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), field.Id, new("Mood", JournalFieldType.ShortText)));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), field.Id, new("Mood", JournalFieldType.Scale, ScaleMin: 1, ScaleMax: 3)));
        await Journals().SaveFieldAsync(JRef(journal), field.Id, new("Energy", JournalFieldType.Scale, ScaleMin: 1, ScaleMax: 5));
        var moved = await Journals().MoveFieldAsync(JRef(journal), next.Id, -1); Assert.Equal(next.Id, moved.Fields[0].Id); Assert.Equal(new[] { 0, 1 }, moved.Fields.Select(f => f.SortOrder));
        Assert.Equal(next.Id, (await JRead(journal)).Journals[0].Fields[0].Id);
        await JSave(journal, field, null); await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), field.Id, new("Mood", JournalFieldType.Number)));
    }
    [Fact]
    public async Task JournalUsedFieldDeletionRequiresServiceConfirmationAndKeepsEntry()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Select); var entry = await JSave(journal, field, new(OptionIds: [field.OrderedOptions[0].Id]));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().DeleteFieldAsync(JRef(journal), field.Id));
        await Journals().DeleteFieldAsync(JRef(journal), field.Id, true);
        var after = Assert.Single((await JRead(journal)).Entries); Assert.Equal(entry!.Id, after.Id); Assert.Empty(after.Values);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalSelections;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalFieldOptions;"));
    }
    [Theory]
    [InlineData(JournalFieldType.TaskReference)] [InlineData(JournalFieldType.TrackerReference)]
    public async Task JournalReferencesRetainLifecycleAndClearSafelyOnPermanentDeletion(JournalFieldType type)
    {
        var journal = await Journal(); var field = await Field(journal, type); var taskService = JournalTasks();
        var id = type == JournalFieldType.TaskReference ? (await taskService.CreateAsync(profile, new("Related task"))).Item.Id : (await Create()).Item.Id;
        var saved = await JSave(journal, field, new(ReferenceId: id));
        if (type == JournalFieldType.TaskReference) await taskService.ApplyAsync(new(profile, id), TaskAction.Archive); else await service.ApplyAsync(new(profile, id), TrackerAction.Archive);
        Assert.Contains("archived", JournalPresentation.Rows(profile, Day, await JRead(journal))[0].Summary);
        if (type == JournalFieldType.TaskReference) await taskService.ApplyAsync(new(profile, id), TaskAction.Trash); else await service.ApplyAsync(new(profile, id), TrackerAction.Trash);
        Assert.Equal(id, (await JRead(journal)).Entries[0].Values[field.Id].ReferenceId); Assert.Contains("in Trash", JournalPresentation.Rows(profile, Day, await JRead(journal))[0].Summary);
        if (type == JournalFieldType.TaskReference) await taskService.PermanentlyDeleteAsync(new(profile, id)); else await service.PermanentlyDeleteAsync(new(profile, id));
        var after = (await JRead(journal)).Entries[0]; Assert.Equal(saved!.Id, after.Id); Assert.Null(after.Values[field.Id].ReferenceId); Assert.Contains("Reference removed", JournalPresentation.Rows(profile, Day, await JRead(journal))[0].Summary);
    }
    [Fact]
    public async Task JournalCrossProfileAndWrongTypeReferencesAreRejected()
    {
        var journal = await Journal(); var taskField = await Field(journal, JournalFieldType.TaskReference); var trackerField = await Field(journal, JournalFieldType.TrackerReference);
        var tracker = await Create(); await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, taskField, new(ReferenceId: tracker.Item.Id)));
        var other = await profiles.CreateAsync("Journal foreign"); var foreignTask = await JournalTasks().CreateAsync(other.Id, new("Foreign task")); var foreignTracker = await service.CreateAsync(other.Id, Draft());
        Assert.Empty((await Journals().GetAsync(other.Id, date: Day)).Journals); await Assert.ThrowsAsync<WorkspaceChangedException>(() => JRead(journal));
        await profiles.SwitchAsync(profile);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, taskField, new(ReferenceId: foreignTask.Item.Id)));
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, trackerField, new(ReferenceId: foreignTracker.Item.Id)));
        Assert.Empty((await JRead(journal)).Entries);
    }
    [Fact]
    public async Task JournalDuplicationCopiesOnlyDefinitionWithNewFieldAndOptionIds()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.MultiSelect); await JSave(journal, field, new(OptionIds: field.OrderedOptions.Select(o => o.Id).ToArray()));
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("Journal tag")); await organization.AssignAsync(JRef(journal), OrganizationKind.Tag, tag, true);
        var copy = await Journals().DuplicateAsync(JRef(journal)); Assert.NotEqual(journal.Item.Id, copy.Item.Id); Assert.Equal(journal.Description, copy.Description);
        var copied = Assert.Single(copy.Fields); Assert.NotEqual(field.Id, copied.Id); Assert.Equal(field.OrderedOptions.Select(o => o.Label), copied.OrderedOptions.Select(o => o.Label));
        Assert.Empty(field.OrderedOptions.Select(o => o.Id).Intersect(copied.OrderedOptions.Select(o => o.Id))); Assert.False(copied.HistoryLocked); Assert.Empty((await JRead(copy)).Entries);
        Assert.DoesNotContain((await organization.GetAsync(profile)).ItemTags, a => a.ItemId == copy.Item.Id);
    }
    [Fact]
    public async Task JournalLifecyclePreservesHistoryAndPermanentDeleteCascadesOnlyItsData()
    {
        var journal = await Journal(); var other = await Journal("Training"); var field = await Field(journal, JournalFieldType.MultiSelect); await JSave(journal, field, new(OptionIds: field.OrderedOptions.Select(o => o.Id).ToArray()));
        await Journals().ApplyAsync(JRef(journal), JournalAction.Archive); Assert.Single((await Journals().GetAsync(profile)).Journals); Assert.Single((await JRead(journal)).Entries);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, null));
        await Journals().ApplyAsync(JRef(journal), JournalAction.RestoreArchive); await Journals().ApplyAsync(JRef(journal), JournalAction.Trash); Assert.Single((await Journals().GetAsync(profile, JournalCollection.Trash)).Journals);
        await Journals().ApplyAsync(JRef(journal), JournalAction.RestoreTrash); Assert.Equal(2, (await Journals().GetAsync(profile)).Journals.Count);
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().PermanentlyDeleteAsync(JRef(journal)));
        await Journals().ApplyAsync(JRef(journal), JournalAction.Trash); await Journals().PermanentlyDeleteAsync(JRef(journal));
        Assert.Equal(other.Item.Id, Assert.Single((await Journals().GetAsync(profile)).Journals).Item.Id);
        foreach (var table in new[] { "JournalFields", "JournalFieldOptions", "JournalEntries", "JournalValues", "JournalSelections" }) Assert.Equal(0L, await Sql($"SELECT COUNT(*) FROM {table};"));
    }
    [Fact]
    public async Task JournalWritesRollbackCreationDuplicationAndAllChangedFields()
    {
        await Sql("CREATE TRIGGER FailJournal BEFORE INSERT ON Journals BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<JournalOperationException>(() => Journal()); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;")); await Sql("DROP TRIGGER FailJournal;");
        var journal = await Journal(); var first = await Field(journal, name: "First"); var second = await Field(journal, name: "Second");
        await JSave(journal, first, new(Text: "old")); await JSave(journal, second, new(Text: "old")); var entry = (await JRead(journal)).Entries[0];
        await Sql($"CREATE TRIGGER FailValue BEFORE INSERT ON JournalValues WHEN NEW.FieldId='{second.Id}' BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<JournalOperationException>(() => Journals().SaveEntryAsync(JRef(journal), Day, new Dictionary<Guid, JournalValue?> { [first.Id] = new(Text: "new"), [second.Id] = new(Text: "new") }));
        var restored = (await JRead(journal)).Entries[0]; Assert.All(restored.Values.Values, value => Assert.Equal("old", value.Text)); Assert.Equal(entry.UpdatedAtUtc, restored.UpdatedAtUtc);
        await Sql("CREATE TRIGGER FailField BEFORE INSERT ON JournalFields BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<JournalOperationException>(() => Journals().DuplicateAsync(JRef(journal))); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Journals;")); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM WorkspaceItems;"));
    }
    [Fact]
    public async Task JournalDateQueriesAreBoundedAndPartialSavesPreserveOtherFields()
    {
        var journal = await Journal(); var first = await Field(journal); var second = await Field(journal, name: "Other");
        for (var i = 0; i < 12; i++) await JSave(journal, first, new(Text: i.ToString()), Day.AddDays(i));
        Assert.Single((await JRead(journal)).Entries); Assert.Equal(3, (await Journals().GetRangeAsync(JRef(journal), Day.AddDays(3), Day.AddDays(5))).Entries.Count);
        await JSave(journal, second, new(Text: "Kept")); Assert.Equal(2, (await JRead(journal)).Entries[0].Values.Count);
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().GetRangeAsync(JRef(journal), Day, Day.AddDays(366)));
        await Assert.ThrowsAsync<JournalValidationException>(() => new SqliteJournalRepository().ReadAsync(new(profile, paths.WorkspaceDatabase(profile)), journal.Item.Id, JournalCollection.Active, Day, null, default));
        Assert.Empty((await Journals().GetAsync(profile)).Entries);
    }
    [Fact]
    public async Task JournalConcurrentFirstSavesKeepOneEntryAndSqlUniqueness()
    {
        var journal = await Journal(); var first = await Field(journal); var second = await Field(journal, name: "Other");
        var saved = await Task.WhenAll(JSave(journal, first, new(Text: "A")), JSave(journal, second, new(Text: "B")));
        Assert.Equal(saved[0]!.Id, saved[1]!.Id); Assert.Equal(2, (await JRead(journal)).Entries[0].Values.Count);
        await Assert.ThrowsAsync<SqliteException>(() => Sql($"INSERT INTO JournalEntries VALUES('{Guid.NewGuid()}','{journal.Item.Id}','{Day:yyyy-MM-dd}','now','now');"));
    }
    [Fact]
    public async Task JournalMultipleDefinitionsRemainIndependentOnSameDateAndGenericAssignmentsWork()
    {
        var first = await Journal(); var second = await Journal("Training"); var a = await Field(first); var b = await Field(second);
        await JSave(first, a, new(Text: "Daily")); await JSave(second, b, new(Text: "Training"));
        Assert.Equal("Daily", (await JRead(first)).Entries[0].Values[a.Id].Text); Assert.Equal("Training", (await JRead(second)).Entries[0].Values[b.Id].Text);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(first, b, new(Text: "Wrong owner")));
        var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("Reflection")); var space = await organization.SaveAsync(profile, OrganizationKind.Space, null, new("Personal"));
        await organization.AssignAsync(JRef(first), OrganizationKind.Tag, tag, true); await organization.AssignAsync(JRef(first), OrganizationKind.Space, space, true);
        var catalog = await organization.GetAsync(profile); Assert.Contains(new(first.Item.Id, tag), catalog.ItemTags); Assert.Contains(new(first.Item.Id, space), catalog.ItemSpaces);
    }
    [Fact]
    public async Task JournalTodayCalendarAndProfileSwitchClearPresentationWithoutCreatingRows()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Scale, "Mood"); var navigation = new NavigationService();
        var model = new JournalWorkspaceViewModel(Journals(), organization, current, navigation, clock, NullLogger<JournalWorkspaceViewModel>.Instance);
        navigation.Navigate(new("Today")); await model.ReloadAsync(); Assert.False(Assert.Single(model.Rows).HasContent); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
        await JSave(journal, field, new(Integer: 4)); await model.ReloadAsync(); Assert.True(Assert.Single(model.Rows).HasContent);
        var events = new EventService(new SqliteEventRepository(), current, gate, clock, NullLogger<EventService>.Instance);
        var calendar = new CalendarViewModel(events, JournalTasks(), current, navigation, clock, NullLogger<CalendarViewModel>.Instance, journals: Journals());
        navigation.Navigate(new("Calendar")); calendar.Mode = CalendarMode.Day; await calendar.ReloadAsync(); Assert.Contains("Mood: 4", Assert.Single(calendar.JournalRows).Summary);
        calendar.SelectedDate = JournalPresentation.Picker(Day.AddDays(-1)); await calendar.ReloadAsync(); Assert.Equal("No entry", Assert.Single(calendar.JournalRows).Summary); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
        calendar.OpenJournalCommand.Execute(calendar.JournalRows[0]); await model.ReloadAsync(); Assert.Equal(Day.AddDays(-1), DateOnly.FromDateTime(model.SelectedDate.DateTime));
        await profiles.CreateAsync("Journal isolated"); await model.ReloadAsync(); Assert.Empty(model.Rows); Assert.Empty(model.Inputs); Assert.Empty(model.Fields); Assert.Empty(model.Assignments);
        await profiles.SwitchAsync(profile); await model.ReloadAsync(); Assert.Equal("Daily", model.Heading);
        Assert.Equal(4, (await JRead(journal)).Entries[0].Values[field.Id].Integer);
    }
    [Fact]
    public async Task JournalUnusedFieldCanChangeTypeButCurrencyLocksAfterFirstValue()
    {
        var journal = await Journal(); var field = await Field(journal);
        var changed = await Journals().SaveFieldAsync(JRef(journal), field.Id, FieldDraft(JournalFieldType.Currency));
        Assert.Equal("EUR", changed.Fields[0].CurrencyCode); await JSave(journal, changed.Fields[0], new(Number: 12.50m));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), field.Id, new("Money", JournalFieldType.Currency, "USD")));
        Assert.Equal("EUR", (await JRead(journal)).Journals[0].Fields[0].CurrencyCode);
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), null, new("Bad currency", JournalFieldType.Currency, "EU")));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), null, new("Bad scale", JournalFieldType.Scale, ScaleMin: 5, ScaleMax: 5)));
    }
    [Fact]
    public async Task JournalOptionsCanReorderAndUnusedOptionsCanBeRemovedWithoutChangingSelections()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.MultiSelect); var used = field.OrderedOptions[1];
        await JSave(journal, field, new(OptionIds: [used.Id]));
        await Journals().SaveFieldAsync(JRef(journal), field.Id, new("Choices", field.Type, Options: field.OrderedOptions.Reverse().ToArray()));
        Assert.Equal(used.Id, (await JRead(journal)).Journals[0].Fields[0].OrderedOptions[0].Id);
        await Journals().SaveFieldAsync(JRef(journal), field.Id, new("Choices", field.Type, Options: [used]));
        Assert.Equal(used.Id, Assert.Single((await JRead(journal)).Entries[0].Values[field.Id].OptionIds!)); Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM JournalFieldOptions;"));
    }
    [Fact]
    public async Task JournalCannotBorrowAnotherFieldsOptionIdentity()
    {
        var journal = await Journal(); var first = await Field(journal, JournalFieldType.Select, "First"); var second = await Field(journal, JournalFieldType.Select, "Second");
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, second, new(OptionIds: [first.OrderedOptions[0].Id])));
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveFieldAsync(JRef(journal), second.Id, new("Second", second.Type, Options: [first.OrderedOptions[0]])));
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM JournalFieldOptions;")); Assert.Equal(first.Id, (await JRead(journal)).Journals[0].Fields[0].Id);
    }
    [Fact]
    public async Task JournalSaveRejectsMixedTypedValuesAndInvalidSingleSelectionsAtomically()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Number); var choice = await Field(journal, JournalFieldType.Select);
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, field, new(Text: "wrong", Number: 1)));
        await Assert.ThrowsAsync<JournalValidationException>(() => JSave(journal, choice, new(OptionIds: choice.OrderedOptions.Select(o => o.Id).ToArray())));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;"));
    }
    [Fact]
    public async Task JournalSaveValidationRollsBackAllFieldsAndPreservesFirstUseLocks()
    {
        var journal = await Journal(); var first = await Field(journal); var scale = await Field(journal, JournalFieldType.Scale);
        await Assert.ThrowsAsync<JournalValidationException>(() => Journals().SaveEntryAsync(JRef(journal), Day, new Dictionary<Guid, JournalValue?> { [first.Id] = new(Text: "Should not save"), [scale.Id] = new(Integer: 99) }));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalEntries;")); Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM JournalFields WHERE HistoryLocked=1;"));
    }
    [Fact]
    public async Task JournalReferenceDisplaysCurrentTitleRatherThanCopyingIt()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.TrackerReference); var tracker = await Create();
        await JSave(journal, field, new(ReferenceId: tracker.Item.Id)); await service.UpdateAsync(Ref(tracker), tracker.Draft with { Title = "Renamed Tracker" });
        Assert.Contains("Renamed Tracker", JournalPresentation.Rows(profile, Day, await JRead(journal))[0].Summary);
        Assert.Null((await JRead(journal)).Entries[0].Values[field.Id].Text);
    }
    [Fact]
    public async Task JournalArchiveFromDetailReturnsToAnActiveDefinition()
    {
        var journal = await Journal(); var other = await Journal("Training"); var navigation = new NavigationService(); navigation.Navigate(new("Journal", $"{journal.Item.Id:D}|{Day:yyyy-MM-dd}"));
        var model = new JournalWorkspaceViewModel(Journals(), organization, current, navigation, clock, NullLogger<JournalWorkspaceViewModel>.Instance); await model.ReloadAsync();
        await model.ApplyAsync(model.DetailRow!, JournalAction.Archive);
        Assert.Equal(other.Item.Id, model.DetailRow!.Journal.Item.Id); Assert.DoesNotContain(model.Rows, r => r.Journal.Item.Id == journal.Item.Id);
    }
    [Fact]
    public async Task JournalTodayUsesInjectedLocalClockAndSummaryUsesFirstTwoOrderedFields()
    {
        clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Journal +14", TimeSpan.FromHours(14), "Journal +14", "Journal +14");
        var journal = await Journal(); var first = await Field(journal, name: "First"); var second = await Field(journal, name: "Second"); var third = await Field(journal, name: "Third");
        foreach (var field in new[] { first, second, third }) await JSave(journal, field, new(Text: field.Name), Day.AddDays(1));
        var model = new JournalWorkspaceViewModel(Journals(), organization, current, new NavigationService(), clock, NullLogger<JournalWorkspaceViewModel>.Instance); await model.ReloadAsync();
        var row = Assert.Single(model.Rows); Assert.Equal(Day.AddDays(1), row.Date); Assert.Contains("First", row.Summary); Assert.Contains("Second", row.Summary); Assert.DoesNotContain("Third", row.Summary);
    }
    [Fact]
    public async Task JournalBlankInputDefaultsDoNotPersistFalseAndCancellationWritesNothing()
    {
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.Checkbox); var input = new JournalFieldInput(field, null, []); Assert.Null(input.Build());
        input.Choice = input.Choices[1]; Assert.Equal(0, input.Build()!.Integer);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Journals().SaveEntryAsync(JRef(journal), Day, new Dictionary<Guid, JournalValue?> { [field.Id] = input.Build() }, cancellation.Token));
        Assert.Empty((await JRead(journal)).Entries);
    }
    [Fact]
    public async Task JournalPermanentDeleteFromDetailClearsDeletedSelection()
    {
        var journal = await Journal(); var other = await Journal("Training");
        await Journals().ApplyAsync(JRef(journal), JournalAction.Trash);
        var navigation = new NavigationService(); navigation.Navigate(new("Journal", $"{journal.Item.Id:D}|{Day:yyyy-MM-dd}"));
        var model = new JournalWorkspaceViewModel(Journals(), organization, current, navigation, clock, NullLogger<JournalWorkspaceViewModel>.Instance); await model.ReloadAsync();
        await model.DeleteAsync(model.DetailRow!);
        Assert.Null(model.Error); Assert.Equal(other.Item.Id, model.DetailRow!.Journal.Item.Id);
        Assert.DoesNotContain(model.Rows, r => r.Journal.Item.Id == journal.Item.Id);
    }
    [Fact]
    public async Task JournalProfileEventReloadsEvenWhenNavigationRouteDoesNotChange()
    {
        await Journal(); var navigation = new NavigationService(); navigation.Navigate(new("Journal"));
        var model = new JournalWorkspaceViewModel(Journals(), organization, current, navigation, clock, NullLogger<JournalWorkspaceViewModel>.Instance); await model.ReloadAsync(); Assert.Single(model.Rows);
        await profiles.CreateAsync("Journal event other");
        for (var i = 0; i < 200 && model.IsBusy; i++) await Task.Delay(10);
        Assert.Empty(model.Rows); Assert.Empty(model.Inputs);
        await profiles.SwitchAsync(profile);
        for (var i = 0; i < 200 && (model.IsBusy || model.Rows.Count == 0); i++) await Task.Delay(10);
        Assert.Single(model.Rows); Assert.Equal("Daily", model.Heading);
    }
}
