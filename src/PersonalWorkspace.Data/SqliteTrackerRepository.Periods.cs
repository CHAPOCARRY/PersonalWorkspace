using System.Globalization;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteTrackerRepository
{
    public async Task<TrackerPeriodSnapshot> ReadPeriodsAsync(WorkspaceContext workspace, IReadOnlyList<Guid> ids, DateOnly date, CancellationToken token)
    {
        var items = new List<TrackerItem>(); var entries = new List<TrackerEntry>();
        await using var connection = await Open(workspace, token); using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var chunk in ids.Distinct().Chunk(400))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            var parameters = string.Join(",", chunk.Select((_, i) => "$t" + i));
            for (var i = 0; i < chunk.Length; i++) Add(command, "$t" + i, chunk[i]);
            command.CommandText = Select + $" WHERE w.Id IN ({parameters}) AND w.DeletedAtUtc IS NULL;";
            var batch = new List<TrackerItem>();
            using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) batch.Add(ReadItem(reader));
            items.AddRange(batch); if (batch.Count == 0) continue;
            var filters = new List<string>();
            for (var i = 0; i < batch.Count; i++)
            {
                Add(command, "$id" + i, batch[i].Item.Id); Add(command, "$p" + i, batch[i].Schedule.PeriodOn(date));
                filters.Add($"(TrackerId=$id{i} AND (PeriodDate=$p{i} OR PeriodDate=(SELECT MAX(PeriodDate) FROM TrackerEntries WHERE TrackerId=$id{i})))");
            }
            command.CommandText = "SELECT Id,TrackerId,PeriodDate,LocalDate,LocalTime,ValueType,Value,IntegerValue,Note,CreatedAtUtc,UpdatedAtUtc FROM TrackerEntries WHERE " + string.Join(" OR ", filters) + ";";
            using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) entries.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(reader.GetString(4), "HH:mm:ss.fffffff", CultureInfo.InvariantCulture), ReadValue(reader, (TrackerValueType)reader.GetInt32(5), 6, 7)!, Text(reader, 8), Utc(reader, 9)!.Value, Utc(reader, 10)!.Value));
        }
        await transaction.CommitAsync(token); return new(items, entries);
    }
}
