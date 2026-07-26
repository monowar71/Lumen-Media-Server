using System.Runtime.InteropServices;

namespace LumenMedia.Application.Common;

/// <summary>
/// Realpath + prefix checks that block path traversal and symlink escapes
/// (library download/delete/playback and artwork store).
/// </summary>
public static class PathSafety
{
    public static bool TryResolveUnderRoots(
        string path,
        IEnumerable<string> roots,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = ResolveRealPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        return IsUnderAnyRoot(fullPath, roots);
    }

    public static bool IsUnderAnyRoot(string fullPath, IEnumerable<string> roots) =>
        roots.Any(root =>
        {
            try
            {
                return IsUnderRoot(fullPath, ResolveRealPath(root));
            }
            catch (Exception)
            {
                return false;
            }
        });

    /// <summary>
    /// Canonicalizes a path by resolving symlinks in every component (realpath semantics —
    /// <see cref="Path.GetFullPath(string)"/> only normalizes lexically).
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
                continue;

            try
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    current = target.FullName;
            }
            catch (IOException)
            {
                // Broken or cyclic link — keep unresolved; subsequent Exists/prefix checks reject it.
            }
        }

        return current;
    }

    public static bool IsUnderRoot(string fullPath, string root)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, comparison)
               || string.Equals(fullPath, root, comparison);
    }
}
