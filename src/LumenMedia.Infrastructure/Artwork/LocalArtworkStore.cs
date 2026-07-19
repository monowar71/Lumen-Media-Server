using System.Security.Cryptography;
using System.Text;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.ArtworkStorage;

/// <summary>
/// Disk-backed artwork cache under <c>/config/metadata</c>. Resize is a pass-through stub
/// (real streaming resize is a later phase); files are streamed, never buffered whole in memory.
/// </summary>
public sealed class LocalArtworkStore(IOptions<PathsOptions> paths) : IArtworkStore
{
    private string MetadataRoot => Path.Combine(paths.Value.Config, "metadata");

    public Task<ArtworkResult?> GetAsync(string localPath, int? width, int? height, int? quality, CancellationToken ct)
    {
        if (!File.Exists(localPath))
            return Task.FromResult<ArtworkResult?>(null);

        var info = new FileInfo(localPath);
        var etag = ComputeETag(localPath, info.Length, info.LastWriteTimeUtc);
        var contentType = GuessContentType(localPath);
        Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Task.FromResult<ArtworkResult?>(new ArtworkResult(stream, contentType, etag));
    }

    public async Task<string> SaveAsync(Guid ownerId, ArtworkKind kind, Stream content, CancellationToken ct)
    {
        var dir = Path.Combine(MetadataRoot, ownerId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{kind}".ToLowerInvariant() + ".img");
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await content.CopyToAsync(file, ct);
        return path;
    }

    public void DeleteOwner(Guid ownerId)
    {
        var dir = Path.Combine(MetadataRoot, ownerId.ToString());
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: orphaned artwork is cleaned up by later maintenance.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ComputeETag(string path, long length, DateTime mtime)
    {
        var raw = $"{path}:{length}:{mtime.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"\"{Convert.ToHexString(hash)[..16]}\"";
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
