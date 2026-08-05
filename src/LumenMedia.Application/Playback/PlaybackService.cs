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
    ITorrentPlaybackResolver torrentPlayback,
    ITorrServerProcess torrServerProcess,
    ITorrServerClient torrServerClient,
    ITorrentSourceProbeCoordinator torrentProbe,
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

        // Force HLS for torrent sources until we have a real probe (no DirectPlay of TorrServer URLs to clients).
        if (source.IsTorrent && decision.Method == PlaybackMethod.DirectPlay)
            decision = decision with { Method = PlaybackMethod.Transcode, Reason = "TorrentStream" };

        if (decision.Method == PlaybackMethod.Transcode && user is { AllowTranscoding: false })
            throw new ForbiddenException("Transcoding is disabled for this user.");

        // Enforce the concurrent-transcode limit (backpressure → 429).
        // set-quality reuses a session id and does not bump this count.
        if (decision.Method == PlaybackMethod.Transcode && transcoder.ActiveSessionCount >= opts.MaxConcurrentSessions)
            throw new RateLimitException("Maximum number of concurrent transcode sessions reached. Try again later.");

        var safePath = await ResolvePlayableInputAsync(source, ct);
        var holdsTorrLease = source.IsTorrent;
        if (holdsTorrLease)
            torrServerProcess.AcquireLease();

        PlaybackSession? session = null;
        try
        {
            var now = clock.GetUtcNow();
            // Full GUID: a truncated id (32 bits) is both guessable and collision-prone.
            var sessionId = $"sess-{Guid.NewGuid():N}";
            session = sessions.Create(new PlaybackSession
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
                HoldsTorrServerLease = holdsTorrLease,
                TorrentInfoHash = source.IsTorrent ? source.InfoHash : null,
                ProbedFormat = source.IsTorrent ? MediaMapper.MapProbedFormat(source.Streams) : null,
            });

            if (decision.Method != PlaybackMethod.DirectPlay)
                await StartTranscodeAsync(session, decision.SelectedQualityId, request.ResumePositionMs, decision.Reason, ct);

            if (source.IsTorrent)
            {
                torrentProbe.ScheduleIfNeeded(
                    sessionId,
                    source.Id,
                    safePath,
                    source.NeedsStreamProbe());
            }

            logger.LogInformation(
                "Playback start {SessionId} media={MediaId} method={Method} quality={Quality} reason={Reason} posMs={PosMs}",
                sessionId,
                request.MediaId,
                decision.Method,
                decision.SelectedQualityId,
                decision.Reason,
                request.ResumePositionMs);

            await notifier.NotifyNowPlayingAsync(caller.UserId, request.MediaId, decision.Method, sessionId, ct);

            var torrentStats = await TryGetTorrentStatsAsync(source.IsTorrent ? source.InfoHash : null, ct);
            return BuildResponse(session, decision, request.MediaId, source, torrentStats);
        }
        catch
        {
            if (session is not null)
                sessions.Remove(session.SessionId);
            if (holdsTorrLease)
                torrServerProcess.ReleaseLease();
            throw;
        }
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

        return BuildResponse(
            session,
            decision,
            session.MediaId,
            source,
            await TryGetTorrentStatsAsync(session.TorrentInfoHash, ct));
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

        return BuildResponse(
            session,
            decision,
            session.MediaId,
            source,
            await TryGetTorrentStatsAsync(session.TorrentInfoHash, ct));
    }

    public async Task<PlaybackPingResponse> PingAsync(Caller caller, string sessionId, CancellationToken ct)
    {
        var session = sessions.Get(sessionId);
        if (session is not null && session.UserId == caller.UserId)
        {
            var now = clock.GetUtcNow();
            session.LastAccess = now;
            sessions.Touch(sessionId, now + SessionLifetime);
            var stats = await TryGetTorrentStatsAsync(session.TorrentInfoHash, ct);
            return new PlaybackPingResponse
            {
                TorrentStats = stats,
                ProbedFormat = session.ProbedFormat,
            };
        }

        return new PlaybackPingResponse();
    }

    public async Task StopAsync(Caller caller, string sessionId, CancellationToken ct)
    {
        var session = sessions.Get(sessionId);
        if (session is null || session.UserId != caller.UserId)
            return;
        await transcoder.StopAsync(sessionId, ct);
        await ReleaseTorrentSessionAsync(session, ct);
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
    /// Resolves ffmpeg/DirectPlay input: local file under library roots, or TorrServer HTTP URL.
    /// </summary>
    private async Task<string> ResolvePlayableInputAsync(MediaSource source, CancellationToken ct)
    {
        if (source.IsTorrent)
            return await ResolveTorrentStreamUrlAsync(source, ct);
        return await ResolveSafeSourcePathAsync(source, ct);
    }

    private async Task<string> ResolveTorrentStreamUrlAsync(MediaSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.TorrentPath)
            || string.IsNullOrWhiteSpace(source.InfoHash)
            || source.TorrentFileIndex is null)
        {
            throw new ValidationException("mediaSourceId", "Torrent source is incomplete.");
        }

        var roots = await ResolveLibraryRootsAsync(source, ct);
        if (roots.Count == 0
            || !PathSafety.TryResolveUnderRoots(source.TorrentPath, roots, out var safeTorrent)
            || !File.Exists(safeTorrent))
        {
            throw new NotFoundException("Torrent file not found.");
        }

        try
        {
            return await torrentPlayback.ResolvePlayUrlAsync(
                safeTorrent,
                source.InfoHash,
                source.TorrentFileIndex.Value,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotFoundException and not ValidationException)
        {
            logger.LogWarning(ex, "Failed to start TorrServer stream for {Path}", safeTorrent);
            throw new ServiceUnavailableException("Torrent stream is temporarily unavailable. Try again.");
        }
    }

    private async Task ReleaseTorrentSessionAsync(PlaybackSession session, CancellationToken ct)
    {
        if (!session.HoldsTorrServerLease)
            return;

        try
        {
            var source = await uow.Media.GetSourceByIdAsync(session.MediaSourceId, ct);
            if (source is { IsTorrent: true, InfoHash: not null })
                await torrServerClient.DropAsync(source.InfoHash, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TorrServer drop on session stop failed");
        }

        torrServerProcess.ReleaseLease();
        session.HoldsTorrServerLease = false;
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
        MediaSource source,
        TorrentPlaybackStatsDto? torrentStats = null)
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
                Codec = MediaMapper.SanitizeCodec(s.Codec),
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
                Format = s.SubtitleFormat ?? MediaMapper.SanitizeCodec(s.Codec) ?? s.Codec,
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
            TorrentStats = torrentStats,
            IsTorrentSource = session.HoldsTorrServerLease || !string.IsNullOrEmpty(session.TorrentInfoHash),
            ProbedFormat = session.ProbedFormat,
        };
    }

    private async Task<TorrentPlaybackStatsDto?> TryGetTorrentStatsAsync(string? infoHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
            return null;
        try
        {
            var status = await torrServerClient.GetAsync(infoHash, ct);
            if (status is null)
                return null;
            var peers = status.TotalPeers > 0 ? status.TotalPeers : status.ActivePeers;
            return new TorrentPlaybackStatsDto
            {
                Seeders = Math.Max(0, status.ConnectedSeeders),
                Peers = Math.Max(0, peers),
                DownloadSpeedBytesPerSec = (long)Math.Max(0, Math.Round(status.DownloadSpeedBytesPerSec)),
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TorrServer stats unavailable for {Hash}", infoHash);
            return null;
        }
    }
}
