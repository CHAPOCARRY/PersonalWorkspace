namespace PersonalWorkspace.Core;

public enum SearchKind { Item = 1, ListItem, JournalEntry, PageContent, Template }
public sealed record SearchTarget(Guid ProfileId, SearchKind Kind, Guid SourceId, Guid? OwnerId, DateOnly? EntryDate);
public sealed record SearchResult(SearchTarget Target, string Type, string Title, string Context, string Snippet, bool Archived)
{
    public string Detail => string.Join(" · ", new[] { Type, Context == Title ? null : Context,
        Target.EntryDate?.ToString("d", System.Globalization.CultureInfo.CurrentCulture), Archived ? "Archived" : null }.Where(s => !string.IsNullOrEmpty(s)));
    public override string ToString() => Title + " · " + Detail + " · " + Snippet;
}
public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, bool HasMore, long Generation);
public sealed record SearchProjection(SearchKind Kind, Guid SourceId, Guid? OwnerId, string Title, string Metadata, string Body,
    string? EntryDate, string? Format, int FormatVersion);
public interface ISearchRepository
{
    Task<SearchResponse> SearchAsync(WorkspaceContext workspace, string query, bool archived, Func<SearchProjection, string> extract, CancellationToken token);
    Task RebuildAsync(WorkspaceContext workspace, CancellationToken token);
    Task<long> GenerationAsync(WorkspaceContext workspace, CancellationToken token);
    Task<NavigationRoute> ResolveAsync(WorkspaceContext workspace, SearchTarget target, bool archived, CancellationToken token);
}
public interface ISearchService
{
    Task<SearchResponse> SearchAsync(Guid profile, string query, bool archived, CancellationToken token = default);
    Task RebuildAsync(Guid profile, CancellationToken token = default);
    Task<long> GenerationAsync(Guid profile, CancellationToken token = default);
    Task<NavigationRoute> ResolveAsync(SearchTarget target, bool archived, CancellationToken token = default);
}
public sealed class SearchException(string message) : Exception(message);
