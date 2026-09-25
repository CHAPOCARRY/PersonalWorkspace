namespace PersonalWorkspace.Core;

public enum OrganizationKind { Tag, Space }
public enum OrganizationColor { None, Blue, Green, Amber, Red }
public sealed record Tag(Guid Id, string Name, OrganizationColor Color, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record Space(Guid Id, string Name, string Description, string Icon, OrganizationColor Color, int SortOrder,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ArchivedAtUtc);
public sealed record OrganizationDraft(string Name, OrganizationColor Color = OrganizationColor.None, string Description = "", string Icon = "");
public sealed record WorkspaceItemReference(Guid ProfileId, Guid ItemId);
public sealed record ItemAssignment(Guid ItemId, Guid EntityId);
public sealed record OrganizationSnapshot(IReadOnlyList<Tag> Tags, IReadOnlyList<Space> Spaces,
    IReadOnlyList<ItemAssignment> ItemTags, IReadOnlyList<ItemAssignment> ItemSpaces)
{
    public static OrganizationSnapshot Empty { get; } = new([], [], [], []);
    // All chosen filters are ANDed; assignments never change ownership or duplicate items.
    public bool Matches(Guid itemId, Guid? tagId, Guid? spaceId) =>
        (tagId is null || ItemTags.Contains(new(itemId, tagId.Value))) &&
        (spaceId is null || ItemSpaces.Contains(new(itemId, spaceId.Value)));
}

public interface IOrganizationRepository
{
    Task<OrganizationSnapshot> GetAsync(WorkspaceContext workspace, CancellationToken cancellationToken);
    Task SaveAsync(WorkspaceContext workspace, OrganizationKind kind, Guid id, OrganizationDraft draft, bool create, DateTimeOffset now, CancellationToken cancellationToken);
    Task DeleteTagAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
    Task ArchiveSpaceAsync(WorkspaceContext workspace, Guid id, bool archive, DateTimeOffset now, CancellationToken cancellationToken);
    Task AssignAsync(WorkspaceContext workspace, Guid itemId, OrganizationKind kind, Guid entityId, bool assigned, CancellationToken cancellationToken);
}

public interface IOrganizationService
{
    Task<OrganizationSnapshot> GetAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<Guid> SaveAsync(Guid profileId, OrganizationKind kind, Guid? id, OrganizationDraft draft, CancellationToken cancellationToken = default);
    // Presentation obtains confirmation naming the tag and explaining removal of its assignments.
    Task DeleteTagAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default);
    Task ArchiveSpaceAsync(Guid profileId, Guid id, bool archive, CancellationToken cancellationToken = default);
    Task AssignAsync(WorkspaceItemReference item, OrganizationKind kind, Guid entityId, bool assigned, CancellationToken cancellationToken = default);
}

public sealed class OrganizationValidationException(string message) : Exception(message);
public sealed class OrganizationOperationException(string message, Exception inner) : Exception(message, inner);
