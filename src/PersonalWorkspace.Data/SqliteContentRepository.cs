using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteContentRepository : IAssetRepository, IRichContentRepository
{
    private static async Task<T> Run<T>(WorkspaceContext w, bool write, Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken token)
    {
        await using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        using var tx = c.BeginTransaction(deferred: !write);
        var result = await action(c, tx); await tx.CommitAsync(token); return result;
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue("$" + key, value switch { null => DBNull.Value, Guid id => id.ToString("D"), DateTimeOffset utc => utc.ToUniversalTime().ToString("O"), _ => value });
        return cmd;
    }
    private static async Task Execute(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken token, params (string, object?)[] values)
    { using var cmd = Command(c, tx, sql, values); await cmd.ExecuteNonQueryAsync(token); }
    private static async Task Owner(SqliteConnection c, SqliteTransaction tx, Guid id, bool active, CancellationToken token)
    {
        using var cmd = Command(c, tx, "SELECT COUNT(*) FROM WorkspaceItems WHERE Id=$id" + (active ? " AND ArchivedAtUtc IS NULL AND DeletedAtUtc IS NULL" : ""), ("id", id));
        if ((long)(await cmd.ExecuteScalarAsync(token))! != 1) throw new ContentValidationException("Choose an existing, active owner in this Profile before changing content.");
    }
    private static Guid GuidAt(SqliteDataReader r, int i) => Guid.Parse(r.GetString(i));
    private static DateTimeOffset Utc(SqliteDataReader r, int i) => DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
    private static Asset AssetRow(SqliteDataReader r) => new(GuidAt(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), Utc(r, 6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.GetBoolean(9));
    private static RichTextDocument DocumentRow(SqliteDataReader r) => new(GuidAt(r, 0), r.GetString(1), r.GetString(2), r.GetInt32(3), Utc(r, 4), Utc(r, 5));
    private static async Task<Asset?> AssetById(SqliteConnection c, SqliteTransaction tx, Guid id, CancellationToken token)
    {
        using var cmd = Command(c, tx, "SELECT * FROM Assets WHERE Id=$id", ("id", id)); using var r = await cmd.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? AssetRow(r) : null;
    }
    public Task BeginImportAsync(WorkspaceContext w, Guid owner, AssetImportJob job, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        await Owner(c, tx, owner, true, token);
        await Execute(c, tx, "INSERT INTO AssetImports VALUES($id,$path)", token, ("id", job.AssetId), ("path", job.StorageRelativePath)); return true;
    }, token);
    public Task<AssetAttachment> CompleteImportAsync(WorkspaceContext w, Guid owner, Asset asset, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        await Owner(c, tx, owner, true, token);
        using (var job = Command(c, tx, "SELECT COUNT(*) FROM AssetImports WHERE AssetId=$id AND StorageRelativePath=$path", ("id", asset.Id), ("path", asset.StorageRelativePath)))
            if ((long)(await job.ExecuteScalarAsync(token))! != 1) throw new ContentValidationException("The import recovery record is missing.");
        await Execute(c, tx, "INSERT INTO Assets VALUES($id,$path,$name,$type,$size,$hash,$utc,$width,$height,0)", token,
            ("id", asset.Id), ("path", asset.StorageRelativePath), ("name", asset.OriginalFileName), ("type", asset.MediaType), ("size", asset.ByteLength), ("hash", asset.ContentHash), ("utc", asset.CreatedAtUtc), ("width", asset.PixelWidth), ("height", asset.PixelHeight));
        var reference = await AssetLink(c, tx, owner, asset.Id, asset.CreatedAtUtc, token);
        await Execute(c, tx, "DELETE FROM AssetImports WHERE AssetId=$id", token, ("id", asset.Id)); return new AssetAttachment(reference, asset);
    }, token);
    public Task<Asset?> FindAsync(WorkspaceContext w, Guid id, CancellationToken token) => Run(w, false, (c, tx) => AssetById(c, tx, id, token), token);
    public Task<IReadOnlyList<Asset>> AssetsAsync(WorkspaceContext w, bool pendingOnly, CancellationToken token) => Run<IReadOnlyList<Asset>>(w, false, async (c, tx) =>
    {
        using var cmd = Command(c, tx, "SELECT * FROM Assets" + (pendingOnly ? " WHERE PendingDelete=1" : "") + " ORDER BY Id"); using var r = await cmd.ExecuteReaderAsync(token); var result = new List<Asset>();
        while (await r.ReadAsync(token)) result.Add(AssetRow(r)); return result;
    }, token);
    public Task<IReadOnlyList<AssetImportJob>> ImportsAsync(WorkspaceContext w, CancellationToken token) => Run<IReadOnlyList<AssetImportJob>>(w, false, async (c, tx) =>
    {
        using var cmd = Command(c, tx, "SELECT AssetId,StorageRelativePath FROM AssetImports ORDER BY AssetId"); using var r = await cmd.ExecuteReaderAsync(token); var result = new List<AssetImportJob>();
        while (await r.ReadAsync(token)) result.Add(new(GuidAt(r, 0), r.GetString(1))); return result;
    }, token);
    public Task FinishCleanupAsync(WorkspaceContext w, Guid id, bool import, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        await Execute(c, tx, import ? "DELETE FROM AssetImports WHERE AssetId=$id AND NOT EXISTS(SELECT 1 FROM Assets WHERE Id=$id)" : "DELETE FROM Assets WHERE Id=$id AND PendingDelete=1 AND NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=$id)", token, ("id", id)); return true;
    }, token);
    private static async Task<AssetReference> AssetLink(SqliteConnection c, SqliteTransaction tx, Guid owner, Guid asset, DateTimeOffset now, CancellationToken token)
    {
        await Owner(c, tx, owner, true, token);
        var existing = await AssetById(c, tx, asset, token);
        if (existing is null || existing.PendingDelete) throw new ContentValidationException("This Asset is unavailable in this Profile.");
        using var order = Command(c, tx, "SELECT COALESCE(MAX(SortOrder),-1)+1 FROM AssetReferences WHERE OwnerItemId=$owner", ("owner", owner));
        var reference = new AssetReference(Guid.NewGuid(), asset, owner, AssetRole.Attachment, checked((int)(long)(await order.ExecuteScalarAsync(token))!), now);
        await Execute(c, tx, "INSERT INTO AssetReferences VALUES($id,$asset,$owner,0,$sort,$utc)", token, ("id", reference.Id), ("asset", asset), ("owner", owner), ("sort", reference.SortOrder), ("utc", now)); return reference;
    }
    public Task<AssetReference> LinkAsync(WorkspaceContext w, Guid owner, Guid asset, DateTimeOffset now, CancellationToken token) => Run(w, true, (c, tx) => AssetLink(c, tx, owner, asset, now, token), token);
    public Task<IReadOnlyList<AssetAttachment>> ListAsync(WorkspaceContext w, Guid owner, CancellationToken token) => Run<IReadOnlyList<AssetAttachment>>(w, false, async (c, tx) =>
    {
        await Owner(c, tx, owner, false, token);
        using var cmd = Command(c, tx, "SELECT a.*,r.Id,r.OwnerItemId,r.Role,r.SortOrder,r.CreatedAtUtc FROM AssetReferences r JOIN Assets a ON a.Id=r.AssetId WHERE r.OwnerItemId=$owner ORDER BY r.SortOrder,r.Id", ("owner", owner));
        using var r = await cmd.ExecuteReaderAsync(token); var result = new List<AssetAttachment>();
        while (await r.ReadAsync(token)) { var a = AssetRow(r); result.Add(new(new(GuidAt(r, 10), a.Id, GuidAt(r, 11), (AssetRole)r.GetInt32(12), r.GetInt32(13), Utc(r, 14)), a)); } return result;
    }, token);
    private static Task Remove(WorkspaceContext w, Guid id, bool rich, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        var table = rich ? "RichTextReferences" : "AssetReferences";
        using var cmd = Command(c, tx, $"SELECT OwnerItemId FROM {table} WHERE Id=$id", ("id", id)); var owner = await cmd.ExecuteScalarAsync(token);
        if (owner is not string value) throw new ContentValidationException("This reference is no longer available.");
        await Owner(c, tx, Guid.Parse(value), true, token);
        await Execute(c, tx, $"DELETE FROM {table} WHERE Id=$id", token, ("id", id)); return true;
    }, token);
    public Task RemoveAsync(WorkspaceContext w, Guid reference, CancellationToken token) => Remove(w, reference, false, token);

    private static async Task<RichTextDocument?> DocumentById(SqliteConnection c, SqliteTransaction tx, Guid id, CancellationToken token)
    {
        using var cmd = Command(c, tx, "SELECT * FROM RichTextDocuments WHERE Id=$id", ("id", id)); using var r = await cmd.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? DocumentRow(r) : null;
    }
    private static async Task<RichTextReference> DocumentLink(SqliteConnection c, SqliteTransaction tx, Guid owner, Guid document, DateTimeOffset now, CancellationToken token)
    {
        await Owner(c, tx, owner, true, token);
        if (await DocumentById(c, tx, document, token) is null) throw new ContentValidationException("This document is unavailable in this Profile.");
        var reference = new RichTextReference(Guid.NewGuid(), document, owner, now);
        await Execute(c, tx, "INSERT INTO RichTextReferences VALUES($id,$document,$owner,$utc)", token, ("id", reference.Id), ("document", document), ("owner", owner), ("utc", now)); return reference;
    }
    public Task<RichTextAttachment> CreateAsync(WorkspaceContext w, Guid owner, RichTextDocument document, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        await Owner(c, tx, owner, true, token);
        await Execute(c, tx, "INSERT INTO RichTextDocuments VALUES($id,$content,$format,$version,$created,$updated)", token, ("id", document.Id), ("content", document.Content), ("format", document.Format), ("version", document.FormatVersion), ("created", document.CreatedAtUtc), ("updated", document.UpdatedAtUtc));
        return new RichTextAttachment(await DocumentLink(c, tx, owner, document.Id, document.CreatedAtUtc, token), document);
    }, token);
    Task<RichTextDocument?> IRichContentRepository.FindAsync(WorkspaceContext w, Guid document, CancellationToken token) => Run(w, false, (c, tx) => DocumentById(c, tx, document, token), token);
    Task<RichTextReference> IRichContentRepository.LinkAsync(WorkspaceContext w, Guid owner, Guid document, DateTimeOffset now, CancellationToken token) => Run(w, true, (c, tx) => DocumentLink(c, tx, owner, document, now, token), token);
    Task IRichContentRepository.RemoveAsync(WorkspaceContext w, Guid reference, CancellationToken token) => Remove(w, reference, true, token);
    public Task<RichTextDocument> UpdateAsync(WorkspaceContext w, Guid reference, string content, DateTimeOffset now, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        Guid owner, id;
        using (var cmd = Command(c, tx, "SELECT OwnerItemId,DocumentId FROM RichTextReferences WHERE Id=$id", ("id", reference)))
        using (var r = await cmd.ExecuteReaderAsync(token))
        { if (!await r.ReadAsync(token)) throw new ContentValidationException("This reference is no longer available."); owner = GuidAt(r, 0); id = GuidAt(r, 1); }
        await Owner(c, tx, owner, true, token); var old = (await DocumentById(c, tx, id, token))!;
        if (content == old.Content) return old;
        var updated = old with { Content = content, UpdatedAtUtc = now > old.UpdatedAtUtc ? now : old.UpdatedAtUtc.AddTicks(1) };
        await Execute(c, tx, "UPDATE RichTextDocuments SET Content=$content,UpdatedAtUtc=$utc WHERE Id=$id", token, ("content", content), ("utc", updated.UpdatedAtUtc), ("id", id)); return updated;
    }, token);
    Task<IReadOnlyList<RichTextAttachment>> IRichContentRepository.ListAsync(WorkspaceContext w, Guid owner, CancellationToken token) => Run<IReadOnlyList<RichTextAttachment>>(w, false, async (c, tx) =>
    {
        await Owner(c, tx, owner, false, token);
        using var cmd = Command(c, tx, "SELECT d.*,r.Id,r.OwnerItemId,r.CreatedAtUtc FROM RichTextReferences r JOIN RichTextDocuments d ON d.Id=r.DocumentId WHERE r.OwnerItemId=$owner ORDER BY r.CreatedAtUtc,r.Id", ("owner", owner));
        using var r = await cmd.ExecuteReaderAsync(token); var result = new List<RichTextAttachment>();
        while (await r.ReadAsync(token)) { var d = DocumentRow(r); result.Add(new(new(GuidAt(r, 6), d.Id, GuidAt(r, 7), Utc(r, 8)), d)); } return result;
    }, token);
}
