using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Abstractions;

/// <summary>Result of probing a local file or HTTP URL with ffprobe.</summary>
public sealed record MediaProbeResult(
    long? DurationMs,
    int? OverallBitrateKbps,
    IReadOnlyList<ProbedMediaStream> Streams);

public sealed record ProbedMediaStream(
    StreamKind Kind,
    int StreamIndex,
    string? Codec,
    string? Profile,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    int? Width,
    int? Height,
    int? Channels,
    string? Hdr,
    string? SubtitleFormat);

/// <summary>ffprobe wrapper — path may be a local file or an HTTP(S) URL (TorrServer play).</summary>
public interface IMediaProbe
{
    Task<MediaProbeResult?> ProbeAsync(string pathOrUrl, CancellationToken ct);
}

/// <summary>
/// Background probe of torrent play URLs after playback starts.
/// Persists streams to the media source and attaches a snapshot to the live session for ping.
/// </summary>
public interface ITorrentSourceProbeCoordinator
{
    /// <summary>
    /// Schedules a non-blocking probe when the source still lacks real codecs.
    /// Safe to call multiple times for the same source (coalesced).
    /// </summary>
    void ScheduleIfNeeded(string sessionId, Guid mediaSourceId, string playUrl, bool needsProbe);
}
