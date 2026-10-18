using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class DecorationService(IDecorationRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<DecorationService> logger) : IDecorationService
{
    public Task<DecorationSnapshot> GetAsync(WorkspaceItemReference page, CancellationToken token = default) => Run(page, w => repository.ReadAsync(w, page.ItemId, token), token);
    public Task<DecorationSnapshot> EditAsync(WorkspaceItemReference page, DecorationEdit edit, CancellationToken token = default) =>
        Run(page, w => repository.EditAsync(w, page.ItemId, before => DecorationRules.Apply(before, edit, clock.GetUtcNow()), token), token);
    private async Task<DecorationSnapshot> Run(WorkspaceItemReference page, Func<WorkspaceContext, Task<DecorationSnapshot>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != page.ProfileId || current.WorkspaceDatabase is not { } path) throw new WorkspaceChangedException();
        try { return await action(new(page.ProfileId, path)); }
        catch (Exception e) when (e is DecorationValidationException or CanvasValidationException or OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Decoration operation failed in profile {ProfileId}", page.ProfileId); throw new DecorationOperationException("Decorations could not be loaded or saved. Please try again.", e); }
    }
}
