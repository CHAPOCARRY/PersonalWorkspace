using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using Xunit;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    [Theory]
    [InlineData("Task")][InlineData("Event")][InlineData("Tracker")][InlineData("Journal")][InlineData("List")][InlineData("Page")]
    public async Task ActivityLifecycleKeepsIdentityAndContextThroughDeletion(string domain)
    {
        WorkspaceItem item; Func<int, Task> apply; Func<Task> delete;
        switch (domain)
        {
            case "Task": var t = await JournalTasks().CreateAsync(profile, new("Historical Task")); item = t.Item; apply = a => JournalTasks().ApplyAsync(new(profile, item.Id), (TaskAction)a); delete = () => JournalTasks().PermanentlyDeleteAsync(new(profile, item.Id)); break;
            case "Event": var e = await WidgetEvents().CreateAsync(profile, new("Historical Event", true, Day, null, Day, null)); item = e.Item; apply = a => WidgetEvents().ApplyAsync(new(profile, item.Id), (EventAction)a); delete = () => WidgetEvents().PermanentlyDeleteAsync(new(profile, item.Id)); break;
            case "Tracker": var tr = await Create(); item = tr.Item; apply = a => service.ApplyAsync(new(profile, item.Id), (TrackerAction)a); delete = () => service.PermanentlyDeleteAsync(new(profile, item.Id)); break;
            case "Journal": var j = await Journal(); item = j.Item; apply = a => Journals().ApplyAsync(new(profile, item.Id), (JournalAction)a); delete = () => Journals().PermanentlyDeleteAsync(new(profile, item.Id)); break;
            case "List": var l = await List(); item = l.Item; apply = a => Lists().ApplyAsync(new(profile, item.Id), (ListAction)a); delete = () => Lists().DeleteAsync(new(profile, item.Id)); break;
            default: var p = await Page(); item = p.Item; apply = a => Pages().ApplyAsync(new(profile, item.Id), (PageAction)a); delete = () => Pages().DeleteAsync(new(profile, item.Id)); break;
        }
        foreach (var (operation, expected) in new[] { (0, AuditAction.Archived), (1, AuditAction.Restored), (2, AuditAction.Trashed), (3, AuditAction.Restored), (2, AuditAction.Trashed) })
        {
            await ResetHistory(); await apply(operation); var activity = await OnlyActivity();
            Assert.Equal(item.Id, activity.Event.Target.Id); Assert.Equal(expected, activity.Event.Action);
            if (expected == AuditAction.Trashed) await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ResolveAsync(profile, activity.Event.Target));
        }
        await ResetHistory(); await delete(); var deleted = await OnlyActivity();
        Assert.Equal(AuditAction.PermanentlyDeleted, deleted.Event.Action); Assert.Equal(item.Title, deleted.Event.Target.Title);
        await Assert.ThrowsAsync<ActivityHistoryException>(() => History().ResolveAsync(profile, deleted.Event.Target));
        Assert.Equal(0L, await Sql($"SELECT count(*) FROM WorkspaceItems WHERE Id='{item.Id}'"));
    }
    [Fact]
    public async Task ActivityTaskRelationshipsRecurrenceAndCarryPolicyHaveMeaningfulChanges()
    {
        var tasks = JournalTasks(); var parent = await tasks.CreateAsync(profile, new("Parent")); var task = await tasks.CreateAsync(profile, new("Child", Value: new(TaskValueType.Number, 20)));
        var reference = new TaskReference(profile, task.Item.Id); await ResetHistory(); await tasks.SetParentAsync(reference, parent.Item.Id);
        var detail = await OnlyActivity(); Assert.Equal(task.Item.Id, detail.Event.Target.Id); Assert.Equal(AuditAction.Moved, detail.Event.Action);
        Assert.Contains(detail.Changes, c => c.Field == "ParentTaskId" && c.After.Value == parent.Item.Id.ToString());
        var dependency = await tasks.CreateAsync(profile, new("Prerequisite")); await ResetHistory(); await tasks.AddDependencyAsync(reference, dependency.Item.Id);
        Assert.Equal(AuditAction.Linked, (await OnlyActivity()).Event.Action); await ResetHistory(); await tasks.RemoveDependencyAsync(reference, dependency.Item.Id);
        Assert.Equal(AuditAction.Unlinked, (await OnlyActivity()).Event.Action);
        await ResetHistory(); await WidgetRecurrence().SetRuleAsync(reference, new(RecurrencePattern.Daily, Day));
        Assert.Contains((await OnlyActivity()).Changes, c => c.Field.StartsWith("Recurrence:") && c.After.Value!.Contains("Daily"));
        await ResetHistory(); await WidgetRecurrence().SetCarrySettingsAsync(reference, true, true);
        detail = await OnlyActivity(); Assert.Equal(task.Item.Id, detail.Event.Target.Id); Assert.Contains(detail.Changes, c => c.Field == "CarryDeficit" && c.Before.Value == "false" && c.After.Value == "true");
        await WidgetRecurrence().SetCarrySettingsAsync(reference, true, true); Assert.Single((await History().ReadAsync(profile, new())).Events);
        var occurrence = (await WidgetRecurrence().GetRangeAsync(profile, Day, Day)).Single(); await ResetHistory(); await WidgetRecurrence().MoveAsync(new(profile, occurrence.Occurrence.Id), Day.AddDays(1));
        detail = await OnlyActivity(); Assert.Equal(occurrence.Occurrence.Id, detail.Event.Target.Id); Assert.Equal(AuditAction.Moved, detail.Event.Action);
    }
    [Fact]
    public async Task ActivityEventAndTrackerDefinitionsKeepScalarChangesAndNoOpsAreSilent()
    {
        var events = WidgetEvents(); var original = new EventDraft("Meeting", false, Day, new(12, 0), Day, new(13, 0)); var item = await events.CreateAsync(profile, original);
        await ResetHistory(); await events.UpdateAsync(new(profile, item.Item.Id), original); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        await events.UpdateAsync(new(profile, item.Item.Id), original with { StartTime = new(11, 30) });
        Assert.Contains((await OnlyActivity()).Changes, c => c.Field == "StartTime" && c.Before.Value == "12:00:00.0000000" && c.After.Value == "11:30:00.0000000");
        var tracker = await Create(Draft() with { Target = new(75m) }); await ResetHistory(); await service.UpdateAsync(Ref(tracker), tracker.Draft with { Target = new(76.25m) });
        Assert.Contains((await OnlyActivity()).Changes, c => c.Field == "Target" && c.Before.Value == "75" && c.After.Value == "76.25");
        await service.UpdateAsync(Ref(tracker), tracker.Draft with { Target = new(76.25m) }); Assert.Single((await History().ReadAsync(profile, new())).Events);
    }
    [Fact]
    public async Task ActivityJournalFieldOptionRenameOrderingAndDeletionAreCaptured()
    {
        var j = await Journal(); var f = await Field(j, JournalFieldType.Select); var other = await Field(j, JournalFieldType.Number, "Number");
        await ResetHistory(); await Journals().SaveFieldAsync(JRef(j), f.Id, FieldDraft(JournalFieldType.Select, "Renamed") with { Options = f.OrderedOptions.Select((o, i) => o with { Label = i == 0 ? "Better" : o.Label }).ToArray() });
        var detail = await OnlyActivity(); Assert.Contains(detail.Changes, c => c.Target.Id == f.Id && c.Field == "Name" && c.After.Value == "Renamed");
        Assert.Contains(detail.Changes, c => c.Target.Id == f.OrderedOptions[0].Id && c.Field == "Label" && c.Before.Value == "Low" && c.After.Value == "Better");
        await ResetHistory(); await Journals().MoveFieldAsync(JRef(j), other.Id, -1); Assert.Equal(2, (await OnlyActivity()).Changes.Count(c => c.Field == "Order"));
        await JSave(j, other, new(Number: 1)); await ResetHistory(); await Journals().DeleteFieldAsync(JRef(j), other.Id, true);
        Assert.Contains((await OnlyActivity()).Changes, c => c.Target.Id == other.Id && c.Field == "Exists" && c.After.Value == "false");
    }
    [Theory][InlineData(OrganizationKind.Tag)][InlineData(OrganizationKind.Space)]
    public async Task ActivityOrganizationAssignmentsAndDeletionKeepAffectedTargets(OrganizationKind kind)
    {
        var task = await JournalTasks().CreateAsync(profile, new("Organized")); var id = await organization.SaveAsync(profile, kind, null, new("Before"));
        await ResetHistory(); await organization.AssignAsync(new(profile, task.Item.Id), kind, id, true);
        var detail = await OnlyActivity(); Assert.Equal(AuditAction.Linked, detail.Event.Action); Assert.Contains(detail.Targets, t => t.Id == task.Item.Id);
        await organization.AssignAsync(new(profile, task.Item.Id), kind, id, true); Assert.Single((await History().ReadAsync(profile, new())).Events);
        await ResetHistory(); await organization.SaveAsync(profile, kind, id, new("After")); Assert.Contains((await OnlyActivity()).Changes, c => c.Field == "Name" && c.Before.Value == "Before" && c.After.Value == "After");
        await ResetHistory(); if (kind == OrganizationKind.Tag) await organization.DeleteTagAsync(profile, id); else await organization.ArchiveSpaceAsync(profile, id, true);
        detail = await OnlyActivity(); Assert.Equal(kind == OrganizationKind.Tag ? AuditAction.PermanentlyDeleted : AuditAction.Archived, detail.Event.Action); Assert.Contains(detail.Targets, t => t.Id == task.Item.Id);
    }
    [Fact]
    public async Task ActivityCanvasGeometryLockAndDecorationStylesExcludeZoomAndNoOps()
    {
        var page = await CanvasPage(); var block = await Block(page); await ResetHistory();
        await Edit(page, new MoveCanvasItems([block.Id], 16, 8)); var detail = await OnlyActivity();
        Assert.Contains(detail.Changes, c => c.Target.Id == block.Id && c.Field == "X" && int.Parse(c.After.Value!) - int.Parse(c.Before.Value!) == 16);
        await ResetHistory(); await Edit(page, new ZoomCanvas(50)); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        await Edit(page, new LockCanvas(true)); detail = await OnlyActivity(); Assert.Equal(page.Item.Title, detail.Event.Target.Title); Assert.Contains(detail.Changes, c => c.Field == "LayoutLocked" && c.After.Value == "true");
        await Edit(page, new LockCanvas(false)); var shape = await Shape(page); await ResetHistory(); await DEdit(page, new ChangeDecorationStyle(shape.Id, shape.Style with { Width = 4, Filled = true }));
        detail = await OnlyActivity(); Assert.Equal(shape.Id, detail.Event.Target.Id); Assert.Contains(detail.Changes, c => c.Field == "StrokeWidth" && c.Before.Value == "2" && c.After.Value == "4");
        await DEdit(page, new ChangeDecorationStyle(shape.Id, shape.Style with { Width = 4, Filled = true })); Assert.Single((await History().ReadAsync(profile, new())).Events);
    }
    [Fact]
    public async Task ActivityPageTemplateGroupIncludesEveryWidgetTypeAndIndependentCanvasIdentity()
    {
        var page = await TemplateDashboard(); var template = await Capture(page.Item, TemplateType.Page); await ResetHistory();
        var copy = await PageInstance(template); var detail = await OnlyActivity(); var widgets = (await Widgets().GetAsync(PRef(copy))).Widgets;
        foreach (var widget in widgets) Assert.Contains(detail.Targets, t => t.Type == AuditEntityType.Widget && t.Id == widget.Id);
        foreach (var item in (await Layout(copy)).Items) Assert.Contains(detail.Targets, t => t.Type == AuditEntityType.CanvasItem && t.Id == item.Id);
        Assert.Contains(detail.Targets, t => t.Type == AuditEntityType.Decoration); Assert.Contains(detail.Targets, t => t.Type == AuditEntityType.Template);
        Assert.Equal(AuditAction.TemplateInstantiated, detail.Event.Action);
    }
    [Fact]
    public async Task ActivityDirectRepositoryWritesFailAtomicallyAndNoOpUpdatesStaySilent()
    {
        var repo = new SqliteTaskRepository(); var task = await JournalTasks().CreateAsync(profile, new("Direct")); var w = new WorkspaceContext(profile, paths.WorkspaceDatabase(profile));
        await ResetHistory(); await repo.UpdateAsync(w, task.Item.Id, t => t, default); Assert.Empty((await History().ReadAsync(profile, new())).Events);
        await repo.UpdateAsync(w, task.Item.Id, t => t with { Priority = TaskPriority.High }, default);
        Assert.Contains((await OnlyActivity()).Changes, c => c.Field == "Priority" && c.After.Value == "High");
        var before = await AuditDatabaseSnapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => repo.TransactAsync<bool>(w, graph => { graph.Tasks[task.Item.Id] = task with { Status = TaskStatus.Done }; throw new InvalidOperationException("rollback"); }, default));
        foreach (var (table, rows) in before) Assert.Equal(rows, await TableRows(profile, table));
    }
}
