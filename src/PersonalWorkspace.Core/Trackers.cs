namespace PersonalWorkspace.Core;

public enum TrackerValueType { Integer, Decimal, Percentage, Currency, Duration, Distance, Boolean, Scale, CustomUnit }
public enum TrackerFrequency { Unscheduled, Daily, Weekly, EveryXDays, SelectedWeekdays, Monthly }
public enum TrackerEntryMode { Single, Multiple }
public enum TrackerAggregation { Sum, Average, Min, Max, Last }
public enum TrackerCollection { Active, Archived, Trash }
public enum TrackerAction { Archive, RestoreArchive, Trash, RestoreTrash }

// Numeric values never pass through floating point. Boolean has its own representation.
public sealed record TrackerValue(decimal? Number = null, bool? Boolean = null);
public sealed record TrackerSettings(TrackerValueType Type, string? Unit = null, string? CurrencyCode = null,
    long? ScaleMin = null, long? ScaleMax = null);
public sealed record TrackerSchedule(TrackerFrequency Frequency = TrackerFrequency.Unscheduled, DateOnly? StartDate = null,
    DateOnly? EndDate = null, int Interval = 1, int Weekdays = 0)
{
    // Weekly/X-day periods span their anchored interval. Selected weekdays are individual days.
    // Monthly uses the start day; a short month has no period, rather than moving its anchor.
    public DateOnly? PeriodOn(DateOnly date)
    {
        if (date < StartDate || date > EndDate) return null;
        if (Frequency is TrackerFrequency.Unscheduled or TrackerFrequency.Daily) return date;
        if (StartDate is not { } start) return null;
        if (Frequency is TrackerFrequency.Weekly or TrackerFrequency.EveryXDays)
        {
            var days = Frequency == TrackerFrequency.Weekly ? 7 : Interval;
            return date.AddDays(-((date.DayNumber - start.DayNumber) % days));
        }
        if (Frequency == TrackerFrequency.SelectedWeekdays) return (Weekdays & (1 << (int)date.DayOfWeek)) != 0 ? date : null;
        if (DateTime.DaysInMonth(date.Year, date.Month) < start.Day || date.Day < start.Day) return null;
        return new(date.Year, date.Month, start.Day);
    }
    public bool IsExpected(DateOnly date) => Frequency != TrackerFrequency.Unscheduled && PeriodOn(date) is not null;
}
public sealed record TrackerDraft(string Title, TrackerSettings Settings, TrackerSchedule Schedule,
    TrackerValue? Target = null, TrackerEntryMode EntryMode = TrackerEntryMode.Single, TrackerAggregation Aggregation = TrackerAggregation.Last);
public sealed record TrackerItem(WorkspaceItem Item, TrackerSettings Settings, TrackerSchedule Schedule,
    TrackerValue? Target, TrackerEntryMode EntryMode, TrackerAggregation Aggregation, bool HasEntries = false)
{
    public bool IsActive => Item.ArchivedAtUtc is null && Item.DeletedAtUtc is null;
    public TrackerDraft Draft => new(Item.Title, Settings, Schedule, Target, EntryMode, Aggregation);
}
public sealed record TrackerEntry(Guid Id, Guid TrackerId, DateOnly PeriodDate, DateOnly LocalDate, TimeOnly LocalTime,
    TrackerValue Value, string? Note, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record TrackerPeriod(TrackerItem Tracker, DateOnly? PeriodDate, TrackerValue? Value, int EntryCount, bool Pending);
public sealed record TrackerPeriodSummary(TrackerPeriod Current, TrackerPeriod Latest);
public sealed record TrackerPeriodSnapshot(IReadOnlyList<TrackerItem> Items, IReadOnlyList<TrackerEntry> Entries);

// Selectors deliberately provide no unbounded history operation.
public sealed record TrackerEntryQuery(DateOnly? From = null, DateOnly? Through = null, DateOnly? Period = null,
    int? Recent = null, Guid? EntryId = null, bool LatestPeriod = false);
public sealed class TrackerState(TrackerItem item, IReadOnlyList<TrackerEntry> entries)
{
    public TrackerItem Item { get; set; } = item;
    public Dictionary<Guid, TrackerEntry> Entries { get; } = entries.ToDictionary(e => e.Id);
}
public interface ITrackerRepository
{
    Task<TrackerPeriodSnapshot> ReadPeriodsAsync(WorkspaceContext workspace, IReadOnlyList<Guid> ids, DateOnly date, CancellationToken token);
    Task<IReadOnlyList<TrackerItem>> GetAsync(WorkspaceContext workspace, TrackerCollection collection, CancellationToken cancellationToken);
    Task<TrackerItem?> FindAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
    Task CreateAsync(WorkspaceContext workspace, TrackerItem item, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrackerEntry>> GetEntriesAsync(WorkspaceContext workspace, Guid id, TrackerEntryQuery query, CancellationToken cancellationToken);
    Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid id, Func<TrackerItem, TrackerEntryQuery?> select,
        Func<TrackerState, T> change, CancellationToken cancellationToken);
    Task PermanentlyDeleteAsync(WorkspaceContext workspace, Guid id, CancellationToken cancellationToken);
}
public interface ITrackerService
{
    Task<IReadOnlyList<TrackerPeriodSummary>> GetPeriodsAsync(Guid profileId, IReadOnlyList<Guid> ids, DateOnly date, CancellationToken token = default);
    Task<IReadOnlyList<TrackerPeriod>> GetAsync(Guid profileId, TrackerCollection collection = TrackerCollection.Active, bool todayOnly = false, CancellationToken cancellationToken = default);
    Task<TrackerItem?> FindAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default);
    Task<TrackerItem> CreateAsync(Guid profileId, TrackerDraft draft, CancellationToken cancellationToken = default);
    Task<TrackerItem> UpdateAsync(WorkspaceItemReference reference, TrackerDraft draft, CancellationToken cancellationToken = default);
    Task<TrackerItem> ApplyAsync(WorkspaceItemReference reference, TrackerAction action, CancellationToken cancellationToken = default);
    Task<TrackerItem> DuplicateAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default);
    Task PermanentlyDeleteAsync(WorkspaceItemReference reference, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackerEntry>> GetEntriesAsync(WorkspaceItemReference reference, TrackerEntryQuery query, CancellationToken cancellationToken = default);
    Task<TrackerPeriod> GetPeriodAsync(WorkspaceItemReference reference, DateOnly periodDate, CancellationToken cancellationToken = default);
    // An omitted id upserts the canonical Single entry, or inserts a new Multiple entry.
    // Editing preserves identity, dates, ordering and creation time.
    Task<TrackerEntry> SaveEntryAsync(WorkspaceItemReference reference, DateOnly localDate, TimeOnly localTime,
        TrackerValue value, string? note = null, Guid? entryId = null, CancellationToken cancellationToken = default);
    Task DeleteEntryAsync(WorkspaceItemReference reference, Guid entryId, CancellationToken cancellationToken = default);
}
public sealed class TrackerValidationException(string message) : Exception(message);
public sealed class TrackerOperationException(string message, Exception inner) : Exception(message, inner);
