using System.Text;
using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class OrganizationService(IOrganizationRepository repository, ICurrentProfile current,
    IWorkspaceOperationGate gate, TimeProvider time, ILogger<OrganizationService> logger) : IOrganizationService
{
    public Task<OrganizationSnapshot> GetAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, workspace => repository.GetAsync(workspace, cancellationToken), cancellationToken);

    public Task<Guid> SaveAsync(Guid profileId, OrganizationKind kind, Guid? id, OrganizationDraft draft, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace =>
        {
            ValidateKind(kind);
            if (string.IsNullOrWhiteSpace(draft.Name)) throw new OrganizationValidationException("Enter a name.");
            if (!Enum.IsDefined(draft.Color)) throw new OrganizationValidationException("Select a color from the palette.");
            var valid = draft with { Name = draft.Name.Trim().Normalize(NormalizationForm.FormC),
                Description = draft.Description?.Trim() ?? "", Icon = draft.Icon?.Trim() ?? "" };
            var snapshot = await repository.GetAsync(workspace, cancellationToken);
            var duplicate = kind == OrganizationKind.Tag
                ? snapshot.Tags.Any(tag => tag.Id != id && StringComparer.OrdinalIgnoreCase.Equals(tag.Name, valid.Name))
                : snapshot.Spaces.Any(space => space.Id != id && StringComparer.OrdinalIgnoreCase.Equals(space.Name, valid.Name));
            if (duplicate) throw new OrganizationValidationException($"A {kind.ToString().ToLowerInvariant()} with this name already exists.");
            var entityId = id ?? Guid.NewGuid();
            await repository.SaveAsync(workspace, kind, entityId, valid, id is null, time.GetUtcNow(), cancellationToken);
            return entityId;
        }, cancellationToken);

    public Task DeleteTagAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace => { await repository.DeleteTagAsync(workspace, id, cancellationToken); return true; }, cancellationToken);

    public Task ArchiveSpaceAsync(Guid profileId, Guid id, bool archive, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, async workspace => { await repository.ArchiveSpaceAsync(workspace, id, archive, time.GetUtcNow(), cancellationToken); return true; }, cancellationToken);

    public Task AssignAsync(WorkspaceItemReference item, OrganizationKind kind, Guid entityId, bool assigned, CancellationToken cancellationToken = default) =>
        RunAsync(item.ProfileId, async workspace =>
        {
            ValidateKind(kind);
            await repository.AssignAsync(workspace, item.ItemId, kind, entityId, assigned, cancellationToken);
            return true;
        }, cancellationToken);

    private static void ValidateKind(OrganizationKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new OrganizationValidationException("Select a tag or space.");
    }

    private async Task<T> RunAsync<T>(Guid expectedProfile, Func<WorkspaceContext, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        if (current.Current?.Id != expectedProfile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await operation(new(expectedProfile, database)); }
        catch (OrganizationValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Organization storage operation failed in profile {ProfileId}", expectedProfile);
            throw new OrganizationOperationException("Tags and spaces could not be loaded or saved. Check access to the local workspace and try again.", exception);
        }
    }
}
