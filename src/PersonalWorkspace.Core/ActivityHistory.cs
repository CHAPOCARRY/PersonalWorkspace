namespace PersonalWorkspace.Core;

public enum AuditEntityType { Task = 1, Event, Tracker, Journal, Page, List, Occurrence, TrackerEntry, JournalEntry, JournalField, JournalOption, ListItem, CanvasItem, Widget, RichText, AssetReference, Decoration, Template, Tag, Space }
public enum AuditAction { Created, Updated, StatusChanged, Completed, Archived, Restored, Trashed, PermanentlyDeleted, Linked, Unlinked, Moved, Reordered, TemplateCaptured, TemplateUpdated, TemplateInstantiated }
public enum AuditValueKind { Text, Integer, Decimal, Boolean, Enum, Guid, Date, LocalTime, Utc, Duration, Geometry, Content }
public enum AuditValueState { Absent, Null, Value, Omitted, Truncated }
public sealed record AuditTarget(AuditEntityType Type, Guid Id, string Title, Guid? ParentId = null, DateOnly? Date = null, string Role = "Affected", bool TitleTruncated = false);
// A GUID identifies this invocation only. Never automatically replay an ambiguous commit.
public sealed record AuditOperation(Guid Id, AuditAction? Action = null, AuditTarget? Primary = null, AuditTarget? Source = null)
{
    public static AuditOperation User(AuditAction? action = null, AuditTarget? primary = null, AuditTarget? source = null) => new(Guid.NewGuid(), action, primary, source);
}
public sealed record AuditValue(AuditValueKind Kind, AuditValueState State, string? Value = null, int? Length = null);
public sealed record AuditChange(int Ordinal, AuditTarget Target, string Field, AuditValue Before, AuditValue After);
public sealed record AuditEvent(long Sequence, Guid Id, Guid OperationId, Guid ProfileId, DateTimeOffset OccurredAtUtc, AuditAction Action, AuditTarget Target, int ChangeCount, int Version = 1);
public sealed record AuditCursor(Guid Epoch, long HighWater, long Ticks, long Sequence);
public sealed record AuditQuery(DateTimeOffset? From = null, DateTimeOffset? Until = null, AuditEntityType? Type = null, AuditAction? Action = null,
    Guid? TargetId = null, string TitlePrefix = "", AuditCursor? Cursor = null, int Limit = 50);
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, AuditCursor? Next, Guid Epoch);
public sealed record AuditDetails(AuditEvent Event, IReadOnlyList<AuditTarget> Targets, IReadOnlyList<AuditChange> Changes, bool HasMore);
public interface IActivityHistoryRepository
{
    Task<AuditPage> ReadAsync(WorkspaceContext workspace, AuditQuery query, CancellationToken token);
    Task<AuditDetails> DetailsAsync(WorkspaceContext workspace, long sequence, int afterChange, CancellationToken token);
    Task ClearAsync(WorkspaceContext workspace, Guid expectedEpoch, CancellationToken token);
    Task<NavigationRoute> ResolveAsync(WorkspaceContext workspace, AuditTarget target, CancellationToken token);
}
public interface IActivityHistoryService
{
    Task<AuditPage> ReadAsync(Guid profile, AuditQuery query, CancellationToken token = default);
    Task<AuditDetails> DetailsAsync(Guid profile, long sequence, int afterChange = -1, CancellationToken token = default);
    Task ClearAsync(Guid profile, Guid expectedEpoch, CancellationToken token = default);
    Task<NavigationRoute> ResolveAsync(Guid profile, AuditTarget target, CancellationToken token = default);
}
public sealed class ActivityHistoryException(string message) : Exception(message);
