using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Storage;

/// <summary>
/// Deletes media files only when the resolved real path stays under a library root
/// (blocks path traversal / symlink escape — same rules as StreamController download).
/// </summary>
public sealed class MediaFileDeleter(ILogger<MediaFileDeleter> logger) : Application.Abstractions.IMediaFileDeleter
{
    public bool TryDelete(string path, IReadOnlyList<string> libraryRoots)
    {
        if (string.IsNullOrWhiteSpace(path) || libraryRoots.Count == 0)
            return false;

        string fullPath;
        try
        {
            fullPath = ResolveRealPath(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve media path {Path}", path);
            return false;
        }

        if (!IsUnderAnyRoot(fullPath, libraryRoots))
        {
            logger.LogWarning(
                "Refusing to delete {Path}: outside library roots",
                fullPath);
            return false;
        }

        if (!File.Exists(fullPath))
            return false;

        try
        {
            File.Delete(fullPath);
            logger.LogInformation("Deleted media file {Path}", fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete media file {Path}", fullPath);
            return false;
        }
    }

    private static bool IsUnderAnyRoot(string fullPath, IEnumerable<string> roots) =>
        roots.Any(root => IsUnderRoot(fullPath, ResolveRealPath(root)));

    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
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
            }
        }

        return current;
    }

    private static bool IsUnderRoot(string fullPath, string root)
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
