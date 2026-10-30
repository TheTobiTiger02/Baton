using Baton.Protocol;

namespace Baton.Host.Media;

/// <summary>
/// The folders this PC lets its phones browse, read-only. Nothing outside them can be listed or
/// fetched: paths are made absolute first, and a path through a link (a junction or a symbolic
/// link) is refused, since it could lead out. Hidden and system files are left out.
/// </summary>
public sealed class SharedFolders
{
    /// <summary>The most entries one listing sends; a bigger folder says it was cut short.</summary>
    public const int MaxEntries = 2000;

    private IReadOnlyList<string> _roots = [];

    /// <summary>The shared folders, as absolute paths.</summary>
    public IReadOnlyList<string> Roots
    {
        get => _roots;
        set => _roots = value.Where(Path.IsPathFullyQualified).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>What is in <paramref name="path"/>; the shared folders themselves when it is null.</summary>
    public FolderListingPayload List(string requestId, string? path)
    {
        if (path is null)
        {
            var shared = Roots.Where(Directory.Exists)
                .Select(root => new FolderEntry(DisplayName(root), root, true, 0, Directory.GetLastWriteTimeUtc(root)))
                .ToArray();
            return new FolderListingPayload(requestId, null, shared,
                shared.Length == 0 ? "No folders are shared. Choose some on your PC: Baton, Settings, Shared folders." : null);
        }

        if (Resolve(path) is not { } folder || !Directory.Exists(folder))
        {
            return new FolderListingPayload(requestId, path, [], "This folder isn't shared, or it's gone.");
        }

        try
        {
            var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(Visible)
                .Take(MaxEntries + 1)
                .Select(info => new FolderEntry(info.Name, info.FullName, info is DirectoryInfo,
                    info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc))
                .ToList();
            var truncated = entries.Count > MaxEntries;
            var sorted = entries.Take(MaxEntries)
                .OrderByDescending(entry => entry.Folder)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return new FolderListingPayload(requestId, folder, sorted, Truncated: truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderListingPayload(requestId, folder, [], "Windows didn't let Baton read this folder.");
        }
    }

    /// <summary>A file inside a shared folder, as an absolute path; null for anything else.</summary>
    public string? ResolveFile(string path) => Resolve(path) is { } file && File.Exists(file) ? file : null;

    /// <summary>
    /// <paramref name="path"/> made absolute, when it is a shared folder or inside one and no part
    /// of the way there is a link; null otherwise.
    /// </summary>
    public string? Resolve(string path)
    {
        // Only plain drive or network paths: no "\\?\" device paths, no ":stream" after the drive.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
        {
            return null;
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var root = Roots.FirstOrDefault(root => IsWithin(root, full));
        if (root is null)
        {
            return null;
        }

        // Each step below the shared folder must be a plain file or folder.
        for (var step = full; step.Length > root.Length; step = Path.GetDirectoryName(step)!)
        {
            try
            {
                if (File.GetAttributes(step) is var attributes && (attributes.HasFlag(FileAttributes.ReparsePoint) || !Visible(attributes)))
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return full;
    }

    /// <summary>The same as or inside <paramref name="root"/>; both already normalized.</summary>
    public static bool IsWithin(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Documents", or "D:" for a whole drive.</summary>
    public static string DisplayName(string root) => Path.GetFileName(root) is { Length: > 0 } name ? name : root.TrimEnd(Path.DirectorySeparatorChar);

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > Path.GetPathRoot(full)!.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    private static bool Visible(FileSystemInfo info) => !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && Visible(info.Attributes);

    private static bool Visible(FileAttributes attributes) => !attributes.HasFlag(FileAttributes.Hidden) && !attributes.HasFlag(FileAttributes.System);
}
