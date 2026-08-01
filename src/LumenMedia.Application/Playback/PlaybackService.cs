using Microsoft.Extensions.Logging;
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
    IRealtimeNotifier notifier,
    ILogger<PlaybackService> logger)
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

        var decision = decider.Decide(
            source,
            request.Profile,
            request.Mode,
            request.QualityId,
            opts,
            userCap,
            request.ForceHdrToSdr,
            request.AudioLayout,
            request.AudioStreamId,
            request.HdrToneMapMethod);
        var (audioIndex, burnInIndex, reasonOverride) = ResolveTracks(source, request.AudioStreamId, request.SubtitleStreamId, decision.Reason);
        if (reasonOverride is not null)
            decision = decision with { Method = PlaybackMethod.Transcode, Reason = reasonOverride };

        if (decision.Method == PlaybackMethod.Transcode && user is { AllowTranscoding: false })
            throw new ForbiddenException("Transcoding is disabled for this user.");

        // Enforce the concurrent-transcode limit (backpressure → 429).
        // set-quality reuses a session id and does not bump this count.
        if (decision.Method == PlaybackMethod.Transcode && transcoder.ActiveSessionCount >= opts.MaxConcurrentSessions)
            throw new RateLimitException("Maximum number of concurrent transcode sessions reached. Try again later.");

        var safePath = await ResolveSafeSourcePathAsync(source, ct);

        var now = clock.GetUtcNow();
        // Full GUID: a truncated id (32 bits) is both guessable and collision-prone.
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var session = sessions.Create(new PlaybackSession
        {
            SessionId = sessionId,
            UserId = caller.UserId,
            MediaId = request.MediaId,
            MediaSourceId = source.Id,
            SourcePath = safePath,
            Container = source.Container,
            Method = decision.Method,
            Mode = request.Mode,
            SelectedQualityId = decision.SelectedQualityId,
            StartPositionMs = request.ResumePositionMs,
            AudioStreamIndex = audioIndex,
            SubtitleBurnInIndex = burnInIndex,
            Profile = request.Profile,
            Reason = decision.Reason,
            ForceHdrToSdr = request.ForceHdrToSdr,
            HdrToneMapMethod = decision.SelectedHdrToneMapMethod,
            AudioLayout = decision.SelectedAudioLayout,
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
            LastAccess = now,
        });

        if (decision.Method != PlaybackMethod.DirectPlay)
            await StartTranscodeAsync(session, decision.SelectedQualityId, request.ResumePositionMs, decision.Reason, ct);

        logger.LogInformation(
            "Playback start {SessionId} media={MediaId} method={Method} quality={Quality} reason={Reason} posMs={PosMs}",
            sessionId,
            request.MediaId,
            decision.Method,
            decision.SelectedQualityId,
            decision.Reason,
            request.ResumePositionMs);

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
        // null = leave session flag alone; only an explicit true/false changes it.
        // Clients must omit the field on unrelated set-quality calls (quality/audio).
        var forceHdr = request.ForceHdrToSdr ?? session.ForceHdrToSdr;
        // Selecting a concrete tonemap method turns HDR→SDR on unless Off was requested.
        if (request.HdrToneMapMethod is not null
            && HdrToneMapMethods.IsKnown(request.HdrToneMapMethod)
            && request.ForceHdrToSdr is not false)
        {
            forceHdr = true;
        }

        var toneMethod = request.HdrToneMapMethod ?? session.HdrToneMapMethod;
        var audioLayout = request.AudioLayout ?? session.AudioLayout;
        var decision = decider.Decide(
            source,
            session.Profile,
            request.Mode,
            request.QualityId,
            opts,
            userCap,
            forceHdr,
            audioLayout,
            request.AudioStreamId,
            toneMethod);
        var (audioIndex, burnInIndex, reasonOverride) = ResolveTracks(
            source,
            request.AudioStreamId,
            request.SubtitleStreamId,
            decision.Reason,
            session.AudioStreamIndex,
            session.SubtitleBurnInIndex);
        // Burn-in still forces Transcode, but must not erase an active HDR→SDR reason.
        if (reasonOverride is not null)
        {
            decision = decision with
            {
                Method = PlaybackMethod.Transcode,
                Reason = decision.ToneMapActive ? decision.Reason : reasonOverride,
            };
        }

        if (decision.Method == PlaybackMethod.Transcode && user is { AllowTranscoding: false })
            throw new ForbiddenException("Transcoding is disabled for this user.");

        session.Mode = request.Mode;
        session.SelectedQualityId = decision.SelectedQualityId;
        session.StartPositionMs = request.ResumePositionMs;
        session.Method = decision.Method;
        session.Reason = decision.Reason;
        session.AudioStreamIndex = audioIndex;
        session.SubtitleBurnInIndex = burnInIndex;
        session.ForceHdrToSdr = forceHdr;
        session.HdrToneMapMethod = decision.SelectedHdrToneMapMethod ?? toneMethod;
        session.AudioLayout = decision.SelectedAudioLayout;
        session.LastAccess = clock.GetUtcNow();
        sessions.Touch(sessionId, clock.GetUtcNow() + SessionLifetime);

        if (decision.Method != PlaybackMethod.DirectPlay)
            await StartTranscodeAsync(session, decision.SelectedQualityId, request.ResumePositionMs, decision.Reason, ct);
        else
            await transcoder.StopAsync(sessionId, ct);

        logger.LogInformation(
            "Playback set-quality {SessionId} method={Method} quality={Quality} reason={Reason} forceHdr={ForceHdr} toneMap={ToneMap} toneMethod={ToneMethod} posMs={PosMs}",
            sessionId,
            decision.Method,
            decision.SelectedQualityId,
            decision.Reason,
            forceHdr,
            decision.ToneMapActive,
            session.HdrToneMapMethod,
            request.ResumePositionMs);

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

        logger.LogInformation(
            "Playback seek {SessionId} method={Method} quality={Quality} posMs={PosMs}",
            sessionId,
            session.Method,
            session.SelectedQualityId,
            request.PositionMs);

        var seekDecision = decider.Decide(
            source,
            session.Profile,
            session.Mode,
            session.Mode == PlaybackMode.Manual ? session.SelectedQualityId : null,
            options.Value,
            (await uow.Users.GetByIdAsync(caller.UserId, ct))?.MaxBitrateRemoteKbps,
            session.ForceHdrToSdr,
            session.AudioLayout,
            hdrToneMapMethod: session.HdrToneMapMethod);
        var decision = new PlaybackDecisionResult
        {
            Method = session.Method,
            Reason = session.Reason,
            SelectedQualityId = session.SelectedQualityId,
            AvailableQualities = seekDecision.AvailableQualities,
            ToneMapActive = PlaybackDecider.NeedsToneMap(
                source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video)?.Hdr,
                session.Profile.SupportsHdr,
                session.ForceHdrToSdr),
            SelectedAudioLayout = session.AudioLayout,
            AvailableAudioLayouts = AudioLayouts.AvailableFor(
                source.Streams.FirstOrDefault(s =>
                    s.Kind == StreamKind.Audio
                    && (session.AudioStreamIndex is null || s.StreamIndex == session.AudioStreamIndex))?.Channels
                ?? source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio)?.Channels),
            SourceHdr = source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video)?.Hdr,
            AvailableHdrToneMapMethods = seekDecision.AvailableHdrToneMapMethods,
            SelectedHdrToneMapMethod = session.HdrToneMapMethod ?? seekDecision.SelectedHdrToneMapMethod,
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
        logger.LogInformation("Playback stop {SessionId}", sessionId);
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
                MaxOutputHeight = ResolutionLimits.ParseMaxHeight(session.Profile.MaxResolution),
                ToneMap = PlaybackDecider.NeedsToneMap(video?.Hdr, session.Profile.SupportsHdr, session.ForceHdrToSdr),
                HdrToneMapMethod = session.HdrToneMapMethod,
                AudioLayout = session.AudioLayout,
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

    /// <summary>
    /// Resolves the media file realpath and refuses symlink escapes outside library roots
    /// (same rules as download / DirectPlay).
    /// </summary>
    private async Task<string> ResolveSafeSourcePathAsync(MediaSource source, CancellationToken ct)
    {
        var roots = await ResolveLibraryRootsAsync(source, ct);
        if (roots.Count == 0
            || !PathSafety.TryResolveUnderRoots(source.Path, roots, out var fullPath)
            || !File.Exists(fullPath))
        {
            throw new NotFoundException("Media file not found.");
        }

        return fullPath;
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
        // DirectPlay also goes through /stream/{sessionId}/… so native players (Android
        // ExoPlayer, <video src>) can keep fetching after the short-lived access JWT expires.
        // The session id is an unguessable capability token; JWT is only required to create it.
        var streamUrl = decision.Method == PlaybackMethod.DirectPlay
            ? $"/api/v1/stream/{session.SessionId}/source"
            : session.Mode == PlaybackMode.Auto
                ? $"/api/v1/stream/{session.SessionId}/master.m3u8"
                : $"/api/v1/stream/{session.SessionId}/index.m3u8";

        var audio = source.Streams
            .Where(s => s.Kind == StreamKind.Audio)
            .Select(s => new AudioStreamOption
            {
                Id = s.Id,
                Language = s.Language,
                Title = s.Title,
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
                Title = s.Title,
                Format = s.SubtitleFormat ?? s.Codec,
                IsDefault = s.IsDefault,
                IsForced = s.IsForced,
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
            SourceHdr = decision.SourceHdr ?? source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video)?.Hdr,
            ToneMapActive = decision.ToneMapActive,
            AvailableAudioLayouts = decision.AvailableAudioLayouts.Count > 0
                ? decision.AvailableAudioLayouts
                : AudioLayouts.AvailableFor(audio.FirstOrDefault(a => a.IsDefault)?.Channels ?? audio.FirstOrDefault()?.Channels),
            SelectedAudioLayout = decision.SelectedAudioLayout,
            AvailableHdrToneMapMethods = decision.AvailableHdrToneMapMethods,
            SelectedHdrToneMapMethod = decision.SelectedHdrToneMapMethod ?? session.HdrToneMapMethod,
        };
    }
}
