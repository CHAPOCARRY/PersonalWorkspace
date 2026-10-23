using CommunityToolkit.Mvvm.ComponentModel;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.App.ViewModels;

public sealed class SearchViewModel : ObservableObject, IDisposable
{
    private readonly ISearchService service;
    private readonly ICurrentProfile current;
    private readonly INavigationService navigation;
    private CancellationTokenSource? pending;
    private long revision, generation = -1;
    private string query = "", status = "Type to search this Profile.";
    private bool archived, busy, open, checking;
    private IReadOnlyList<SearchResult> results = [];
    private SearchResult? selected;
    public SearchViewModel(ISearchService service, ICurrentProfile current, INavigationService navigation)
    { this.service = service; this.current = current; this.navigation = navigation; current.Changed += ProfileChanged; }
    public event EventHandler? CloseRequested;
    public string Query { get => query; set { if (SetProperty(ref query, value)) _ = RefreshAsync(); } }
    public bool IncludeArchived { get => archived; set { if (SetProperty(ref archived, value)) _ = RefreshAsync(); } }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public IReadOnlyList<SearchResult> Results { get => results; private set => SetProperty(ref results, value); }
    public SearchResult? Selected { get => selected; set => SetProperty(ref selected, value); }
    public void Open() { open = true; _ = RefreshAsync(false); }
    public void Close() { open = false; Cancel(); Results = []; Selected = null; IsBusy = false; }
    private void Cancel() { ++revision; pending?.Cancel(); pending?.Dispose(); pending = null; }
    private void ProfileChanged(object? sender, EventArgs args)
    { Close(); query = ""; archived = false; generation = -1; OnPropertyChanged(nameof(Query)); OnPropertyChanged(nameof(IncludeArchived)); Status = "Profile changed. Open search again."; CloseRequested?.Invoke(this, EventArgs.Empty); }
    public async Task RefreshAsync(bool debounce = true)
    {
        Cancel(); Results = []; Selected = null;
        if (!open || current.Current is not { } profile) return;
        if (string.IsNullOrWhiteSpace(Query)) { IsBusy = false; Status = "Type to search this Profile."; return; }
        var request = revision; pending = new(); var token = pending.Token; var text = Query; var include = IncludeArchived;
        IsBusy = true; Status = "Updating search index…";
        try
        {
            if (debounce) await Task.Delay(250, token);
            while (Fresh(request, profile.Id))
            {
                var response = await service.SearchAsync(profile.Id, text, include, token);
                if (!Fresh(request, profile.Id)) return;
                if (response.Generation != await service.GenerationAsync(profile.Id, token)) continue;
                if (!Fresh(request, profile.Id)) return;
                generation = response.Generation; Results = response.Results; Selected = Results.FirstOrDefault();
                Status = response.HasMore ? "50 results shown. More matches available—refine your search." : Results.Count == 0 ? "No matches." : $"{Results.Count} results";
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (Fresh(request, profile.Id)) Status = e is SearchException or WorkspaceChangedException ? e.Message : "Search could not update its index. Retry or rebuild the search index."; }
        finally { if (Fresh(request, profile.Id)) IsBusy = false; }
    }
    public async Task RefreshIfChangedAsync()
    {
        if (!open || IsBusy || checking || generation < 0 || current.Current is not { } profile) return;
        var request = revision; checking = true;
        try { if (await service.GenerationAsync(profile.Id) != generation && Fresh(request, profile.Id)) await RefreshAsync(false); }
        catch (Exception) { if (Fresh(request, profile.Id)) { Results = []; Selected = null; Status = "Search is unavailable. Retry."; } }
        finally { checking = false; }
    }
    public async Task RebuildAsync()
    {
        if (IsBusy || current.Current is not { } profile) return;
        Cancel(); var request = revision; pending = new(); var token = pending.Token;
        Results = []; Selected = null; IsBusy = true; Status = "Rebuilding search index…";
        try
        {
            await service.RebuildAsync(profile.Id, token);
            await service.SearchAsync(profile.Id, "", IncludeArchived, token);
            if (Fresh(request, profile.Id)) await RefreshAsync(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Fresh(request, profile.Id)) Status = "The index could not be rebuilt. Retry."; }
        finally { if (Fresh(request, profile.Id)) IsBusy = false; }
    }
    public async Task OpenSelectedAsync()
    {
        if (IsBusy || Selected is not { } row || current.Current?.Id != row.Target.ProfileId) return;
        var request = revision; IsBusy = true;
        try
        {
            var route = await service.ResolveAsync(row.Target, IncludeArchived);
            if (!Fresh(request, row.Target.ProfileId)) return;
            // No await between the final session/Profile check and dispatch on the UI thread.
            navigation.Navigate(route); CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { if (Fresh(request, row.Target.ProfileId)) { Results = []; Selected = null; Status = e is SearchException or WorkspaceChangedException ? e.Message : "This result could not be opened. Search again."; } }
        finally { if (request == revision) IsBusy = false; }
    }
    public void Move(int delta)
    { if (Results.Count == 0) return; var index = Selected is null ? -1 : Results.ToList().IndexOf(Selected); Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)]; }
    private bool Fresh(long request, Guid profile) => open && request == revision && current.Current?.Id == profile;
    public void Dispose() { Close(); current.Changed -= ProfileChanged; }
}
