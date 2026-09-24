using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class WorkspaceOperationGate : IWorkspaceOperationGate, IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
    public void Dispose() => semaphore.Dispose();
}
