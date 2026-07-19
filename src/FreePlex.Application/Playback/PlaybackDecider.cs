using FreePlex.Application.Contracts;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;

namespace FreePlex.Application.Playback;

public sealed record PlaybackDecisionResult
{
    public required PlaybackMethod Method { get; init; }
    public required string Reason { get; init; }
    public required string SelectedQualityId { get; init; }
    public required IReadOnlyList<QualityOption> AvailableQualities { get; init; }
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
        int? userRemoteCapKbps = null)
    {
        var video = source.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        var audios = source.Streams.Where(s => s.Kind == StreamKind.Audio).ToList();

        var cap = ResolveBitrateCap(profile.MaxBitrateKbps, userRemoteCapKbps);
        var availableQualities = BuildLadder(source, video, mode, options, cap);

        var (method, reason) = DecideMethod(source, video, audios, profile, cap);

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
        }

        return new PlaybackDecisionResult
        {
            Method = method,
            Reason = reason,
            SelectedQualityId = selected,
            AvailableQualities = availableQualities,
        };
    }

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
        int? cap)
    {
        if (video is null)
            return (PlaybackMethod.Transcode, "NoVideoStream");

        var videoCodec = video.Codec ?? string.Empty;
        var codecSupported = ContainsIgnoreCase(profile.VideoCodecs, videoCodec);
        var hevc = videoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                   || videoCodec.Equals("h265", StringComparison.OrdinalIgnoreCase);

        if (!codecSupported || (hevc && !profile.SupportsHevc))
            return (PlaybackMethod.Transcode, "VideoCodecNotSupported");

        if (!string.IsNullOrEmpty(video.Hdr) && !profile.SupportsHdr)
            return (PlaybackMethod.Transcode, "HdrNotSupported");

        var maxHeight = ParseMaxHeight(profile.MaxResolution);
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

        list.Add(new QualityOption
        {
            Id = "original",
            Label = "Original",
            Adaptive = false,
            Width = sourceWidth,
            Height = sourceHeight,
            BitrateKbps = originalBitrate,
        });

        foreach (var rung in options.Ladder.OrderByDescending(r => r.Height))
        {
            // No upscaling: skip rungs at or above the source resolution.
            if (sourceHeight is not null && rung.Height >= sourceHeight)
                continue;
            // Clamp by the network cap.
            if (cap is not null && rung.VideoBitrateKbps > cap)
                continue;

            list.Add(new QualityOption
            {
                Id = rung.Id,
                Label = $"{rung.Height}p",
                Adaptive = false,
                Width = ComputeWidth(rung.Height, sourceWidth, sourceHeight),
                Height = rung.Height,
                BitrateKbps = rung.VideoBitrateKbps,
            });
        }

        return list;
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

    private static int? ParseMaxHeight(string? maxResolution)
    {
        if (string.IsNullOrWhiteSpace(maxResolution))
            return null;
        var res = maxResolution.Trim().ToLowerInvariant();
        return res switch
        {
            "4k" or "uhd" or "2160p" => 2160,
            "1440p" or "qhd" => 1440,
            "1080p" or "fhd" => 1080,
            "720p" or "hd" => 720,
            "480p" or "sd" => 480,
            "360p" => 360,
            _ => int.TryParse(res.TrimEnd('p'), out var h) ? h : null,
        };
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> list, string value) =>
        list.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
}
