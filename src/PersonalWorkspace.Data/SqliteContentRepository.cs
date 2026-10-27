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
    internal static Asset AssetRow(SqliteDataReader r) => new(GuidAt(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), Utc(r, 6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.GetBoolean(9));
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
        var result = await CompleteImport(c, tx, owner, asset, token);
        await SqliteAuditWriter.Append(c, tx, w, [], AssetAudit(result), token); return result;
    }, token);
    private static List<AuditEntity> AssetAudit(AssetAttachment a) => [AuditProjection.Entity(AuditEntityType.AssetReference, a.Reference.Id, a.Asset.OriginalFileName, a.Reference.OwnerItemId,
        fields: [("Asset", a.Asset.Id), ("Bytes", a.Asset.ByteLength), ("MediaType", a.Asset.MediaType)])];
    private static List<AuditEntity> DocumentAudit(RichTextDocument d, Guid owner) => [AuditProjection.Entity(AuditEntityType.RichText, d.Id, "Rich text", owner,
        fields: [("Content", AuditProjection.Content(d.Content)), ("Format", d.Format), ("FormatVersion", d.FormatVersion)])];
    internal static async Task<AssetAttachment> CompleteImport(SqliteConnection c, SqliteTransaction tx, Guid owner, Asset asset, CancellationToken token)
    {
        await Owner(c, tx, owner, true, token);
        using (var job = Command(c, tx, "SELECT COUNT(*) FROM AssetImports WHERE AssetId=$id AND StorageRelativePath=$path", ("id", asset.Id), ("path", asset.StorageRelativePath)))
            if ((long)(await job.ExecuteScalarAsync(token))! != 1) throw new ContentValidationException("The import recovery record is missing.");
        await Execute(c, tx, "INSERT INTO Assets VALUES($id,$path,$name,$type,$size,$hash,$utc,$width,$height,0)", token,
            ("id", asset.Id), ("path", asset.StorageRelativePath), ("name", asset.OriginalFileName), ("type", asset.MediaType), ("size", asset.ByteLength), ("hash", asset.ContentHash), ("utc", asset.CreatedAtUtc), ("width", asset.PixelWidth), ("height", asset.PixelHeight));
        var reference = await AssetLink(c, tx, owner, asset.Id, asset.CreatedAtUtc, token);
        await Execute(c, tx, "DELETE FROM AssetImports WHERE AssetId=$id", token, ("id", asset.Id)); return new AssetAttachment(reference, asset);
    }
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
        await Execute(c, tx, import ? "DELETE FROM AssetImports WHERE AssetId=$id AND NOT EXISTS(SELECT 1 FROM Assets WHERE Id=$id)" : "DELETE FROM Assets WHERE Id=$id AND PendingDelete=1 AND NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=$id) AND NOT EXISTS(SELECT 1 FROM TemplateAssetReferences WHERE AssetId=$id)", token, ("id", id)); return true;
    }, token);
    public Task<bool> CanCleanupAsync(WorkspaceContext w, Guid id, CancellationToken token) => Run(w, false, async (c, tx) =>
    {
        using var cmd = Command(c, tx, "SELECT EXISTS(SELECT 1 FROM Assets WHERE Id=$id AND PendingDelete=1 AND NOT EXISTS(SELECT 1 FROM AssetReferences WHERE AssetId=$id) AND NOT EXISTS(SELECT 1 FROM TemplateAssetReferences WHERE AssetId=$id))", ("id", id));
        return (long)(await cmd.ExecuteScalarAsync(token))! == 1;
    }, token);
    internal static async Task<AssetReference> AssetLink(SqliteConnection c, SqliteTransaction tx, Guid owner, Guid asset, DateTimeOffset now, CancellationToken token)
    {
        await Owner(c, tx, owner, true, token);
        var existing = await AssetById(c, tx, asset, token);
        if (existing is null || existing.PendingDelete) throw new ContentValidationException("This Asset is unavailable in this Profile.");
        using var order = Command(c, tx, "SELECT COALESCE(MAX(SortOrder),-1)+1 FROM AssetReferences WHERE OwnerItemId=$owner", ("owner", owner));
        var reference = new AssetReference(Guid.NewGuid(), asset, owner, AssetRole.Attachment, checked((int)(long)(await order.ExecuteScalarAsync(token))!), now);
        await Execute(c, tx, "INSERT INTO AssetReferences VALUES($id,$asset,$owner,0,$sort,$utc)", token, ("id", reference.Id), ("asset", asset), ("owner", owner), ("sort", reference.SortOrder), ("utc", now)); return reference;
    }
    public Task<AssetReference> LinkAsync(WorkspaceContext w, Guid owner, Guid asset, DateTimeOffset now, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        var reference = await AssetLink(c, tx, owner, asset, now, token);
        await SqliteAuditWriter.Append(c, tx, w, [], AssetAudit(new(reference, (await AssetById(c, tx, asset, token))!)), token, AuditAction.Linked); return reference;
    }, token);
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
        using var identity = Command(c, tx, $"SELECT {(rich ? "DocumentId" : "AssetId")} FROM {table} WHERE Id=$id", ("id", id));
        var content = Guid.Parse((string)(await identity.ExecuteScalarAsync(token))!);
        var before = rich ? DocumentAudit((await DocumentById(c, tx, content, token))!, Guid.Parse(value))
            : AssetAudit(new(new(id, content, Guid.Parse(value), AssetRole.Attachment, 0, default), (await AssetById(c, tx, content, token))!));
        await Execute(c, tx, $"DELETE FROM {table} WHERE Id=$id", token, ("id", id));
        await SqliteAuditWriter.Append(c, tx, w, before, [], token, AuditAction.Unlinked); return true;
    }, token);
    public Task RemoveAsync(WorkspaceContext w, Guid reference, CancellationToken token) => Remove(w, reference, false, token);

    internal static async Task<RichTextDocument?> DocumentById(SqliteConnection c, SqliteTransaction tx, Guid id, CancellationToken token)
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
        var result = await CreateDocument(c, tx, owner, document, token);
        await SqliteAuditWriter.Append(c, tx, w, [], DocumentAudit(document, owner), token); return result;
    }, token);
    internal static async Task<RichTextAttachment> CreateDocument(SqliteConnection c, SqliteTransaction tx, Guid owner, RichTextDocument document, CancellationToken token)
    {
        await Owner(c, tx, owner, true, token);
        return await InsertDocument(c, tx, owner, document, token);
    }
    private static async Task<RichTextAttachment> InsertDocument(SqliteConnection c, SqliteTransaction tx, Guid owner, RichTextDocument document, CancellationToken token)
    {
        await Execute(c, tx, "INSERT INTO RichTextDocuments VALUES($id,$content,$format,$version,$created,$updated)", token, ("id", document.Id), ("content", document.Content), ("format", document.Format), ("version", document.FormatVersion), ("created", document.CreatedAtUtc), ("updated", document.UpdatedAtUtc));
        var reference = new RichTextReference(Guid.NewGuid(), document.Id, owner, document.CreatedAtUtc);
        await Execute(c, tx, "INSERT INTO RichTextReferences VALUES($id,$document,$owner,$utc)", token, ("id", reference.Id), ("document", document.Id), ("owner", owner), ("utc", document.CreatedAtUtc));
        return new(reference, document);
    }
    internal static async Task<IReadOnlyList<WidgetInstance>> CreatePageWidgetContent(SqliteConnection c, SqliteTransaction tx, Guid page,
        IReadOnlyList<PageTemplateWidget> entries, IReadOnlyList<Asset> assets, CancellationToken token)
    {
        await Owner(c, tx, page, true, token); var available = assets.ToDictionary(a => a.Id); var result = new List<WidgetInstance>(); var order = 0;
        foreach (var entry in entries)
        {
            var widget = entry.Widget;
            if (entry.Text is { } text)
            {
                var document = new RichTextDocument(Guid.NewGuid(), text.Content, text.Format, text.FormatVersion, widget.CreatedAtUtc, widget.CreatedAtUtc);
                var created = await InsertDocument(c, tx, page, document, token); widget = widget with { RichText = new(created.Reference.Id) };
            }
            if (entry.File is { } file)
            {
                if (!available.TryGetValue(file.AssetId, out var asset) || asset.PendingDelete) throw new ContentValidationException("The template Asset is unavailable or pending cleanup.");
                var reference = Guid.NewGuid();
                await Execute(c, tx, "INSERT INTO AssetReferences VALUES($id,$asset,$owner,0,$sort,$utc)", token, ("id", reference), ("asset", asset.Id), ("owner", page), ("sort", order++), ("utc", widget.CreatedAtUtc));
                widget = widget with { Asset = new(reference, file.Fit, file.AltText) };
            }
            result.Add(widget);
        }
        return result;
    }
    Task<RichTextDocument?> IRichContentRepository.FindAsync(WorkspaceContext w, Guid document, CancellationToken token) => Run(w, false, (c, tx) => DocumentById(c, tx, document, token), token);
    Task<RichTextReference> IRichContentRepository.LinkAsync(WorkspaceContext w, Guid owner, Guid document, DateTimeOffset now, CancellationToken token) => Run(w, true, async (c, tx) =>
    {
        var result = await DocumentLink(c, tx, owner, document, now, token);
        await SqliteAuditWriter.Append(c, tx, w, [], DocumentAudit((await DocumentById(c, tx, document, token))!, owner), token, AuditAction.Linked); return result;
    }, token);
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
        await Execute(c, tx, "UPDATE RichTextDocuments SET Content=$content,UpdatedAtUtc=$utc WHERE Id=$id", token, ("content", content), ("utc", updated.UpdatedAtUtc), ("id", id));
        var before = DocumentAudit(old, owner); var after = DocumentAudit(updated, owner);
        using (var owners = Command(c, tx, "SELECT w.Id,w.ItemType,w.Title FROM RichTextReferences r JOIN WorkspaceItems w ON w.Id=r.OwnerItemId WHERE r.DocumentId=$id", ("id", id)))
        using (var r = await owners.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var context = AuditProjection.Entity((AuditEntityType)r.GetInt32(1), GuidAt(r, 0), r.GetString(2));
            before.Add(context); after.Add(context);
        }
        await SqliteAuditWriter.Append(c, tx, w, before, after, token); return updated;
    }, token);
    Task<IReadOnlyList<RichTextAttachment>> IRichContentRepository.ListAsync(WorkspaceContext w, Guid owner, CancellationToken token) => Run<IReadOnlyList<RichTextAttachment>>(w, false, async (c, tx) =>
    {
        await Owner(c, tx, owner, false, token);
        using var cmd = Command(c, tx, "SELECT d.*,r.Id,r.OwnerItemId,r.CreatedAtUtc FROM RichTextReferences r JOIN RichTextDocuments d ON d.Id=r.DocumentId WHERE r.OwnerItemId=$owner ORDER BY r.CreatedAtUtc,r.Id", ("owner", owner));
        using var r = await cmd.ExecuteReaderAsync(token); var result = new List<RichTextAttachment>();
        while (await r.ReadAsync(token)) { var d = DocumentRow(r); result.Add(new(new(GuidAt(r, 6), d.Id, GuidAt(r, 7), Utc(r, 8)), d)); } return result;
    }, token);
}
