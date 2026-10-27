using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

// Only this explicit allow-list reaches history. Comparison may use in-memory content;
// persisted Content values contain lengths, never bodies, hashes or filesystem paths.
internal sealed record AuditField(AuditValue Value, object? Comparison);
internal sealed record AuditEntity(AuditTarget Target, Dictionary<string, AuditField> Fields);
internal static class AuditProjection
{
    internal static AuditField Value(object? value, AuditValueKind? kind = null)
    {
        var type = kind ?? value switch { bool => AuditValueKind.Boolean, Enum => AuditValueKind.Enum, Guid => AuditValueKind.Guid,
            decimal => AuditValueKind.Decimal, int or long => AuditValueKind.Integer, double => AuditValueKind.Geometry,
            DateOnly => AuditValueKind.Date, TimeOnly => AuditValueKind.LocalTime, DateTimeOffset => AuditValueKind.Utc, _ => AuditValueKind.Text };
        if (value is null) return new(new(type, AuditValueState.Null), null);
        var text = value switch { decimal d => ExactValueText.Store(d), DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), DateTimeOffset t => t.ToUniversalTime().ToString("O"),
            bool b => b ? "true" : "false", double d => d.ToString("R", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
        var limit = type == AuditValueKind.Text ? 256 : 512;
        return new(new(type, text.Length > limit ? AuditValueState.Truncated : AuditValueState.Value, text[..Math.Min(limit, text.Length)], text.Length), value);
    }
    internal static AuditField Content(string? text) => new(new(AuditValueKind.Content, text is null ? AuditValueState.Null : AuditValueState.Omitted, Length: text?.Length), text);
    internal static AuditEntity Entity(AuditEntityType type, Guid id, string title, Guid? parent = null, DateOnly? date = null, params (string Name, object? Value)[] fields)
        => new(new(type, id, title, parent, date), fields.ToDictionary(f => f.Name, f => f.Value is AuditField field ? field : Value(f.Value)));
    internal static AuditEntity Item(WorkspaceItem item, params (string Name, object? Value)[] fields)
    {
        var result = Entity((AuditEntityType)item.ItemType, item.Id, item.Title, fields: fields);
        result.Fields["Title"] = Value(item.Title); result.Fields["Archived"] = Value(item.ArchivedAtUtc is not null); result.Fields["Trashed"] = Value(item.DeletedAtUtc is not null); return result;
    }
    internal static List<AuditEntity> Tasks(IEnumerable<TaskItem> tasks, IEnumerable<TaskDependency>? edges = null)
    {
        var result = tasks.Select(t => Item(t.Item, ("Description", Content(t.Description)), ("Status", t.Status), ("Priority", t.Priority),
            ("ScheduledDate", t.ScheduledDate), ("ParentTaskId", t.ParentTaskId), ("ValueType", t.ValueType),
            ("Target", Value(t.Value?.Target, t.ValueType == TaskValueType.Duration ? AuditValueKind.Duration : AuditValueKind.Decimal)),
            ("Actual", Value(t.Value?.Actual, t.ValueType == TaskValueType.Duration ? AuditValueKind.Duration : AuditValueKind.Decimal)),
            ("Unit", t.Value?.Unit), ("Currency", t.Value?.CurrencyCode))).ToList();
        if (edges is not null) foreach (var edge in edges) result.Single(t => t.Target.Id == edge.TaskId).Fields["Dependency:" + edge.DependsOnTaskId] = Value(edge.DependsOnTaskId);
        return result;
    }
    internal static List<AuditEntity> Recurrence(RecurrenceState state, RecurrenceQuery query)
    {
        var result = Tasks(state.Graph.Tasks.Values, state.Graph.Dependencies);
        foreach (var s in state.Segments.Values) result.Single(t => t.Target.Id == s.TaskId).Fields["Recurrence:" + s.Id] = Value($"{s.Rule.Pattern}; {s.Rule.StartDate:yyyy-MM-dd}; interval={s.Rule.Interval}; weekdays={s.Rule.Weekdays}; day={s.Rule.MonthDay}; ordinal={s.Rule.Ordinal}; weekday={s.Rule.Weekday}; end={s.Rule.EndDate:yyyy-MM-dd}; from={s.From:yyyy-MM-dd}; until={s.Until:yyyy-MM-dd}; enabled={s.Enabled}");
        foreach (var (id, p) in state.CarryPolicies)
        { var item = result.Single(t => t.Target.Id == id); item.Fields["CarryDeficit"] = Value(p.Deficit); item.Fields["CarrySurplus"] = Value(p.Surplus); }
        // Only the explicitly addressed occurrence is authored input. Suffix calculations are not history.
        foreach (var o in state.Occurrences.Values.Where(o => o.Id == query.OccurrenceId))
        {
            var task = state.Graph.Tasks[o.TaskId];
            result.Add(Entity(AuditEntityType.Occurrence, o.Id, task.Item.Title, o.TaskId, o.OccurrenceDate,
                ("SlotDate", o.SlotDate), ("Date", o.OccurrenceDate), ("Status", o.Status), ("Skipped", o.IsSkipped),
                ("Actual", Value(o.Actual, task.ValueType == TaskValueType.Duration ? AuditValueKind.Duration : AuditValueKind.Decimal))));
        }
        return result;
    }
    internal static List<AuditEntity> Event(EventItem e) => [Item(e.Item, ("AllDay", e.AllDay), ("StartDate", e.StartDate), ("StartTime", e.StartTime), ("EndDate", e.EndDate), ("EndTime", e.EndTime))];
    internal static List<AuditEntity> Tracker(TrackerItem t, IEnumerable<TrackerEntry>? entries = null)
    {
        var result = new List<AuditEntity> { Item(t.Item, ("ValueType", t.Settings.Type), ("Unit", t.Settings.Unit), ("Currency", t.Settings.CurrencyCode),
            ("ScaleMin", t.Settings.ScaleMin), ("ScaleMax", t.Settings.ScaleMax), ("Frequency", t.Schedule.Frequency), ("StartDate", t.Schedule.StartDate),
            ("EndDate", t.Schedule.EndDate), ("Interval", t.Schedule.Interval), ("Weekdays", t.Schedule.Weekdays), ("EntryMode", t.EntryMode), ("Aggregation", t.Aggregation),
            ("Target", TrackerValue(t.Target, t.Settings.Type))) };
        foreach (var e in entries ?? []) result.Add(Entity(AuditEntityType.TrackerEntry, e.Id, t.Item.Title, e.TrackerId, e.LocalDate,
            ("PeriodDate", e.PeriodDate), ("Date", e.LocalDate), ("Time", e.LocalTime), ("Value", TrackerValue(e.Value, t.Settings.Type)), ("Note", Content(e.Note))));
        return result;
    }
    private static AuditField TrackerValue(TrackerValue? value, TrackerValueType type) => type == TrackerValueType.Boolean ? Value(value?.Boolean, AuditValueKind.Boolean)
        : Value(value?.Number, type == TrackerValueType.Duration ? AuditValueKind.Duration : AuditValueKind.Decimal);
    internal static List<AuditEntity> Journal(JournalDefinition d, JournalEntry? entry = null)
    {
        var result = new List<AuditEntity> { Item(d.Item, ("Description", Content(d.Description))) };
        foreach (var f in d.Fields)
        {
            result.Add(Entity(AuditEntityType.JournalField, f.Id, f.Name, d.Item.Id, fields: [("Name", f.Name), ("Type", f.Type), ("Order", f.SortOrder), ("Currency", f.CurrencyCode), ("ScaleMin", f.ScaleMin), ("ScaleMax", f.ScaleMax)]));
            foreach (var o in f.OrderedOptions) result.Add(Entity(AuditEntityType.JournalOption, o.Id, o.Label, d.Item.Id, fields: [("Field", f.Id), ("Label", o.Label), ("Order", o.SortOrder)]));
        }
        if (entry is not null)
        {
            var e = Entity(AuditEntityType.JournalEntry, entry.Id, d.Item.Title, d.Item.Id, entry.Date, ("Date", entry.Date));
            foreach (var (id, v) in entry.Values)
            {
                var field = d.Fields.Single(f => f.Id == id); var prefix = id + ":" + field.Name[..Math.Min(80, field.Name.Length)] + ":";
                e.Fields[prefix + "Label"] = Value(field.Name);
                e.Fields[prefix + "Text"] = Content(v.Text); e.Fields[prefix + "Number"] = Value(v.Number);
                e.Fields[prefix + "Integer"] = Value(v.Integer, field.Type == JournalFieldType.Duration ? AuditValueKind.Duration : AuditValueKind.Integer);
                e.Fields[prefix + "Date"] = Value(v.Date); e.Fields[prefix + "Reference"] = Value(v.ReferenceId);
                foreach (var option in v.OptionIds ?? []) e.Fields[prefix + "Option:" + option] = Value(field.OrderedOptions.Single(o => o.Id == option).Label);
            }
            result.Add(e);
        }
        return result;
    }
    internal static List<AuditEntity> Lists(IEnumerable<ListDefinition> lists, IEnumerable<ListItem> items)
    {
        var result = lists.Select(l => Item(l.Item, ("Description", Content(l.Description)), ("QuantityEnabled", l.ShowQuantity), ("PricesEnabled", l.ShowPrices), ("Currency", l.CurrencyCode))).ToList();
        result.AddRange(items.Select(i => Entity(AuditEntityType.ListItem, i.Id, i.Title, i.ListId, fields: [("Title", i.Title), ("Checked", i.IsChecked), ("Quantity", i.Quantity), ("Price", i.UnitPrice), ("Note", Content(i.Note)), ("Link", Content(i.Link)), ("Order", i.SortOrder)]))); return result;
    }
    internal static List<AuditEntity> Pages(IEnumerable<PageItem> pages) => pages.Select(p => Item(p.Item, ("Parent", p.ParentPageId), ("Order", p.SortOrder), ("Icon", p.Icon))).ToList();
    internal static List<AuditEntity> Canvas(CanvasSnapshot canvas)
    {
        var result = new List<AuditEntity> { Entity(AuditEntityType.Page, canvas.PageId, "Page", fields: [("LayoutLocked", canvas.Settings.LayoutLocked)]) };
        result.AddRange(canvas.Items.Select(i => Entity(AuditEntityType.CanvasItem, i.Id, string.IsNullOrEmpty(i.Title) ? i.Kind.ToString() : i.Title, canvas.PageId,
            fields: [("Kind", i.Kind), ("Parent", i.ParentContainerId), ("Title", i.Title), ("X", i.Rect.X), ("Y", i.Rect.Y), ("Width", i.Rect.Width), ("Height", i.Rect.Height)]))); return result;
    }
    internal static List<AuditEntity> Widgets(WidgetPage page)
    {
        var result = Canvas(page.Canvas);
        foreach (var w in page.Widgets) result.Add(Entity(AuditEntityType.Widget, w.Id, page.Sources.FirstOrDefault(s => s.Id == w.SourceItemId)?.Title ?? w.Type.ToString(), page.Canvas.PageId,
            fields: [("Type", w.Type), ("CanvasItem", w.CanvasItemId), ("Source", w.SourceItemId), ("Mode", w.Mode), ("ChartKind", w.Chart?.Kind), ("ChartRange", w.Chart?.Range),
                ("ListFilter", w.List?.Filter), ("RichTextReference", w.RichText?.ReferenceId), ("AssetReference", w.Asset?.ReferenceId), ("Fit", w.Asset?.Fit), ("AltText", Content(w.Asset?.AltText))])); return result;
    }
    internal static List<AuditEntity> Decorations(DecorationSnapshot snapshot) => snapshot.Items.Select(d => Entity(AuditEntityType.Decoration, d.Id, d.Kind.ToString(), d.PageId,
        fields: [("Kind", d.Kind), ("ZOrder", d.ZOrder), ("Color", d.Style.Color), ("StrokeWidth", d.Style.Width), ("Filled", d.Style.Filled), ("Dashed", d.Style.Dashed),
            ("X", d.Bounds?.X), ("Y", d.Bounds?.Y), ("Width", d.Bounds?.Width), ("Height", d.Bounds?.Height),
            ("StartTarget", d.Start?.Target), ("StartAnchor", d.Start?.Anchor), ("StartX", d.Start?.Point.X), ("StartY", d.Start?.Point.Y),
            ("EndTarget", d.End?.Target), ("EndAnchor", d.End?.Anchor), ("EndX", d.End?.Point.X), ("EndY", d.End?.Point.Y), ("PointCount", d.Points?.Count),
            ("Stroke", Content(d.Points is null ? null : string.Join(";", d.Points.Select(p => $"{p.X:R},{p.Y:R}"))))])).ToList();
    internal static List<AuditEntity> Template(TemplateSnapshot snapshot) => [Entity(AuditEntityType.Template, snapshot.Metadata.Id, snapshot.Metadata.Name,
        fields: [("Name", snapshot.Metadata.Name), ("Description", Content(snapshot.Metadata.Description)), ("Type", snapshot.Metadata.Type), ("DefaultTitle", snapshot.Blueprint.Title),
            ("Definition", Content(System.Text.Json.JsonSerializer.Serialize(snapshot.Blueprint, snapshot.Blueprint.GetType())))])];
}
