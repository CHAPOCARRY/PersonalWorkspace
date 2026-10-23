using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class NavigationService(ICurrentProfile? profile = null) : INavigationService
{
    public NavigationRoute Current { get; private set; } = new("Today");
    public event EventHandler? Changed;

    public void Navigate(NavigationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.Destination);
        if (route.Context is { } context && profile is not null && profile.Current?.Id != context.ProfileId) throw new WorkspaceChangedException();
        // Search is an explicit open action, even when its target is already current:
        // the user may have changed local filters or selection since the last dispatch.
        if (route == Current && route.Context is null) return;
        Current = route;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
