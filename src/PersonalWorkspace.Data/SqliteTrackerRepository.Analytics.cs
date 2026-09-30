using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteTrackerRepository
{
    public Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(WorkspaceContext workspace, CancellationToken cancellationToken) => GetAsync(workspace, TrackerCollection.Active, cancellationToken);
    public async Task<AnalyticsSnapshot> ReadAnalyticsAsync(WorkspaceContext workspace, IReadOnlyList<Guid> ids, AnalyticsRange? range, DateOnly today, CancellationToken cancellationToken)
    {
        if (ids.Count is < 1 or > 4 || ids.Distinct().Count() != ids.Count) throw new TrackerValidationException("Choose one to four distinct Trackers.");
        await using var connection = await Open(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var parameters = string.Join(",", ids.Select((_, i) => "$t" + i));
        for (var i = 0; i < ids.Count; i++) Add(command, "$t" + i, ids[i]);
        command.CommandText = Select + $" WHERE w.Id IN ({parameters}) AND w.DeletedAtUtc IS NULL;";
        var items = new List<TrackerItem>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) items.Add(ReadItem(reader));
        if (items.Count != ids.Count) throw new TrackerValidationException("One of these Trackers is unavailable or in Trash. Reopen it in its profile.");
        items = ids.Select(id => items.Single(t => t.Item.Id == id)).ToList();
        if (items.Skip(1).Any(t => !TrackerAnalytics.Compatible(items[0].Settings, t.Settings)))
            throw new TrackerValidationException("Compare Trackers with the same value type, unit, currency and scale bounds. No conversion or normalization is applied.");
        if (range is null)
        {
            command.CommandText = $"SELECT MIN(PeriodDate) FROM TrackerEntries WHERE TrackerId IN ({parameters});";
            var first = await command.ExecuteScalarAsync(cancellationToken) is string text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : today;
            foreach (var item in items) if (item.Schedule.StartDate is { } start && start < first) first = start;
            range = new(first > today ? today : first, today);
        }
        if (range.From > range.Through) throw new TrackerValidationException("The range end cannot precede its start.");
        command.CommandText = $"""
            SELECT Id,TrackerId,PeriodDate,LocalDate,LocalTime,ValueType,Value,IntegerValue,Note,CreatedAtUtc,UpdatedAtUtc
            FROM TrackerEntries WHERE TrackerId IN ({parameters}) AND PeriodDate BETWEEN $from AND $through
            ORDER BY TrackerId,PeriodDate,LocalDate,LocalTime,CreatedAtUtc,Id;
            """;
        Add(command, "$from", range.From); Add(command, "$through", range.Through);
        var entries = new List<TrackerEntry>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) entries.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(reader.GetString(4), "HH:mm:ss.fffffff", CultureInfo.InvariantCulture), ReadValue(reader, (TrackerValueType)reader.GetInt32(5), 6, 7)!,
                Text(reader, 8), Utc(reader, 9)!.Value, Utc(reader, 10)!.Value));
        await transaction.CommitAsync(cancellationToken);
        return new(range, items, entries);
    }
}
