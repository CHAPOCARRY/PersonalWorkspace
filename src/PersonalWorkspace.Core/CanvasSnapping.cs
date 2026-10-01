namespace PersonalWorkspace.Core;

public sealed record CanvasSnap(int DeltaX, int DeltaY, int? GuideX = null, int? GuideY = null);
public static class CanvasSnapping
{
    public static CanvasSnap Move(CanvasSnapshot snapshot, IReadOnlySet<Guid> selection, double deltaX, double deltaY)
    {
        var dx = CanvasLayout.Snap(deltaX); var dy = CanvasLayout.Snap(deltaY); var moving = snapshot.Items.Where(i => selection.Contains(i.Id)).ToArray();
        if (dx == 0 && dy == 0) return new(0, 0);
        if (moving.Length == 0) return new(dx, dy);
        var left = moving.Min(i => i.Rect.X); var right = (int)moving.Max(i => i.Rect.Right); var top = moving.Min(i => i.Rect.Y); var bottom = (int)moving.Max(i => i.Rect.Bottom);
        var siblings = snapshot.Items.Where(i => i.ParentContainerId == moving[0].ParentContainerId && !selection.Contains(i.Id)).OrderBy(i => i.Id).ToArray();
        static (int Delta, int? Guide) Axis(int delta, int start, int end, IEnumerable<(int Start, int End)> targets)
        {
            var best = 9; var result = delta; int? guide = null;
            foreach (var target in targets) foreach (var edge in new[] { target.Start, (target.Start + target.End) / 2, target.End }) foreach (var own in new[] { start, (start + end) / 2, end })
            {
                var candidate = edge - own; var distance = Math.Abs(candidate - delta);
                if (candidate % CanvasLayout.Grid == 0 && distance < best) { best = distance; result = candidate; guide = edge; }
            }
            return (result, guide);
        }
        var x = Axis(dx, left, right, siblings.Select(i => (i.Rect.X, (int)i.Rect.Right))); var y = Axis(dy, top, bottom, siblings.Select(i => (i.Rect.Y, (int)i.Rect.Bottom)));
        return new(x.Delta, y.Delta, x.Guide, y.Guide);
    }
}
