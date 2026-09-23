using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class NavigationService : INavigationService
{
    public NavigationRoute Current { get; private set; } = new("Today");
    public event EventHandler? Changed;

    public void Navigate(NavigationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.Destination);
        if (route == Current) return;
        Current = route;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
