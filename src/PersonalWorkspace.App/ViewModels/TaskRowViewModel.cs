using System.Globalization;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.App.ViewModels;

public sealed record TaskRowViewModel(Guid ProfileId, TaskItem Task)
{
    public TaskReference Reference => new(ProfileId, Task.Item.Id);
    public string Title => Task.Item.Title;
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
