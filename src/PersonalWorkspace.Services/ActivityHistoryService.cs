using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class ActivityHistoryService(IActivityHistoryRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate) : IActivityHistoryService
{
    public Task<AuditPage> ReadAsync(Guid profile, AuditQuery query, CancellationToken token = default) => Run(profile, w => repository.ReadAsync(w, query, token), token);
    public Task<AuditDetails> DetailsAsync(Guid profile, long sequence, int afterChange = -1, CancellationToken token = default) => Run(profile, w => repository.DetailsAsync(w, sequence, afterChange, token), token);
    public Task ClearAsync(Guid profile, Guid expectedEpoch, CancellationToken token = default) => Run(profile, async w => { await repository.ClearAsync(w, expectedEpoch, token); return true; }, token);
    public Task<NavigationRoute> ResolveAsync(Guid profile, AuditTarget target, CancellationToken token = default) => Run(profile, w => repository.ResolveAsync(w, target, token), token);
    private Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> operation, CancellationToken token) => Task.Run(async () =>
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        return await operation(new(profile, database));
    }, token);
}
