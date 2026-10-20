namespace PersonalWorkspace.Core;

public enum TemplateType { Task = 1, List = 2, Journal = 3, Page = 4 }
public sealed record TemplateReference(Guid ProfileId, Guid Id);
public sealed record TemplateMetadata(Guid Id, TemplateType Type, string Name, string? Description, int FormatVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public abstract record TemplateBlueprint(string Title);
public sealed record TaskBlueprint(TaskDraft Definition) : TemplateBlueprint(Definition.Title);
public sealed record ListBlueprint(ListDraft Definition, IReadOnlyList<ListItemDraft> Items) : TemplateBlueprint(Definition.Title);
public sealed record JournalBlueprint(string JournalTitle, string Description, IReadOnlyList<JournalFieldDraft> Fields) : TemplateBlueprint(JournalTitle);
public sealed record TemplateSnapshot(TemplateMetadata Metadata, TemplateBlueprint Blueprint);
public sealed record TemplateSource(WorkspaceItemReference Reference, TemplateType Type, string Title)
{
    public override string ToString() => $"{Title} · {Reference.ItemId.ToString("N")[..8]}";
}
public interface ITemplateRepository
{
    Task<IReadOnlyList<TemplateMetadata>> CatalogAsync(WorkspaceContext workspace, TemplateType? type, CancellationToken token);
    Task<TemplateSnapshot> ReadAsync(WorkspaceContext workspace, Guid id, CancellationToken token);
    Task SaveAsync(WorkspaceContext workspace, TemplateSnapshot snapshot, bool create, CancellationToken token);
    Task RenameAsync(WorkspaceContext workspace, Guid id, string name, string? description, DateTimeOffset now, CancellationToken token);
    Task DeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken token);
    Task<IReadOnlyList<TemplateSource>> SourcesAsync(WorkspaceContext workspace, TemplateType type, CancellationToken token);
}
public interface ITemplateService
{
    Task<IReadOnlyList<TemplateMetadata>> CatalogAsync(Guid profile, TemplateType? type = null, CancellationToken token = default);
    Task<IReadOnlyList<TemplateSource>> SourcesAsync(Guid profile, TemplateType type, CancellationToken token = default);
    Task<TemplateSnapshot> ReadAsync(TemplateReference template, CancellationToken token = default);
    Task<TemplateSnapshot> CaptureAsync(WorkspaceItemReference source, TemplateType type, string name, string? description = null, CancellationToken token = default);
    Task<TemplateSnapshot> ReplaceAsync(TemplateReference template, WorkspaceItemReference source, CancellationToken token = default);
    Task RenameAsync(TemplateReference template, string name, string? description, CancellationToken token = default);
    Task DeleteAsync(TemplateReference template, CancellationToken token = default);
    Task<WorkspaceItem> InstantiateAsync(TemplateReference template, string? title = null, CancellationToken token = default);
    Task<WorkspaceItem> InstantiatePageAsync(TemplateReference template, string? title = null, Guid? parent = null, CancellationToken token = default) => throw new NotSupportedException();
}
public sealed class TemplateValidationException(string message) : Exception(message);
public sealed class TemplateOperationException(string message, Exception inner) : Exception(message, inner);
