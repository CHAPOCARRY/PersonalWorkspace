using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class CanvasService(ICanvasRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate, TimeProvider clock, ILogger<CanvasService> logger) : ICanvasService
{
    public Task<CanvasSnapshot> GetAsync(WorkspaceItemReference page, CancellationToken token = default) => Run(page, w => repository.GetAsync(w, page.ItemId, token), token);
    public Task<CanvasSnapshot> EditAsync(WorkspaceItemReference page, CanvasEdit edit, CancellationToken token = default) => Run(page, w => repository.EditAsync(w, page.ItemId, before => CanvasLayout.Apply(before, edit, clock.GetUtcNow()), token), token);
    private async Task<CanvasSnapshot> Run(WorkspaceItemReference page, Func<WorkspaceContext, Task<CanvasSnapshot>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != page.ProfileId || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(page.ProfileId, database)); }
        catch (CanvasValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Canvas operation failed in profile {ProfileId}", page.ProfileId); throw new CanvasOperationException("The layout could not be loaded or saved. Check workspace access and try again.", exception); }
    }
}
