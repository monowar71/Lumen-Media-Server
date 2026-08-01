using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Playback;

public sealed record PlaybackDecisionResult
{
    public required PlaybackMethod Method { get; init; }
    public required string Reason { get; init; }
    public required string SelectedQualityId { get; init; }
    public required IReadOnlyList<QualityOption> AvailableQualities { get; init; }
    public bool ToneMapActive { get; init; }
    public required string SelectedAudioLayout { get; init; }
    public IReadOnlyList<AudioLayoutOption> AvailableAudioLayouts { get; init; } = [];
    public string? SourceHdr { get; init; }
    public IReadOnlyList<HdrToneMapMethodOption> AvailableHdrToneMapMethods { get; init; } = [];
    public string? SelectedHdrToneMapMethod { get; init; }
}

/// <summary>
/// Pure decision engine: given a media source's streams and a device profile, decides
/// Direct Play / Direct Stream / Transcode and builds the (no-upscale, cap-clamped)
/// quality ladder. Fully unit-tested; no I/O, no ffmpeg. See transcoding.md §2 and §7.
/// </summary>
public sealed class PlaybackDecider
{
    public PlaybackDecisionResult Decide(
        MediaSource source,
        DeviceProfile profile,
        PlaybackMode mode,
        string? requestedQualityId,
        PlaybackOptions options,
        int? userRemoteCapKbps = null,
        bool forceHdrToSdr = false,
        string? audioLayout = null,
        Guid? audioStreamId = null,
        string? hdrToneMapMethod = null)
    {
        var video = source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        var audios = source.Streams.Where(s => s.Kind == StreamKind.Audio).ToList();
        var selectedAudio = ResolveAudioStream(audios, audioStreamId);
        var sourceChannels = selectedAudio?.Channels;
        var availableLayouts = AudioLayouts.AvailableFor(sourceChannels);
        var hasHdr = !string.IsNullOrEmpty(video?.Hdr);
        var availableToneMapMethods = hasHdr
            ? HdrToneMapMethods.AvailableFor(options.HardwareAccel)
            : Array.Empty<HdrToneMapMethodOption>();

        var cap = ResolveBitrateCap(profile.MaxBitrateKbps, userRemoteCapKbps);
        var availableQualities = BuildLadder(source, video, mode, options, cap);

        var (method, reason) = DecideMethod(source, video, audios, profile, cap, forceHdrToSdr);

        string selectedLayout;
        if (!string.IsNullOrWhiteSpace(audioLayout) && AudioLayouts.IsKnown(audioLayout))
        {
            selectedLayout = AudioLayouts.Resolve(audioLayout, sourceChannels, encodingAudio: true);
            if (AudioLayouts.RequiresDownmix(selectedLayout, sourceChannels)
                && method != PlaybackMethod.Transcode)
            {
                method = PlaybackMethod.Transcode;
                reason = "AudioDownmix";
            }
        }
        else
        {
            // No explicit layout: DirectPlay/DirectStream keep source; Transcode defaults to stereo.
            selectedLayout = AudioLayouts.Resolve(
                null,
                sourceChannels,
                encodingAudio: method == PlaybackMethod.Transcode);
        }

        var selected = ResolveSelectedQuality(mode, requestedQualityId, availableQualities, method);

        // Manual rungs below Original must always open a transcode session.
        // Otherwise set-quality to e.g. 360p keeps Method=DirectPlay (codec
        // matches) and the client keeps streaming the full-quality download URL.
        if (mode == PlaybackMode.Manual
            && !string.Equals(selected, "original", StringComparison.OrdinalIgnoreCase)
            && method != PlaybackMethod.Transcode)
        {
            method = PlaybackMethod.Transcode;
            reason = "ManualQuality";
            if (string.IsNullOrWhiteSpace(audioLayout))
                selectedLayout = AudioLayouts.Resolve(null, sourceChannels, encodingAudio: true);
        }

        var toneMap = NeedsToneMap(video?.Hdr, profile.SupportsHdr, forceHdrToSdr);
        if (toneMap)
        {
            // Keep HDR reason even when AudioDownmix/ManualQuality also apply so session
            // logs and reason-based ffmpeg guards stay consistent with ToneMapActive.
            if (method != PlaybackMethod.Transcode)
            {
                method = PlaybackMethod.Transcode;
                if (string.IsNullOrWhiteSpace(audioLayout))
                    selectedLayout = AudioLayouts.Resolve(null, sourceChannels, encodingAudio: true);
            }

            reason = forceHdrToSdr ? "ForceHdrToSdr" : "HdrNotSupported";
        }

        var toneMapActive = toneMap && method == PlaybackMethod.Transcode;
        // Preferred method whenever source is HDR (sticky for the player menu even if Off).
        var preferredToneMapMethod = hasHdr
            ? HdrToneMapMethods.Resolve(
                hdrToneMapMethod,
                options.HardwareAccel,
                options.HdrToneMapMethod)
            : null;

        return new PlaybackDecisionResult
        {
            Method = method,
            Reason = reason,
            SelectedQualityId = selected,
            AvailableQualities = availableQualities,
            ToneMapActive = toneMapActive,
            SelectedAudioLayout = selectedLayout,
            AvailableAudioLayouts = availableLayouts,
            SourceHdr = string.IsNullOrEmpty(video?.Hdr) ? null : video!.Hdr,
            AvailableHdrToneMapMethods = availableToneMapMethods,
            SelectedHdrToneMapMethod = preferredToneMapMethod,
        };
    }

    private static MediaStream? ResolveAudioStream(IReadOnlyList<MediaStream> audios, Guid? audioStreamId)
    {
        if (audioStreamId is not null)
            return audios.FirstOrDefault(a => a.Id == audioStreamId.Value);
        return audios.FirstOrDefault(a => a.IsDefault) ?? audios.FirstOrDefault();
    }

    internal static bool NeedsToneMap(string? hdr, bool supportsHdr, bool forceHdrToSdr) =>
        !string.IsNullOrEmpty(hdr) && (forceHdrToSdr || !supportsHdr);

    private static int? ResolveBitrateCap(int? profileCap, int? userCap)
    {
        if (profileCap is > 0 && userCap is > 0)
            return Math.Min(profileCap.Value, userCap.Value);
        return profileCap is > 0 ? profileCap : (userCap is > 0 ? userCap : null);
    }

    private static (PlaybackMethod, string) DecideMethod(
        MediaSource source,
        MediaStream? video,
        IReadOnlyList<MediaStream> audios,
        DeviceProfile profile,
        int? cap,
        bool forceHdrToSdr)
    {
        if (video is null)
            return (PlaybackMethod.Transcode, "NoVideoStream");

        var videoCodec = video.Codec ?? string.Empty;
        var codecSupported = ContainsIgnoreCase(profile.VideoCodecs, videoCodec);
        var hevc = videoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                   || videoCodec.Equals("h265", StringComparison.OrdinalIgnoreCase);

        if (!codecSupported || (hevc && !profile.SupportsHevc))
            return (PlaybackMethod.Transcode, "VideoCodecNotSupported");

        if (!string.IsNullOrEmpty(video.Hdr) && forceHdrToSdr)
            return (PlaybackMethod.Transcode, "ForceHdrToSdr");

        if (!string.IsNullOrEmpty(video.Hdr) && !profile.SupportsHdr)
            return (PlaybackMethod.Transcode, "HdrNotSupported");

        var maxHeight = ResolutionLimits.ParseMaxHeight(profile.MaxResolution);
        if (maxHeight is not null && video.Height is not null && video.Height > maxHeight)
            return (PlaybackMethod.Transcode, "ResolutionTooHigh");

        var effectiveBitrate = source.OverallBitrateKbps ?? video.BitrateKbps;
        if (cap is not null && effectiveBitrate is not null && effectiveBitrate > cap)
            return (PlaybackMethod.Transcode, "BitrateTooHigh");

        var audioSupported = audios.Count == 0
            || audios.Any(a => ContainsIgnoreCase(profile.AudioCodecs, a.Codec ?? string.Empty));
        if (!audioSupported)
            return (PlaybackMethod.Transcode, "AudioCodecNotSupported");

        if (!ContainsIgnoreCase(profile.Containers, source.Container))
            return (PlaybackMethod.DirectStream, "ContainerNotSupported");

        return (PlaybackMethod.DirectPlay, "DirectPlay");
    }

    private static IReadOnlyList<QualityOption> BuildLadder(
        MediaSource source,
        MediaStream? video,
        PlaybackMode mode,
        PlaybackOptions options,
        int? cap)
    {
        var list = new List<QualityOption>();

        if (mode == PlaybackMode.Auto && options.AbrEnabled)
            list.Add(new QualityOption { Id = "auto", Label = "Auto", Adaptive = true });

        var sourceHeight = video?.Height;
        var sourceWidth = video?.Width;
        var originalBitrate = source.OverallBitrateKbps ?? video?.BitrateKbps;
        // Ultrawide / open-matte (e.g. 1920×696) still belongs on the 1080p tier by width.
        var tierHeight = EffectiveTierHeight(sourceWidth, sourceHeight);

        list.Add(new QualityOption
        {
            Id = "original",
            Label = "Original",
            Adaptive = false,
            Width = sourceWidth,
            Height = sourceHeight,
            BitrateKbps = originalBitrate,
        });

        foreach (var rung in options.EffectiveLadder
                     .OrderByDescending(r => r.Height)
                     .ThenByDescending(r => r.VideoBitrateKbps))
        {
            // Skip rungs above the source tier (allows same-height re-encode, e.g. 1080p→1080p).
            if (tierHeight is not null && rung.Height > tierHeight)
                continue;
            // Clamp by the network cap.
            if (cap is not null && rung.VideoBitrateKbps > cap)
                continue;

            // Never upscale pixels: clamp output frame to the source.
            var outHeight = sourceHeight is int sh ? Math.Min(rung.Height, sh) : rung.Height;
            var outWidth = sourceWidth is int sw && sourceHeight is int && outHeight == sourceHeight
                ? sw
                : ComputeWidth(outHeight, sourceWidth, sourceHeight);

            list.Add(new QualityOption
            {
                Id = rung.Id,
                Label = FormatRungLabel(rung),
                Adaptive = false,
                Width = outWidth,
                Height = outHeight,
                BitrateKbps = rung.VideoBitrateKbps,
            });
        }

        return list;
    }

    /// <summary>
    /// 16:9-equivalent height so cinema / open-matte frames (wide but short) still
    /// expose 1080p bitrate rungs without offering true upscales.
    /// </summary>
    internal static int? EffectiveTierHeight(int? sourceWidth, int? sourceHeight)
    {
        if (sourceHeight is null)
            return null;
        if (sourceWidth is not > 0)
            return sourceHeight;
        var equiv = (int)Math.Round(sourceWidth.Value * 9.0 / 16.0);
        return Math.Max(sourceHeight.Value, equiv);
    }

    internal static string FormatRungLabel(LadderRung rung)
    {
        var rate = rung.VideoBitrateKbps >= 1000
            ? $"{rung.VideoBitrateKbps / 1000.0:0.##} Mbps"
            : $"{rung.VideoBitrateKbps} kbps";
        if (rung.Id.Contains("high", StringComparison.OrdinalIgnoreCase))
            return $"{rung.Height}p High (~{rate})";
        return $"{rung.Height}p (~{rate})";
    }

    private static string ResolveSelectedQuality(
        PlaybackMode mode,
        string? requestedQualityId,
        IReadOnlyList<QualityOption> qualities,
        PlaybackMethod method)
    {
        if (mode == PlaybackMode.Manual && requestedQualityId is not null)
        {
            if (qualities.All(q => q.Id != requestedQualityId))
                throw new Common.UnprocessableException($"Quality '{requestedQualityId}' is not available for this media.");
            return requestedQualityId;
        }

        if (mode == PlaybackMode.Auto && qualities.Any(q => q.Id == "auto"))
            return "auto";

        return method == PlaybackMethod.DirectPlay ? "original" : qualities[0].Id;
    }

    private static int? ComputeWidth(int height, int? sourceWidth, int? sourceHeight)
    {
        if (sourceWidth is > 0 && sourceHeight is > 0)
        {
            var w = (int)Math.Round(height * (double)sourceWidth.Value / sourceHeight.Value);
            return w % 2 == 0 ? w : w + 1;
        }
        return (int)Math.Round(height * 16.0 / 9.0);
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> list, string value) =>
        list.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
}
