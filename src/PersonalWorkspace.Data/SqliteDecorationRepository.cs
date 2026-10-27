using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteDecorationRepository : IDecorationRepository
{
    public Task<DecorationSnapshot> ReadAsync(WorkspaceContext workspace, Guid page, CancellationToken token) => Run(workspace, page, null, token);
    public Task<DecorationSnapshot> EditAsync(WorkspaceContext workspace, Guid page, Func<DecorationSnapshot, DecorationSnapshot> change, CancellationToken token) => Run(workspace, page, change, token);
    private static async Task<DecorationSnapshot> Run(WorkspaceContext w, Guid page, Func<DecorationSnapshot, DecorationSnapshot>? change, CancellationToken token)
    {
        await using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        using var tx = c.BeginTransaction(deferred: change is null);
        var before = await Read(c, tx, page, token); var after = change?.Invoke(before) ?? before;
        if (change is not null) await Write(c, tx, page, before, after, token);
        if (change is not null) await SqliteAuditWriter.Append(c, tx, w, AuditProjection.Decorations(before), AuditProjection.Decorations(after), token);
        await tx.CommitAsync(token); return after;
    }
    internal static async Task Write(SqliteConnection c, SqliteTransaction tx, Guid page, DecorationSnapshot before, DecorationSnapshot after, CancellationToken token)
    {
        foreach (var removed in before.Items.Where(i => !after.Items.Any(a => a.Id == i.Id)))
            await Execute(c, tx, "DELETE FROM CanvasDecorations WHERE Id=$id AND PageId=$page", token, ("id", removed.Id), ("page", page));
        foreach (var item in after.Items)
        {
            var old = before.Items.SingleOrDefault(i => i.Id == item.Id);
            if (item == old) continue;
            DecorationRules.Validate(item, before.Canvas);
            if (item.PageId != page || old is not null && (old.Kind != item.Kind || old.CreatedAtUtc != item.CreatedAtUtc)) throw new DecorationValidationException("Decoration ownership cannot change.");
            var written = await Execute(c, tx, """
                INSERT INTO CanvasDecorations VALUES($id,$page,$kind,$z,$color,$width,$fill,$dash,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET ZOrder=excluded.ZOrder,Color=excluded.Color,StrokeWidth=excluded.StrokeWidth,Filled=excluded.Filled,Dashed=excluded.Dashed,UpdatedAtUtc=excluded.UpdatedAtUtc WHERE PageId=excluded.PageId;
                """, token, ("id",item.Id),("page",page),("kind",(int)item.Kind),("z",item.ZOrder),("color",(int)item.Style.Color),("width",item.Style.Width),("fill",item.Style.Filled),("dash",item.Style.Dashed),("created",item.CreatedAtUtc),("updated",item.UpdatedAtUtc));
            if (written != 1) throw new DecorationValidationException("Decoration ownership cannot change.");
            if (item.Bounds is { } rect && rect != old?.Bounds)
                await Execute(c, tx, "INSERT INTO CanvasShapeSettings VALUES($id,$x,$y,$w,$h) ON CONFLICT(DecorationId) DO UPDATE SET X=excluded.X,Y=excluded.Y,Width=excluded.Width,Height=excluded.Height", token,
                    ("id",item.Id),("x",rect.X),("y",rect.Y),("w",rect.Width),("h",rect.Height));
            if (item.Start is not null) for (var slot = 0; slot < 2; slot++)
            {
                var endpoint = slot == 0 ? item.Start : item.End!;
                if (endpoint == (slot == 0 ? old?.Start : old?.End)) continue;
                await Execute(c, tx, "INSERT INTO CanvasDecorationEndpoints VALUES($id,$slot,$x,$y,$target,$anchor) ON CONFLICT(DecorationId,Endpoint) DO UPDATE SET X=excluded.X,Y=excluded.Y,TargetCanvasItemId=excluded.TargetCanvasItemId,Anchor=excluded.Anchor", token,
                    ("id",item.Id),("slot",slot),("x",endpoint.Point.X),("y",endpoint.Point.Y),("target",endpoint.Target),("anchor",(int)endpoint.Anchor));
            }
            if (item.Points is { } points && !ReferenceEquals(points, old?.Points))
            {
                await Execute(c, tx, "DELETE FROM CanvasStrokePoints WHERE DecorationId=$id", token, ("id",item.Id));
                using var cmd = Command(c, tx, "INSERT INTO CanvasStrokePoints VALUES($id,$index,$x,$y)", ("id",item.Id),("index",0),("x",0d),("y",0d));
                cmd.Prepare();
                for (var i = 0; i < points.Count; i++)
                {
                    cmd.Parameters["$index"].Value = i; cmd.Parameters["$x"].Value = points[i].X; cmd.Parameters["$y"].Value = points[i].Y;
                    await cmd.ExecuteNonQueryAsync(token);
                }
            }
        }
    }
    internal static async Task<DecorationSnapshot> Read(SqliteConnection c, SqliteTransaction tx, Guid page, CancellationToken token)
    {
        var canvas = await SqliteCanvasRepository.Read(c, tx, page, token); var items = new Dictionary<Guid, CanvasDecoration>();
        using (var cmd = Command(c, tx, "SELECT d.*,s.X,s.Y,s.Width,s.Height FROM CanvasDecorations d LEFT JOIN CanvasShapeSettings s ON s.DecorationId=d.Id WHERE d.PageId=$page ORDER BY d.ZOrder,d.Id", ("page",page)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Guid.Parse(r.GetString(0));
            items.Add(id, new(id,page,(DecorationKind)r.GetInt32(2),new((DecorationColor)r.GetInt32(4),r.GetInt32(5),r.GetBoolean(6),r.GetBoolean(7)),r.GetInt32(3),
                DateTimeOffset.Parse(r.GetString(8),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture),r.IsDBNull(10)?null:new(r.GetInt32(10),r.GetInt32(11),r.GetInt32(12),r.GetInt32(13))));
        }
        using (var cmd = Command(c, tx, "SELECT e.* FROM CanvasDecorationEndpoints e JOIN CanvasDecorations d ON d.Id=e.DecorationId WHERE d.PageId=$page ORDER BY e.DecorationId,e.Endpoint", ("page",page)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Guid.Parse(r.GetString(0)); var endpoint = new DecorationEndpoint(new(r.GetDouble(2),r.GetDouble(3)),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4)),(DecorationAnchor)r.GetInt32(5));
            items[id] = r.GetInt32(1)==0 ? items[id] with { Start=endpoint } : items[id] with { End=endpoint };
        }
        var strokes = new Dictionary<Guid, List<DecorationPoint>>();
        using (var cmd = Command(c, tx, "SELECT p.DecorationId,p.X,p.Y FROM CanvasStrokePoints p JOIN CanvasDecorations d ON d.Id=p.DecorationId WHERE d.PageId=$page ORDER BY p.DecorationId,p.PointIndex", ("page",page)))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var id = Guid.Parse(r.GetString(0)); if (!strokes.TryGetValue(id,out var points)) strokes[id] = points = [];
            points.Add(new(r.GetDouble(1),r.GetDouble(2)));
        }
        foreach (var (id, points) in strokes) items[id] = items[id] with { Points=points.ToArray() };
        foreach (var item in items.Values) DecorationRules.Validate(item,canvas);
        return new(canvas,items.Values.OrderBy(i=>i.ZOrder).ThenBy(i=>i.Id).ToArray());
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name,object? Value)[] values)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (name,value) in values) cmd.Parameters.AddWithValue("$"+name,value switch { null=>DBNull.Value,Guid id=>id.ToString("D"),DateTimeOffset utc=>utc.ToUniversalTime().ToString("O"),_=>value });
        return cmd;
    }
    private static async Task<int> Execute(SqliteConnection c,SqliteTransaction tx,string sql,CancellationToken token,params (string Name,object? Value)[] values)
    { using var cmd=Command(c,tx,sql,values);return await cmd.ExecuteNonQueryAsync(token); }
}
