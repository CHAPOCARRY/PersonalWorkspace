using System.Globalization;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Data;

public sealed partial class SqliteTaskRepository
{
    public async Task<T> TransactAsync<T>(WorkspaceContext workspace, RecurrenceQuery query, Func<RecurrenceState, Func<T>> change,
        Action<RecurrenceState> calculate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(workspace, cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var graph = await ReadGraphAsync(connection, transaction, cancellationToken);
        var originalTasks = graph.Tasks.ToDictionary(); var dependencies = graph.Dependencies.ToArray();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var taskId = query.TaskId;
        if (query.OccurrenceId is { } occurrenceId)
        {
            command.CommandText = "SELECT TaskId FROM TaskOccurrences WHERE Id=$occurrence;";
            command.Parameters.AddWithValue("$occurrence", occurrenceId.ToString("D"));
            taskId = await command.ExecuteScalarAsync(cancellationToken) is string id ? Guid.Parse(id)
                : throw new TaskValidationException("This occurrence is no longer available.");
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$task", taskId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$from", query.From is { } from ? Date(from) : DBNull.Value);
        command.Parameters.AddWithValue("$through", query.Through is { } through ? Date(through) : DBNull.Value);
        command.CommandText = """
            SELECT Id,TaskId,Pattern,StartDate,Interval,Weekdays,MonthDay,Ordinal,Weekday,EndDate,FromDate,UntilDate,Enabled,CreatedAtUtc
            FROM TaskRecurrenceRules WHERE 1=1
            """ + (taskId is not null ? " AND TaskId=$task" : "")
                + (query.From is not null ? " AND Enabled=1 AND FromDate <= $through AND (UntilDate IS NULL OR UntilDate > $from)" : "") + ";";
        var segments = new List<RecurrenceSegment>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                segments.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    new((RecurrencePattern)reader.GetInt32(2), ParseDate(reader.GetString(3)), reader.GetInt32(4), reader.GetInt32(5),
                        reader.GetInt32(6), reader.GetInt32(7), (DayOfWeek)reader.GetInt32(8), reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9))),
                    ParseDate(reader.GetString(10)), reader.IsDBNull(11) ? null : ParseDate(reader.GetString(11)), reader.GetBoolean(12), ReadUtc(reader,13)!.Value));
        command.CommandText = OccurrenceSelect + (taskId is not null ? " AND o.TaskId=$task" : "")
                + (query.From is not null ? " AND (SlotDate BETWEEN $from AND $through OR OccurrenceDate BETWEEN $from AND $through)"
                : query.OccurrenceId is not null ? " AND o.Id=$occurrence" : query.TaskId is null || query.RulesOnly ? " AND 0" : "") + ";";
        if (query.OccurrenceId is { } selected) command.Parameters.AddWithValue("$occurrence", selected.ToString("D"));
        var occurrences = new List<TaskOccurrence>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                occurrences.Add(ReadOccurrence(reader));
        var state = new RecurrenceState(graph, segments, occurrences);
        command.Parameters.Clear();
        command.CommandText = """
            SELECT TaskId,CarryDeficit,CarrySurplus,HistoryLocked FROM TaskCarrySettings;
            """;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) state.CarryPolicies.Add(Guid.Parse(reader.GetString(0)),new(reader.GetBoolean(1),reader.GetBoolean(2),reader.GetBoolean(3)));
        var policies = state.CarryPolicies.ToDictionary();
        var finish = change(state);
        // Only actual mutations request a chain read. Ordinary Today/Calendar refreshes stay range-scoped.
        foreach (var (task, affected) in state.Recalculation)
        {
            if (!state.Policy(task).Enabled && policies.GetValueOrDefault(task)?.Enabled != true) continue;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$task",task.ToString("D"));
            command.Parameters.AddWithValue("$first",Date(affected.First));
            command.CommandText = OccurrenceSelect + """
                 AND o.TaskId=$task AND o.SlotDate >= COALESCE(
                    (SELECT SlotDate FROM TaskOccurrences WHERE TaskId=$task AND SlotDate < $first
                        AND IsSkipped=0 AND IsSuppressed=0 ORDER BY SlotDate DESC LIMIT 1),$first);
                """;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var occurrence = ReadOccurrence(reader);
                    if (state.Occurrences.TryAdd(occurrence.Id,occurrence)) occurrences.Add(occurrence);
                }
            command.CommandText = "SELECT Id,TaskId,Pattern,StartDate,Interval,Weekdays,MonthDay,Ordinal,Weekday,EndDate,FromDate,UntilDate,Enabled,CreatedAtUtc FROM TaskRecurrenceRules WHERE TaskId=$task;";
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var segment = new RecurrenceSegment(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),
                        new((RecurrencePattern)reader.GetInt32(2),ParseDate(reader.GetString(3)),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6),reader.GetInt32(7),
                            (DayOfWeek)reader.GetInt32(8),reader.IsDBNull(9)?null:ParseDate(reader.GetString(9))),ParseDate(reader.GetString(10)),
                        reader.IsDBNull(11)?null:ParseDate(reader.GetString(11)),reader.GetBoolean(12),ReadUtc(reader,13)!.Value);
                    if (state.Segments.TryAdd(segment.Id,segment)) segments.Add(segment);
                }
        }
        calculate(state);
        var result = finish(); // Includes post-calculation completion validation, still before commit.
        // Existing IDs are updated in place. Suppressed/skip tombstones are never deleted by reconciliation.
        foreach (var segment in state.Segments.Values.Except(segments))
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO TaskRecurrenceRules VALUES ($id,$task,$pattern,$start,$interval,$days,$monthday,$ordinal,$weekday,$end,$from,$until,$enabled,$created)
                ON CONFLICT(Id) DO UPDATE SET UntilDate=$until, Enabled=$enabled;
                """;
            BindParameters(command, ("id",segment.Id), ("task",segment.TaskId), ("pattern",(int)segment.Rule.Pattern), ("start",Date(segment.Rule.StartDate)),
                ("interval",segment.Rule.Interval), ("days",segment.Rule.Weekdays), ("monthday",segment.Rule.MonthDay), ("ordinal",segment.Rule.Ordinal),
                ("weekday",(int)segment.Rule.Weekday), ("end",segment.Rule.EndDate is { } end ? Date(end) : null), ("from",Date(segment.From)),
                ("until",segment.Until is { } until ? Date(until) : null), ("enabled",segment.Enabled), ("created",segment.CreatedAtUtc.ToString("O")));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var (task, policy) in state.CarryPolicies)
        {
            if (policies.GetValueOrDefault(task)==policy) continue;
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO TaskCarrySettings VALUES ($task,$deficit,$surplus,$locked)
                ON CONFLICT(TaskId) DO UPDATE SET CarryDeficit=$deficit,CarrySurplus=$surplus,HistoryLocked=$locked;
                """;
            BindParameters(command,("task",task),("deficit",policy.Deficit),("surplus",policy.Surplus),("locked",policy.Locked));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var occurrence in state.Occurrences.Values.Except(occurrences))
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO TaskOccurrences VALUES ($id,$task,$segment,$slot,$date,$status,$actual,$skip,$override,$suppressed,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET SegmentId=$segment, OccurrenceDate=$date, Status=$status, Actual=$actual,
                    IsSkipped=$skip, IsOverride=$override, IsSuppressed=$suppressed, UpdatedAtUtc=$updated;
                """;
            BindParameters(command, ("id",occurrence.Id), ("task",occurrence.TaskId), ("segment",occurrence.SegmentId), ("slot",Date(occurrence.SlotDate)),
                ("date",Date(occurrence.OccurrenceDate)), ("status",(int)occurrence.Status), ("actual",occurrence.Actual is { } actual ? ExactValueText.Store(actual) : null),
                ("skip",occurrence.IsSkipped), ("override",occurrence.IsOverride), ("suppressed",occurrence.IsSuppressed),
                ("created",occurrence.CreatedAtUtc.ToString("O")), ("updated",occurrence.UpdatedAtUtc.ToString("O")));
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (occurrence.Calculation is { } value)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO TaskOccurrenceCalculations VALUES ($id,$base,$in,$effective,$out)
                    ON CONFLICT(OccurrenceId) DO UPDATE SET CarryIn=$in,EffectiveTarget=$effective,CarryOut=$out;
                    """;
                BindParameters(command,("id",occurrence.Id),("base",DecimalText(value.BaseTarget)),("in",DecimalText(value.CarryIn)),
                    ("effective",DecimalText(value.EffectiveTarget)),("out",DecimalText(value.CarryOut)));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await PersistGraphAsync(connection, transaction, graph, originalTasks, dependencies, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string DecimalText(decimal value) => ExactValueText.Store(value);
    private const string OccurrenceSelect = """
        SELECT o.Id,o.TaskId,o.SegmentId,o.SlotDate,o.OccurrenceDate,o.Status,o.Actual,o.IsSkipped,o.IsOverride,o.IsSuppressed,o.CreatedAtUtc,o.UpdatedAtUtc,
            c.BaseTarget,c.CarryIn,c.EffectiveTarget,c.CarryOut
        FROM TaskOccurrences o LEFT JOIN TaskOccurrenceCalculations c ON c.OccurrenceId=o.Id WHERE 1=1
        """;
    private static TaskOccurrence ReadOccurrence(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)),
        ParseDate(reader.GetString(3)),ParseDate(reader.GetString(4)),(TaskStatus)reader.GetInt32(5),reader.IsDBNull(6)?null:ReadDecimal(reader,6),
        reader.GetBoolean(7),reader.GetBoolean(8),reader.GetBoolean(9),ReadUtc(reader,10)!.Value,ReadUtc(reader,11)!.Value,
        reader.IsDBNull(12)?null:new(ReadDecimal(reader,12),ReadDecimal(reader,13),ReadDecimal(reader,14),ReadDecimal(reader,15)));
    private static decimal ReadDecimal(SqliteDataReader reader,int column) => ExactValueText.Restore(reader.GetString(column));
    private static DateOnly ParseDate(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static void BindParameters(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values) command.Parameters.AddWithValue("$"+name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
    }
}
