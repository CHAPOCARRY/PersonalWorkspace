using System.Text.RegularExpressions;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

public sealed partial class AssetPaths(IApplicationPaths paths)
{
    [GeneratedRegex(@"^[a-f0-9]{2}/[a-f0-9]{32}\.[a-z0-9]{1,10}(\.import)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StoredName();
    public static string FileName(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ContentValidationException("Choose a file with a valid display filename.");
        return name;
    }
    public static string Relative(Guid id, string originalName)
    {
        if (id == Guid.Empty) throw new ContentValidationException("An Asset identity is required.");
        var extension = Path.GetExtension(originalName).TrimStart('.').ToLowerInvariant();
        if (extension == "import" || extension.Length is < 1 or > 10 || extension.Any(c => !char.IsAsciiLetterOrDigit(c))) extension = "bin";
        var key = id.ToString("N"); return key[..2] + "/" + key + "." + extension;
    }
    public static void ValidateIdentity(Guid id, string relative)
    {
        if (id == Guid.Empty || relative.EndsWith(".import", StringComparison.Ordinal) || !StoredName().IsMatch(relative) || relative[..2] != id.ToString("N")[..2] || relative.Substring(3, 32) != id.ToString("N"))
            throw new ContentValidationException("The stored Asset path does not match its identity.");
    }
    public string Resolve(Guid profile, string relative)
    {
        if (!StoredName().IsMatch(relative) || relative[..2] != relative.Substring(3, 2)) throw new ContentValidationException("Invalid relative Asset storage path.");
        var root = Path.GetFullPath(paths.ProfileAttachments(profile));
        var expected = Path.GetFullPath(Path.Combine(paths.ProfileDirectory(profile), "attachments"));
        if (!string.Equals(root, expected, StringComparison.OrdinalIgnoreCase)) throw new ContentValidationException("Invalid Profile attachment directory.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ContentValidationException("Asset path escapes Profile storage.");
        RejectLinks(full); return full;
    }
    // Walk every existing component, including dangling links and ancestors of the application root.
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ContentValidationException("Asset paths must not contain symbolic links or junctions."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
