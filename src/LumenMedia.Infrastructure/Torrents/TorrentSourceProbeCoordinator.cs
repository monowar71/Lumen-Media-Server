using System.Collections.Concurrent;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Torrents;

/// <summary>
/// After torrent playback starts, probes the TorrServer play URL once enough header data
/// is available, persists streams, and attaches <see cref="ProbedFormatDto"/> to the session.
/// </summary>
public sealed class TorrentSourceProbeCoordinator(
    IServiceScopeFactory scopeFactory,
    IPlaybackSessionStore sessions,
    IMediaProbe mediaProbe,
    ILogger<TorrentSourceProbeCoordinator> logger) : ITorrentSourceProbeCoordinator
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2.5);
    private const int MaxAttempts = 4;

    private readonly ConcurrentDictionary<Guid, byte> _inflight = new();

    public void ScheduleIfNeeded(string sessionId, Guid mediaSourceId, string playUrl, bool needsProbe)
    {
        if (!needsProbe || string.IsNullOrWhiteSpace(playUrl))
            return;
        if (!_inflight.TryAdd(mediaSourceId, 0))
            return;

        logger.LogDebug(
            "Scheduling torrent play-time probe for source {SourceId} session {SessionId}",
            mediaSourceId,
            sessionId);
        _ = Task.Run(() => RunAsync(mediaSourceId, playUrl));
    }

    private async Task RunAsync(Guid mediaSourceId, string playUrl)
    {
        try
        {
            await Task.Delay(InitialDelay).ConfigureAwait(false);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                MediaProbeResult? probe;
                try
                {
                    probe = await mediaProbe.ProbeAsync(playUrl, attemptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    probe = null;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Torrent ffprobe attempt {Attempt} failed for {SourceId}", attempt + 1, mediaSourceId);
                    probe = null;
                }

                if (probe is not null && HasUsableVideo(probe))
                {
                    await PersistAndAttachAsync(mediaSourceId, probe).ConfigureAwait(false);
                    return;
                }

                var backoff = TimeSpan.FromSeconds(2 * (attempt + 1));
                await Task.Delay(backoff).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Torrent play-time probe did not yield codecs for source {SourceId} after {Attempts} attempts",
                mediaSourceId,
                MaxAttempts);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Torrent play-time probe aborted for source {SourceId}", mediaSourceId);
        }
        finally
        {
            _inflight.TryRemove(mediaSourceId, out _);
        }
    }

    private async Task PersistAndAttachAsync(Guid mediaSourceId, MediaProbeResult probe)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var source = await uow.Media.GetTrackedSourceByIdWithStreamsAsync(mediaSourceId, CancellationToken.None)
            .ConfigureAwait(false);
        if (source is null)
            return;

        // Another play may have filled codecs already.
        if (!source.NeedsStreamProbe())
        {
            AttachToSessions(mediaSourceId, MediaMapper.MapProbedFormat(source.Streams));
            return;
        }

        var previous = source.Streams.ToList();
        if (previous.Count > 0)
            uow.Media.RemoveStreams(previous);

        var mapped = probe.Streams.Select(MapStream).ToList();
        source.ReplaceStreams(mapped);
        source.SetProbeInfo(probe.DurationMs, probe.OverallBitrateKbps);
        await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        var format = MediaMapper.MapProbedFormat(mapped);
        AttachToSessions(mediaSourceId, format);
        logger.LogInformation(
            "Torrent probe stored for {SourceId}: video={Codec} {Width}x{Height} hdr={Hdr}",
            mediaSourceId,
            format?.VideoCodec,
            format?.Width,
            format?.Height,
            format?.VideoHdr);
    }

    private void AttachToSessions(Guid mediaSourceId, ProbedFormatDto? format)
    {
        if (format is null)
            return;
        foreach (var session in sessions.ActiveSessions.Where(s => s.MediaSourceId == mediaSourceId))
            session.ProbedFormat = format;
    }

    private static bool HasUsableVideo(MediaProbeResult probe) =>
        probe.Streams.Any(s =>
            s.Kind == StreamKind.Video
            && !string.IsNullOrWhiteSpace(s.Codec)
            && !s.Codec.Equals("unknown", StringComparison.OrdinalIgnoreCase));

    private static MediaStream MapStream(ProbedMediaStream s)
    {
        var stream = new MediaStream(s.Kind, s.StreamIndex)
        {
            Codec = s.Codec,
            Profile = s.Profile,
            Language = s.Language,
            Title = s.Title,
            IsDefault = s.IsDefault,
            IsForced = s.IsForced,
            Width = s.Width,
            Height = s.Height,
            Channels = s.Channels,
            Hdr = s.Hdr,
            SubtitleFormat = s.SubtitleFormat ?? (s.Kind == StreamKind.Subtitle ? s.Codec : null),
        };
        return stream;
    }
}
