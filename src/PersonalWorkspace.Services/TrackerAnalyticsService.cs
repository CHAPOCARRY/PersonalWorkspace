using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class TrackerAnalyticsService(ITrackerAnalyticsRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<TrackerAnalyticsService> logger) : ITrackerAnalyticsService
{
    public Task<IReadOnlyList<TrackerItem>> GetCandidatesAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Run(profileId, workspace => repository.GetCandidatesAsync(workspace, cancellationToken), cancellationToken);
    public Task<TrackerAnalyticsResult> GetAsync(Guid profileId, IReadOnlyList<Guid> ids, AnalyticsPreset preset = AnalyticsPreset.ThirtyDays,
        DateOnly? from = null, DateOnly? through = null, CancellationToken cancellationToken = default) => Run(profileId, async workspace =>
    {
        if (ids.Count is < 1 or > 4 || ids.Distinct().Count() != ids.Count) throw new TrackerValidationException("Choose one to four distinct Trackers.");
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var range = AnalyticsRange.Resolve(preset, today, from, through);
        var snapshot = await repository.ReadAnalyticsAsync(workspace, ids, range, today, cancellationToken);
        var entries = snapshot.Entries.ToLookup(e => e.TrackerId);
        var result = snapshot.Trackers.Select(item => TrackerAnalytics.Calculate(item, entries[item.Item.Id], snapshot.Range, today, clock.LocalTimeZone)).ToArray();
        return new TrackerAnalyticsResult(snapshot.Range, result);
    }, cancellationToken);
    private async Task<T> Run<T>(Guid profileId, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profileId || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profileId, database)); }
        catch (TrackerValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tracker analytics query failed for profile {ProfileId}", profileId);
            throw new TrackerOperationException("Analytics could not be loaded. Check access to the local workspace and try again.", exception);
        }
    }
}
