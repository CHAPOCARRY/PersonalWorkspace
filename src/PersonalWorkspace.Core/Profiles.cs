namespace PersonalWorkspace.Core;

public sealed record Profile(Guid Id, string Name, string FolderName, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastOpenedAtUtc, int SortOrder);

public interface ICurrentProfile
{
    Profile? Current { get; }
    string? WorkspaceDatabase { get; }
    event EventHandler? Changed;
}

public interface IProfileService
{
    Task RestoreAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Profile> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task SwitchAsync(Guid id, CancellationToken cancellationToken = default);
    // Call only after the user explicitly confirms the named profile's permanent deletion.
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IProfileRepository
{
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken);
    Task CreateAndSelectAsync(Profile profile, CancellationToken cancellationToken);
    Task RenameAsync(Guid id, string name, CancellationToken cancellationToken);
    Task SelectAsync(Profile profile, CancellationToken cancellationToken);
    Task DeleteAndSelectAsync(Guid id, Profile? replacement, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetPendingDeletionsAsync(CancellationToken cancellationToken);
    Task CompleteDeletionAsync(Guid id, CancellationToken cancellationToken);
}

public interface IWorkspaceInitializer
{
    Task InitializeAsync(Guid id, bool create, CancellationToken cancellationToken = default);
}

public interface IProfileFiles
{
    void Create(Guid id);
    void Delete(Guid id);
}

public sealed class ProfileValidationException(string message) : Exception(message);
public sealed class ProfileOperationException(string message, Exception? inner = null) : Exception(message, inner);
