using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class PageService(IPageRepository repository, ICurrentProfile current, IWorkspaceOperationGate gate, TimeProvider clock, ILogger<PageService> logger) : IPageService
{
    public Task<PageGraph> GetAsync(Guid profileId, CancellationToken token = default) => Run(profileId, w => repository.GetAsync(w, token), token);
    public Task<PageItem> CreateAsync(Guid profileId, string title, Guid? parent = null, PageIcon icon = PageIcon.None, CancellationToken token = default) => Change(profileId, graph =>
    {
        var id = Guid.NewGuid(); graph.ValidateParent(id, parent); var now = clock.GetUtcNow();
        var page = new PageItem(new(id, WorkspaceItemType.Page, Title(title), now, now, null, null), parent, graph.Children(parent).Count, Icon(icon));
        graph.Pages.Add(id, page); return page;
    }, token);
    public Task<PageItem> UpdateAsync(WorkspaceItemReference reference, string title, PageIcon icon, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var old = Editable(graph.Find(reference.ItemId)); return Put(graph, old with { Item = old.Item with { Title = Title(title) }, Icon = Icon(icon) });
    }, token);
    public Task<PageItem> MoveAsync(WorkspaceItemReference reference, Guid? parent, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var old = Editable(graph.Find(reference.ItemId)); graph.ValidateParent(old.Item.Id, parent);
        if (old.ParentPageId == parent) return old;
        Put(graph, old with { ParentPageId = parent, SortOrder = graph.Children(parent).Count });
        Normalize(graph, old.ParentPageId); Normalize(graph, parent); return graph.Find(old.Item.Id);
    }, token);
    public Task ReorderAsync(WorkspaceItemReference reference, int direction, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var page = Editable(graph.Find(reference.ItemId));
        if (direction is not (-1 or 1)) throw new PageValidationException("Move a Page up or down.");
        var siblings = graph.Children(page.ParentPageId).ToList(); var index = siblings.FindIndex(p => p.Item.Id == page.Item.Id);
        // Skip hidden siblings so one click moves past a visible neighbour.
        var next = index + direction;
        while (next >= 0 && next < siblings.Count && !siblings[next].Matches(page.IsActive ? PageCollection.Active : PageCollection.Archived)) next += direction;
        if (next < 0 || next >= siblings.Count) return false;
        (siblings[index], siblings[next]) = (siblings[next], siblings[index]);
        for (var i = 0; i < siblings.Count; i++) Put(graph, siblings[i] with { SortOrder = i });
        return true;
    }, token);
    public Task<PageItem> ApplyAsync(WorkspaceItemReference reference, PageAction action, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var old = graph.Find(reference.ItemId); var item = old.Item;
        if (action is PageAction.Archive or PageAction.RestoreArchive) Editable(old);
        item = action switch
        {
            PageAction.Archive => item with { ArchivedAtUtc = item.ArchivedAtUtc ?? clock.GetUtcNow() },
            PageAction.RestoreArchive => item with { ArchivedAtUtc = null },
            PageAction.Trash => item with { DeletedAtUtc = item.DeletedAtUtc ?? clock.GetUtcNow() },
            PageAction.RestoreTrash => item.DeletedAtUtc is null ? item : item with { DeletedAtUtc = null, ArchivedAtUtc = null },
            _ => throw new PageValidationException("Choose a valid Page action.")
        };
        return Put(graph, old with { Item = item });
    }, token);
    public Task<PageItem> DuplicateAsync(WorkspaceItemReference reference, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var source = Editable(graph.Find(reference.ItemId)); var now = clock.GetUtcNow();
        var copy = source with { Item = new(Guid.NewGuid(), WorkspaceItemType.Page, source.Item.Title, now, now, null, null), SortOrder = graph.Children(source.ParentPageId).Count };
        graph.Pages.Add(copy.Item.Id, copy); return copy;
    }, token);
    public Task DeleteAsync(WorkspaceItemReference reference, CancellationToken token = default) => Change(reference.ProfileId, graph =>
    {
        var page = graph.Find(reference.ItemId);
        if (page.Item.DeletedAtUtc is null) throw new PageValidationException("Move the Page to Trash before permanently deleting it.");
        var children = graph.Children(page.Item.Id); var rootOrder = graph.Children(null).Count;
        foreach (var child in children) Put(graph, child with { ParentPageId = null, SortOrder = rootOrder++ });
        graph.Pages.Remove(page.Item.Id); Normalize(graph, page.ParentPageId); Normalize(graph, null); return true;
    }, token);
    private void Normalize(PageGraph graph, Guid? parent)
    {
        var siblings = graph.Children(parent); for (var i = 0; i < siblings.Count; i++) Put(graph, siblings[i] with { SortOrder = i });
    }
    private PageItem Put(PageGraph graph, PageItem page)
    {
        var old = graph.Find(page.Item.Id); if (old == page) return old;
        var now = clock.GetUtcNow(); page = page with { Item = page.Item with { UpdatedAtUtc = now > old.Item.UpdatedAtUtc ? now : old.Item.UpdatedAtUtc.AddTicks(1) } };
        graph.Pages[page.Item.Id] = page; return page;
    }
    private static PageItem Editable(PageItem page) => page.Item.DeletedAtUtc is null ? page : throw new PageValidationException("Restore this Page from Trash before editing it.");
    private static string Title(string title) => title.Trim() is { Length: > 0 and <= 200 } value && !value.Any(char.IsControl) ? value : throw new PageValidationException("Enter a Page title of 1–200 characters without control characters.");
    private static PageIcon Icon(PageIcon icon) => Enum.IsDefined(icon) ? icon : throw new PageValidationException("Choose a built-in Page icon.");
    private Task<T> Change<T>(Guid profile, Func<PageGraph, T> change, CancellationToken token) => Run(profile, w => repository.TransactAsync(w, change, token), token);
    private async Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profile, database)); }
        catch (PageValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Page operation failed in profile {ProfileId}", profile); throw new PageOperationException("The Page could not be loaded or saved. Check access to the workspace and try again.", exception); }
    }
}
