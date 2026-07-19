namespace LumenMedia.Infrastructure.Scanning;

/// <summary>Pure helpers for library auto-scan path filtering (unit-tested).</summary>
public static class LibraryAutoScanRules
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".wmv", ".flv", ".m4v",
    };

    private static readonly string[] IncompleteSuffixes =
    [
        ".part", ".!qb", ".tmp", ".temp", ".download", ".crdownload", ".partial", ".aria2",
    ];

    public static bool IsVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name is "." or "..")
            return false;
        if (IsIncompleteName(name))
            return false;
        var ext = Path.GetExtension(name);
        return VideoExtensions.Contains(ext);
    }

    public static bool IsIncompleteName(string fileName)
    {
        foreach (var suffix in IncompleteSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Libraries whose configured roots contain <paramref name="fullPath"/>
    /// (file or directory under the root).
    /// </summary>
    public static IReadOnlyList<Guid> LibrariesForPath(
        string fullPath,
        IReadOnlyList<(Guid LibraryId, IReadOnlyList<string> Roots)> libraries)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(fullPath);
        }
        catch
        {
            return [];
        }

        var matches = new List<Guid>();
        foreach (var (libraryId, roots) in libraries)
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                string rootFull;
                try
                {
                    rootFull = Path.GetFullPath(root);
                }
                catch
                {
                    continue;
                }

                var normalizedRoot = rootFull.EndsWith(Path.DirectorySeparatorChar)
                    ? rootFull
                    : rootFull + Path.DirectorySeparatorChar;
                if (resolved.StartsWith(normalizedRoot, StringComparison.Ordinal)
                    || string.Equals(resolved, rootFull, StringComparison.Ordinal))
                {
                    matches.Add(libraryId);
                    break;
                }
            }
        }

        return matches;
    }
}
