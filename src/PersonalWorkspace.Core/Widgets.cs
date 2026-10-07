namespace PersonalWorkspace.Core;

public enum WidgetType { Task, Tracker, TrackerChart, Event, Journal, PageLink }
public enum WidgetMode { Card, Checkbox, Progress, CurrentValue, QuickEntry, Chart, TodaySummary }
public enum WidgetChartKind { Line, Bar, Area }
public sealed record WidgetChartSettings(WidgetChartKind Kind = WidgetChartKind.Line, AnalyticsPreset Range = AnalyticsPreset.ThirtyDays);
public sealed record WidgetInstance(Guid Id, Guid CanvasItemId, WidgetType Type, Guid? SourceItemId, WidgetMode Mode,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, WidgetChartSettings? Chart = null);
public sealed record WidgetPage(CanvasSnapshot Canvas, IReadOnlyList<WidgetInstance> Widgets, IReadOnlyList<WorkspaceItem> Sources);
public sealed class WidgetState(WidgetPage page)
{
    public CanvasSnapshot Canvas { get; set; } = page.Canvas;
    public Dictionary<Guid, WidgetInstance> Widgets { get; } = page.Widgets.ToDictionary(w => w.Id);
    public IReadOnlyList<WorkspaceItem> Sources { get; } = page.Sources;
    public WidgetPage Snapshot() => new(Canvas, Widgets.Values.OrderBy(w => w.CreatedAtUtc).ThenBy(w => w.Id).ToArray(), Sources);
}
public abstract record WidgetEdit;
public sealed record AddWidget(WidgetType Type, Guid Source, WidgetMode Mode, Guid? EmptyBlock = null, Guid? Parent = null,
    int X = 24, int Y = 24, WidgetChartSettings? Chart = null) : WidgetEdit;
public sealed record ConfigureWidget(Guid Id, WidgetMode Mode, WidgetChartSettings? Chart = null, Guid? Source = null) : WidgetEdit;
public sealed record ReplaceWidgetSource(Guid Id, Guid Source) : WidgetEdit;
public sealed record RemoveWidget(Guid Id, bool RemoveBlock = false) : WidgetEdit;
public sealed record DuplicateWidget(Guid Id) : WidgetEdit;
public interface IWidgetRepository
{
    Task<WidgetPage> ReadAsync(WorkspaceContext workspace, Guid pageId, CancellationToken token);
    Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceContext workspace, WorkspaceItemType type, Guid pageId, CancellationToken token);
    Task<WidgetPage> EditAsync(WorkspaceContext workspace, Guid pageId, Guid? sourceId, Func<WidgetState, WidgetPage> change, CancellationToken token);
}
public interface IWidgetService
{
    Task<WidgetPage> GetAsync(WorkspaceItemReference page, CancellationToken token = default);
    Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceItemReference page, WidgetType type, CancellationToken token = default);
    Task<WidgetPage> EditAsync(WorkspaceItemReference page, WidgetEdit edit, CancellationToken token = default);
}
public sealed class WidgetValidationException(string message) : Exception(message);
public sealed class WidgetOperationException(string message, Exception inner) : Exception(message, inner);

public static class WidgetRules
{
    public static WorkspaceItemType SourceType(WidgetType type) => type switch
    {
        WidgetType.Task => WorkspaceItemType.Task, WidgetType.Tracker or WidgetType.TrackerChart => WorkspaceItemType.Tracker,
        WidgetType.Event => WorkspaceItemType.Event, WidgetType.Journal => WorkspaceItemType.Journal, WidgetType.PageLink => WorkspaceItemType.Page,
        _ => throw new WidgetValidationException("Choose a supported Widget type.")
    };
    public static WidgetMode[] Modes(WidgetType type) => type switch
    {
        WidgetType.Task => [WidgetMode.Card, WidgetMode.Checkbox, WidgetMode.Progress],
        WidgetType.Tracker => [WidgetMode.CurrentValue, WidgetMode.Progress, WidgetMode.QuickEntry],
        WidgetType.TrackerChart => [WidgetMode.Chart], WidgetType.Journal => [WidgetMode.TodaySummary],
        WidgetType.Event or WidgetType.PageLink => [WidgetMode.Card], _ => []
    };
    public static (int Width, int Height) Size(WidgetType type) => type switch
    {
        WidgetType.TrackerChart => (480, 320), WidgetType.PageLink => (256, 160), _ => (320, 240)
    };
    public static void Validate(WidgetType type, WidgetMode mode, WidgetChartSettings? chart)
    {
        if (!Modes(type).Contains(mode)) throw new WidgetValidationException("Choose a presentation supported by this Widget type.");
        if (type == WidgetType.TrackerChart)
        {
            if (chart is null || !Enum.IsDefined(chart.Kind) || chart.Range is not (AnalyticsPreset.SevenDays or AnalyticsPreset.ThirtyDays or AnalyticsPreset.NinetyDays or AnalyticsPreset.ThisMonth))
                throw new WidgetValidationException("Choose Line, Bar or Area and a 7/30/90-day or This month range.");
        }
        else if (chart is not null) throw new WidgetValidationException("Chart settings belong only to Tracker Chart Widgets.");
    }
    public static WidgetPage Apply(WidgetState state, WidgetEdit edit, DateTimeOffset now)
    {
        if (!state.Canvas.PageActive || state.Canvas.Settings.LayoutLocked) throw new WidgetValidationException("Unlock an active Page before configuring its Widgets.");
        WidgetInstance Find(Guid id) => state.Widgets.GetValueOrDefault(id) ?? throw new WidgetValidationException("This Widget is no longer on this Page.");
        void Source(WidgetType type, Guid id)
        {
            var item = state.Sources.SingleOrDefault(s => s.Id == id);
            if (item is null || item.ItemType != SourceType(type) || item.ArchivedAtUtc is not null || item.DeletedAtUtc is not null)
                throw new WidgetValidationException("Choose an active source of the correct type in this Profile.");
            if (type == WidgetType.PageLink && id == state.Canvas.PageId) throw new WidgetValidationException("Choose a different Page.");
        }
        void Update(WidgetInstance before, WidgetInstance after)
        {
            if (after != before) state.Widgets[before.Id] = after with { UpdatedAtUtc = now > before.UpdatedAtUtc ? now : before.UpdatedAtUtc.AddTicks(1) };
        }
        Guid CreateHost(WidgetType type, Guid? parent, int x, int y, CanvasRect? original = null)
        {
            var size = Size(type); var ids = state.Canvas.Items.Select(i => i.Id).ToHashSet();
            state.Canvas = CanvasLayout.Apply(state.Canvas, new CreateCanvasItem(CanvasKind.Block, parent, x, y, original?.Width ?? size.Width, original?.Height ?? size.Height), now);
            return state.Canvas.Items.Single(i => !ids.Contains(i.Id)).Id;
        }
        switch (edit)
        {
            case AddWidget add:
                var chart = add.Type == WidgetType.TrackerChart ? add.Chart ?? new() : add.Chart;
                Validate(add.Type, add.Mode, chart); Source(add.Type, add.Source);
                var host = add.EmptyBlock ?? CreateHost(add.Type, add.Parent, add.X, add.Y);
                if (!state.Canvas.Items.Any(i => i.Id == host && i.Kind == CanvasKind.Block) || state.Widgets.Values.Any(w => w.CanvasItemId == host))
                    throw new WidgetValidationException("Choose an Empty Block on this Page.");
                var widget = new WidgetInstance(Guid.NewGuid(), host, add.Type, add.Source, add.Mode, now, now, chart); state.Widgets.Add(widget.Id, widget); break;
            case ConfigureWidget config:
                var current = Find(config.Id); Validate(current.Type, config.Mode, config.Chart);
                if (config.Source is { } replacement && replacement != current.SourceItemId) Source(current.Type, replacement);
                Update(current, current with { Mode = config.Mode, Chart = config.Chart, SourceItemId = config.Source ?? current.SourceItemId }); break;
            case ReplaceWidgetSource replace:
                var missing = Find(replace.Id); Source(missing.Type, replace.Source); Update(missing, missing with { SourceItemId = replace.Source }); break;
            case RemoveWidget remove:
                var removed = Find(remove.Id); state.Widgets.Remove(removed.Id);
                if (remove.RemoveBlock) state.Canvas = CanvasLayout.Apply(state.Canvas, new DeleteCanvasItems([removed.CanvasItemId]), now);
                break;
            case DuplicateWidget duplicate:
                var old = Find(duplicate.Id); var item = state.Canvas.Items.Single(i => i.Id == old.CanvasItemId);
                var clone = old with { Id = Guid.NewGuid(), CanvasItemId = CreateHost(old.Type, item.ParentContainerId, item.Rect.X, item.Rect.Y, item.Rect), CreatedAtUtc = now, UpdatedAtUtc = now };
                state.Widgets.Add(clone.Id, clone); break;
            default: throw new WidgetValidationException("Choose a supported Widget action.");
        }
        return state.Snapshot();
    }
}
