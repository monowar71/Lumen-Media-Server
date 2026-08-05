using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Abstractions;

/// <summary>Ephemeral, in-memory playback session (not persisted; see database.md §3.13).</summary>
public sealed class PlaybackSession
{
    public required string SessionId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid MediaId { get; init; }
    public required Guid MediaSourceId { get; init; }
    public required string SourcePath { get; init; }
    public required string Container { get; init; }
    public PlaybackMethod Method { get; set; }
    public PlaybackMode Mode { get; set; }
    public string SelectedQualityId { get; set; } = "auto";
    public long StartPositionMs { get; set; }
    /// <summary>ffprobe absolute stream index for the selected audio track (null = first audio).</summary>
    public int? AudioStreamIndex { get; set; }
    /// <summary>ffprobe absolute stream index for burn-in subtitle (bitmap); text uses sidecar.</summary>
    public int? SubtitleBurnInIndex { get; set; }
    /// <summary>Client device profile from the original decision — reused on set-quality/seek.</summary>
    public DeviceProfile Profile { get; set; } = new();
    /// <summary>Last PlaybackDecider reason (drives copy vs encode in ffmpeg).</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>Force HDR→SDR tonemap for this session.</summary>
    public bool ForceHdrToSdr { get; set; }
    /// <summary>Selected HDR→SDR method id (<c>vaapi</c>, <c>hable</c>, …); sticky across set-quality.</summary>
    public string? HdrToneMapMethod { get; set; }
    /// <summary>Selected channel layout id (stereo, 2.1, 5.1, mono).</summary>
    public string AudioLayout { get; set; } = "stereo";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Last playlist/segment/ping access — used for idle cleanup and throttle.</summary>
    public DateTimeOffset LastAccess { get; set; }
    /// <summary>True when this session holds a TorrServer process lease (torrent sources).</summary>
    public bool HoldsTorrServerLease { get; set; }
    /// <summary>Infohash for live TorrServer stats while the session is active.</summary>
    public string? TorrentInfoHash { get; set; }
    /// <summary>Codecs discovered by play-time ffprobe (torrent); surfaced via ping for HUD.</summary>
    public ProbedFormatDto? ProbedFormat { get; set; }
}

public interface IPlaybackSessionStore
{
    PlaybackSession Create(PlaybackSession session);
    PlaybackSession? Get(string sessionId);
    void Touch(string sessionId, DateTimeOffset newExpiry);
    void Remove(string sessionId);
    IReadOnlyCollection<PlaybackSession> ActiveSessions { get; }
}

public sealed record TranscodeRequest
{
    public required PlaybackSession Session { get; init; }
    public required string QualityId { get; init; }
    public long StartPositionMs { get; init; }
    /// <summary>Decision reason from PlaybackDecider (drives copy vs encode).</summary>
    public string Reason { get; init; } = string.Empty;
    /// <summary>Absolute container stream index for audio; null → first audio (<c>0:a:0</c>).</summary>
    public int? AudioStreamIndex { get; init; }
    /// <summary>Absolute container stream index for bitmap subtitle burn-in.</summary>
    public int? SubtitleBurnInIndex { get; init; }
    /// <summary>Source video frame size — used to clamp ladder scales (no upscale).</summary>
    public int? SourceWidth { get; init; }
    public int? SourceHeight { get; init; }
    /// <summary>
    /// Device-profile max height (e.g. 1080). Applied to <c>auto</c>/<c>original</c> so
    /// ResolutionTooHigh does not re-encode full 4K when the client cannot Direct Play it.
    /// </summary>
    public int? MaxOutputHeight { get; init; }
    /// <summary>Apply HDR→SDR tonemap filters.</summary>
    public bool ToneMap { get; init; }
    /// <summary>HDR→SDR method id (<c>vaapi</c> = GPU VPP; otherwise software algorithm).</summary>
    public string? HdrToneMapMethod { get; init; }
    /// <summary>Target audio channel layout id for AAC encode.</summary>
    public string AudioLayout { get; init; } = "stereo";
}

public interface ITranscoder
{
    Task StartAsync(TranscodeRequest request, CancellationToken ct);
    Task StopAsync(string sessionId, CancellationToken ct);
    /// <summary>Player requested a media segment — used to resume a throttled ffmpeg.</summary>
    void NotifySegmentRequested(string sessionId, string segmentFileName);
    /// <summary>Player hit the playlist (or init) — resume throttle without advancing the segment cursor.</summary>
    void NotifyPlaybackActive(string sessionId);
    int ActiveSessionCount { get; }
}
