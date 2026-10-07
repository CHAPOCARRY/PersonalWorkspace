using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class WidgetService(IWidgetRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<WidgetService> logger) : IWidgetService
{
    public Task<WidgetPage> GetAsync(WorkspaceItemReference page, CancellationToken token = default) => Run(page, w => repository.ReadAsync(w, page.ItemId, token), token);
    public Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceItemReference page, WidgetType type, CancellationToken token = default) =>
        Run(page, w => repository.CandidatesAsync(w, WidgetRules.SourceType(type), page.ItemId, token), token);
    public Task<WidgetPage> EditAsync(WorkspaceItemReference page, WidgetEdit edit, CancellationToken token = default) =>
        Run(page, w => repository.EditAsync(w, page.ItemId, edit switch { AddWidget add => add.Source, ReplaceWidgetSource replace => replace.Source, ConfigureWidget config => config.Source, _ => null },
            state => WidgetRules.Apply(state, edit, clock.GetUtcNow()), token), token);
    private async Task<T> Run<T>(WorkspaceItemReference page, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != page.ProfileId || current.WorkspaceDatabase is not { } path) throw new WorkspaceChangedException();
        try { return await action(new(page.ProfileId, path)); }
        catch (Exception e) when (e is WidgetValidationException or CanvasValidationException or OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Widget operation failed in profile {ProfileId}", page.ProfileId); throw new WidgetOperationException("The Widget could not be loaded or saved. Please try again.", e); }
    }
}
