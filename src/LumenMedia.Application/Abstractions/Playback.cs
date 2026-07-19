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
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Last playlist/segment/ping access — used for idle cleanup and throttle.</summary>
    public DateTimeOffset LastAccess { get; set; }
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
}

public interface ITranscoder
{
    Task StartAsync(TranscodeRequest request, CancellationToken ct);
    Task StopAsync(string sessionId, CancellationToken ct);
    /// <summary>Player requested a media segment — used to resume a throttled ffmpeg.</summary>
    void NotifySegmentRequested(string sessionId, string segmentFileName);
    int ActiveSessionCount { get; }
}
