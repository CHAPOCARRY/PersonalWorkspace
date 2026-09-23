using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed class ProfileFiles(IApplicationPaths paths) : IProfileFiles
{
    public void Create(Guid id)
    {
        var directory = ValidateRoot(id);
        if (Directory.Exists(directory)) throw new IOException("Profile directory already exists.");
        Directory.CreateDirectory(paths.ProfileAttachments(id));
    }

    public void Delete(Guid id)
    {
        var directory = ValidateRoot(id);
        if (!Directory.Exists(directory)) return;
        // Refuse links/junctions, including nested attachment directories, before recursive deletion.
        ValidateTree(directory);
        Directory.Delete(directory, recursive: true);
    }

    private string ValidateRoot(Guid id)
    {
        var parent = Path.GetFullPath(paths.Profiles) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(paths.ProfileDirectory(id));
        if (!directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(directory) != id.ToString("D"))
            throw new IOException("Profile path is outside the profiles directory.");
        foreach (var ancestor in new[] { paths.Root, paths.Profiles, directory })
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Profile paths must not be symbolic links or junctions.");
        return directory;
    }

    private static void ValidateTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile contains a symbolic link or junction.");
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry);
        }
    }
}
