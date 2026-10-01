namespace PersonalWorkspace.Core;

public enum CanvasKind { Block, Container }
public sealed record CanvasRect(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public bool Overlaps(CanvasRect other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}
public sealed record CanvasItem(Guid Id, Guid PageId, Guid? ParentContainerId, CanvasKind Kind, CanvasRect Rect, string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CanvasSettings(bool LayoutLocked = true, int ZoomPercent = 100);
public sealed record CanvasSnapshot(Guid PageId, bool PageActive, CanvasSettings Settings, IReadOnlyList<CanvasItem> Items);
public sealed class CanvasState(CanvasSnapshot snapshot)
{
    public Guid PageId { get; } = snapshot.PageId;
    public bool PageActive { get; } = snapshot.PageActive;
    public CanvasSettings Settings { get; set; } = snapshot.Settings;
    public Dictionary<Guid, CanvasItem> Items { get; } = snapshot.Items.ToDictionary(i => i.Id);
    public CanvasItem Find(Guid id) => Items.GetValueOrDefault(id) ?? throw new CanvasValidationException("This layout item is no longer available on this Page.");
    public CanvasSnapshot Snapshot() => new(PageId, PageActive, Settings, Items.Values.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id).ToArray());
}
public abstract record CanvasEdit;
public sealed record CreateCanvasItem(CanvasKind Kind, Guid? Parent = null, int X = 24, int Y = 24) : CanvasEdit;
public sealed record MoveCanvasItems(IReadOnlyList<Guid> Ids, int DeltaX, int DeltaY) : CanvasEdit;
public sealed record ResizeCanvasItem(Guid Id, int Width, int Height) : CanvasEdit;
public sealed record ReparentCanvasItem(Guid Id, Guid? Parent) : CanvasEdit;
public sealed record DeleteCanvasItems(IReadOnlyList<Guid> Ids) : CanvasEdit;
public sealed record RenameCanvasContainer(Guid Id, string Title) : CanvasEdit;
public sealed record LockCanvas(bool Locked) : CanvasEdit;
public sealed record ZoomCanvas(int Percent) : CanvasEdit;
public interface ICanvasRepository
{
    Task<CanvasSnapshot> GetAsync(WorkspaceContext workspace, Guid pageId, CancellationToken token);
    Task<CanvasSnapshot> EditAsync(WorkspaceContext workspace, Guid pageId, Func<CanvasSnapshot, CanvasSnapshot> edit, CancellationToken token);
}
public interface ICanvasService
{
    Task<CanvasSnapshot> GetAsync(WorkspaceItemReference page, CancellationToken token = default);
    Task<CanvasSnapshot> EditAsync(WorkspaceItemReference page, CanvasEdit edit, CancellationToken token = default);
}
public sealed class CanvasValidationException(string message) : Exception(message);
public sealed class CanvasOperationException(string message, Exception inner) : Exception(message, inner);

public static class CanvasLayout
{
    public const int Extent = 65536, Grid = 8, MinWidth = 64, MinHeight = 48, Inset = 8, Header = 40;
    public static int Snap(double value)
    {
        if (!double.IsFinite(value) || value < -Extent * 2 || value > Extent * 2) throw new CanvasValidationException("The proposed position is outside the supported canvas.");
        return checked((int)(Math.Round(value / Grid, MidpointRounding.AwayFromZero) * Grid));
    }
    public static CanvasRect Absolute(CanvasState state, CanvasItem item)
    {
        if (item.ParentContainerId is not { } id) return item.Rect;
        var parent = state.Find(id); return item.Rect with { X = item.Rect.X + parent.Rect.X + Inset, Y = item.Rect.Y + parent.Rect.Y + Header };
    }
    public static (int Width, int Height) Bounds(CanvasState state, Guid? parent)
    {
        if (parent is null) return (Extent, Extent);
        var container = state.Find(parent.Value);
        if (container.Kind != CanvasKind.Container || container.ParentContainerId is not null) throw new CanvasValidationException("Choose a top-level Container.");
        return (container.Rect.Width - Inset * 2, container.Rect.Height - Header - Inset);
    }
    public static void Validate(CanvasState state, IEnumerable<Guid>? changed = null)
    {
        var candidates = changed is null ? state.Items.Values.ToArray() : changed.Distinct().Select(state.Find).ToArray();
        foreach (var item in candidates)
        {
            var r = item.Rect;
            if (item.PageId != state.PageId || !Enum.IsDefined(item.Kind)) throw new CanvasValidationException("Invalid layout item identity.");
            if (item.Kind == CanvasKind.Container && item.ParentContainerId is not null) throw new CanvasValidationException("Containers cannot be placed inside Containers.");
            if (r.Width < MinWidth || r.Height < MinHeight || r.X < 0 || r.Y < 0 || r.X % Grid != 0 || r.Y % Grid != 0 || r.Width % Grid != 0 || r.Height % Grid != 0) throw new CanvasValidationException("Use the 8-unit grid, positive bounds and minimum size of 64 × 48.");
            var bounds = Bounds(state, item.ParentContainerId);
            if (r.Right > bounds.Width || r.Bottom > bounds.Height) throw new CanvasValidationException("Keep the item inside its canvas or Container content area.");
            if (state.Items.Values.Any(other => other.Id != item.Id && other.ParentContainerId == item.ParentContainerId && r.Overlaps(other.Rect))) throw new CanvasValidationException("Items cannot overlap. Choose a free position.");
            if (item.Kind == CanvasKind.Container && state.Items.Values.Any(child => child.ParentContainerId == item.Id && (child.Rect.Right > r.Width - 2 * Inset || child.Rect.Bottom > r.Height - Header - Inset))) throw new CanvasValidationException("The Container must remain large enough for its children.");
        }
    }
    // Candidate edge positions find space without scanning billions of grid cells.
    // Sorted squared distance, then Y/X, gives deterministic placement near the request.
    public static CanvasRect FreePosition(CanvasState state, Guid? parent, CanvasRect desired, Guid? exclude = null)
    {
        var bounds = Bounds(state, parent); var siblings = state.Items.Values.Where(i => i.ParentContainerId == parent && i.Id != exclude).Select(i => i.Rect).ToArray();
        var maxX = bounds.Width - desired.Width; var maxY = bounds.Height - desired.Height;
        if (maxX < 0 || maxY < 0) throw new CanvasValidationException("There is not enough room for this item.");
        var x0 = Math.Clamp(Snap(desired.X), 0, maxX); var y0 = Math.Clamp(Snap(desired.Y), 0, maxY);
        var xs = new HashSet<int> { 0, x0 }; var ys = new HashSet<int> { 0, y0 };
        foreach (var r in siblings) { if (r.Right <= maxX) xs.Add((int)r.Right); if (r.X - desired.Width >= 0) xs.Add(r.X - desired.Width); if (r.Bottom <= maxY) ys.Add((int)r.Bottom); if (r.Y - desired.Height >= 0) ys.Add(r.Y - desired.Height); }
        foreach (var point in xs.SelectMany(x => ys.Select(y => (X: x, Y: y, Distance: (long)(x - x0) * (x - x0) + (long)(y - y0) * (y - y0)))).OrderBy(p => p.Distance).ThenBy(p => p.Y).ThenBy(p => p.X))
        {
            var rect = desired with { X = point.X, Y = point.Y }; if (!siblings.Any(rect.Overlaps)) return rect;
        }
        throw new CanvasValidationException("No free position is available. Move or resize existing items first.");
    }
    public static CanvasSnapshot Apply(CanvasSnapshot snapshot, CanvasEdit edit, DateTimeOffset now)
    {
        var state = new CanvasState(snapshot);
        if (!state.PageActive) throw new CanvasValidationException("Restore this Page before editing its layout.");
        if (edit is LockCanvas locked) { state.Settings = state.Settings with { LayoutLocked = locked.Locked }; return state.Snapshot(); }
        if (edit is ZoomCanvas zoom)
        {
            if (zoom.Percent is < 25 or > 200) throw new CanvasValidationException("Choose a zoom from 25% to 200%.");
            state.Settings = state.Settings with { ZoomPercent = zoom.Percent }; return state.Snapshot();
        }
        if (state.Settings.LayoutLocked) throw new CanvasValidationException("Unlock Layout before editing it.");
        switch (edit)
        {
            case CreateCanvasItem create:
                if (!Enum.IsDefined(create.Kind)) throw new CanvasValidationException("Choose a Block or Container.");
                if (create.Kind == CanvasKind.Container && create.Parent is not null) throw new CanvasValidationException("Containers cannot be nested.");
                var size = create.Kind == CanvasKind.Container ? (512, 384) : (192, 128);
                var rectangle = FreePosition(state, create.Parent, new(Snap(create.X), Snap(create.Y), size.Item1, size.Item2));
                var item = new CanvasItem(Guid.NewGuid(), state.PageId, create.Parent, create.Kind, rectangle, "", now, now); state.Items.Add(item.Id, item); break;
            case MoveCanvasItems move:
                var moving = Selection(state, move.Ids); var dx = Snap(move.DeltaX); var dy = Snap(move.DeltaY);
                foreach (var old in moving) state.Items[old.Id] = old with { Rect = old.Rect with { X = checked(old.Rect.X + dx), Y = checked(old.Rect.Y + dy) } };
                break;
            case ResizeCanvasItem resize:
                var resizing = state.Find(resize.Id); state.Items[resize.Id] = resizing with { Rect = resizing.Rect with { Width = Snap(resize.Width), Height = Snap(resize.Height) } }; break;
            case ReparentCanvasItem membership:
                var member = state.Find(membership.Id);
                if (member.Kind != CanvasKind.Block) throw new CanvasValidationException("Only Blocks can change Container membership.");
                if (member.ParentContainerId == membership.Parent) return snapshot;
                var absolute = Absolute(state, member); var target = membership.Parent is { } parent ? state.Find(parent) : null;
                Bounds(state, membership.Parent);
                var desired = absolute with { X = absolute.X - (target is null ? 0 : target.Rect.X + Inset), Y = absolute.Y - (target is null ? 0 : target.Rect.Y + Header) };
                state.Items[member.Id] = member with { ParentContainerId = membership.Parent, Rect = FreePosition(state, membership.Parent, desired, member.Id) }; break;
            case DeleteCanvasItems delete:
                var deleting = Selection(state, delete.Ids); var removed = deleting.Select(i => i.Id).ToHashSet();
                var survivors = state.Items.Values.Where(i => i.ParentContainerId is { } container && removed.Contains(container)).ToArray();
                foreach (var child in survivors) state.Items[child.Id] = child with { ParentContainerId = null, Rect = Absolute(state, child) };
                foreach (var old in deleting) state.Items.Remove(old.Id);
                break;
            case RenameCanvasContainer rename:
                var containerItem = state.Find(rename.Id);
                if (containerItem.Kind != CanvasKind.Container || rename.Title.Trim().Length > 120 || rename.Title.Any(char.IsControl)) throw new CanvasValidationException("Use a plain Container title of up to 120 characters.");
                state.Items[rename.Id] = containerItem with { Title = rename.Title.Trim() }; break;
            default: throw new CanvasValidationException("Choose a supported layout operation.");
        }
        var before = snapshot.Items.ToDictionary(i => i.Id);
        var changed = state.Items.Values.Where(i => !before.TryGetValue(i.Id, out var old) || i != old).Select(i => i.Id).ToArray();
        Validate(state, changed);
        foreach (var id in changed) if (before.TryGetValue(id, out var old)) state.Items[id] = state.Items[id] with { UpdatedAtUtc = now > old.UpdatedAtUtc ? now : old.UpdatedAtUtc.AddTicks(1) };
        return state.Snapshot();
    }
    private static CanvasItem[] Selection(CanvasState state, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new CanvasValidationException("Select one or more distinct layout items.");
        var items = ids.Select(state.Find).ToArray();
        if (items.Select(i => i.ParentContainerId).Distinct().Count() != 1) throw new CanvasValidationException("Select items in the same Container or Page canvas.");
        return items;
    }
}
