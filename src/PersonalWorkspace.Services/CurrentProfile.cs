using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class CurrentProfile(IApplicationPaths paths) : ICurrentProfile
{
    public Profile? Current { get; private set; }
    public string? WorkspaceDatabase => Current is { } profile ? paths.WorkspaceDatabase(profile.Id) : null;
    public event EventHandler? Changed;

    internal void Set(Profile? profile)
    {
        Current = profile;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
