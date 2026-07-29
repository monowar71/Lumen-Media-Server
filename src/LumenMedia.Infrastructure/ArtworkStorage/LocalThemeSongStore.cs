using System.Security.Cryptography;
using System.Text;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.ArtworkStorage;

/// <summary>
/// Theme MP3 cache under <c>/config/metadata/{itemId}/theme.mp3</c>
/// (same root as artwork so <see cref="IArtworkStore.DeleteOwner"/> cleans both).
/// </summary>
public sealed class LocalThemeSongStore(IOptions<PathsOptions> paths) : IThemeSongStore
{
    private string MetadataRoot => Path.Combine(paths.Value.Config, "metadata");

    public bool Exists(Guid itemId)
    {
        var path = ThemePath(itemId);
        return File.Exists(path);
    }

    public async Task SaveAsync(Guid itemId, Stream mp3Content, CancellationToken ct)
    {
        var dir = Path.Combine(MetadataRoot, itemId.ToString());
        Directory.CreateDirectory(dir);
        var path = ThemePath(itemId);
        var temp = path + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await mp3Content.CopyToAsync(file, ct);
        }

        File.Move(temp, path, overwrite: true);
    }

    public Task<ThemeSongResult?> OpenAsync(Guid itemId, CancellationToken ct)
    {
        var path = ThemePath(itemId);
        if (!PathSafety.TryResolveUnderRoots(path, [MetadataRoot], out var fullPath) || !File.Exists(fullPath))
            return Task.FromResult<ThemeSongResult?>(null);

        var info = new FileInfo(fullPath);
        var etag = ComputeETag(fullPath, info.Length, info.LastWriteTimeUtc);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Task.FromResult<ThemeSongResult?>(new ThemeSongResult(stream, "audio/mpeg", etag));
    }

    public void Delete(Guid itemId)
    {
        var path = ThemePath(itemId);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ThemePath(Guid itemId) =>
        Path.Combine(MetadataRoot, itemId.ToString(), "theme.mp3");

    private static string ComputeETag(string path, long length, DateTime mtime)
    {
        var raw = $"{path}:{length}:{mtime.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"\"{Convert.ToHexString(hash)[..16]}\"";
    }
}
