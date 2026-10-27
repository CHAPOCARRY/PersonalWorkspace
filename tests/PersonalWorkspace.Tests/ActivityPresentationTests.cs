using PersonalWorkspace.App.ViewModels;
using PersonalWorkspace.Core;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed class ActivityPresentationTests
{
    [Theory]
    [InlineData(AuditAction.TemplateCaptured, AuditEntityType.Template, "Template captured: Title")]
    [InlineData(AuditAction.TemplateUpdated, AuditEntityType.Template, "Template updated: Title")]
    [InlineData(AuditAction.TemplateInstantiated, AuditEntityType.Page, "Page created from Template: Title")]
    public void ActivityTemplateSummariesDescribeTheOperationWithoutRepeatingTemplate(AuditAction action, AuditEntityType type, string expected)
    {
        var row = new ActivityRow(new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, action, new(type, Guid.NewGuid(), "Title"), 1), "Today");
        Assert.Equal(expected, row.Summary); Assert.Contains(expected, row.ToString());
    }
    private sealed class ProfileState : ICurrentProfile
    {
        public Profile? Current { get; private set; }
        public string? WorkspaceDatabase => "unused";
        public event EventHandler? Changed;
        public void Set(Guid id) { Current = new(id, "Profile", id.ToString(), default, null, 0); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private sealed class Backend(Guid profile) : IActivityHistoryService
    {
        public readonly Guid Epoch = Guid.NewGuid();
        public readonly AuditEvent Event = new(1, Guid.NewGuid(), Guid.NewGuid(), profile, DateTimeOffset.UtcNow, AuditAction.Updated, new(AuditEntityType.Task, Guid.NewGuid(), "Private"), 1);
        public TaskCompletionSource<AuditPage>? Read;
        public TaskCompletionSource<AuditDetails>? Details;
        public TaskCompletionSource<NavigationRoute>? Route;
        public int Clears;
        public AuditQuery? Query;
        public AuditPage Page => new([Event], null, Epoch);
        public AuditDetails Detail => new(Event, [Event.Target], [], false);
        public Task<AuditPage> ReadAsync(Guid p, AuditQuery q, CancellationToken t = default) { Query = q; return Read?.Task ?? Task.FromResult(Page); }
        public Task<AuditDetails> DetailsAsync(Guid p, long s, int after = -1, CancellationToken t = default) => Details?.Task ?? Task.FromResult(Detail);
        public Task<NavigationRoute> ResolveAsync(Guid p, AuditTarget target, CancellationToken t = default) => Route?.Task ?? Task.FromResult(new NavigationRoute("Task", target.Id.ToString(), new(p)));
        public Task ClearAsync(Guid p, Guid e, CancellationToken t = default) { Clears++; return Task.CompletedTask; }
    }
    [Theory][InlineData("read")][InlineData("detail")][InlineData("open")][InlineData("clear")]
    public async Task ActivityProfileRoundTripRejectsStaleResultsNavigationAndConfirmation(string operation)
    {
        var state = new ProfileState(); var a = Guid.NewGuid(); state.Set(a); var nav = new NavigationService(state); nav.Navigate(new("History"));
        var backend = new Backend(a); using var vm = new ActivityHistoryViewModel(backend, state, nav); await vm.RefreshAsync();
        var confirmation = vm.ClearRequest!.Value; Task pending = Task.CompletedTask;
        if (operation == "read") { backend.Read = new(); pending = vm.RefreshAsync(); }
        if (operation == "detail") { backend.Details = new(); vm.Selected = vm.Rows[0]; }
        if (operation == "open") { vm.Selected = vm.Rows[0]; Assert.True(vm.CanOpen); backend.Route = new(); pending = vm.OpenAsync(); }
        nav.Navigate(new("Today")); state.Set(Guid.NewGuid()); state.Set(a);
        backend.Read?.SetResult(backend.Page); backend.Details?.SetResult(backend.Detail); backend.Route?.SetResult(new("Task", backend.Event.Target.Id.ToString(), new(a)));
        await pending;
        await vm.ClearConfirmedAsync(confirmation.Profile, confirmation.Epoch, confirmation.Revision);
        Assert.Empty(vm.Rows); Assert.Null(vm.Selected); Assert.False(vm.CanOpen); Assert.False(vm.IsBusy); Assert.Equal(0, backend.Clears); Assert.Equal("Today", nav.Current.Destination);
    }
    [Fact]
    public async Task ActivityClearingSelectionCancelsBusyDetailsWithoutRepopulation()
    {
        var state = new ProfileState(); var a = Guid.NewGuid(); state.Set(a); var nav = new NavigationService(state); nav.Navigate(new("History"));
        var backend = new Backend(a); using var vm = new ActivityHistoryViewModel(backend, state, nav); await vm.RefreshAsync();
        backend.Details = new(); vm.Selected = vm.Rows[0]; Assert.True(vm.IsBusy); vm.Selected = null;
        backend.Details.SetResult(backend.Detail); Assert.False(vm.IsBusy); Assert.Empty(vm.Changes); Assert.False(vm.CanOpen);
    }
    [Fact]
    public async Task ActivityNullableDateFiltersUseLocalInclusiveDaysAndResetOnProfileSwitch()
    {
        var state = new ProfileState(); var a = Guid.NewGuid(); state.Set(a); var nav = new NavigationService(state); nav.Navigate(new("History"));
        var backend = new Backend(a); using var vm = new ActivityHistoryViewModel(backend, state, nav); await vm.RefreshAsync();
        Assert.Null(backend.Query!.From); Assert.Null(backend.Query.Until);
        var day = new DateTimeOffset(new DateTime(2026, 10, 26));
        vm.From = day; await vm.RefreshAsync(); Assert.Equal(day, backend.Query.From); Assert.Null(backend.Query.Until);
        vm.Through = day; await vm.RefreshAsync(); Assert.Equal(day.AddDays(1), backend.Query.Until);
        vm.From = null; await vm.RefreshAsync(); Assert.Null(backend.Query.From); Assert.Equal(day.AddDays(1), backend.Query.Until);
        vm.Through = null; await vm.RefreshAsync(); Assert.Null(backend.Query.Until);
        vm.From = vm.Through = day; state.Set(Guid.NewGuid()); Assert.Null(vm.From); Assert.Null(vm.Through);
        Assert.DoesNotContain("1926", new ActivityRow(backend.Event, "Today").ToString());
        Assert.DoesNotContain(backend.Event.Id.ToString(), new ActivityRow(backend.Event, "Today").ToString());
    }
}
