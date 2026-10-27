using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;

namespace PersonalWorkspace.Tests;

// Populate historical schemas directly. Current public writers require migration 21 and
// must never silently omit history merely because the audit schema is missing.
internal static class LegacyWorkspaceFixture
{
    internal static async Task CreateTask(WorkspaceContext w, TaskItem task)
    {
        using var c = await Open(w); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO WorkspaceItems VALUES($id,1,$title,$created,$updated,$archived,$deleted); INSERT INTO Tasks(ItemId,Description,Status,Priority,ScheduledDate) VALUES($id,$description,$status,$priority,$date);";
        void Add(string key, object? value) => cmd.Parameters.AddWithValue("$" + key, value ?? DBNull.Value);
        Add("id", task.Item.Id.ToString()); Add("title", task.Item.Title); Add("created", task.Item.CreatedAtUtc.ToString("O")); Add("updated", task.Item.UpdatedAtUtc.ToString("O"));
        Add("archived", task.Item.ArchivedAtUtc?.ToString("O")); Add("deleted", task.Item.DeletedAtUtc?.ToString("O")); Add("description", task.Description); Add("status", (int)task.Status); Add("priority", (int)task.Priority); Add("date", task.ScheduledDate?.ToString("yyyy-MM-dd"));
        await cmd.ExecuteNonQueryAsync();
        if (task.ParentTaskId is { } parent) { cmd.CommandText = "UPDATE Tasks SET ParentTaskId=$parent WHERE ItemId=$id"; Add("parent", parent.ToString()); await cmd.ExecuteNonQueryAsync(); }
        if (task.Value is { } v)
        {
            cmd.CommandText = "INSERT INTO TaskValues VALUES($id,$type,$target,$actual,$seconds,$actualSeconds,$currency,$unit)";
            var duration = v.Type == TaskValueType.Duration;
            Add("type", (int)v.Type); Add("target", duration ? null : ExactValueText.Store(v.Target)); Add("actual", duration || v.Actual is null ? null : ExactValueText.Store(v.Actual.Value));
            Add("seconds", duration ? (long)v.Target : null); Add("actualSeconds", duration && v.Actual is { } a ? (long)a : null); Add("currency", v.CurrencyCode); Add("unit", v.Unit); await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }
    internal static async Task CreateEvent(WorkspaceContext w, EventItem item)
    {
        using var c = await Open(w); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "INSERT INTO WorkspaceItems VALUES($id,2,$title,$created,$created,NULL,NULL); INSERT INTO Events VALUES($id,$all,$start,$startTime,$end,$endTime)",
            ("id", item.Item.Id), ("title", item.Item.Title), ("created", item.Item.CreatedAtUtc.ToString("O")), ("all", item.AllDay), ("start", item.StartDate), ("startTime", item.StartTime?.ToString("HH:mm:ss.fffffff")), ("end", item.EndDate), ("endTime", item.EndTime?.ToString("HH:mm:ss.fffffff")));
        await cmd.ExecuteNonQueryAsync(); tx.Commit();
    }
    internal static async Task CreateTagAssignment(WorkspaceContext w, Guid tag, Guid task, DateTimeOffset now)
    {
        using var c = await Open(w); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "INSERT INTO Tags VALUES($tag,'health',NULL,$now,$now); INSERT INTO ItemTags VALUES($task,$tag)", ("tag", tag), ("task", task), ("now", now.ToString("O")));
        await cmd.ExecuteNonQueryAsync(); tx.Commit();
    }
    internal static async Task<RichTextAttachment> CreateDocument(WorkspaceContext w, Guid page, RichTextDocument document)
    {
        using var c = await Open(w); using var tx = c.BeginTransaction();
        var reference = new RichTextReference(Guid.NewGuid(), document.Id, page, document.CreatedAtUtc);
        using var cmd = Command(c, tx, "INSERT INTO RichTextDocuments VALUES($id,$content,$format,$version,$created,$updated); INSERT INTO RichTextReferences VALUES($ref,$id,$page,$created)",
            ("id", document.Id), ("content", document.Content), ("format", document.Format), ("version", document.FormatVersion), ("created", document.CreatedAtUtc.ToString("O")), ("updated", document.UpdatedAtUtc.ToString("O")), ("ref", reference.Id), ("page", page));
        await cmd.ExecuteNonQueryAsync(); tx.Commit(); return new(reference, document);
    }
    internal static async Task CompleteImport(WorkspaceContext w, Guid page, Asset asset)
    {
        using var c = await Open(w); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "INSERT INTO Assets VALUES($id,$path,$name,$type,$size,$hash,$created,$width,$height,0); INSERT INTO AssetReferences VALUES($ref,$id,$page,0,0,$created); DELETE FROM AssetImports WHERE AssetId=$id",
            ("id", asset.Id), ("path", asset.StorageRelativePath), ("name", asset.OriginalFileName), ("type", asset.MediaType), ("size", asset.ByteLength), ("hash", asset.ContentHash), ("created", asset.CreatedAtUtc.ToString("O")), ("width", asset.PixelWidth), ("height", asset.PixelHeight), ("ref", Guid.NewGuid()), ("page", page));
        await cmd.ExecuteNonQueryAsync(); tx.Commit();
    }
    private static async Task<SqliteConnection> Open(WorkspaceContext w)
    { var c = new SqliteConnection($"Data Source={w.DatabasePath};Pooling=False;Foreign Keys=True"); c.CreateCollation("WORKSPACE_NAME", StringComparer.OrdinalIgnoreCase.Compare); await c.OpenAsync(); return c; }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue("$" + name, value switch { null => DBNull.Value, Guid id => id.ToString(), DateOnly date => date.ToString("yyyy-MM-dd"), _ => value }); return cmd;
    }
}
