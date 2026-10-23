using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed class SearchPresentationTests
{
    private sealed class ProfileState : ICurrentProfile
    {
        public Profile? Current { get; private set; }
        public string? WorkspaceDatabase => "unused";
        public event EventHandler? Changed;
        public void Set(Guid id) { Current=new(id,"Profile",id.ToString(),default,null,0);Changed?.Invoke(this,EventArgs.Empty); }
    }
    private sealed class DelayedSearch : ISearchService
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SearchResponse> Response { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NavigationRoute> Route { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayRoute;
        public Task<SearchResponse> SearchAsync(Guid profile,string query,bool archived,CancellationToken token=default){Entered.TrySetResult();return Response.Task;}
        public Task<long> GenerationAsync(Guid profile,CancellationToken token=default)=>Task.FromResult(1L);
        public Task RebuildAsync(Guid profile,CancellationToken token=default)=>Task.CompletedTask;
        public Task<NavigationRoute> ResolveAsync(SearchTarget target,bool archived,CancellationToken token=default)=>DelayRoute?Route.Task:Task.FromResult(new NavigationRoute("Task",target.SourceId.ToString(),new(target.ProfileId)));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProfileRoundTripDiscardsLateSearchOrNavigation(bool navigation)
    {
        var profile=new ProfileState();var a=Guid.NewGuid();profile.Set(a);var service=new DelayedSearch();var nav=new NavigationService(profile);
        using var model=new SearchViewModel(service,profile,nav);model.Open();model.Query="secret";
        var pending=model.RefreshAsync(false);await service.Entered.Task;
        var row=new SearchResult(new(a,SearchKind.Item,Guid.NewGuid(),Guid.NewGuid(),null),"Task","Secret","","",false);
        if(navigation)
        {
            service.Response.SetResult(new([row],false,1));await pending;service.DelayRoute=true;pending=model.OpenSelectedAsync();
        }
        profile.Set(Guid.NewGuid());profile.Set(a);
        if(navigation)service.Route.SetResult(new("Task",row.Target.SourceId.ToString(),new(a)));else service.Response.SetResult(new([row],false,1));
        await pending;Assert.Empty(model.Results);Assert.Null(model.Selected);Assert.Equal("Today",nav.Current.Destination);
    }
    [Fact]
    public async Task ClosingSearchDiscardsEvenUncooperativeBackendResults()
    {
        var profile=new ProfileState();profile.Set(Guid.NewGuid());var service=new DelayedSearch();using var model=new SearchViewModel(service,profile,new NavigationService());
        model.Open();model.Query="text";var pending=model.RefreshAsync(false);await service.Entered.Task;model.Close();
        service.Response.SetResult(new([],false,1));await pending;Assert.Empty(model.Results);Assert.False(model.IsBusy);
    }
}
