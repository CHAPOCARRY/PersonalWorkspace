namespace PersonalWorkspace.Core;

public enum AnalyticsPreset { SevenDays, ThirtyDays, NinetyDays, ThisMonth, LastMonth, ThisYear, AllTime, Custom }
public sealed record AnalyticsRange(DateOnly From, DateOnly Through)
{
    public static AnalyticsRange? Resolve(AnalyticsPreset preset, DateOnly today, DateOnly? from = null, DateOnly? through = null)
    {
        DateOnly Back(int days) => DateOnly.FromDayNumber(Math.Max(0, today.DayNumber - days));
        var month = new DateOnly(today.Year, today.Month, 1);
        var range = preset switch
        {
            AnalyticsPreset.SevenDays => new AnalyticsRange(Back(6), today),
            AnalyticsPreset.ThirtyDays => new(Back(29), today), AnalyticsPreset.NinetyDays => new(Back(89), today),
            AnalyticsPreset.ThisMonth => new(month, today), AnalyticsPreset.ThisYear => new(new(today.Year, 1, 1), today),
            AnalyticsPreset.LastMonth when month > DateOnly.MinValue => new(month.AddMonths(-1), month.AddDays(-1)),
            AnalyticsPreset.AllTime => null,
            AnalyticsPreset.Custom when from is { } start && through is { } end => new(start, end),
            _ => throw new TrackerValidationException("Choose a valid date range with both dates.")
        };
        if (range?.From > range?.Through) throw new TrackerValidationException("The range end cannot precede its start.");
        return range;
    }
}
public sealed record AnalyticsSnapshot(AnalyticsRange Range, IReadOnlyList<TrackerItem> Trackers, IReadOnlyList<TrackerEntry> Entries);
public sealed record AnalyticsPoint(DateOnly Date, TrackerValue? Value, bool Expected, bool? TargetReached, int EntryCount);
public sealed record AnalyticsStreak(int Current, int Best);
public sealed record AnalyticsHeatCell(DateOnly Date, DateOnly? PeriodDate, TrackerValue? Value, bool Expected, bool? TargetReached);
public sealed record AnalyticsSummary(int Count, TrackerValue? Latest, decimal? Average, decimal? Minimum, decimal? Maximum,
    decimal? Total, int TrueCount, int FalseCount, decimal? CompletionRate);
public sealed record AnalyticsTarget(int Reached, int Expected, decimal? HitRate, decimal? AverageAttainment, AnalyticsStreak Streak);
public sealed record TrackerAnalyticsSeries(TrackerItem Tracker, IReadOnlyList<AnalyticsPoint> Points, AnalyticsSummary Summary,
    int Expected, int Recorded, int Missing, decimal? RecordingRate, AnalyticsStreak Streak, AnalyticsTarget? Target);
public sealed record TrackerAnalyticsResult(AnalyticsRange Range, IReadOnlyList<TrackerAnalyticsSeries> Series);
public interface ITrackerAnalyticsRepository
{
    Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(WorkspaceContext workspace, CancellationToken cancellationToken);
    // Ranges select complete canonical periods by their local start date. Null explicitly means All time.
    Task<AnalyticsSnapshot> ReadAnalyticsAsync(WorkspaceContext workspace, IReadOnlyList<Guid> ids, AnalyticsRange? range, DateOnly today, CancellationToken cancellationToken);
}
public interface ITrackerAnalyticsService
{
    Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<TrackerAnalyticsResult> GetAsync(Guid profileId, IReadOnlyList<Guid> ids, AnalyticsPreset preset = AnalyticsPreset.ThirtyDays,
        DateOnly? from = null, DateOnly? through = null, CancellationToken cancellationToken = default);
}

public static class TrackerAnalytics
{
    public static IReadOnlyList<AnalyticsHeatCell> Heatmap(TrackerAnalyticsSeries series, AnalyticsRange range)
    {
        // The detail heatmap shows at most the latest year of the selected range.
        var from = Math.Max(range.From.DayNumber, range.Through.DayNumber - 365);
        var points = series.Points.ToDictionary(p => p.Date);
        return Enumerable.Range(from, range.Through.DayNumber - from + 1).Select(day =>
        {
            var date = DateOnly.FromDayNumber(day); var period = series.Tracker.Schedule.PeriodOn(date);
            var point = period is { } p ? points.GetValueOrDefault(p) : null;
            return new AnalyticsHeatCell(date, period, point?.Value, point?.Expected ?? false, point?.TargetReached);
        }).ToArray();
    }
    public static bool Compatible(TrackerSettings a, TrackerSettings b) => a.Type == b.Type && a.Unit == b.Unit &&
        a.CurrencyCode == b.CurrencyCode && a.ScaleMin == b.ScaleMin && a.ScaleMax == b.ScaleMax;

    public static TrackerAnalyticsSeries Calculate(TrackerItem item, IEnumerable<TrackerEntry> entries, AnalyticsRange range, DateOnly today, TimeZoneInfo zone)
    {
        var values = entries.Where(e => e.TrackerId == item.Item.Id && e.PeriodDate >= range.From && e.PeriodDate <= range.Through)
            .GroupBy(e => e.PeriodDate).ToDictionary(g => g.Key, g => (Value: TrackerRules.Aggregate(item, g), Count: g.Count()));
        var expected = new HashSet<DateOnly>();
        var last = Math.Min(range.Through.DayNumber, today.DayNumber);
        if (item.Item.ArchivedAtUtc is { } archived) last = Math.Min(last, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(archived, zone).DateTime).DayNumber - 1);
        if (item.Item.DeletedAtUtc is { } deleted) last = Math.Min(last, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deleted, zone).DateTime).DayNumber - 1);
        if (item.Schedule.Frequency != TrackerFrequency.Unscheduled)
            for (var day = range.From.DayNumber; day <= last; day++)
            {
                var date = DateOnly.FromDayNumber(day);
                if (item.Schedule.PeriodOn(date) == date) expected.Add(date);
            }
        var points = expected.Union(values.Keys).Order().Select(date =>
        {
            var value = values.GetValueOrDefault(date);
            return new AnalyticsPoint(date, value.Value, expected.Contains(date), expected.Contains(date) ? Reached(item.Target, value.Value) : null, value.Count);
        }).ToArray();
        var recorded = points.Where(p => p.Value is not null).ToArray();
        var numbers = recorded.Where(p => p.Value!.Number is not null).Select(p => p.Value!.Number!.Value).ToArray();
        decimal? average = numbers.Length == 0 ? null : TrackerRules.SumOrAverage(numbers, true);
        if (item.Settings.Type == TrackerValueType.Duration && average is { } duration) average = decimal.Round(duration, 0, MidpointRounding.AwayFromZero);
        var totalMeaningful = item.Settings.Type is not (TrackerValueType.Boolean or TrackerValueType.Scale or TrackerValueType.Percentage) &&
            (item.EntryMode == TrackerEntryMode.Multiple ? item.Aggregation == TrackerAggregation.Sum : item.Settings.Type != TrackerValueType.Decimal);
        var positive = recorded.Count(p => p.Value!.Boolean == true); var negative = recorded.Count(p => p.Value!.Boolean == false);
        var summary = new AnalyticsSummary(recorded.Length, recorded.LastOrDefault()?.Value, average,
            numbers.Length == 0 ? null : numbers.Min(), numbers.Length == 0 ? null : numbers.Max(),
            totalMeaningful && numbers.Length > 0 ? TrackerRules.SumOrAverage(numbers, false) : null,
            positive, negative, Rate(positive, positive + negative));
        var expectedPoints = points.Where(p => p.Expected).ToArray();
        var count = expectedPoints.Count(p => p.Value is not null);
        bool Recorded(AnalyticsPoint point) => item.Settings.Type == TrackerValueType.Boolean ? point.Value?.Boolean == true : point.Value is not null;
        var streak = Streak(expectedPoints, Recorded, item, today, range);
        AnalyticsTarget? target = null;
        if (item.Target is { } goal && item.Schedule.Frequency != TrackerFrequency.Unscheduled)
        {
            var hit = expectedPoints.Count(p => p.TargetReached == true);
            decimal? attainment = null;
            // Attainment is defined only for positive numeric targets and recorded expected periods.
            if (goal.Number > 0 && count > 0)
            {
                var actuals = expectedPoints.Where(p => p.Value?.Number is not null).Select(p => p.Value!.Number!.Value).ToArray();
                if (actuals.Length > 0)
                {
                    try { attainment = checked(TrackerRules.SumOrAverage(actuals, true) / goal.Number.Value * 100); }
                    catch (OverflowException) { throw new TrackerValidationException("Target attainment exceeds the supported decimal range. Choose a smaller range or review the target."); }
                }
            }
            target = new(hit, expected.Count, Rate(hit, expected.Count), attainment, Streak(expectedPoints, p => p.TargetReached == true, item, today, range));
        }
        return new(item, points, summary, expected.Count, count, expected.Count - count, Rate(count, expected.Count), streak, target);
    }
    private static bool? Reached(TrackerValue? target, TrackerValue? value) => target is null || value is null ? null
        : target.Boolean is { } boolean ? value.Boolean == boolean : value.Number >= target.Number;
    private static decimal? Rate(int count, int total) => total == 0 ? null : count * 100m / total;
    private static AnalyticsStreak Streak(AnalyticsPoint[] points, Func<AnalyticsPoint, bool> qualifies, TrackerItem item, DateOnly today, AnalyticsRange range)
    {
        var run = 0; var best = 0;
        foreach (var point in points) { run = qualifies(point) ? run + 1 : 0; best = Math.Max(best, run); }
        // Current is as of today, limited to this range. Every missing expected period,
        // including the current one, breaks the streak.
        var current = 0;
        if (item.IsActive && today >= range.From && today <= range.Through && today >= item.Schedule.StartDate && !(today > item.Schedule.EndDate))
        {
            var index = points.Length - 1;
            for (; index >= 0 && qualifies(points[index]); index--) current++;
        }
        return new(current, best);
    }
}
