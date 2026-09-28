using PersonalWorkspace.Core;
using TaskStatus = PersonalWorkspace.Core.TaskStatus;

namespace PersonalWorkspace.Services;

internal static class CarryRecalculator
{
    public static void Apply(RecurrenceState state, TimeProvider clock)
    {
        var prerequisites = state.Graph.Prerequisites();
        var occurrences = state.Occurrences.Values.ToLookup(o => o.TaskId);
        var segments = state.Segments.Values.Where(s => s.Enabled).ToLookup(s => s.TaskId);
        foreach (var (taskId, affected) in state.Recalculation)
        {
            var task = state.Graph.Tasks[taskId];
            if (task.Value is not { } definition) continue;
            var policy = state.Policy(taskId);
            var guards = prerequisites[taskId].All(id => state.Graph.Tasks[id].Status == TaskStatus.Done && !state.Graph.Tasks[id].IsRecurring);
            var rules = segments[taskId].OrderBy(s => s.From).ToArray();
            var ruleIndex = 0;
            decimal incoming = 0;
            DateOnly? previous = null;
            foreach (var old in occurrences[taskId].OrderBy(o => o.SlotDate))
            {
                // A never-materialized conceptual execution is null Actual, not an implicit deficit or skip.
                if (previous is { } prior && old.SlotDate.DayNumber > prior.DayNumber + 1)
                {
                    var from = prior.AddDays(1); var through = old.SlotDate.AddDays(-1);
                    while (ruleIndex < rules.Length && rules[ruleIndex].Until <= from) ruleIndex++;
                    for (var i = ruleIndex; i < rules.Length && rules[i].From <= through; i++)
                        if (RecurrenceSchedule.Next(rules[i], from, through) is not null) { incoming = 0; break; }
                }
                previous = old.SlotDate;
                if (old.IsSuppressed) continue;
                if (old.SlotDate < affected.First)
                {
                    if (!old.IsSkipped) incoming = old.Calculation?.CarryOut ?? 0;
                    continue;
                }
                var calculation = OccurrenceCalculation.Calculate(old.Calculation?.BaseTarget ?? definition.Target, incoming, old.Actual, policy, old.IsSkipped);
                var value = (definition with { Target = calculation.EffectiveTarget, Actual = old.Actual }).Validate(allowZeroTarget: true);
                // A read/unchanged carry does not retroactively reevaluate old completion against changed blockers.
                var evaluate = state.ReevaluateCompletion.Contains(old.Id) || old.Calculation != calculation;
                var status = old.Status;
                if (!old.IsSkipped && evaluate)
                    status = value.IsReached && guards ? TaskStatus.Done : status == TaskStatus.Done ? TaskStatus.ToDo : status;
                var updated = old with { Calculation = calculation, Status = status };
                if (updated != old)
                    state.Occurrences[old.Id] = updated with { UpdatedAtUtc = clock.GetUtcNow() > old.UpdatedAtUtc ? clock.GetUtcNow() : old.UpdatedAtUtc.AddTicks(1) };
                if (CarrySettings.Supports(definition.Type) && (updated.IsOverride || updated.IsSkipped || updated.Status != TaskStatus.ToDo || updated.Actual is not null))
                    state.CarryPolicies[taskId] = policy = policy with { Locked = true };
                if (old.IsSkipped) continue; // Keep incoming unchanged; this execution consumes nothing.
                incoming = calculation.CarryOut;
                // Past the explicit input range, identical output means the remaining suffix is still valid.
                if (old.SlotDate > affected.Last && old.Calculation?.CarryOut == incoming) break;
            }
        }
    }
}
