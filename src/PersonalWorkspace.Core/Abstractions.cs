namespace PersonalWorkspace.Core;

public interface IApplicationPaths
{
    string Root { get; }
    string Database { get; }
    string Logs { get; }
    string Backups { get; }
    string Profiles { get; }
    void EnsureDirectories();
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<T> GetAsync<T>(string key, T fallback, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}

public sealed record NavigationRoute(string Destination, string? EntityId = null);

public interface INavigationService
{
    NavigationRoute Current { get; }
    event EventHandler? Changed;
    void Navigate(NavigationRoute route);
}

public sealed record WindowPreferences(int Width = 1200, int Height = 800, bool Maximized = false)
{
    public bool IsValid => Width is >= 640 and <= 7680 && Height is >= 480 and <= 4320;
}

public static class SettingKeys
{
    public const string SidebarCollapsed = "shell.sidebarCollapsed";
    public const string Window = "shell.window";
}
