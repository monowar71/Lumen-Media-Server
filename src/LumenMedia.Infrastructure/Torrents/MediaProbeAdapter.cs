using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Scanning;

namespace LumenMedia.Infrastructure.Torrents;

/// <summary>Adapts <see cref="FfprobeClient"/> to the application <see cref="IMediaProbe"/> port.</summary>
public sealed class MediaProbeAdapter(FfprobeClient ffprobe) : IMediaProbe
{
    public async Task<MediaProbeResult?> ProbeAsync(string pathOrUrl, CancellationToken ct)
    {
        var result = await ffprobe.ProbeAsync(pathOrUrl, ct);
        if (result is null)
            return null;

        var streams = result.Streams.Select(s => new ProbedMediaStream(
            s.Kind,
            s.StreamIndex,
            s.Codec,
            s.Profile,
            s.Language,
            s.Title,
            s.IsDefault,
            s.IsForced,
            s.Width,
            s.Height,
            s.Channels,
            s.Hdr,
            s.SubtitleFormat)).ToList();

        return new MediaProbeResult(result.DurationMs, result.OverallBitrateKbps, streams);
    }
}
