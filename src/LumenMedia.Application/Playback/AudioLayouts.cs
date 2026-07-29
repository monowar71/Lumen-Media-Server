using LumenMedia.Application.Contracts;

namespace LumenMedia.Application.Playback;

/// <summary>
/// Channel-layout ids accepted by playback decision / set-quality and mapped to ffmpeg <c>-ac</c>.
/// Layouts never upmix beyond the source channel count.
/// </summary>
public static class AudioLayouts
{
    public const string Stereo = "stereo";
    public const string Surround21 = "2.1";
    public const string Surround51 = "5.1";
    public const string Mono = "mono";

    /// <summary>Default when encoding audio without an explicit client choice (MSE-safe).</summary>
    public const string DefaultEncode = Stereo;

    private static readonly (string Id, string Label, int Channels)[] All =
    [
        (Mono, "Mono", 1),
        (Stereo, "Stereo (2.0)", 2),
        (Surround21, "2.1", 3),
        (Surround51, "5.1", 6),
    ];

    public static int ChannelCount(string layoutId) =>
        All.FirstOrDefault(x => x.Id.Equals(layoutId, StringComparison.OrdinalIgnoreCase)).Channels;

    public static bool IsKnown(string? layoutId) =>
        !string.IsNullOrWhiteSpace(layoutId)
        && All.Any(x => x.Id.Equals(layoutId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Layouts the client may pick for a track with <paramref name="sourceChannels"/> channels.</summary>
    public static IReadOnlyList<AudioLayoutOption> AvailableFor(int? sourceChannels)
    {
        var channels = sourceChannels is > 0 ? sourceChannels.Value : 2;
        return All
            .Where(x => x.Channels <= channels)
            .Select(x => new AudioLayoutOption { Id = x.Id, Label = x.Label, Channels = x.Channels })
            .ToList();
    }

    /// <summary>
    /// Resolves the effective layout. Null request → stereo when encoding would downmix
    /// a multi-channel source; otherwise the highest available ≤ source (still stereo default for encode).
    /// </summary>
    public static string Resolve(string? requested, int? sourceChannels, bool encodingAudio)
    {
        var available = AvailableFor(sourceChannels);
        if (requested is not null && IsKnown(requested)
            && available.Any(a => a.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)))
        {
            return available.First(a => a.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)).Id;
        }

        if (!encodingAudio)
        {
            // Direct Play / Direct Stream: keep source as "stereo" label only when already ≤2.
            var channels = sourceChannels is > 0 ? sourceChannels.Value : 2;
            return All.LastOrDefault(x => x.Channels <= channels).Id ?? Stereo;
        }

        return available.Any(a => a.Id == Stereo) ? Stereo : available[0].Id;
    }

    /// <summary>True when the chosen layout needs fewer channels than the source (forces audio encode).</summary>
    public static bool RequiresDownmix(string layoutId, int? sourceChannels)
    {
        if (sourceChannels is null or <= 0)
            return false;
        var target = ChannelCount(layoutId);
        return target > 0 && target < sourceChannels.Value;
    }

    public static int AacBitrateKbps(string layoutId) => ChannelCount(layoutId) switch
    {
        <= 1 => 96,
        2 => 128,
        3 => 192,
        _ => 384,
    };
}
