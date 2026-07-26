using Microsoft.Extensions.Options;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Playback;

public sealed class PlaybackService(
    IUnitOfWork uow,
    PlaybackDecider decider,
    IPlaybackSessionStore sessions,
    ITranscoder transcoder,
    IOptions<PlaybackOptions> options,
    TimeProvider clock,
    IRealtimeNotifier notifier)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> BitmapSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "dvdsub", "vobsub", "xsub",
    };

    public async Task<PlaybackDecisionResponse> CreateDecisionAsync(Caller caller, PlaybackDecisionRequest request, CancellationToken ct)
    {
        var source = await ResolveSourceAsync(caller, request.MediaId, request.MediaSourceId, ct);
        var opts = options.Value;

        if (request.Mode == PlaybackMode.Manual && request.QualityId is null)
            throw new ValidationException("qualityId", "qualityId is required in manual mode.");

        var user = await uow.Users.GetByIdAsync(caller.UserId, ct);
        var userCap = user?.MaxBitrateRemoteKbps;

        var decision = decider.Decide(source, request.Profile, request.Mode, request.QualityId, opts, userCap);
        var (audioIndex, burnInIndex, reasonOverride) = ResolveTracks(source, request.AudioStreamId, request.SubtitleStreamId, decision.Reason);
        if (reasonOverride is not null)
            decision = decision with { Method = PlaybackMethod.Transcode, Reason = reasonOverride };

        // Enforce the concurrent-transcode limit (backpressure → 429).
        // set-quality reuses a session id and does not bump this count.
        if (decision.Method == PlaybackMethod.Transcode && transcoder.ActiveSessionCount >= opts.MaxConcurrentSessions)
            throw new RateLimitException("Maximum number of concurrent transcode sessions reached. Try again later.");

        var now = clock.GetUtcNow();
        // Full GUID: a truncated id (32 bits) is both guessable and collision-prone.
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var session = sessions.Create(new PlaybackSession
        {
            SessionId = sessionId,
            UserId = caller.UserId,
            MediaId = request.MediaId,
            MediaSourceId = source.Id,
            SourcePath = source.Path,
            Container = source.Container,
            Method = decision.Method,
            Mode = request.Mode,
            SelectedQualityId = decision.SelectedQualityId,
            StartPositionMs = request.ResumePositionMs,
            AudioStreamIndex = audioIndex,
            SubtitleBurnInIndex = burnInIndex,
            Profile = request.Profile,
            Reason = decision.Reason,
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
            LastAccess = now,
        });

        if (decision.Method != PlaybackMethod.DirectPlay)
            await StartTranscodeAsync(session, decision.SelectedQualityId, request.ResumePositionMs, decision.Reason, ct);

        await notifier.NotifyNowPlayingAsync(caller.UserId, request.MediaId, decision.Method, sessionId, ct);

        return BuildResponse(session, decision, request.MediaId, source);
    }

    public async Task<PlaybackDecisionResponse> SetQualityAsync(Caller caller, string sessionId, SetQualityRequest request, CancellationToken ct)
    {
        var session = sessions.Get(sessionId)
                      ?? throw new NotFoundException("Playback session not found.");
        if (session.UserId != caller.UserId)
            throw new ForbiddenException("Session belongs to another user.");

        var source = await uow.Media.GetSourceByIdAsync(session.MediaSourceId, ct)
                     ?? throw new NotFoundException("Media source not found.");

        var opts = options.Value;
        var user = await uow.Users.GetByIdAsync(caller.UserId, ct);
        var userCap = user?.MaxBitrateRemoteKbps;
        var decision = decider.Decide(source, session.Profile, request.Mode, request.QualityId, opts, userCap);
        var (audioIndex, burnInIndex, reasonOverride) = ResolveTracks(
            source,
            request.AudioStreamId,
            request.SubtitleStreamId,
            decision.Reason,
            session.AudioStreamIndex,
            session.SubtitleBurnInIndex);
        if (reasonOverride is not null)
            decision = decision with { Method = PlaybackMethod.Transcode, Reason = reasonOverride };

        session.Mode = request.Mode;
        session.SelectedQualityId = decision.SelectedQualityId;
        session.StartPositionMs = request.ResumePositionMs;
        session.Method = decision.Method;
        session.Reason = decision.Reason;
        session.AudioStreamIndex = audioIndex;
        session.SubtitleBurnInIndex = burnInIndex;
        session.LastAccess = clock.GetUtcNow();
        sessions.Touch(sessionId, clock.GetUtcNow() + SessionLifetime);

        if (decision.Method != PlaybackMethod.DirectPlay)
            await StartTranscodeAsync(session, decision.SelectedQualityId, request.ResumePositionMs, decision.Reason, ct);
        else
            await transcoder.StopAsync(sessionId, ct);

        return BuildResponse(session, decision, session.MediaId, source);
    }

    public async Task<PlaybackDecisionResponse> SeekAsync(Caller caller, string sessionId, SeekRequest request, CancellationToken ct)
    {
        var session = sessions.Get(sessionId)
                      ?? throw new NotFoundException("Playback session not found.");
        if (session.UserId != caller.UserId)
            throw new ForbiddenException("Session belongs to another user.");

        if (request.PositionMs < 0)
            throw new ValidationException("positionMs", "positionMs must be >= 0.");

        var source = await uow.Media.GetSourceByIdAsync(session.MediaSourceId, ct)
                     ?? throw new NotFoundException("Media source not found.");

        session.StartPositionMs = request.PositionMs;
        session.LastAccess = clock.GetUtcNow();
        sessions.Touch(sessionId, clock.GetUtcNow() + SessionLifetime);

        if (session.Method != PlaybackMethod.DirectPlay)
            await StartTranscodeAsync(session, session.SelectedQualityId, request.PositionMs, session.Reason, ct);

        var decision = new PlaybackDecisionResult
        {
            Method = session.Method,
            Reason = session.Reason,
            SelectedQualityId = session.SelectedQualityId,
            AvailableQualities = decider.Decide(
                source,
                session.Profile,
                session.Mode,
                session.Mode == PlaybackMode.Manual ? session.SelectedQualityId : null,
                options.Value,
                (await uow.Users.GetByIdAsync(caller.UserId, ct))?.MaxBitrateRemoteKbps).AvailableQualities,
        };

        return BuildResponse(session, decision, session.MediaId, source);
    }

    public Task PingAsync(Caller caller, string sessionId, CancellationToken ct)
    {
        var session = sessions.Get(sessionId);
        if (session is not null && session.UserId == caller.UserId)
        {
            var now = clock.GetUtcNow();
            session.LastAccess = now;
            sessions.Touch(sessionId, now + SessionLifetime);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(Caller caller, string sessionId, CancellationToken ct)
    {
        var session = sessions.Get(sessionId);
        if (session is null || session.UserId != caller.UserId)
            return;
        await transcoder.StopAsync(sessionId, ct);
        sessions.Remove(sessionId);
    }

    /// <summary>Resolves the file to serve for Direct Play download, enforcing access.</summary>
    public async Task<MediaSource> GetPlayableSourceAsync(Caller caller, Guid mediaId, Guid? sourceId, CancellationToken ct) =>
        await ResolveSourceAsync(caller, mediaId, sourceId, ct);

    /// <summary>Extends session lifetime after a successful stream GET.</summary>
    public void TouchSession(string sessionId)
    {
        var now = clock.GetUtcNow();
        var session = sessions.Get(sessionId);
        if (session is null)
            return;
        session.LastAccess = now;
        sessions.Touch(sessionId, now + SessionLifetime);
    }

    private async Task StartTranscodeAsync(
        PlaybackSession session,
        string qualityId,
        long startPositionMs,
        string reason,
        CancellationToken ct)
    {
        var source = await uow.Media.GetSourceByIdAsync(session.MediaSourceId, ct);
        var video = source?.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        await transcoder.StartAsync(
            new TranscodeRequest
            {
                Session = session,
                QualityId = qualityId,
                StartPositionMs = startPositionMs,
                Reason = reason,
                AudioStreamIndex = session.AudioStreamIndex,
                SubtitleBurnInIndex = session.SubtitleBurnInIndex,
                SourceWidth = video?.Width,
                SourceHeight = video?.Height,
            },
            ct);
    }

    private async Task<MediaSource> ResolveSourceAsync(Caller caller, Guid mediaId, Guid? sourceId, CancellationToken ct)
    {
        var source = sourceId is not null
            ? await uow.Media.GetSourceByIdAsync(sourceId.Value, ct)
            : await uow.Media.GetPrimarySourceForMediaAsync(mediaId, ct);

        if (source is null)
            throw new NotFoundException("No playable media source found.");

        await EnsureAccessAsync(caller, source, ct);
        return source;
    }

    private async Task EnsureAccessAsync(Caller caller, MediaSource source, CancellationToken ct)
    {
        Guid? libraryId = null;
        if (source.MediaItemId is not null)
        {
            var item = await uow.Media.GetByIdAsync(source.MediaItemId.Value, ct);
            libraryId = item?.LibraryId;
        }
        else if (source.EpisodeId is not null)
        {
            var episode = await uow.Media.GetEpisodeAsync(source.EpisodeId.Value, ct);
            if (episode is not null)
            {
                var series = await uow.Media.GetByIdAsync(episode.SeriesId, ct);
                libraryId = series?.LibraryId;
            }
        }

        if (libraryId is null || !caller.CanAccess(libraryId.Value))
            throw new NotFoundException("Media not found.");
    }

    private static (int? AudioIndex, int? BurnInIndex, string? ReasonOverride) ResolveTracks(
        MediaSource source,
        Guid? audioStreamId,
        Guid? subtitleStreamId,
        string currentReason,
        int? fallbackAudioIndex = null,
        int? fallbackBurnIn = null)
    {
        int? audioIndex = fallbackAudioIndex;
        if (audioStreamId is not null)
        {
            var audio = source.Streams.FirstOrDefault(s => s.Id == audioStreamId.Value && s.Kind == StreamKind.Audio)
                        ?? throw new ValidationException("audioStreamId", "Audio stream not found on this media source.");
            audioIndex = audio.StreamIndex;
        }
        else if (audioIndex is null)
        {
            var preferred = source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio && s.IsDefault)
                            ?? source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio);
            audioIndex = preferred?.StreamIndex;
        }

        int? burnIn = fallbackBurnIn;
        string? reasonOverride = null;
        if (subtitleStreamId is not null)
        {
            var sub = source.Streams.FirstOrDefault(s => s.Id == subtitleStreamId.Value && s.Kind == StreamKind.Subtitle)
                      ?? throw new ValidationException("subtitleStreamId", "Subtitle stream not found on this media source.");
            if (IsBitmapSubtitle(sub))
            {
                burnIn = sub.IsExternal ? null : sub.StreamIndex;
                if (burnIn is not null)
                    reasonOverride = string.IsNullOrEmpty(currentReason) || currentReason == "DirectPlay" || currentReason == "ContainerNotSupported"
                        ? "SubtitleBurnIn"
                        : currentReason;
            }
            else
            {
                // Text subs are delivered as WebVTT sidecar — clear any prior burn-in.
                burnIn = null;
            }
        }

        return (audioIndex, burnIn, reasonOverride);
    }

    private static bool IsBitmapSubtitle(MediaStream stream)
    {
        var codec = stream.Codec ?? stream.SubtitleFormat ?? string.Empty;
        return BitmapSubtitleCodecs.Contains(codec);
    }

    private static PlaybackDecisionResponse BuildResponse(
        PlaybackSession session,
        PlaybackDecisionResult decision,
        Guid mediaId,
        MediaSource source)
    {
        var streamUrl = decision.Method == PlaybackMethod.DirectPlay
            ? $"/api/v1/items/{mediaId}/download"
            : session.Mode == PlaybackMode.Auto
                ? $"/api/v1/stream/{session.SessionId}/master.m3u8"
                : $"/api/v1/stream/{session.SessionId}/index.m3u8";

        var audio = source.Streams
            .Where(s => s.Kind == StreamKind.Audio)
            .Select(s => new AudioStreamOption
            {
                Id = s.Id,
                Language = s.Language,
                Codec = s.Codec,
                Channels = s.Channels,
                IsDefault = s.IsDefault,
            }).ToList();

        var subtitles = source.Streams
            .Where(s => s.Kind == StreamKind.Subtitle)
            .Where(s => !IsBitmapSubtitle(s)) // bitmap only via burn-in, no empty VTT stubs
            .Select(s => new SubtitleStreamOption
            {
                Id = s.Id,
                Language = s.Language,
                Format = s.SubtitleFormat ?? s.Codec,
                DeliveryUrl = ArtworkUrlBuilder.SubtitleUrl(mediaId, s.Id),
            }).ToList();

        return new PlaybackDecisionResponse
        {
            SessionId = session.SessionId,
            Method = decision.Method,
            Mode = session.Mode,
            StreamUrl = streamUrl,
            Container = decision.Method == PlaybackMethod.DirectPlay ? source.Container : "hls",
            StartPositionMs = session.StartPositionMs,
            DurationMs = source.DurationMs,
            SelectedQualityId = decision.SelectedQualityId,
            AvailableQualities = decision.AvailableQualities,
            AudioStreams = audio,
            SubtitleStreams = subtitles,
            ExpiresAt = session.ExpiresAt,
            Reason = decision.Reason,
        };
    }
}
