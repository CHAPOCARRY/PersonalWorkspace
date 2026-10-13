namespace PersonalWorkspace.Core;

public enum AssetRole { Attachment }
public sealed record ContentKey(Guid ProfileId, Guid Id);
public sealed record Asset(Guid Id, string StorageRelativePath, string OriginalFileName, string MediaType,
    long ByteLength, string ContentHash, DateTimeOffset CreatedAtUtc, int? PixelWidth = null, int? PixelHeight = null, bool PendingDelete = false);
public sealed record AssetReference(Guid Id, Guid AssetId, Guid OwnerItemId, AssetRole Role, int SortOrder, DateTimeOffset CreatedAtUtc);
public sealed record AssetAttachment(AssetReference Reference, Asset Asset);
public sealed record AssetImportJob(Guid AssetId, string StorageRelativePath)
{
    public string StagingRelativePath => StorageRelativePath + ".import";
}
public sealed record AssetFileInfo(long ByteLength, string ContentHash, string MediaType, int? PixelWidth = null, int? PixelHeight = null);
public sealed record AssetCleanupResult(int Removed, IReadOnlyList<Guid> Pending);
public sealed record AssetIntegrityIssue(Guid AssetId, string Reason);
public sealed record RichTextDocument(Guid Id, string Content, string Format, int FormatVersion, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record RichTextReference(Guid Id, Guid DocumentId, Guid OwnerItemId, DateTimeOffset CreatedAtUtc);
public sealed record RichTextAttachment(RichTextReference Reference, RichTextDocument Document);
public sealed class ContentValidationException(string message) : Exception(message);
public sealed class ContentOperationException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAssetService
{
    Task<AssetAttachment> ImportAsync(WorkspaceItemReference owner, string sourcePath, CancellationToken token = default);
    Task<Asset> GetAsync(ContentKey asset, CancellationToken token = default);
    Task<IReadOnlyList<AssetAttachment>> ListAsync(WorkspaceItemReference owner, CancellationToken token = default);
    Task<AssetReference> AddReferenceAsync(WorkspaceItemReference owner, ContentKey asset, CancellationToken token = default);
    Task<AssetCleanupResult> RemoveReferenceAsync(ContentKey reference, CancellationToken token = default);
    // Copy rather than returning an unguarded stream/path whose lifetime can outlive profile switching.
    Task CopyToAsync(ContentKey asset, Stream destination, CancellationToken token = default);
    Task<AssetCleanupResult> CleanupAsync(Guid profile, CancellationToken token = default);
    Task<IReadOnlyList<AssetIntegrityIssue>> CheckIntegrityAsync(Guid profile, CancellationToken token = default);
}
public interface IRichContentService
{
    Task<RichTextAttachment> CreateAsync(WorkspaceItemReference owner, string content, CancellationToken token = default);
    Task<RichTextDocument> GetAsync(ContentKey document, CancellationToken token = default);
    Task<RichTextDocument> UpdateAsync(ContentKey reference, string content, CancellationToken token = default);
    Task<RichTextReference> AddReferenceAsync(WorkspaceItemReference owner, ContentKey document, CancellationToken token = default);
    Task RemoveReferenceAsync(ContentKey reference, CancellationToken token = default);
    Task<IReadOnlyList<RichTextAttachment>> ListAsync(WorkspaceItemReference owner, CancellationToken token = default);
    string ExtractPlainText(RichTextDocument document);
}
public interface IAssetFileStore
{
    void CheckImportDestination(Guid profile, AssetImportJob job);
    Task<AssetFileInfo> ImportAsync(Guid profile, AssetImportJob job, string sourcePath, CancellationToken token);
    void Delete(Guid profile, string relativePath);
    Task CopyToAsync(Guid profile, Asset asset, Stream destination, CancellationToken token);
    Task<string?> CheckIntegrityAsync(Guid profile, Asset asset, CancellationToken token);
}
public interface IWorkspaceMaintenance
{
    // Caller holds the workspace gate; does not depend on the current published profile.
    Task RecoverAsync(Guid profile, CancellationToken token);
}
public interface IAssetRepository
{
    Task BeginImportAsync(WorkspaceContext w, Guid owner, AssetImportJob job, CancellationToken token);
    Task<AssetAttachment> CompleteImportAsync(WorkspaceContext w, Guid owner, Asset asset, CancellationToken token);
    Task<Asset?> FindAsync(WorkspaceContext w, Guid id, CancellationToken token);
    Task<IReadOnlyList<Asset>> AssetsAsync(WorkspaceContext w, bool pendingOnly, CancellationToken token);
    Task<IReadOnlyList<AssetImportJob>> ImportsAsync(WorkspaceContext w, CancellationToken token);
    Task FinishCleanupAsync(WorkspaceContext w, Guid id, bool import, CancellationToken token);
    Task<IReadOnlyList<AssetAttachment>> ListAsync(WorkspaceContext w, Guid owner, CancellationToken token);
    Task<AssetReference> LinkAsync(WorkspaceContext w, Guid owner, Guid asset, DateTimeOffset now, CancellationToken token);
    Task RemoveAsync(WorkspaceContext w, Guid reference, CancellationToken token);
}
public interface IRichContentRepository
{
    Task<RichTextAttachment> CreateAsync(WorkspaceContext w, Guid owner, RichTextDocument document, CancellationToken token);
    Task<RichTextDocument?> FindAsync(WorkspaceContext w, Guid document, CancellationToken token);
    Task<RichTextDocument> UpdateAsync(WorkspaceContext w, Guid reference, string content, DateTimeOffset now, CancellationToken token);
    Task<RichTextReference> LinkAsync(WorkspaceContext w, Guid owner, Guid document, DateTimeOffset now, CancellationToken token);
    Task RemoveAsync(WorkspaceContext w, Guid reference, CancellationToken token);
    Task<IReadOnlyList<RichTextAttachment>> ListAsync(WorkspaceContext w, Guid owner, CancellationToken token);
}
