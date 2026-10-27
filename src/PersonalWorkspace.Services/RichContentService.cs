using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class RichContentService(IRichContentRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<RichContentService> logger) : IRichContentService
{
    public Task<RichTextAttachment> CreateAsync(WorkspaceItemReference owner, string content, CancellationToken token = default) => Run(owner.ProfileId, w =>
    {
        var canonical = RichTextFormat.Validate(content); var now = clock.GetUtcNow();
        return repository.CreateAsync(w, owner.ItemId, new(Guid.NewGuid(), canonical, RichTextFormat.Name, RichTextFormat.Version, now, now), token);
    }, token);
    public Task<RichTextDocument> GetAsync(ContentKey document, CancellationToken token = default) => Run(document.ProfileId, async w =>
        await repository.FindAsync(w, document.Id, token) ?? throw new ContentValidationException("This document is unavailable in this Profile."), token);
    public Task<RichTextDocument> UpdateAsync(ContentKey reference, string content, CancellationToken token = default) => Run(reference.ProfileId,
        w => repository.UpdateAsync(w, reference.Id, RichTextFormat.Validate(content), clock.GetUtcNow(), token), token);
    public Task<RichTextReference> AddReferenceAsync(WorkspaceItemReference owner, ContentKey document, CancellationToken token = default) => Run(owner.ProfileId, w =>
    {
        if (owner.ProfileId != document.ProfileId) throw new ContentValidationException("Content references must stay in the same Profile.");
        return repository.LinkAsync(w, owner.ItemId, document.Id, clock.GetUtcNow(), token);
    }, token);
    public Task RemoveReferenceAsync(ContentKey reference, CancellationToken token = default) => Run(reference.ProfileId, async w => { await repository.RemoveAsync(w, reference.Id, token); return true; }, token);
    public Task<IReadOnlyList<RichTextAttachment>> ListAsync(WorkspaceItemReference owner, CancellationToken token = default) => Run(owner.ProfileId, w => repository.ListAsync(w, owner.ItemId, token), token);
    public string ExtractPlainText(RichTextDocument document) => RichTextFormat.Extract(document);
    private async Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new WorkspaceContext(profile, database) { Audit = AuditOperation.User() }); }
        catch (ContentValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Rich content operation failed in profile {ProfileId}", profile); throw new ContentOperationException("Rich content could not be loaded or saved. Check workspace access and retry.", e); }
    }
}
