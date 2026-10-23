using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class SearchService(ISearchRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate) : ISearchService
{
    public Task<SearchResponse> SearchAsync(Guid profile, string query, bool archived, CancellationToken token = default) =>
        Run(profile, w => repository.SearchAsync(w, query, archived, p => p.Format is null ? p.Body : RichTextFormat.Extract(new(p.SourceId, p.Body, p.Format, p.FormatVersion, default, default)), token), token);
    public Task<long> GenerationAsync(Guid profile, CancellationToken token = default) => Run(profile, w => repository.GenerationAsync(w, token), token);
    public Task<NavigationRoute> ResolveAsync(SearchTarget target, bool archived, CancellationToken token = default) => Run(target.ProfileId, w => repository.ResolveAsync(w, target, archived, token), token);
    public Task RebuildAsync(Guid profile, CancellationToken token = default) => Run(profile, async w => { await repository.RebuildAsync(w, token); return true; }, token);
    private Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        return await action(new(profile, database));
    }, token);
}
