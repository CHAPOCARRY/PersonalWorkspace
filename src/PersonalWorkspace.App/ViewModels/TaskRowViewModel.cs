using System.Globalization;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed record TaskRowViewModel(Guid ProfileId, TaskItem Task, string Context = "", TaskProgress? Progress = null, int Depth = 0, TaskOccurrence? Occurrence = null)
{
    public TaskReference Reference => new(ProfileId, Task.Item.Id);
    public string Title => Task.Item.Title;
    public string HierarchyTitle => new string(' ', Math.Min(Depth, 16) * 3) + (Depth > 0 ? "↳ " : "") + Title;
    public string ProgressText => Progress?.ToString() ?? "";
    public string ValueText => TaskValuePresentation.Progress(Task.Value is { } value && Occurrence is { } occurrence ? value with { Actual=occurrence.Actual } : Task.Value);
    public string ContextText => string.Join(" · ", new[] { Context, Occurrence is not null ? "Occurrence" : Task.IsRecurring ? "Repeats" : "", Progress is null ? "" : "Subtasks: " + ProgressText, ValueText, IsDeleted ? "In Trash" : IsArchived ? "Archived" : "" }.Where(s => s.Length > 0));
    public override string ToString() => Title;
    public TaskStatus ExecutionStatus => Occurrence?.Status ?? Task.Status;
    public string StatusText => Task.IsRecurring && Occurrence is null ? "Series" : ExecutionStatus == TaskStatus.ToDo ? "To Do" : ExecutionStatus.ToString();
    public string PriorityText => Task.Priority.ToString();
    public string ScheduledText => (Occurrence?.OccurrenceDate ?? Task.ScheduledDate)?.ToString("d", CultureInfo.CurrentCulture) ?? "Unscheduled";
    public string CompletionLabel => ExecutionStatus == TaskStatus.Done ? "Reopen" : "Mark done";
    public bool IsDeleted => Task.Item.DeletedAtUtc is not null;
    public bool IsArchived => Task.Item.ArchivedAtUtc is not null;
    public bool CanEdit => !IsDeleted;
    public bool CanComplete => CanEdit && (!Task.IsRecurring || Occurrence is not null);
    public bool CanChangeLifecycle => CanEdit && Occurrence is null;
    public bool CanArchive => !IsDeleted && !IsArchived && Occurrence is null;
    public bool CanRestore => (IsDeleted || IsArchived) && Occurrence is null;
}
