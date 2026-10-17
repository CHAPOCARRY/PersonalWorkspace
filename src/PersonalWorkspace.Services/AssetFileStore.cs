using System.Security.Cryptography;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class AssetFileStore(AssetPaths paths) : IAssetFileStore
{
    public async Task UseFileAsync(Guid profile, Asset asset, IAssetFileConsumer consumer, CancellationToken token)
    {
        AssetPaths.ValidateIdentity(asset.Id, asset.StorageRelativePath);
        var path = paths.Resolve(profile, asset.StorageRelativePath);
        if (!File.Exists(path)) throw new ContentValidationException("The attachment file is unavailable.");
        token.ThrowIfCancellationRequested();
        await consumer.UseAsync(path, asset, token);
    }
    public void CheckImportDestination(Guid profile, AssetImportJob job)
    {
        AssetPaths.ValidateIdentity(job.AssetId, job.StorageRelativePath);
        foreach (var relative in new[] { job.StorageRelativePath, job.StagingRelativePath })
        {
            var path = paths.Resolve(profile, relative);
            if (File.Exists(path) || Directory.Exists(path)) throw new ContentValidationException("The generated Asset destination is already occupied.");
        }
    }
    public async Task<AssetFileInfo> ImportAsync(Guid profile, AssetImportJob job, string sourcePath, CancellationToken token)
    {
        AssetPaths.ValidateIdentity(job.AssetId, job.StorageRelativePath);
        if (!Path.IsPathFullyQualified(sourcePath)) throw new ContentValidationException("Select an absolute source file path.");
        AssetPaths.RejectLinks(sourcePath);
        var staged = paths.Resolve(profile, job.StagingRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!); staged = paths.Resolve(profile, job.StagingRelativePath);
        long length = 0; string hash;
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920]; int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            { length = checked(length + count); hashing.AppendData(buffer, 0, count); await target.WriteAsync(buffer.AsMemory(0, count), token); }
            await target.FlushAsync(token); target.Flush(flushToDisk: true); hash = Convert.ToHexString(hashing.GetHashAndReset());
        }
        token.ThrowIfCancellationRequested();
        AssetFileInfo info;
        using (var input = File.OpenRead(paths.Resolve(profile, job.StagingRelativePath))) info = AssetMedia.Inspect(input, Path.GetExtension(sourcePath), length, hash);
        File.Move(paths.Resolve(profile, job.StagingRelativePath), paths.Resolve(profile, job.StorageRelativePath), overwrite: false);
        return info;
    }
    public void Delete(Guid profile, string relativePath)
    {
        var path = paths.Resolve(profile, relativePath);
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // A crash may have preceded even staging-directory creation.
    }
    private FileStream Read(Guid profile, Asset asset)
    {
        AssetPaths.ValidateIdentity(asset.Id, asset.StorageRelativePath);
        return new(paths.Resolve(profile, asset.StorageRelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    public async Task CopyToAsync(Guid profile, Asset asset, Stream destination, CancellationToken token)
    {
        await using var source = Read(profile, asset); await source.CopyToAsync(destination, 81920, token);
    }
    public async Task<string?> CheckIntegrityAsync(Guid profile, Asset asset, CancellationToken token)
    {
        await using var source = Read(profile, asset);
        if (source.Length != asset.ByteLength) return "Stored file length differs from metadata.";
        return Convert.ToHexString(await SHA256.HashDataAsync(source, token)) == asset.ContentHash ? null : "Stored file hash differs from metadata.";
    }
}
