using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class WidgetService(IWidgetRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate,
    TimeProvider clock, ILogger<WidgetService> logger, IAssetService? assets = null) : IWidgetService
{
    private sealed class ImportTarget(IWidgetRepository repository, AddWidget edit, DateTimeOffset now) : IAssetImportTarget
    {
        public WidgetPage? Page { get; private set; }
        public async Task<AssetAttachment> CompleteAsync(WorkspaceContext workspace, Guid owner, Asset asset, CancellationToken token)
        {
            var result = await repository.ImportAsync(workspace, owner, asset, edit, now, token);
            Page = result.Page; return result.Attachment;
        }
    }
    public async Task<WidgetPage> ImportAsync(WorkspaceItemReference page, AddWidget edit, string sourcePath, CancellationToken token = default)
    {
        if (assets is null) throw new WidgetValidationException("File import is unavailable.");
        var target = new ImportTarget(repository, edit, clock.GetUtcNow());
        await assets.ImportToAsync(page, sourcePath, target, token);
        return target.Page!;
    }
    public Task<WidgetPage> GetAsync(WorkspaceItemReference page, CancellationToken token = default) => Run(page, w => repository.ReadAsync(w, page.ItemId, token), token);
    public Task<IReadOnlyList<WorkspaceItem>> CandidatesAsync(WorkspaceItemReference page, WidgetType type, CancellationToken token = default) =>
        Run(page, w => WidgetRules.IsContent(type) ? Task.FromResult<IReadOnlyList<WorkspaceItem>>([]) : repository.CandidatesAsync(w, WidgetRules.SourceType(type), page.ItemId, token), token);
    public async Task<WidgetPage> EditAsync(WorkspaceItemReference page, WidgetEdit edit, CancellationToken token = default)
    {
        var result = await Run(page, w => repository.EditAsync(w, page.ItemId, edit switch { AddWidget add => add.Source, ReplaceWidgetSource replace => replace.Source, ConfigureWidget config => config.Source, _ => null },
            state => WidgetRules.Apply(state, edit, clock.GetUtcNow()), token), token);
        if (edit is RemoveWidget && assets is not null)
        {
            try { await assets.CleanupAsync(page.ProfileId, CancellationToken.None); }
            catch (Exception e) { logger.LogWarning(e, "Widget removal committed; Asset cleanup will retry when the Profile opens."); }
        }
        return result;
    }
    private async Task<T> Run<T>(WorkspaceItemReference page, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != page.ProfileId || current.WorkspaceDatabase is not { } path) throw new WorkspaceChangedException();
        try { return await action(new(page.ProfileId, path)); }
        catch (Exception e) when (e is WidgetValidationException or CanvasValidationException or ContentValidationException or OperationCanceledException) { throw; }
        catch (Exception e) { logger.LogError(e, "Widget operation failed in profile {ProfileId}", page.ProfileId); throw new WidgetOperationException("The Widget could not be loaded or saved. Please try again.", e); }
    }
}
