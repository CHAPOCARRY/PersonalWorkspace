using System.Globalization;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed record TaskRowViewModel(Guid ProfileId, TaskItem Task, string Context = "", TaskProgress? Progress = null, int Depth = 0)
{
    public TaskReference Reference => new(ProfileId, Task.Item.Id);
    public string Title => Task.Item.Title;
    public string HierarchyTitle => new string(' ', Math.Min(Depth, 16) * 3) + (Depth > 0 ? "↳ " : "") + Title;
    public string ProgressText => Progress?.ToString() ?? "";
    public string ValueText => TaskValuePresentation.Progress(Task.Value);
    public string ContextText => string.Join(" · ", new[] { Context, Progress is null ? "" : "Subtasks: " + ProgressText, ValueText, IsDeleted ? "In Trash" : IsArchived ? "Archived" : "" }.Where(s => s.Length > 0));
    public override string ToString() => Title;
    public string StatusText => Task.Status == TaskStatus.ToDo ? "To Do" : Task.Status.ToString();
    public string PriorityText => Task.Priority.ToString();
    public string ScheduledText => Task.ScheduledDate?.ToString("d", CultureInfo.CurrentCulture) ?? "Unscheduled";
    public string CompletionLabel => Task.Status == TaskStatus.Done ? "Reopen" : "Mark done";
    public bool IsDeleted => Task.Item.DeletedAtUtc is not null;
    public bool IsArchived => Task.Item.ArchivedAtUtc is not null;
    public bool CanEdit => !IsDeleted;
    public bool CanArchive => !IsDeleted && !IsArchived;
    public bool CanRestore => IsDeleted || IsArchived;
}
