using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class AssetService(IAssetRepository repository, IAssetFileStore files, IApplicationPaths paths, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<AssetService> logger) : IAssetService, IWorkspaceMaintenance
{
    public Task<AssetAttachment> ImportAsync(WorkspaceItemReference owner, string sourcePath, CancellationToken token = default) => Import(owner, sourcePath, null, token);
    public Task<AssetAttachment> ImportToAsync(WorkspaceItemReference owner, string sourcePath, IAssetImportTarget target, CancellationToken token = default) => Import(owner, sourcePath, target, token);
    private Task<AssetAttachment> Import(WorkspaceItemReference owner, string sourcePath, IAssetImportTarget? target, CancellationToken token) => Run(owner.ProfileId, async w =>
    {
        var name = AssetPaths.FileName(sourcePath); var id = Guid.NewGuid(); var job = new AssetImportJob(id, AssetPaths.Relative(id, name));
        files.CheckImportDestination(w.ProfileId, job);
        await repository.BeginImportAsync(w, owner.ItemId, job, token);
        try
        {
            var info = await files.ImportAsync(w.ProfileId, job, sourcePath, token);
            var asset = new Asset(id, job.StorageRelativePath, name, info.MediaType, info.ByteLength, info.ContentHash, clock.GetUtcNow(), info.PixelWidth, info.PixelHeight);
            return target is null ? await repository.CompleteImportAsync(w, owner.ItemId, asset, token) : await target.CompleteAsync(w, owner.ItemId, asset, token);
        }
        catch
        {
            // A failed/ambiguous commit must never cause deletion of a published Asset.
            try { await RecoverImport(w, job, CancellationToken.None); }
            catch (Exception e) { logger.LogError(e, "Import cleanup remains pending for Asset {AssetId}", id); throw new ContentOperationException("Import failed and file cleanup is pending. Reopen the Profile or retry Asset cleanup after checking file access.", e); }
            throw;
        }
    }, token);
    private async Task<Asset> Find(WorkspaceContext w, Guid id, CancellationToken token)
    {
        var asset = await repository.FindAsync(w, id, token);
        if (asset is null || asset.PendingDelete) throw new ContentValidationException("This Asset is unavailable or pending deletion in this Profile.");
        AssetPaths.ValidateIdentity(asset.Id, asset.StorageRelativePath); return asset;
    }
    public Task<Asset> GetAsync(ContentKey asset, CancellationToken token = default) => Run(asset.ProfileId, w => Find(w, asset.Id, token), token);
    public Task<IReadOnlyList<AssetAttachment>> ListAsync(WorkspaceItemReference owner, CancellationToken token = default) => Run(owner.ProfileId, w => repository.ListAsync(w, owner.ItemId, token), token);
    public Task<AssetReference> AddReferenceAsync(WorkspaceItemReference owner, ContentKey asset, CancellationToken token = default) => Run(owner.ProfileId, async w =>
    {
        if (owner.ProfileId != asset.ProfileId) throw new ContentValidationException("Asset references must stay in the same Profile.");
        await Find(w, asset.Id, token); return await repository.LinkAsync(w, owner.ItemId, asset.Id, clock.GetUtcNow(), token);
    }, token);
    public Task<AssetCleanupResult> RemoveReferenceAsync(ContentKey reference, CancellationToken token = default) => Run(reference.ProfileId, async w =>
    { await repository.RemoveAsync(w, reference.Id, token); return await Cleanup(w, CancellationToken.None); }, token);
    public Task CopyToAsync(ContentKey asset, Stream destination, CancellationToken token = default) => Run(asset.ProfileId, async w =>
    { await files.CopyToAsync(w.ProfileId, await Find(w, asset.Id, token), destination, token); return true; }, token);
    public Task<AssetCleanupResult> CleanupAsync(Guid profile, CancellationToken token = default) => Run(profile, w => Cleanup(w, token), token);
    public Task UseFileAsync(ContentKey asset, IAssetFileConsumer consumer, CancellationToken token = default) => Run(asset.ProfileId, async w =>
    { await files.UseFileAsync(w.ProfileId, await Find(w, asset.Id, token), consumer, token); return true; }, token);
    async Task IWorkspaceMaintenance.RecoverAsync(Guid profile, CancellationToken token) => _ = await Cleanup(new(profile, paths.WorkspaceDatabase(profile)), token);
    private async Task RecoverImport(WorkspaceContext w, AssetImportJob job, CancellationToken token)
    {
        AssetPaths.ValidateIdentity(job.AssetId, job.StorageRelativePath);
        if (await repository.FindAsync(w, job.AssetId, token) is not null) throw new ContentValidationException("An import recovery record conflicts with a published Asset; no files were removed.");
        files.Delete(w.ProfileId, job.StagingRelativePath); files.Delete(w.ProfileId, job.StorageRelativePath);
        await repository.FinishCleanupAsync(w, job.AssetId, true, token);
    }
    private async Task<AssetCleanupResult> Cleanup(WorkspaceContext w, CancellationToken token)
    {
        var pending = new List<Guid>(); var removed = 0;
        foreach (var job in await repository.ImportsAsync(w, token))
        {
            token.ThrowIfCancellationRequested();
            try { await RecoverImport(w, job, token); removed++; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { pending.Add(job.AssetId); logger.LogWarning(e, "Asset import cleanup pending {AssetId}", job.AssetId); }
        }
        foreach (var asset in await repository.AssetsAsync(w, true, token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                AssetPaths.ValidateIdentity(asset.Id, asset.StorageRelativePath); files.Delete(w.ProfileId, asset.StorageRelativePath);
                await repository.FinishCleanupAsync(w, asset.Id, false, token); removed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { pending.Add(asset.Id); logger.LogWarning(e, "Orphan Asset cleanup pending {AssetId}", asset.Id); }
        }
        return new(removed, pending);
    }
    public Task<IReadOnlyList<AssetIntegrityIssue>> CheckIntegrityAsync(Guid profile, CancellationToken token = default) => Run<IReadOnlyList<AssetIntegrityIssue>>(profile, async w =>
    {
        var issues = new List<AssetIntegrityIssue>();
        foreach (var job in await repository.ImportsAsync(w, token)) issues.Add(new(job.AssetId, "Interrupted import awaits cleanup."));
        foreach (var asset in await repository.AssetsAsync(w, false, token))
        {
            if (asset.PendingDelete) { issues.Add(new(asset.Id, "Orphan Asset awaits cleanup.")); continue; }
            try { if (await files.CheckIntegrityAsync(w.ProfileId, asset, token) is { } error) issues.Add(new(asset.Id, error)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContentValidationException) { issues.Add(new(asset.Id, "File is missing, inaccessible or has an unsafe storage path.")); }
        }
        return issues;
    }, token);
    private async Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profile, database)); }
        catch (ContentValidationException) { throw; }
        catch (ContentOperationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Asset operation failed in profile {ProfileId}", profile); throw new ContentOperationException("The Asset operation failed. Check file/workspace access and retry. Interrupted imports are tracked for cleanup.", e); }
    }
}
