using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

// Capture identities before FK SET NULL/cascades remove the relationship used to find them.
internal sealed class AuditConsequences
{
    internal List<AuditEntity> Before { get; } = [];
    private readonly List<Func<Task<AuditEntity?>>> readers = [];
    internal async Task<List<AuditEntity>> After()
    { var result = new List<AuditEntity>(); foreach (var read in readers) if (await read() is { } e) result.Add(e); return result; }
    internal static async Task<AuditConsequences> ForDeletedItems(SqliteConnection c, SqliteTransaction tx, IEnumerable<Guid> ids, CancellationToken token)
    {
        var capture = new AuditConsequences();
        foreach (var id in ids)
        {
            var widgets = new List<Guid>(); var templates = new List<Guid>(); var values = new List<(Guid Entry, Guid Field)>();
            using (var cmd = SqliteAuditWriter.Command(c, tx, "SELECT Id FROM WidgetInstances WHERE SourceItemId=$id", ("id", id)))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) widgets.Add(Guid.Parse(r.GetString(0)));
            using (var cmd = SqliteAuditWriter.Command(c, tx, "SELECT Id FROM PageTemplateWidgets WHERE SourceItemId=$id", ("id", id)))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) templates.Add(Guid.Parse(r.GetString(0)));
            using (var cmd = SqliteAuditWriter.Command(c, tx, "SELECT EntryId,FieldId FROM JournalValues WHERE TaskId=$id OR TrackerId=$id", ("id", id)))
            using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token)) values.Add((Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));
            foreach (var widget in widgets) await capture.Add(async () =>
            {
                using var cmd = SqliteAuditWriter.Command(c, tx, "SELECT w.SourceItemId,c.PageId,w.WidgetType FROM WidgetInstances w JOIN PageCanvasItems c ON c.Id=w.CanvasItemId WHERE w.Id=$id", ("id", widget));
                using var r = await cmd.ExecuteReaderAsync(token); return !await r.ReadAsync(token) ? null : AuditProjection.Entity(AuditEntityType.Widget, widget, ((WidgetType)r.GetInt32(2)).ToString(), Guid.Parse(r.GetString(1)), fields: [("Source", r.IsDBNull(0) ? null : Guid.Parse(r.GetString(0)))]);
            });
            foreach (var widget in templates) await capture.Add(async () =>
            {
                using var cmd = SqliteAuditWriter.Command(c, tx, "SELECT w.TemplateId,t.Name,w.SourceItemId FROM PageTemplateWidgets w JOIN Templates t ON t.Id=w.TemplateId WHERE w.Id=$id", ("id", widget));
                using var r = await cmd.ExecuteReaderAsync(token); return !await r.ReadAsync(token) ? null : AuditProjection.Entity(AuditEntityType.Template, Guid.Parse(r.GetString(0)), r.GetString(1), fields: [("WidgetSource:" + widget, r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)))]);
            });
            foreach (var value in values) await capture.Add(async () =>
            {
                using var cmd = SqliteAuditWriter.Command(c, tx, "SELECT e.JournalId,w.Title,e.EntryDate,coalesce(v.TaskId,v.TrackerId) FROM JournalValues v JOIN JournalEntries e ON e.Id=v.EntryId JOIN WorkspaceItems w ON w.Id=e.JournalId WHERE v.EntryId=$entry AND v.FieldId=$field", ("entry", value.Entry), ("field", value.Field));
                using var r = await cmd.ExecuteReaderAsync(token); return !await r.ReadAsync(token) ? null : AuditProjection.Entity(AuditEntityType.JournalEntry, value.Entry, r.GetString(1), Guid.Parse(r.GetString(0)), DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd"), ("Reference:" + value.Field, r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3))));
            });
        }
        return capture;
    }
    private async Task Add(Func<Task<AuditEntity?>> read)
    { if (await read() is { } before) { Before.Add(before); readers.Add(read); } }
    internal static async Task<List<AuditEntity>> PageContents(SqliteConnection c, SqliteTransaction tx, Guid page, CancellationToken token)
    {
        var result = AuditProjection.Widgets(await SqliteWidgetRepository.Read(c, tx, page, null, token));
        result.AddRange(AuditProjection.Decorations(await SqliteDecorationRepository.Read(c, tx, page, token))); return result;
    }
}
