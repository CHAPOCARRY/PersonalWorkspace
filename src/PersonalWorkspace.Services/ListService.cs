using Microsoft.Extensions.Logging;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class ListService(IListRepository repository, ITaskService tasks, ICurrentProfile current, IWorkspaceOperationGate gate, TimeProvider clock, ILogger<ListService> logger) : IListService
{
    public Task<IReadOnlyList<ListDefinition>> GetDefinitionsAsync(Guid profile, CancellationToken token = default) => Run(profile, w => repository.GetDefinitionsAsync(w, token), token);
    public Task<ListSnapshot> ReadAsync(WorkspaceItemReference list, CancellationToken token = default) => Run(list.ProfileId, w => repository.ReadAsync(w, list.ItemId, token), token);
    public Task<IReadOnlyList<ListItem>> GetItemsAsync(WorkspaceItemReference list, ListItemQuery query, CancellationToken token = default) => Run(list.ProfileId, w =>
    {
        if (!Enum.IsDefined(query.Filter) || query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ListValidationException("Request 1–1,000 items with a valid filter and nonnegative offset.");
        return repository.GetItemsAsync(w, list.ItemId, query, token);
    }, token);
    public Task<ListDefinition> CreateAsync(Guid profile, ListDraft draft, CancellationToken token = default) => Change(profile, null, state =>
    {
        var valid = ListRules.Validate(draft); var now = clock.GetUtcNow();
        var list = new ListDefinition(new(Guid.NewGuid(), WorkspaceItemType.List, valid.Title, now, now, null, null), valid.Description, valid.ShowQuantity, valid.ShowPrices, valid.CurrencyCode);
        state.Lists.Add(list.Item.Id, list); return list;
    }, token);
    public Task<ListDefinition> UpdateAsync(WorkspaceItemReference list, ListDraft draft, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        var old = Active(state.Find(list.ItemId)); var valid = ListRules.Validate(draft);
        return Put(state, old with { Item = old.Item with { Title = valid.Title }, Description = valid.Description, ShowQuantity = valid.ShowQuantity, ShowPrices = valid.ShowPrices, CurrencyCode = valid.CurrencyCode });
    }, token);
    public Task<ListItem> AddAsync(WorkspaceItemReference list, ListItemDraft draft, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        Active(state.Find(list.ItemId)); var valid = ListRules.Validate(draft); var now = clock.GetUtcNow();
        var item = new ListItem(Guid.NewGuid(), list.ItemId, valid.Title, false, valid.Quantity, valid.UnitPrice, valid.Note, valid.Link, state.Items.Count, now, now);
        state.Items.Add(item.Id, item); CheckTotals(state, list.ItemId); return item;
    }, token);
    public Task<ListItem> UpdateItemAsync(ListItemReference item, ListItemDraft draft, CancellationToken token = default) => ItemChange(item, (state, old) =>
    {
        var valid = ListRules.Validate(draft); var result = Put(state, old with { Title = valid.Title, Quantity = valid.Quantity, UnitPrice = valid.UnitPrice, Note = valid.Note, Link = valid.Link });
        CheckTotals(state, item.ListId); return result;
    }, token);
    public Task SetCheckedAsync(ListItemReference item, bool value, CancellationToken token = default) => ItemChange(item, (state, old) => Put(state, old with { IsChecked = value }), token);
    public Task ReorderAsync(ListItemReference item, int direction, CancellationToken token = default) => ItemChange(item, (state, old) =>
    {
        if (direction is not (-1 or 1)) throw new ListValidationException("Move an item up or down.");
        var ordered = state.Ordered(item.ListId); var index = Array.FindIndex(ordered, i => i.Id == old.Id); var next = index + direction;
        if (next < 0 || next >= ordered.Length) return false;
        (ordered[index], ordered[next]) = (ordered[next], ordered[index]); Normalize(state, ordered); return true;
    }, token);
    public Task DeleteItemAsync(ListItemReference item, CancellationToken token = default) => ItemChange(item, (state, old) => { state.Items.Remove(old.Id); Normalize(state, state.Ordered(item.ListId)); return true; }, token);
    public Task ClearCheckedAsync(WorkspaceItemReference list, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        Active(state.Find(list.ItemId)); foreach (var item in state.Ordered(list.ItemId).Where(i => i.IsChecked)) state.Items.Remove(item.Id);
        Normalize(state, state.Ordered(list.ItemId)); return true;
    }, token);
    public Task UncheckAllAsync(WorkspaceItemReference list, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        Active(state.Find(list.ItemId)); foreach (var item in state.Ordered(list.ItemId)) Put(state, item with { IsChecked = false }); return true;
    }, token);
    public async Task<TaskItem> CreateTaskAsync(ListItemReference item, CancellationToken token = default)
    {
        // Capture a read-only source snapshot, then let TaskService own its gate and canonical transaction.
        // No source mutation or enduring relationship needs a cross-service transaction.
        var source = await Run(item.ProfileId, async w =>
        {
            var snapshot = await repository.ReadAsync(w, item.ListId, token); Active(snapshot.Definition);
            return snapshot.Items.SingleOrDefault(i => i.Id == item.ItemId) ?? throw new ListValidationException("This item is no longer available in this List.");
        }, token);
        return await tasks.CreateAsync(item.ProfileId, new(source.Title, source.Note ?? ""), token);
    }
    public Task<ListDefinition> DuplicateAsync(WorkspaceItemReference list, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        var source = state.Find(list.ItemId); if (source.Item.DeletedAtUtc is not null) throw new ListValidationException("Restore the List from Trash before duplicating it.");
        var now = clock.GetUtcNow(); var copy = source with { Item = new(Guid.NewGuid(), WorkspaceItemType.List, source.Item.Title, now, now, null, null) };
        state.Lists.Add(copy.Item.Id, copy);
        foreach (var item in state.Ordered(list.ItemId)) { var next = item with { Id = Guid.NewGuid(), ListId = copy.Item.Id, IsChecked = false, CreatedAtUtc = now, UpdatedAtUtc = now }; state.Items.Add(next.Id, next); }
        return copy;
    }, token);
    public Task ApplyAsync(WorkspaceItemReference list, ListAction action, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        var old = state.Find(list.ItemId); var item = old.Item;
        if (item.DeletedAtUtc is not null && action is ListAction.Archive or ListAction.RestoreArchive) throw new ListValidationException("Restore this List from Trash first.");
        item = action switch
        {
            ListAction.Archive => item with { ArchivedAtUtc = item.ArchivedAtUtc ?? clock.GetUtcNow() },
            ListAction.RestoreArchive => item with { ArchivedAtUtc = null },
            ListAction.Trash => item with { DeletedAtUtc = item.DeletedAtUtc ?? clock.GetUtcNow() },
            ListAction.RestoreTrash => item.DeletedAtUtc is null ? item : item with { DeletedAtUtc = null, ArchivedAtUtc = null },
            _ => throw new ListValidationException("Choose a valid List action.")
        };
        return Put(state, old with { Item = item });
    }, token);
    public Task DeleteAsync(WorkspaceItemReference list, CancellationToken token = default) => Change(list.ProfileId, list.ItemId, state =>
    {
        if (state.Find(list.ItemId).Item.DeletedAtUtc is null) throw new ListValidationException("Move the List to Trash before permanently deleting it.");
        state.Lists.Remove(list.ItemId); state.Items.Clear(); return true;
    }, token);
    private static ListDefinition Active(ListDefinition list) => list.IsActive ? list : throw new ListValidationException("Restore this List before editing its definition or items.");
    private static void CheckTotals(ListState state, Guid list) => _ = ListRules.Totals(state.Ordered(list));
    private void Normalize(ListState state, ListItem[] items) { for (var i = 0; i < items.Length; i++) Put(state, items[i] with { SortOrder = i }); }
    private DateTimeOffset Updated(DateTimeOffset old) { var now = clock.GetUtcNow(); return now > old ? now : old.AddTicks(1); }
    private ListItem Put(ListState state, ListItem item)
    {
        var old = state.Items[item.Id]; if (old == item) return old;
        state.Items[item.Id] = item = item with { UpdatedAtUtc = Updated(old.UpdatedAtUtc) }; return item;
    }
    private ListDefinition Put(ListState state, ListDefinition list)
    {
        var old = state.Find(list.Item.Id); if (old == list) return old;
        state.Lists[list.Item.Id] = list = list with { Item = list.Item with { UpdatedAtUtc = Updated(old.Item.UpdatedAtUtc) } }; return list;
    }
    private Task<T> ItemChange<T>(ListItemReference item, Func<ListState, ListItem, T> action, CancellationToken token) => Change(item.ProfileId, item.ListId, state => { Active(state.Find(item.ListId)); return action(state, state.FindItem(item.ListId, item.ItemId)); }, token);
    private Task<T> Change<T>(Guid profile, Guid? id, Func<ListState, T> change, CancellationToken token) => Run(profile, w => repository.TransactAsync(w, id, change, token), token);
    private async Task<T> Run<T>(Guid profile, Func<WorkspaceContext, Task<T>> action, CancellationToken token)
    {
        using var lease = await gate.EnterAsync(token);
        if (current.Current?.Id != profile || current.WorkspaceDatabase is not { } database) throw new WorkspaceChangedException();
        try { return await action(new(profile, database)); }
        catch (ListValidationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { logger.LogError(exception, "List operation failed in profile {ProfileId}", profile); throw new ListOperationException("The List could not be loaded or saved. Check access to the workspace and try again.", exception); }
    }
}
