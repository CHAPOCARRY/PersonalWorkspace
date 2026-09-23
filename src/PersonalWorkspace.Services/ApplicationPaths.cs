using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class ApplicationPaths : IApplicationPaths
{
    public ApplicationPaths() : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    public ApplicationPaths(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        if (!Path.IsPathFullyQualified(localApplicationData)) throw new ArgumentException("An absolute root is required.", nameof(localApplicationData));
        Root = Path.Combine(localApplicationData, "PersonalWorkspace");
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "app.db");
    public string Logs => Path.Combine(Root, "Logs");
    public string Backups => Path.Combine(Root, "Backups");
    public string Profiles => Path.Combine(Root, "Profiles");
    public string InstanceLock => Path.Combine(Root, "application.lock");

    public string ProfileDirectory(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("A non-empty profile ID is required.", nameof(profileId));
        return Path.Combine(Profiles, profileId.ToString("D"));
    }
    public string WorkspaceDatabase(Guid profileId) => Path.Combine(ProfileDirectory(profileId), "workspace.db");
    public string ProfileAttachments(Guid profileId) => Path.Combine(ProfileDirectory(profileId), "attachments");

    public void EnsureDirectories()
    {
        foreach (var directory in new[] { Root, Logs, Backups, Profiles }) Directory.CreateDirectory(directory);
    }
}
