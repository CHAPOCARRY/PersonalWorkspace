namespace PersonalWorkspace.Core;

public enum RecurrencePattern { Daily, Weekly, MonthlyDay, MonthlyWeekday }
public enum RecurrenceScope { EntireSeries, ThisAndFuture }

// Weekday bits follow DayOfWeek (Sunday = bit 0). Weeks always start Monday.
public sealed record RecurrenceRule(RecurrencePattern Pattern, DateOnly StartDate, int Interval = 1,
    int Weekdays = 0, int MonthDay = 1, int Ordinal = 1, DayOfWeek Weekday = DayOfWeek.Monday, DateOnly? EndDate = null)
{
    public RecurrenceRule Validate()
    {
        if (!Enum.IsDefined(Pattern) || Interval is < 1 or > 999)
            throw new TaskValidationException("Choose a recurrence pattern and an interval from 1 to 999.");
        if (EndDate < StartDate) throw new TaskValidationException("Recurrence end cannot precede its start.");
        if (Pattern == RecurrencePattern.Weekly && Weekdays is < 1 or > 127)
            throw new TaskValidationException("Choose at least one weekday.");
        if (Pattern == RecurrencePattern.MonthlyDay && MonthDay is < 1 or > 31)
            throw new TaskValidationException("Choose a day of month from 1 to 31.");
        if (Pattern == RecurrencePattern.MonthlyWeekday && (Ordinal is not (-1 or 1 or 2 or 3 or 4 or 5) || !Enum.IsDefined(Weekday)))
            throw new TaskValidationException("Choose first, second, third, fourth, fifth or last and a weekday.");
        return this;
    }

    public bool Matches(DateOnly date)
    {
        if (date < StartDate || date > EndDate) return false;
        var days = date.DayNumber - StartDate.DayNumber;
        var months = (date.Year - StartDate.Year) * 12 + date.Month - StartDate.Month;
        return Pattern switch
        {
            RecurrencePattern.Daily => days % Interval == 0,
            RecurrencePattern.Weekly => ((days + ((int)StartDate.DayOfWeek + 6) % 7) / 7) % Interval == 0
                && (Weekdays & (1 << (int)date.DayOfWeek)) != 0,
            RecurrencePattern.MonthlyDay => months % Interval == 0 && date.Day == MonthDay,
            RecurrencePattern.MonthlyWeekday => months % Interval == 0 && date.DayOfWeek == Weekday
                && (Ordinal == -1 ? date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month) : (date.Day - 1) / 7 + 1 == Ordinal),
            _ => false
        };
    }

    public IEnumerable<DateOnly> Dates(DateOnly from, DateOnly through)
    {
        Validate();
        if (from > through || through.DayNumber - from.DayNumber > 365)
            throw new TaskValidationException("Choose a date window of at most 366 days.");
        for (var day = Math.Max(from.DayNumber, StartDate.DayNumber); day <= through.DayNumber; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            if (Matches(date)) yield return date;
        }
    }
}

public sealed record RecurrenceSegment(Guid Id, Guid TaskId, RecurrenceRule Rule, DateOnly From, DateOnly? Until,
    bool Enabled, DateTimeOffset CreatedAtUtc)
{
    public bool Matches(DateOnly date) => Enabled && date >= From && (Until is null || date < Until) && Rule.Matches(date);
}

public sealed record TaskOccurrence(Guid Id, Guid TaskId, Guid SegmentId, DateOnly SlotDate, DateOnly OccurrenceDate,
    TaskStatus Status, decimal? Actual, bool IsSkipped, bool IsOverride, bool IsSuppressed,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, OccurrenceCalculation? Calculation = null)
{
    public bool IsProtected(DateOnly today) => SlotDate < today || OccurrenceDate < today || IsOverride || IsSkipped || Status != TaskStatus.ToDo || Actual is not null;
}
public sealed record OccurrenceReference(Guid ProfileId, Guid Id);
public sealed record OccurrenceItem(TaskItem Definition, TaskOccurrence Occurrence)
{
    public TaskValue? Value => Definition.Value is { } value ? value with { Target = Occurrence.Calculation?.EffectiveTarget ?? value.Target, Actual = Occurrence.Actual } : null;
}

// Range reads stay scoped; mutations may request an existing carry suffix before the transaction commits.
public sealed class RecurrenceState(TaskGraph graph, IEnumerable<RecurrenceSegment> segments, IEnumerable<TaskOccurrence> occurrences)
{
    public TaskGraph Graph { get; } = graph;
    public Dictionary<Guid, RecurrenceSegment> Segments { get; } = segments.ToDictionary(s => s.Id);
    public Dictionary<Guid, TaskOccurrence> Occurrences { get; } = occurrences.ToDictionary(o => o.Id);
    public Dictionary<Guid, CarrySettings> CarryPolicies { get; } = [];
    public Dictionary<Guid, (DateOnly First, DateOnly Last)> Recalculation { get; } = [];
    public HashSet<Guid> ReevaluateCompletion { get; } = [];
    public void RecalculateFrom(Guid task, DateOnly slot)
    {
        Recalculation[task] = Recalculation.TryGetValue(task, out var range)
            ? (slot < range.First ? slot : range.First, slot > range.Last ? slot : range.Last) : (slot, slot);
    }
    public CarrySettings Policy(Guid task) => CarryPolicies.GetValueOrDefault(task) ?? new();
}
public sealed record RecurrenceQuery(Guid? TaskId = null, Guid? OccurrenceId = null, DateOnly? From = null, DateOnly? Through = null, bool RulesOnly = false);
public interface IRecurrenceRepository
{
    Task<T> TransactAsync<T>(WorkspaceContext workspace, RecurrenceQuery query, Func<RecurrenceState, Func<T>> change,
        Action<RecurrenceState> calculate, CancellationToken cancellationToken);
}
public interface IRecurrenceService
{
    Task<IReadOnlyList<OccurrenceItem>> GetRangeAsync(Guid profile, DateOnly from, DateOnly through, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OccurrenceItem>> GetHistoryAsync(TaskReference task, DateOnly from, DateOnly through, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecurrenceSegment>> GetSegmentsAsync(TaskReference task, CancellationToken cancellationToken = default);
    Task<OccurrenceItem> FindAsync(OccurrenceReference occurrence, CancellationToken cancellationToken = default);
    Task SetRuleAsync(TaskReference task, RecurrenceRule rule, RecurrenceScope scope = RecurrenceScope.EntireSeries, Guid? fromOccurrence = null, CancellationToken cancellationToken = default);
    Task RemoveAsync(TaskReference task, CancellationToken cancellationToken = default);
    Task<OccurrenceItem> UpdateAsync(OccurrenceReference occurrence, DateOnly date, TaskStatus status, decimal? actual, bool skipped, CancellationToken cancellationToken = default);
    Task MoveAsync(OccurrenceReference occurrence, DateOnly date, CancellationToken cancellationToken = default);
    Task<CarrySettings> GetCarrySettingsAsync(TaskReference task, CancellationToken cancellationToken = default);
    Task SetCarrySettingsAsync(TaskReference task, bool deficit, bool surplus, CancellationToken cancellationToken = default);
}
