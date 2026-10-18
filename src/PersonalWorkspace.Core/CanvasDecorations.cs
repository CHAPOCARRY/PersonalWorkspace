namespace PersonalWorkspace.Core;

public enum DecorationKind { Rectangle, RoundedRectangle, Ellipse, Line, Arrow, Connector, Stroke }
public enum DecorationColor { Neutral, Accent, Success, Warning, Danger }
public enum DecorationAnchor { Left, Right, Top, Bottom, Center }
public sealed record DecorationPoint(double X, double Y);
public sealed record DecorationEndpoint(DecorationPoint Point, Guid? Target = null, DecorationAnchor Anchor = DecorationAnchor.Center);
public sealed record DecorationStyle(DecorationColor Color = DecorationColor.Accent, int Width = 2, bool Filled = false, bool Dashed = false);
public sealed record CanvasDecoration(Guid Id, Guid PageId, DecorationKind Kind, DecorationStyle Style, int ZOrder,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, CanvasRect? Bounds = null,
    DecorationEndpoint? Start = null, DecorationEndpoint? End = null, IReadOnlyList<DecorationPoint>? Points = null);
public sealed record DecorationSnapshot(CanvasSnapshot Canvas, IReadOnlyList<CanvasDecoration> Items);
public abstract record DecorationEdit;
public sealed record CreateDecoration(DecorationKind Kind, DecorationStyle Style, CanvasRect? Bounds = null,
    DecorationEndpoint? Start = null, DecorationEndpoint? End = null, IReadOnlyList<DecorationPoint>? Points = null) : DecorationEdit;
public sealed record MoveDecoration(Guid Id, double DeltaX, double DeltaY) : DecorationEdit;
public sealed record ResizeDecoration(Guid Id, CanvasRect Bounds) : DecorationEdit;
public sealed record ChangeDecorationStyle(Guid Id, DecorationStyle Style) : DecorationEdit;
public sealed record ChangeDecorationEndpoint(Guid Id, bool Start, DecorationEndpoint Endpoint) : DecorationEdit;
public sealed record DeleteDecoration(Guid Id) : DecorationEdit;
public sealed record DuplicateDecoration(Guid Id) : DecorationEdit;
public sealed record ReorderDecoration(Guid Id, bool Front) : DecorationEdit;
public interface IDecorationRepository
{
    Task<DecorationSnapshot> ReadAsync(WorkspaceContext workspace, Guid page, CancellationToken token);
    Task<DecorationSnapshot> EditAsync(WorkspaceContext workspace, Guid page, Func<DecorationSnapshot, DecorationSnapshot> change, CancellationToken token);
}
public interface IDecorationService
{
    Task<DecorationSnapshot> GetAsync(WorkspaceItemReference page, CancellationToken token = default);
    Task<DecorationSnapshot> EditAsync(WorkspaceItemReference page, DecorationEdit edit, CancellationToken token = default);
}
public sealed class DecorationValidationException(string message) : Exception(message);
public sealed class DecorationOperationException(string message, Exception inner) : Exception(message, inner);

public static class DecorationRules
{
    public const int Minimum = 16, MaxPoints = 8192, MaxDecorations = 2000, MaxPagePoints = 200000;
    public static bool IsShape(DecorationKind kind) => kind is DecorationKind.Rectangle or DecorationKind.RoundedRectangle or DecorationKind.Ellipse;
    public static bool IsLine(DecorationKind kind) => kind is DecorationKind.Line or DecorationKind.Arrow or DecorationKind.Connector;
    public static void ValidatePoint(DecorationPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > CanvasLayout.Extent || point.Y > CanvasLayout.Extent)
            throw new DecorationValidationException("Keep decorations inside the Page canvas.");
    }
    public static DecorationPoint Resolve(DecorationEndpoint endpoint, CanvasSnapshot canvas)
    {
        if (!Enum.IsDefined(endpoint.Anchor)) throw new DecorationValidationException("Choose a supported connector anchor.");
        if (endpoint.Target is not { } id) return endpoint.Point;
        var state = new CanvasState(canvas); var item = state.Items.GetValueOrDefault(id)
            ?? throw new DecorationValidationException("Choose a connector target on this Page.");
        var r = CanvasLayout.Absolute(state, item);
        return endpoint.Anchor switch
        {
            DecorationAnchor.Left => new(r.X, r.Y + r.Height / 2d), DecorationAnchor.Right => new(r.Right, r.Y + r.Height / 2d),
            DecorationAnchor.Top => new(r.X + r.Width / 2d, r.Y), DecorationAnchor.Bottom => new(r.X + r.Width / 2d, r.Bottom),
            _ => new(r.X + r.Width / 2d, r.Y + r.Height / 2d)
        };
    }
    public static void Validate(CanvasDecoration item, CanvasSnapshot canvas)
    {
        if (item.Id == Guid.Empty || item.PageId != canvas.PageId || !Enum.IsDefined(item.Kind) || item.ZOrder < 0)
            throw new DecorationValidationException("Invalid decoration identity.");
        if (!Enum.IsDefined(item.Style.Color) || item.Style.Width is < 1 or > 16 || !IsShape(item.Kind) && item.Style.Filled)
            throw new DecorationValidationException("Choose a palette color and stroke width from 1 to 16. Fill applies only to shapes.");
        if (IsShape(item.Kind))
        {
            if (item.Bounds is not { } r || item.Start is not null || item.End is not null || item.Points is not null || r.Width < Minimum || r.Height < Minimum || r.X % 8 != 0 || r.Y % 8 != 0 || r.Width % 8 != 0 || r.Height % 8 != 0)
                throw new DecorationValidationException("Shapes use the 8-unit grid and a minimum size of 16 by 16.");
            ValidatePoint(new(r.X, r.Y)); ValidatePoint(new(r.Right, r.Bottom));
        }
        else if (IsLine(item.Kind))
        {
            if (item.Bounds is not null || item.Points is not null || item.Start is null || item.End is null)
                throw new DecorationValidationException("Lines and connectors require two endpoints.");
            foreach (var end in new[] { item.Start, item.End })
            {
                if (item.Kind != DecorationKind.Connector && end.Target is not null) throw new DecorationValidationException("Only Connectors attach to layout items.");
                ValidatePoint(end.Point); ValidatePoint(Resolve(end, canvas));
            }
        }
        else
        {
            if (item.Bounds is not null || item.Start is not null || item.End is not null || item.Points is not { Count: >= 2 and <= MaxPoints })
                throw new DecorationValidationException($"A stroke needs 2 to {MaxPoints:N0} reduced points. Use several strokes for longer drawings.");
            foreach (var point in item.Points) ValidatePoint(point);
        }
    }
    // Radial coalescing has a maximum one-logical-unit deviation; always retain the final point.
    public static IReadOnlyList<DecorationPoint> Reduce(IReadOnlyList<DecorationPoint> source)
    {
        if (source.Count > 32768) throw new DecorationValidationException("Finish this stroke and start another to continue drawing.");
        var result = new List<DecorationPoint>();
        foreach (var point in source)
        {
            ValidatePoint(point);
            if (result.Count == 0 || Distance(result[^1], point) >= 1) result.Add(point);
        }
        if (source.Count > 1 && result[^1] != source[^1]) result.Add(source[^1]);
        if (result.Count > MaxPoints) throw new DecorationValidationException("This stroke is too long. Draw it as several shorter strokes.");
        return result.ToArray();
    }
    public static double Distance(DecorationPoint a, DecorationPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    public static double SegmentDistance(DecorationPoint p, DecorationPoint a, DecorationPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y; var square = dx * dx + dy * dy;
        var t = square == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / square, 0, 1);
        return Distance(p, new(a.X + t * dx, a.Y + t * dy));
    }
    public static bool Hit(CanvasDecoration item, CanvasSnapshot canvas, DecorationPoint p, double tolerance)
    {
        if (item.Bounds is { } r)
        {
            if (item.Kind == DecorationKind.Ellipse)
            {
                var rx = r.Width / 2d; var ry = r.Height / 2d; var x = (p.X - r.X - rx) / rx; var y = (p.Y - r.Y - ry) / ry;
                var radius = Math.Sqrt(x * x + y * y); return item.Style.Filled ? radius <= 1 + tolerance / Math.Min(rx, ry) : Math.Abs(radius - 1) <= tolerance / Math.Min(rx, ry);
            }
            if (p.X < r.X - tolerance || p.X > r.Right + tolerance || p.Y < r.Y - tolerance || p.Y > r.Bottom + tolerance) return false;
            return item.Style.Filled || p.X <= r.X + tolerance || p.X >= r.Right - tolerance || p.Y <= r.Y + tolerance || p.Y >= r.Bottom - tolerance;
        }
        var points = item.Points ?? new[] { Resolve(item.Start!, canvas), Resolve(item.End!, canvas) };
        return points.Zip(points.Skip(1), (a, b) => SegmentDistance(p, a, b) <= tolerance + item.Style.Width / 2d).Any(hit => hit);
    }
    public static DecorationSnapshot Apply(DecorationSnapshot before, DecorationEdit edit, DateTimeOffset now)
    {
        if (!before.Canvas.PageActive || before.Canvas.Settings.LayoutLocked) throw new DecorationValidationException("Unlock an active Page before editing decorations.");
        var items = before.Items.OrderBy(i => i.ZOrder).ThenBy(i => i.Id).ToList();
        CanvasDecoration Find(Guid id) => items.SingleOrDefault(i => i.Id == id) ?? throw new DecorationValidationException("This decoration is no longer on this Page.");
        void Update(CanvasDecoration old, CanvasDecoration next)
        {
            if (old == next) return;
            items[items.IndexOf(old)] = next with { UpdatedAtUtc = now > old.UpdatedAtUtc ? now : old.UpdatedAtUtc.AddTicks(1) };
        }
        DecorationEndpoint Endpoint(DecorationEndpoint e) => e with { Point = Resolve(e, before.Canvas) };
        switch (edit)
        {
            case CreateDecoration create:
                var item = new CanvasDecoration(Guid.NewGuid(), before.Canvas.PageId, create.Kind, create.Style, items.Count == 0 ? 0 : checked(items.Max(i => i.ZOrder) + 1), now, now,
                    create.Bounds, create.Start is null ? null : Endpoint(create.Start), create.End is null ? null : Endpoint(create.End), create.Points is null ? null : Reduce(create.Points));
                items.Add(item); break;
            case DeleteDecoration delete: items.Remove(Find(delete.Id)); break;
            case ChangeDecorationStyle style:
                var styling = Find(style.Id); Update(styling, styling with { Style = style.Style }); break;
            case ResizeDecoration resize:
                var resizing = Find(resize.Id); if (!IsShape(resizing.Kind)) throw new DecorationValidationException("Resize a basic shape using its handles.");
                Update(resizing, resizing with { Bounds = resize.Bounds }); break;
            case ChangeDecorationEndpoint change:
                var line = Find(change.Id); if (!IsLine(line.Kind)) throw new DecorationValidationException("Select a Line, Arrow or Connector.");
                Update(line, change.Start ? line with { Start = Endpoint(change.Endpoint) } : line with { End = Endpoint(change.Endpoint) }); break;
            case MoveDecoration move:
                var moving = Find(move.Id); var dx = CanvasLayout.Snap(move.DeltaX); var dy = CanvasLayout.Snap(move.DeltaY);
                if (dx == 0 && dy == 0) break;
                if (moving.Start?.Target is not null || moving.End?.Target is not null) throw new DecorationValidationException("Detach attached endpoints before moving the whole Connector.");
                DecorationPoint Shift(DecorationPoint p) => new(p.X + dx, p.Y + dy);
                Update(moving, moving with { Bounds = moving.Bounds is { } rect ? rect with { X = rect.X + dx, Y = rect.Y + dy } : null,
                    Start = moving.Start is { } a ? a with { Point = Shift(a.Point) } : null, End = moving.End is { } b ? b with { Point = Shift(b.Point) } : null,
                    Points = moving.Points?.Select(Shift).ToArray() }); break;
            case DuplicateDecoration duplicate:
                var source = Find(duplicate.Id); if (!IsShape(source.Kind)) throw new DecorationValidationException("Duplication is available for basic shapes.");
                var bounds = source.Bounds!; var offsetX = bounds.Right + 16 <= CanvasLayout.Extent ? 16 : -16; var offsetY = bounds.Bottom + 16 <= CanvasLayout.Extent ? 16 : -16;
                items.Add(source with { Id = Guid.NewGuid(), Bounds = bounds with { X = Math.Max(0, bounds.X + offsetX), Y = Math.Max(0, bounds.Y + offsetY) }, ZOrder = checked(items.Max(i => i.ZOrder) + 1), CreatedAtUtc = now, UpdatedAtUtc = now }); break;
            case ReorderDecoration order:
                var ordered = Find(order.Id); items.Remove(ordered); items.Insert(order.Front ? items.Count : 0, ordered);
                foreach (var (old, index) in items.ToArray().Select((old, index) => (old, index))) Update(old, old with { ZOrder = index }); break;
            default: throw new DecorationValidationException("Choose a supported decoration operation.");
        }
        if (items.Count > MaxDecorations || items.Sum(i => i.Points?.Count ?? 0) > MaxPagePoints) throw new DecorationValidationException("This Page has reached its decoration capacity. Use another Page for more drawing.");
        foreach (var item in items) Validate(item, before.Canvas);
        return before with { Items = items.OrderBy(i => i.ZOrder).ThenBy(i => i.Id).ToArray() };
    }
}
