using LumenMedia.Api.Auth;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class StreamController(
    PlaybackService playback,
    IPlaybackSessionStore sessions,
    IUnitOfWork uow,
    ITranscoder transcoder,
    ISubtitleConverter subtitles,
    IOptions<PathsOptions> paths) : ControllerBase
{
    private const string HlsContentType = "application/vnd.apple.mpegurl";
    private static readonly TimeSpan PlaylistWait = TimeSpan.FromSeconds(45);
    /// <summary>Short wait — if the segment is not ready, fail fast so hls.js
    /// reloads the playlist instead of hanging the buffer for a minute.</summary>
    private static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(3);

    [HttpGet("stream/{sessionId}/master.m3u8")]
    public async Task<IActionResult> Master(string sessionId, CancellationToken ct)
    {
        var session = GetOwnedSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);

        var file = SegmentPath(sessionId, "master.m3u8");
        if (file is null)
            return NotFound();

        if (!await WaitForFileAsync(file, PlaylistWait, ct))
            return NotFound();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return PhysicalFile(file, HlsContentType);
    }

    [HttpGet("stream/{sessionId}/index.m3u8")]
    public async Task<IActionResult> Index(string sessionId, CancellationToken ct)
    {
        var session = GetOwnedSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);

        var file = SegmentPath(sessionId, "index.m3u8");
        if (file is null)
            return NotFound();

        // Wait until ffmpeg has written at least one media segment reference.
        if (!await WaitForPlaylistReadyAsync(file, PlaylistWait, ct))
            return NotFound();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return PhysicalFile(file, HlsContentType);
    }

    [HttpGet("stream/{sessionId}/{segment}")]
    public async Task<IActionResult> Segment(string sessionId, string segment, CancellationToken ct)
    {
        var session = GetOwnedSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);
        transcoder.NotifySegmentRequested(sessionId, segment);

        var file = SegmentPath(sessionId, segment);
        if (file is null)
            return NotFound();

        // Wait until size stops growing so we never serve a half-written fMP4 fragment
        // (incomplete moof/mdat → MSE append errors → hls.js reload loops).
        if (!await WaitForStableFileAsync(file, SegmentWait, ct))
            return NotFound();

        var contentType = segment.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            ? "video/iso.segment"
            : "video/mp4";
        Response.Headers.CacheControl = "no-store";
        // Full-file responses only — range GETs on a just-finalized m4s are a common
        // source of truncated fragments under Docker bind mounts.
        return PhysicalFile(file, contentType, enableRangeProcessing: false);
    }

    [HttpGet("items/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] Guid? sourceId = null, CancellationToken ct = default)
    {
        var caller = User.ToCaller();
        var source = await playback.GetPlayableSourceAsync(caller, id, sourceId, ct);

        var roots = await ResolveLibraryRootsAsync(source, ct);
        var fullPath = ResolveRealPath(source.Path);
        if (!System.IO.File.Exists(fullPath) || !IsUnderAnyRoot(fullPath, roots))
            return NotFound();

        return PhysicalFile(fullPath, ContentTypeForContainer(source.Container), enableRangeProcessing: true);
    }

    [HttpGet("items/{id:guid}/subtitles/{streamId}.vtt")]
    public async Task<IActionResult> Subtitles(Guid id, string streamId, CancellationToken ct)
    {
        if (!Guid.TryParse(streamId, out var streamGuid))
            return NotFound();

        var caller = User.ToCaller();
        var source = await playback.GetPlayableSourceAsync(caller, id, sourceId: null, ct);
        var stream = source.Streams.FirstOrDefault(s => s.Id == streamGuid);
        if (stream is null)
            return NotFound();

        if (stream.IsExternal && !string.IsNullOrWhiteSpace(stream.ExternalPath))
        {
            var roots = await ResolveLibraryRootsAsync(source, ct);
            var full = ResolveRealPath(stream.ExternalPath);
            if (!System.IO.File.Exists(full) || !IsUnderAnyRoot(full, roots))
                return NotFound();
        }

        var vtt = await subtitles.ToWebVttAsync(source, stream, ct);
        if (vtt is null)
            return NotFound();

        Response.Headers.CacheControl = "private, max-age=3600";
        return Content(vtt, "text/vtt");
    }

    private PlaybackSession? GetOwnedSession(string sessionId)
    {
        var session = sessions.Get(sessionId);
        return session is not null && session.UserId == User.GetUserId() ? session : null;
    }

    /// <summary>Resolves a segment file inside the session's transcode dir, blocking path traversal.</summary>
    private string? SegmentPath(string sessionId, string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..") || Path.IsPathRooted(fileName))
            return null;

        var dir = Path.GetFullPath(Path.Combine(paths.Value.Transcodes, sessionId));
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        return IsUnderRoot(full, dir) ? full : null;
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (System.IO.File.Exists(path) && new FileInfo(path).Length > 0)
                return true;
            await Task.Delay(50, ct);
        }

        return System.IO.File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>
    /// Like <see cref="WaitForFileAsync"/> but requires two consecutive identical
    /// non-zero sizes (~100ms apart) so ffmpeg has finished the fragment.
    /// </summary>
    private static async Task<bool> WaitForStableFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastLen = -1;
        var stableCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (System.IO.File.Exists(path))
            {
                long len;
                try
                {
                    len = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    len = -1;
                }

                if (len > 0 && len == lastLen)
                {
                    stableCount++;
                    if (stableCount >= 2)
                        return true;
                }
                else
                {
                    stableCount = 0;
                    lastLen = len;
                }
            }

            await Task.Delay(50, ct);
        }

        try
        {
            return System.IO.File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForPlaylistReadyAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var text = await System.IO.File.ReadAllTextAsync(path, ct);
                    // Ready once ffmpeg listed a media segment (init alone is not enough to play).
                    if (text.Contains(".m4s", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("#EXTINF", StringComparison.Ordinal))
                        return true;
                }
                catch (IOException)
                {
                    // File still being written.
                }
            }

            await Task.Delay(50, ct);
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> ResolveLibraryRootsAsync(MediaSource source, CancellationToken ct)
    {
        Guid? libraryId = null;
        if (source.MediaItemId is not null)
            libraryId = (await uow.Media.GetByIdAsync(source.MediaItemId.Value, ct))?.LibraryId;
        else if (source.EpisodeId is not null)
        {
            var episode = await uow.Media.GetEpisodeAsync(source.EpisodeId.Value, ct);
            if (episode is not null)
                libraryId = (await uow.Media.GetByIdAsync(episode.SeriesId, ct))?.LibraryId;
        }

        if (libraryId is null)
            return [];
        var library = await uow.Libraries.GetByIdAsync(libraryId.Value, ct);
        return library?.Paths.Select(p => p.Path).ToList() ?? [];
    }

    private static bool IsUnderAnyRoot(string fullPath, IEnumerable<string> roots) =>
        roots.Any(root => IsUnderRoot(fullPath, ResolveRealPath(root)));

    /// <summary>
    /// Canonicalizes a path by resolving symlinks in every component (realpath semantics —
    /// <see cref="Path.GetFullPath(string)"/> only normalizes lexically). A link planted inside
    /// a library (e.g. from a torrent) therefore cannot escape the root prefix check. Library
    /// roots are canonicalized the same way, so setups where the root itself is a symlink
    /// (or files legitimately link within the library) keep working.
    /// </summary>
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
                // Broken or cyclic link chain — keep the unresolved component; the
                // subsequent File.Exists / prefix check will reject it.
            }
        }

        return current;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal)
               || string.Equals(fullPath, root, StringComparison.Ordinal);
    }

    private static string ContentTypeForContainer(string container) => container.ToLowerInvariant() switch
    {
        "mkv" => "video/x-matroska",
        "mp4" or "m4v" => "video/mp4",
        "webm" => "video/webm",
        "avi" => "video/x-msvideo",
        "ts" => "video/mp2t",
        _ => "application/octet-stream",
    };
}
