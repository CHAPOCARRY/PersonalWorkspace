using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private ActivityHistoryService History() => new(new SqliteActivityHistoryRepository(), current, gate);
    private async Task ResetHistory() => await History().ClearAsync(profile, (await History().ReadAsync(profile, new())).Epoch);
    private async Task<AuditDetails> OnlyActivity()
    { var e = Assert.Single((await History().ReadAsync(profile, new())).Events); return await History().DetailsAsync(profile, e.Sequence); }
    private async Task<Dictionary<string, string>> AuditDatabaseSnapshot(Guid? id = null)
    {
        var tables = new List<string>();
        using (var c = await Connection(id)) using (var cmd = c.CreateCommand())
        { cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name<>'sqlite_sequence' ORDER BY name"; using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) tables.Add(r.GetString(0)); }
        var result = new Dictionary<string, string>(); foreach (var table in tables) result.Add(table, await TableRows(id ?? profile, table)); return result;
    }
    [Fact]
    public async Task ActivityTaskExactValuesNoOpAndParentCompletionAreOneOperation()
    {
        var tasks = JournalTasks(); var parent = await tasks.CreateAsync(profile, new("Read")); var p = new TaskReference(profile, parent.Item.Id);
        var child = await tasks.CreateSubtaskAsync(p, "Pages"); var r = new TaskReference(profile, child.Item.Id);
        await tasks.UpdateAsync(r, new("Pages", Value: new(TaskValueType.CustomUnit, 20m, Unit: "pages")));
        await ResetHistory(); await tasks.UpdateAsync(r, new("Pages", Value: new(TaskValueType.CustomUnit, 30m, Unit: "pages")));
        var update = await OnlyActivity(); var target = Assert.Single(update.Changes, c => c.Field == "Target"); Assert.Equal("20", target.Before.Value); Assert.Equal("30", target.After.Value);
        await tasks.UpdateAsync(r, new("Pages", Value: new(TaskValueType.CustomUnit, 30m, Unit: "pages"))); Assert.Single((await History().ReadAsync(profile, new())).Events);
        await ResetHistory(); await tasks.RecordActualAsync(r, 30m); var complete = await OnlyActivity(); Assert.Equal(child.Item.Id, complete.Event.Target.Id); Assert.Equal(AuditAction.Completed, complete.Event.Action);
        Assert.Equal(2, complete.Changes.Count(c => c.Field == "Status" && c.After.Value == "Done"));
    }
    [Theory]
    [InlineData(TrackerValueType.Integer)][InlineData(TrackerValueType.Decimal)][InlineData(TrackerValueType.Percentage)]
    [InlineData(TrackerValueType.Currency)][InlineData(TrackerValueType.Duration)][InlineData(TrackerValueType.Distance)]
    [InlineData(TrackerValueType.Boolean)][InlineData(TrackerValueType.Scale)][InlineData(TrackerValueType.CustomUnit)]
    public async Task ActivityTrackerCorrectionsPreserveIdentityAndExactTypedValues(TrackerValueType type)
    {
        var tracker = await Create(Draft(type));
        var first = type == TrackerValueType.Boolean ? new TrackerValue(Boolean: false) : new TrackerValue(type == TrackerValueType.Scale ? 1 : 75);
        var second = type == TrackerValueType.Boolean ? new TrackerValue(Boolean: true) : new TrackerValue(type == TrackerValueType.Duration ? 4530 : type == TrackerValueType.Scale ? 4 : type == TrackerValueType.Integer ? 78 : 78.200000000000000000000000001m);
        var entry = await service.SaveEntryAsync(Ref(tracker), Day, new(12, 0), first);
        await ResetHistory(); var saved = await service.SaveEntryAsync(Ref(tracker), Day, new(12, 0), second);
        Assert.Equal(entry.Id, saved.Id); var detail = await OnlyActivity(); Assert.Equal(entry.Id, detail.Event.Target.Id);
        var change = Assert.Single(detail.Changes, c => c.Field == "Value");
        Assert.Equal(type == TrackerValueType.Boolean ? "true" : ExactValueText.Store(second.Number!.Value), change.After.Value);
        if (type == TrackerValueType.Duration) Assert.Equal(AuditValueKind.Duration, change.After.Kind);
        var route = await History().ResolveAsync(profile, detail.Event.Target); Assert.Equal(entry.Id, route.Context!.ChildId); Assert.Equal(Day, route.Context.PeriodDate);
    }
    [Fact]
    public async Task ActivityHistoricalJournalCorrectionIsPrivateAndCannotNavigateToReplacementEntry()
    {
        var journal = await Journal(); var number = await Field(journal, JournalFieldType.Number, "Pages"); var text = await Field(journal, JournalFieldType.LongText, "Private thoughts");
        var date = Day.AddYears(-3); var entry = await JSave(journal, number, new(Number: 20), date); await JSave(journal, text, new(Text: "private original"), date);
        await ResetHistory(); await Journals().SaveEntryAsync(JRef(journal), date, new Dictionary<Guid, JournalValue?> { [number.Id] = new(Number: 30), [text.Id] = new(Text: "private corrected") });
        var detail = await OnlyActivity(); Assert.Equal(entry!.Id, detail.Event.Target.Id); Assert.Equal(date, detail.Event.Target.Date);
        Assert.Contains(detail.Changes, c => c.Before.Value == "20" && c.After.Value == "30");
        var body = Assert.Single(detail.Changes, c => c.Field.EndsWith(":Text")); Assert.Equal(AuditValueState.Omitted, body.Before.State); Assert.Null(body.After.Value);
        Assert.Equal($"{journal.Item.Id:D}|{date:yyyy-MM-dd}", (await History().ResolveAsync(profile, detail.Event.Target)).EntityId);
        await Journals().SaveEntryAsync(JRef(journal), date, new Dictionary<Guid, JournalValue?> { [number.Id] = null, [text.Id] = null });
        await JSave(journal, number, new(Number: 10), date);
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ResolveAsync(profile, detail.Event.Target));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM AuditEventChanges WHERE BeforeValue LIKE '%private%' OR AfterValue LIKE '%private%'"));
    }
    [Fact]
    public async Task ActivityBulkListReorderAndUncheckAreGroupedWithoutIntermediatePositions()
    {
        var list = await List(); var a = await ListAdd(list, "A", 2, 3.25m); var b = await ListAdd(list, "B");
        await Lists().SetCheckedAsync(new(profile, list.Item.Id, a.Id), true); await Lists().SetCheckedAsync(new(profile, list.Item.Id, b.Id), true);
        await ResetHistory(); await Lists().UncheckAllAsync(LRef(list)); Assert.Equal(2, (await OnlyActivity()).Changes.Count(c => c.Field == "Checked"));
        await ResetHistory(); await Lists().ReorderAsync(new(profile, list.Item.Id, b.Id), -1);
        var detail = await OnlyActivity(); Assert.Equal(2, detail.Changes.Count); Assert.All(detail.Changes, c => { Assert.Contains(c.Before.Value, new[] { "0", "1" }); Assert.Contains(c.After.Value, new[] { "0", "1" }); });
        await ResetHistory(); await Lists().UpdateItemAsync(new(profile, list.Item.Id, a.Id), new("A", 3, 3.250000000000000000000000001m));
        Assert.Contains((await OnlyActivity()).Changes, c => c.Field == "Price" && c.After.Value == "3.250000000000000000000000001");
    }
    [Fact]
    public async Task ActivityRecurrenceMaterializationAndCarryRecalculationNeverFloodTimeline()
    {
        var tasks = JournalTasks(); var recurrence = WidgetRecurrence(); var task = await tasks.CreateAsync(profile, new("Read", Value: new(TaskValueType.Number, 20)));
        var reference = new TaskReference(profile, task.Item.Id); await recurrence.SetRuleAsync(reference, new(RecurrencePattern.Daily, Day)); await recurrence.SetCarrySettingsAsync(reference, true, true);
        await ResetHistory(); var entries = await recurrence.GetRangeAsync(profile, Day, Day.AddDays(8)); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        var first = entries[0].Occurrence; await recurrence.UpdateAsync(new(profile, first.Id), first.OccurrenceDate, TaskStatus.ToDo, 10m, false);
        var detail = await OnlyActivity(); Assert.Contains(detail.Changes, c => c.Field == "Actual" && c.After.Value == "10");
        Assert.DoesNotContain(detail.Changes, c => c.Field.Contains("CarryIn") || c.Field.Contains("EffectiveTarget") || c.Field.Contains("CarryOut"));
        await recurrence.GetRangeAsync(profile, Day, Day.AddDays(20)); await recurrence.GetHistoryAsync(reference, Day, Day.AddDays(20)); Assert.Single((await History().ReadAsync(profile, new())).Events);
    }
    [Theory]
    [InlineData(TemplateType.Task)][InlineData(TemplateType.List)][InlineData(TemplateType.Journal)][InlineData(TemplateType.Page)]
    public async Task ActivityTemplateInstantiationIsOneGroupWithProvenance(TemplateType type)
    {
        var source = type switch { TemplateType.Task => (await JournalTasks().CreateAsync(profile, new("Task"))).Item, TemplateType.List => (await List()).Item,
            TemplateType.Journal => (await Journal()).Item, _ => (await TemplateDashboard()).Item };
        var template = await Capture(source, type); await ResetHistory();
        var result = await Templates().InstantiateAsync(TRef(template), "From template"); var detail = await OnlyActivity();
        Assert.Equal(AuditAction.TemplateInstantiated, detail.Event.Action); Assert.Equal(result.Id, detail.Event.Target.Id);
        Assert.Contains(detail.Targets, t => t.Id == template.Metadata.Id && t.Role == "Source");
        await ResetHistory(); await Templates().RenameAsync(TRef(template), "Renamed", "private description"); Assert.Equal(AuditAction.TemplateUpdated, (await OnlyActivity()).Event.Action);
    }
    [Fact]
    public async Task ActivityWidgetRemovalGroupsConnectorDetachmentAndRetainsDeletedTargetContext()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Workout")); var widget = await WAdd(page, task.Item);
        var connector = await Connector(page, (await Layout(page)).Items.Single(i => i.Id == widget.CanvasItemId));
        await ResetHistory(); await WEdit(page, new RemoveWidget(widget.Id, true)); var detail = await OnlyActivity();
        Assert.Equal(widget.Id, detail.Event.Target.Id); Assert.Equal(AuditEntityType.Widget, detail.Event.Target.Type);
        Assert.Contains(detail.Changes, c => c.Target.Id == connector.Id && c.Field == "StartTarget" && c.After.State == AuditValueState.Null);
        Assert.Contains(detail.Targets, t => t.Id == widget.Id); Assert.Contains(detail.Targets, t => t.Id == page.Item.Id);
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ResolveAsync(profile, detail.Targets.Single(t => t.Id == widget.Id)));
    }
    [Fact]
    public async Task ActivityCanonicalDeletionGroupsMissingWidgetAndJournalReferences()
    {
        var page = await CanvasPage(); var task = await JournalTasks().CreateAsync(profile, new("Referenced")); var widget = await WAdd(page, task.Item);
        var journal = await Journal(); var field = await Field(journal, JournalFieldType.TaskReference); await JSave(journal, field, new(ReferenceId: task.Item.Id));
        var template = await Capture(page.Item, TemplateType.Page); var reference = new TaskReference(profile, task.Item.Id);
        await JournalTasks().ApplyAsync(reference, TaskAction.Trash); await ResetHistory(); await JournalTasks().PermanentlyDeleteAsync(reference);
        var detail = await OnlyActivity(); Assert.Equal(AuditAction.PermanentlyDeleted, detail.Event.Action); Assert.Equal("Referenced", detail.Event.Target.Title);
        Assert.Contains(detail.Changes, c => c.Target.Id == widget.Id && c.Field == "Source" && c.After.State == AuditValueState.Null);
        Assert.Contains(detail.Targets, t => t.Id == template.Metadata.Id); Assert.Null(await Sql("PRAGMA foreign_key_check"));
    }
    [Fact]
    public async Task ActivityRichTextSharedOwnersAndAssetsRemainIndependentOfHistory()
    {
        var a = await CanvasPage(); var b = await Page("Second"); var document = await Rich().CreateAsync(PRef(a), "private original");
        await Rich().AddReferenceAsync(PRef(b), new(profile, document.Document.Id)); await ResetHistory();
        await Rich().UpdateAsync(new(profile, document.Reference.Id), "private corrected"); var detail = await OnlyActivity();
        Assert.Contains(detail.Targets, t => t.Id == a.Item.Id); Assert.Contains(detail.Targets, t => t.Id == b.Item.Id); Assert.All(detail.Changes.Where(c => c.Field == "Content"), c => Assert.Null(c.After.Value));
        var asset = await Assets().ImportAsync(PRef(a), await SourceFile("audit.txt")); await ResetHistory(); await Assets().RemoveReferenceAsync(new(profile, asset.Reference.Id));
        Assert.Single((await History().ReadAsync(profile, new())).Events); Assert.Equal(0L, await Sql("SELECT count(*) FROM Assets"));
        Assert.False(File.Exists(new AssetPaths(paths).Resolve(profile, asset.Asset.StorageRelativePath)));
    }
    [Theory]
    [InlineData("Task")][InlineData("Event")][InlineData("Tracker")][InlineData("Journal")][InlineData("List")][InlineData("Page")]
    [InlineData("Canvas")][InlineData("Widget")][InlineData("RichText")][InlineData("Decoration")][InlineData("Template")][InlineData("Organization")][InlineData("Asset")]
    public async Task ActivityAuditFailureRollsBackEveryMutationFamily(string domain)
    {
        var action = await ActivityMutation(domain); await ResetHistory();
        await Sql("CREATE TRIGGER RejectAudit BEFORE INSERT ON AuditEventChanges BEGIN SELECT RAISE(ABORT,'audit failure'); END");
        var before = await AuditDatabaseSnapshot(); await Assert.ThrowsAnyAsync<Exception>(action);
        var after = await AuditDatabaseSnapshot(); foreach (var table in before.Keys) Assert.Equal(before[table], after[table]);
        Assert.Empty((await History().ReadAsync(profile, new())).Events); Assert.Null(await Sql("PRAGMA foreign_key_check"));
    }
    private async Task<Func<Task>> ActivityMutation(string domain)
    {
        switch (domain)
        {
            case "Task": return async () => { await JournalTasks().CreateAsync(profile, new("Atomic")); };
            case "Event": return async () => { await WidgetEvents().CreateAsync(profile, new("Atomic", true, Day, null, Day, null)); };
            case "Tracker": var tracker = await Create(); return async () => { await Save(tracker, 78.2m); };
            case "Journal": var journal = await Journal(); var field = await Field(journal, JournalFieldType.Number); return async () => { await JSave(journal, field, new(Number: 30)); };
            case "List": var list = await List(); var item = await ListAdd(list); return async () => { await Lists().SetCheckedAsync(new(profile, list.Item.Id, item.Id), true); };
            case "Page": var page = await Page(); return async () => { await Pages().UpdateAsync(PRef(page), "Atomic", PageIcon.Star); };
            case "Canvas": var canvas = await CanvasPage(); var block = await Block(canvas); return async () => { await Edit(canvas, new MoveCanvasItems([block.Id], 16, 0)); };
            case "Widget": var host = await CanvasPage(); var widget = await ContentWidget(host); return async () => { await WEdit(host, new RemoveWidget(widget.Id, true)); };
            case "RichText": var owner = await Page(); var document = await Rich().CreateAsync(PRef(owner), "before"); return async () => { await Rich().UpdateAsync(new(profile, document.Reference.Id), "after"); };
            case "Decoration": var drawing = await CanvasPage(); var shape = await Shape(drawing); return async () => { await DEdit(drawing, new MoveDecoration(shape.Id, 16, 0)); };
            case "Template": var source = await TemplateDashboard(); var template = await Capture(source.Item, TemplateType.Page); return async () => { await PageInstance(template); };
            case "Organization": var tag = await organization.SaveAsync(profile, OrganizationKind.Tag, null, new("before")); return async () => { await organization.SaveAsync(profile, OrganizationKind.Tag, tag, new("after")); };
            case "Asset": var assetPage = await Page(); var asset = await Assets().ImportAsync(PRef(assetPage), await SourceFile("audit-atomic.txt")); return async () => { await Assets().RemoveReferenceAsync(new(profile, asset.Reference.Id)); };
            default: throw new ArgumentOutOfRangeException(nameof(domain));
        }
    }
    [Fact]
    public async Task ActivityOperationIdentityDoesNotPromiseReplayAfterClear()
    {
        var operation = AuditOperation.User(); var tasks = JournalTasks(); var original = await tasks.CreateAsync(profile, new("Once"), audit: operation);
        await Assert.ThrowsAsync<TaskOperationException>(() => tasks.CreateAsync(profile, new("Duplicate"), audit: operation));
        Assert.Single(await tasks.GetAsync(profile, TaskCollection.Active), t => t.Item.Title is "Once" or "Duplicate");
        await ResetHistory(); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        // There is deliberately no replay API/automatic retry. Reconcile the known canonical identity.
        Assert.Equal(original, await tasks.FindAsync(new(profile, original.Item.Id)));
        Assert.Empty((await History().ReadAsync(profile, new())).Events);
        var deliberateNew = await tasks.CreateAsync(profile, new("Once")); Assert.NotEqual(original.Item.Id, deliberateNew.Item.Id); Assert.Single((await History().ReadAsync(profile, new())).Events);
    }
    [Fact]
    public async Task ActivityHistoryClearIsAtomicDoesNotTouchCanonicalOrSearchAndInvalidatesCursors()
    {
        for (var i = 0; i < 4; i++) await JournalTasks().CreateAsync(profile, new("Keep " + i)); await Search("Keep");
        var page = await History().ReadAsync(profile, new(Limit: 2)); var before = await AuditDatabaseSnapshot();
        await History().ClearAsync(profile, page.Epoch); var after = await AuditDatabaseSnapshot();
        foreach (var table in before.Keys.Where(t => !t.StartsWith("Audit", StringComparison.Ordinal))) Assert.Equal(before[table], after[table]);
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ReadAsync(profile, new(Cursor: page.Next, Limit: 2)));
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ClearAsync(profile, page.Epoch));
        Assert.Equal(4, (await Search("Keep")).Results.Count); Assert.Empty((await History().ReadAsync(profile, new())).Events);
    }
    [Fact]
    public async Task ActivityPaginationIsStableDuringNewWritesAndSupportsFilters()
    {
        for (var i = 0; i < 7; i++) await JournalTasks().CreateAsync(profile, new("Page " + i));
        var first = await History().ReadAsync(profile, new(Limit: 3)); await JournalTasks().CreateAsync(profile, new("Later"));
        var second = await History().ReadAsync(profile, new(Cursor: first.Next, Limit: 3)); var third = await History().ReadAsync(profile, new(Cursor: second.Next, Limit: 3));
        var all = first.Events.Concat(second.Events).Concat(third.Events).ToArray(); Assert.Equal(7, all.Length); Assert.Equal(7, all.Select(e => e.Id).Distinct().Count()); Assert.DoesNotContain(all, e => e.Target.Title == "Later");
        Assert.Single((await History().ReadAsync(profile, new(TitlePrefix: "LATER", Type: AuditEntityType.Task, Action: AuditAction.Created))).Events);
    }
    [Fact]
    public async Task ActivityHistorySurvivesRestartAndIsProfileLocal()
    {
        await JournalTasks().CreateAsync(profile, new("Personal activity")); var expected = await OnlyActivity();
        var other = await profiles.CreateAsync("Other activity"); Assert.Empty((await History().ReadAsync(other.Id, new())).Events);
        await Assert.ThrowsAsync<WorkspaceChangedException>(() => History().DetailsAsync(profile, expected.Event.Sequence));
        await profiles.SwitchAsync(profile); Assert.Equal(expected.Event, Assert.Single((await History().ReadAsync(profile, new())).Events));
        await initializer.InitializeAsync(profile, false); Assert.Equal(expected.Event, (await OnlyActivity()).Event);
    }
    [Fact]
    public async Task ActivitySearchRebuildAndReadOnlyBrowsingCreateNoEvents()
    {
        await JournalTasks().CreateAsync(profile, new("Searchable")); await ResetHistory();
        await SearchService().RebuildAsync(profile); Assert.Single((await Search("Searchable")).Results);
        await History().ReadAsync(profile, new()); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM SearchDirtyDocuments")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task ActivityMissingAuditSchemaFailsClosed()
    {
        await Sql("DROP TABLE AuditEventChanges");
        await Assert.ThrowsAsync<TaskOperationException>(() => JournalTasks().CreateAsync(profile, new("Must roll back")));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM Tasks")); Assert.Equal(0L, await Sql("SELECT count(*) FROM AuditEvents"));
    }
    [Fact]
    public void ActivityMigrationTwentySqlHashRemainsFrozen()
    {
        var sql = WorkspaceMigrationCatalog.All.Single(m => m.Version == 20).Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("AF87AD07983D52D950178F426CB5C72D825AE0116F0C94CCA58A8DED2D65D515", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql))));
    }
    [Fact]
    public async Task ActivityDateBoundariesAreInclusiveFromExclusiveUntilAndRejectInvertedRanges()
    {
        await JournalTasks().CreateAsync(profile, new("Dated")); var item = (await OnlyActivity()).Event;
        Assert.Single((await History().ReadAsync(profile, new(From: item.OccurredAtUtc))).Events);
        Assert.Empty((await History().ReadAsync(profile, new(Until: item.OccurredAtUtc))).Events);
        Assert.Single((await History().ReadAsync(profile, new(From: item.OccurredAtUtc, Until: item.OccurredAtUtc.AddTicks(1)))).Events);
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ReadAsync(profile, new(From: item.OccurredAtUtc.AddDays(1), Until: item.OccurredAtUtc)));
    }
    [Fact]
    public async Task ActivityLargeGroupedChangesArePagedWithoutLoadingAllTargets()
    {
        await Lists().CreateDefinitionAsync(profile, new("Large list"), Enumerable.Range(0, 230).Select(i => new ListItemDraft("Item " + i)).ToArray());
        var first = await OnlyActivity(); Assert.Equal(231, first.Event.ChangeCount); Assert.Equal(100, first.Changes.Count); Assert.True(first.HasMore);
        Assert.True(first.Targets.Count <= 201);
        var second = await History().DetailsAsync(profile, first.Event.Sequence, first.Changes[^1].Ordinal); Assert.Equal(100, second.Changes.Count); Assert.True(second.HasMore);
        var third = await History().DetailsAsync(profile, first.Event.Sequence, second.Changes[^1].Ordinal); Assert.Equal(31, third.Changes.Count); Assert.False(third.HasMore);
        Assert.Equal(231, first.Changes.Concat(second.Changes).Concat(third.Changes).Select(c => c.Ordinal).Distinct().Count());
    }
}
