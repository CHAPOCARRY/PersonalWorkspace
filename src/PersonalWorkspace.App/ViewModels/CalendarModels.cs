using System.Globalization;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public enum CalendarMode { Month, Week, Day }
public sealed record CalendarEntry(Guid ProfileId, DateOnly DisplayDate, TaskItem? Task, EventItem? Event, DateTime LocalNow)
{
    public Guid Id => Task?.Item.Id ?? Event!.Item.Id;
    public WorkspaceItemReference Reference => new(ProfileId, Id);
    public string Title => Task?.Item.Title ?? Event!.Item.Title;
    public bool IsTask => Task is not null;
    public bool IsEvent => Event is not null;
    public bool IsDeleted => Event?.Item.DeletedAtUtc is not null;
    public bool CanOpen => !IsDeleted;
    public bool CanArchive => Event is { Item.ArchivedAtUtc: null, Item.DeletedAtUtc: null };
    public bool CanRestore => Event is { Item.ArchivedAtUtc: not null } || IsDeleted;
    public string TimeLabel => Event is not { } item ? "Task"
        : item.AllDay ? "All day" : (item.StartDate < DisplayDate ? "Continues" : item.StartTime!.Value.ToString("t", CultureInfo.CurrentCulture));
    public string Label => Task is { } task ? (task.Status == TaskStatus.Done ? "✓ Task · " : "□ Task · ") + Title : TimeLabel + " · " + Title;
    public override string ToString() => Label;
    public string DragLabel => "Drag " + (IsTask ? "task " : "event ") + Title;
    public string EventSummary => Event is { } item ?
        (item.AllDay ? $"{item.StartDate:d} – {item.EndDate:d} · All day" : $"{item.StartDate:d} {item.StartTime:t} – {item.EndDate:d} {item.EndTime:t}")
        + (item.IsPast(LocalNow) ? " · Past" : "") : "";
}
public sealed record CalendarDay(DateOnly Date, bool IsToday, bool InMonth, IReadOnlyList<CalendarEntry> Entries)
{
    public string Label => Date.ToString("ddd d", CultureInfo.CurrentCulture) + (IsToday ? " · Today" : "");
    public IEnumerable<CalendarEntry> Tasks => Entries.Where(entry => entry.IsTask);
    public IEnumerable<CalendarEntry> AllDayEvents => Entries.Where(entry => entry.Event?.AllDay == true);
    public IEnumerable<CalendarEntry> TimedEvents => Entries.Where(entry => entry.Event?.AllDay == false)
        .OrderBy(entry => entry.Event!.StartDate < Date ? TimeOnly.MinValue : entry.Event.StartTime);
}

public static class CalendarPeriods
{
    public static (DateOnly From, DateOnly Through) Range(DateOnly anchor, CalendarMode mode, DayOfWeek firstDay)
    {
        if (mode == CalendarMode.Day) return (anchor, anchor);
        var start = mode == CalendarMode.Month ? new DateOnly(anchor.Year, anchor.Month, 1) : anchor;
        var offset = ((int)start.DayOfWeek - (int)firstDay + 7) % 7;
        var from = DateOnly.FromDayNumber(Math.Max(0, start.DayNumber - offset));
        return (from, DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, from.DayNumber + (mode == CalendarMode.Month ? 41 : 6))));
    }
}
