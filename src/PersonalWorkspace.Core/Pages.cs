namespace PersonalWorkspace.Core;

public enum PageCollection { Active, Archived, Trash }
public enum PageAction { Archive, RestoreArchive, Trash, RestoreTrash }
public enum PageIcon { None, Book, Star, Flag, Home }
public sealed record PageItem(WorkspaceItem Item, Guid? ParentPageId, int SortOrder, PageIcon Icon = PageIcon.None)
{
    public bool IsActive => Item.ArchivedAtUtc is null && Item.DeletedAtUtc is null;
    public bool Matches(PageCollection collection) => collection switch
    {
        PageCollection.Active => IsActive,
        PageCollection.Archived => Item.ArchivedAtUtc is not null && Item.DeletedAtUtc is null,
        PageCollection.Trash => Item.DeletedAtUtc is not null,
        _ => false
    };
}
public sealed class PageGraph(IEnumerable<PageItem> pages)
{
    public Dictionary<Guid, PageItem> Pages { get; } = pages.ToDictionary(p => p.Item.Id);
    public PageItem Find(Guid id) => Pages.GetValueOrDefault(id) ?? throw new PageValidationException("This Page is no longer available in this profile.");
    public IReadOnlyList<PageItem> Children(Guid? parent) => Pages.Values.Where(p => p.ParentPageId == parent).OrderBy(p => p.SortOrder).ThenBy(p => p.Item.Id).ToArray();
    public IReadOnlyList<PageItem> Ancestors(Guid id)
    {
        var result = new List<PageItem>(); var seen = new HashSet<Guid> { id }; var parent = Find(id).ParentPageId;
        while (parent is { } next)
        {
            if (!seen.Add(next)) throw new PageValidationException("The Page hierarchy contains a cycle.");
            var page = Find(next); result.Add(page); parent = page.ParentPageId;
        }
        result.Reverse(); return result;
    }
    public HashSet<Guid> Descendants(Guid id)
    {
        var children = Pages.Values.Where(p => p.ParentPageId.HasValue).ToLookup(p => p.ParentPageId!.Value, p => p.Item.Id);
        var result = new HashSet<Guid>(); var pending = new Stack<Guid>(); pending.Push(id);
        while (pending.TryPop(out var next)) foreach (var child in children[next]) if (result.Add(child)) pending.Push(child);
        return result;
    }
    public void ValidateParent(Guid id, Guid? parent)
    {
        if (parent is null) return;
        var target = Find(parent.Value);
        if (!target.IsActive) throw new PageValidationException("Choose an active parent Page, or move to root.");
        if (parent == id || Ancestors(parent.Value).Any(p => p.Item.Id == id)) throw new PageValidationException("This move would create a Page hierarchy cycle. Choose a different parent.");
    }
}
public interface IPageRepository
{
    Task<PageGraph> GetAsync(WorkspaceContext workspace, CancellationToken token);
    Task<T> TransactAsync<T>(WorkspaceContext workspace, Func<PageGraph, T> change, CancellationToken token);
}
public interface IPageService
{
    Task<PageGraph> GetAsync(Guid profileId, CancellationToken token = default);
    Task<PageItem> CreateAsync(Guid profileId, string title, Guid? parent = null, PageIcon icon = PageIcon.None, CancellationToken token = default);
    Task<PageItem> UpdateAsync(WorkspaceItemReference reference, string title, PageIcon icon, CancellationToken token = default);
    Task<PageItem> MoveAsync(WorkspaceItemReference reference, Guid? parent, CancellationToken token = default);
    Task ReorderAsync(WorkspaceItemReference reference, int direction, CancellationToken token = default);
    Task<PageItem> ApplyAsync(WorkspaceItemReference reference, PageAction action, CancellationToken token = default);
    Task<PageItem> DuplicateAsync(WorkspaceItemReference reference, CancellationToken token = default);
    Task DeleteAsync(WorkspaceItemReference reference, CancellationToken token = default);
}
public sealed class PageValidationException(string message) : Exception(message);
public sealed class PageOperationException(string message, Exception inner) : Exception(message, inner);
