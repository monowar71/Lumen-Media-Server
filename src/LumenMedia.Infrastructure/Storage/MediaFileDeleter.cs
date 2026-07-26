using Microsoft.Extensions.Logging;
using LumenMedia.Application.Common;

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

        if (!PathSafety.TryResolveUnderRoots(path, libraryRoots, out var fullPath))
        {
            logger.LogWarning("Refusing to delete {Path}: outside library roots or unresolvable", path);
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
}
