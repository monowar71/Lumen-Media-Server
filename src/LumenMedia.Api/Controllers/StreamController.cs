using System.Security.Claims;
using LumenMedia.Api.Auth;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Configuration;
using LumenMedia.Infrastructure.Transcoding;
using Microsoft.AspNetCore.Authorization;
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
    IOptions<PathsOptions> paths,
    TimeProvider clock) : ControllerBase
{
    private const string HlsContentType = "application/vnd.apple.mpegurl";
    private static readonly TimeSpan PlaylistWait = TimeSpan.FromSeconds(45);
    /// <summary>Wait for the first encoded segment. Keep short enough that hls.js
    /// can reload the playlist on stall, but long enough for VAAPI cold-start.</summary>
    private static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Reserved path segment for DirectPlay — must not be treated as an HLS fragment name.
    /// </summary>
    private const string DirectPlaySegment = "source";

    [AllowAnonymous]
    [HttpGet("stream/{sessionId}/master.m3u8")]
    public async Task<IActionResult> Master(string sessionId, CancellationToken ct)
    {
        var session = ResolveStreamSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);
        transcoder.NotifyPlaybackActive(sessionId);

        var file = SegmentPath(sessionId, "master.m3u8");
        if (file is null)
            return NotFound();

        if (!await WaitForFileAsync(file, PlaylistWait, ct))
            return NotFound();

        var bytes = await StableFileSnapshot.ReadAsync(file, TimeSpan.FromSeconds(2), ct);
        if (bytes is null)
            return NotFound();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return File(bytes, HlsContentType);
    }

    [AllowAnonymous]
    [HttpGet("stream/{sessionId}/index.m3u8")]
    public async Task<IActionResult> Index(string sessionId, CancellationToken ct)
    {
        var session = ResolveStreamSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);
        transcoder.NotifyPlaybackActive(sessionId);

        var file = SegmentPath(sessionId, "index.m3u8");
        if (file is null)
            return NotFound();

        // Wait until ffmpeg has written at least one media segment reference.
        if (!await WaitForPlaylistReadyAsync(file, PlaylistWait, ct))
            return NotFound();

        // Snapshot so Content-Length matches body even if ffmpeg rewrites the playlist.
        var bytes = await StableFileSnapshot.ReadAsync(file, TimeSpan.FromSeconds(2), ct);
        if (bytes is null)
            return NotFound();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return File(bytes, HlsContentType);
    }

    /// <summary>
    /// DirectPlay media for a playback session. Auth is the unguessable session id
    /// (capability URL) so Android / native players survive JWT access-token expiry.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("stream/{sessionId}/source")]
    public async Task<IActionResult> Source(string sessionId, CancellationToken ct)
    {
        var session = ResolveStreamSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);

        var source = await uow.Media.GetSourceByIdAsync(session.MediaSourceId, ct);
        if (source is null)
            return NotFound();

        var roots = await ResolveLibraryRootsAsync(source, ct);
        var fullPath = PathSafety.ResolveRealPath(source.Path);
        if (!System.IO.File.Exists(fullPath) || !PathSafety.IsUnderAnyRoot(fullPath, roots))
            return NotFound();

        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"media.{source.Container ?? "mkv"}";

        return PhysicalFile(
            fullPath,
            ContentTypeForContainer(source.Container ?? "mkv"),
            fileDownloadName: fileName,
            enableRangeProcessing: true);
    }

    [AllowAnonymous]
    [HttpGet("stream/{sessionId}/{segment}")]
    public async Task<IActionResult> Segment(string sessionId, string segment, CancellationToken ct)
    {
        if (string.Equals(segment, DirectPlaySegment, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var session = ResolveStreamSession(sessionId);
        if (session is null)
            return NotFound();

        playback.TouchSession(sessionId);
        transcoder.NotifySegmentRequested(sessionId, segment);

        var file = SegmentPath(sessionId, segment);
        if (file is null)
            return NotFound();

        // Stabilize + byte snapshot: PhysicalFile sets Content-Length from FileInfo, then
        // ffmpeg can append → "too many bytes written" and a broken HLS fragment.
        var bytes = await StableFileSnapshot.ReadAsync(file, SegmentWait, ct);
        if (bytes is null)
            return NotFound();

        var contentType = segment.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            ? "video/iso.segment"
            : "video/mp4";
        Response.Headers.CacheControl = "no-store";
        return File(bytes, contentType);
    }

    [HttpGet("items/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] Guid? sourceId = null, CancellationToken ct = default)
    {
        var caller = User.ToCaller();
        var source = await playback.GetPlayableSourceAsync(caller, id, sourceId, ct);

        var roots = await ResolveLibraryRootsAsync(source, ct);
        var fullPath = PathSafety.ResolveRealPath(source.Path);
        if (!System.IO.File.Exists(fullPath) || !PathSafety.IsUnderAnyRoot(fullPath, roots))
            return NotFound();

        // Without fileDownloadName browsers name the file after the URL segment ("download.mkv").
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"media.{source.Container ?? "mkv"}";

        return PhysicalFile(
            fullPath,
            ContentTypeForContainer(source.Container ?? "mkv"),
            fileDownloadName: fileName,
            enableRangeProcessing: true);
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
            var full = PathSafety.ResolveRealPath(stream.ExternalPath);
            if (!System.IO.File.Exists(full) || !PathSafety.IsUnderAnyRoot(full, roots))
                return NotFound();
        }

        var vtt = await subtitles.ToWebVttAsync(source, stream, ct);
        if (vtt is null)
            return NotFound();

        Response.Headers.CacheControl = "private, max-age=3600";
        return Content(vtt, "text/vtt");
    }

    /// <summary>
    /// Resolves a live playback session for media delivery.
    /// Possession of the unguessable <paramref name="sessionId"/> is sufficient (capability URL)
    /// so ExoPlayer / native HLS can keep requesting segments after the access JWT expires.
    /// When a valid authenticated caller is present, ownership is still enforced.
    /// </summary>
    private PlaybackSession? ResolveStreamSession(string sessionId)
    {
        var session = sessions.Get(sessionId);
        if (session is null)
            return null;

        if (session.ExpiresAt <= clock.GetUtcNow())
            return null;

        if (User.Identity?.IsAuthenticated == true)
        {
            var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(sub, out var userId) || session.UserId != userId)
                return null;
        }

        return session;
    }

    /// <summary>Resolves a segment file inside the session's transcode dir, blocking path traversal.</summary>
    private string? SegmentPath(string sessionId, string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..") || Path.IsPathRooted(fileName))
            return null;

        var dir = Path.GetFullPath(Path.Combine(paths.Value.Transcodes, sessionId));
        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        return PathSafety.IsUnderRoot(full, dir) ? full : null;
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
