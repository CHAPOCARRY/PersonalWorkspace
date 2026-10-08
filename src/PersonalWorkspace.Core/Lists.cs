using System.Numerics;

namespace PersonalWorkspace.Core;

public enum ListCollection { Active, Archived, Trash }
public enum ListAction { Archive, RestoreArchive, Trash, RestoreTrash }
public enum ListFilter { All, Unchecked, Checked }
public sealed record ListDraft(string Title, string Description = "", bool ShowQuantity = false, bool ShowPrices = false, string? CurrencyCode = null);
public sealed record ListDefinition(WorkspaceItem Item, string Description, bool ShowQuantity, bool ShowPrices, string? CurrencyCode)
{
    public bool IsActive => Item.ArchivedAtUtc is null && Item.DeletedAtUtc is null;
    public bool Matches(ListCollection collection) => collection switch
    {
        ListCollection.Active => IsActive,
        ListCollection.Archived => Item.ArchivedAtUtc is not null && Item.DeletedAtUtc is null,
        ListCollection.Trash => Item.DeletedAtUtc is not null,
        _ => false
    };
}
public sealed record ListItemDraft(string Title, decimal? Quantity = null, decimal? UnitPrice = null, string? Note = null, string? Link = null);
public sealed record ListItem(Guid Id, Guid ListId, string Title, bool IsChecked, decimal? Quantity, decimal? UnitPrice,
    string? Note, string? Link, int SortOrder, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ListItemReference(Guid ProfileId, Guid ListId, Guid ItemId);
public sealed record ListItemQuery(ListFilter Filter = ListFilter.All, string Title = "", int Offset = 0, int Limit = 200);
public sealed record ListTotals(decimal Total, decimal Remaining);
public sealed record ListSnapshot(ListDefinition Definition, IReadOnlyList<ListItem> Items)
{
    public ListTotals Totals => ListRules.Totals(Items);
}
// An operation contains only its selected List; duplication may add one new List.
public sealed class ListState(ListSnapshot? snapshot)
{
    public Dictionary<Guid, ListDefinition> Lists { get; } = snapshot is null ? [] : new() { [snapshot.Definition.Item.Id] = snapshot.Definition };
    public Dictionary<Guid, ListItem> Items { get; } = snapshot?.Items.ToDictionary(i => i.Id) ?? [];
    public ListDefinition Find(Guid id) => Lists.GetValueOrDefault(id) ?? throw new ListValidationException("This List is no longer available in this profile.");
    public ListItem FindItem(Guid list, Guid id) => Items.TryGetValue(id, out var item) && item.ListId == list ? item : throw new ListValidationException("This item is no longer available in this List.");
    public ListItem[] Ordered(Guid list) => Items.Values.Where(i => i.ListId == list).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToArray();
}
public interface IListRepository
{
    Task<IReadOnlyList<ListDefinition>> GetDefinitionsAsync(WorkspaceContext workspace, CancellationToken token);
    Task<ListSnapshot> ReadAsync(WorkspaceContext workspace, Guid id, CancellationToken token);
    Task<IReadOnlyList<ListItem>> GetItemsAsync(WorkspaceContext workspace, Guid id, ListItemQuery query, CancellationToken token);
    Task<T> TransactAsync<T>(WorkspaceContext workspace, Guid? id, Func<ListState, T> change, CancellationToken token);
}
public interface IListService
{
    Task<IReadOnlyList<ListDefinition>> GetDefinitionsAsync(Guid profile, CancellationToken token = default);
    Task<ListSnapshot> ReadAsync(WorkspaceItemReference list, CancellationToken token = default);
    Task<IReadOnlyList<ListItem>> GetItemsAsync(WorkspaceItemReference list, ListItemQuery query, CancellationToken token = default);
    Task<ListDefinition> CreateAsync(Guid profile, ListDraft draft, CancellationToken token = default);
    Task<ListDefinition> UpdateAsync(WorkspaceItemReference list, ListDraft draft, CancellationToken token = default);
    Task<ListItem> AddAsync(WorkspaceItemReference list, ListItemDraft draft, CancellationToken token = default);
    Task<ListItem> UpdateItemAsync(ListItemReference item, ListItemDraft draft, CancellationToken token = default);
    Task SetCheckedAsync(ListItemReference item, bool value, CancellationToken token = default);
    Task ReorderAsync(ListItemReference item, int direction, CancellationToken token = default);
    Task DeleteItemAsync(ListItemReference item, CancellationToken token = default);
    Task ClearCheckedAsync(WorkspaceItemReference list, CancellationToken token = default);
    Task UncheckAllAsync(WorkspaceItemReference list, CancellationToken token = default);
    Task<TaskItem> CreateTaskAsync(ListItemReference item, CancellationToken token = default);
    Task<ListDefinition> DuplicateAsync(WorkspaceItemReference list, CancellationToken token = default);
    Task ApplyAsync(WorkspaceItemReference list, ListAction action, CancellationToken token = default);
    Task DeleteAsync(WorkspaceItemReference list, CancellationToken token = default);
}
public sealed class ListValidationException(string message) : Exception(message);
public sealed class ListOperationException(string message, Exception inner) : Exception(message, inner);

public static class ListRules
{
    public static string Title(string title) => title.Trim() is { Length: > 0 and <= 200 } value && !value.Any(char.IsControl) ? value : throw new ListValidationException("Enter a title of 1–200 characters without control characters.");
    public static ListDraft Validate(ListDraft draft)
    {
        var description = draft.Description.Trim(); var currency = Optional(draft.CurrencyCode)?.ToUpperInvariant();
        if (description.Length > 50000) throw new ListValidationException("Keep the description within 50,000 characters.");
        if (currency is not null && (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))) throw new ListValidationException("Use a three-letter currency code such as EUR, or leave it empty.");
        return draft with { Title = Title(draft.Title), Description = description, CurrencyCode = currency };
    }
    public static ListItemDraft Validate(ListItemDraft draft)
    {
        if (draft.Quantity is <= 0) throw new ListValidationException("Quantity must be greater than zero, or empty.");
        if (draft.UnitPrice is < 0) throw new ListValidationException("Unit price must be zero or greater, or empty.");
        var note = Optional(draft.Note); var link = Optional(draft.Link);
        if (note?.Length > 50000) throw new ListValidationException("Keep the note within 50,000 characters.");
        if (link is not null && (link.Length > 2048 || link.Any(char.IsWhiteSpace) || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")))
            throw new ListValidationException("Enter an absolute http, https or mailto link without spaces.");
        return draft with { Title = Title(draft.Title), Note = note, Link = link };
    }
    public static bool Matches(ListItem item, ListFilter filter, string title) => (filter == ListFilter.All || item.IsChecked == (filter == ListFilter.Checked)) && item.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    // Products and sums are formed as integer coefficients. Decimal multiplication can otherwise round silently.
    private static (BigInteger Number, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var number = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
        return ((bits[3] & int.MinValue) == 0 ? number : -number, (bits[3] >> 16) & 255);
    }
    private static decimal Exact(BigInteger number, int scale)
    {
        while (scale > 0 && number % 10 == 0) { number /= 10; scale--; }
        if (scale > 28 || number < 0 || number > (BigInteger.One << 96) - 1) throw new ListValidationException("The shopping calculation exceeds the supported exact decimal range. Reduce the quantity or price.");
        return new((int)(uint)(number & uint.MaxValue), (int)(uint)((number >> 32) & uint.MaxValue), (int)(uint)(number >> 64), false, (byte)scale);
    }
    public static decimal? Subtotal(ListItem item)
    {
        if (item.UnitPrice is not { } price) return null;
        var a = Parts(item.Quantity ?? 1); var b = Parts(price); return Exact(a.Number * b.Number, a.Scale + b.Scale);
    }
    public static ListTotals Totals(IEnumerable<ListItem> items)
    {
        BigInteger total = 0, remaining = 0;
        foreach (var item in items)
            if (Subtotal(item) is { } value)
            {
                var part = Parts(value); var scaled = part.Number * BigInteger.Pow(10, 28 - part.Scale);
                total += scaled; if (!item.IsChecked) remaining += scaled;
            }
        return new(Exact(total, 28), Exact(remaining, 28));
    }
}
