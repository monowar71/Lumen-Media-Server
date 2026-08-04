using FluentAssertions;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Tests;

public class PlaybackDeciderTests
{
    private readonly PlaybackDecider _decider = new();
    private readonly PlaybackOptions _options = new();

    private static MediaSource BuildSource(
        string container = "mkv",
        string videoCodec = "h264",
        int width = 1920,
        int height = 1080,
        int? overallBitrateKbps = 8000,
        string? hdr = null,
        string audioCodec = "aac")
    {
        var source = new MediaSource("/media/x.mkv", container, 1_000_000, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.SetProbeInfo(7_200_000, overallBitrateKbps);
        source.AddStream(new MediaStream(StreamKind.Video, 0)
        {
            Codec = videoCodec,
            Width = width,
            Height = height,
            BitrateKbps = overallBitrateKbps,
            Hdr = hdr,
        });
        source.AddStream(new MediaStream(StreamKind.Audio, 1) { Codec = audioCodec, Channels = 6 });
        return source;
    }

    private static DeviceProfile Profile(
        string? maxResolution = "1080p",
        int? maxBitrateKbps = null,
        string[]? videoCodecs = null,
        string[]? audioCodecs = null,
        string[]? containers = null,
        bool supportsHevc = false,
        bool supportsHdr = false) => new()
        {
            MaxResolution = maxResolution,
            MaxBitrateKbps = maxBitrateKbps,
            VideoCodecs = videoCodecs ?? ["h264"],
            AudioCodecs = audioCodecs ?? ["aac"],
            Containers = containers ?? ["mkv", "mp4"],
            SupportsHevc = supportsHevc,
            SupportsHdr = supportsHdr,
        };

    [Fact]
    public void Direct_play_when_everything_matches()
    {
        var result = _decider.Decide(BuildSource(), Profile(), PlaybackMode.Auto, null, _options);
        result.Method.Should().Be(PlaybackMethod.DirectPlay);
    }

    [Fact]
    public void Transcode_when_video_codec_unsupported()
    {
        var source = BuildSource(videoCodec: "hevc");
        var result = _decider.Decide(source, Profile(supportsHevc: false), PlaybackMode.Auto, null, _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("VideoCodecNotSupported");
    }

    [Fact]
    public void Direct_stream_when_only_container_unsupported()
    {
        var source = BuildSource(container: "avi");
        var result = _decider.Decide(source, Profile(containers: ["mp4"]), PlaybackMode.Auto, null, _options);

        result.Method.Should().Be(PlaybackMethod.DirectStream);
        result.Reason.Should().Be("ContainerNotSupported");
    }

    [Fact]
    public void Transcode_when_bitrate_exceeds_cap()
    {
        var source = BuildSource(overallBitrateKbps: 80000);
        var result = _decider.Decide(source, Profile(maxBitrateKbps: 5000), PlaybackMode.Auto, null, _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("BitrateTooHigh");
    }

    [Fact]
    public void Transcode_when_resolution_exceeds_profile()
    {
        var source = BuildSource(width: 1920, height: 1080);
        var result = _decider.Decide(source, Profile(maxResolution: "720p"), PlaybackMode.Auto, null, _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("ResolutionTooHigh");
    }

    [Fact]
    public void Ladder_does_not_upscale_beyond_source()
    {
        var source = BuildSource(width: 1280, height: 720, overallBitrateKbps: 4000);
        var result = _decider.Decide(source, Profile(maxResolution: "1080p"), PlaybackMode.Auto, null, _options);

        result.AvailableQualities.Select(q => q.Id).Should().Contain("original");
        result.AvailableQualities.Select(q => q.Id).Should().Contain("720p"); // same-height re-encode allowed
        result.AvailableQualities.Select(q => q.Id).Should().NotContain("1080p");
        result.AvailableQualities.Select(q => q.Id).Should().NotContain("1080p-high");
        result.AvailableQualities.Select(q => q.Id).Should().NotContain("1440p");
    }

    [Fact]
    public void Ladder_offers_1080p_for_1080p_source()
    {
        var source = BuildSource(width: 1920, height: 1080, overallBitrateKbps: 20000);
        var result = _decider.Decide(source, Profile(), PlaybackMode.Auto, null, _options);

        result.AvailableQualities.Select(q => q.Id).Should().Contain("1080p");
        result.AvailableQualities.Select(q => q.Id).Should().Contain("1080p-high");
        result.AvailableQualities.Select(q => q.Id).Should().Contain("720p");
        result.AvailableQualities.Select(q => q.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ladder_offers_1080p_tier_for_ultrawide_open_matte()
    {
        // The Creator-style frame: full HD width, short height.
        var source = BuildSource(width: 1920, height: 696, overallBitrateKbps: 21557);
        var result = _decider.Decide(source, Profile(), PlaybackMode.Auto, null, _options);

        var ids = result.AvailableQualities.Select(q => q.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().Contain("1080p");
        ids.Should().Contain("720p");
        var q1080 = result.AvailableQualities.Single(q => q.Id == "1080p");
        q1080.Height.Should().Be(696); // clamped — no upscale
        q1080.Width.Should().Be(1920);
    }

    [Fact]
    public void Auto_mode_selects_auto_quality()
    {
        var result = _decider.Decide(BuildSource(), Profile(), PlaybackMode.Auto, null, _options);
        result.SelectedQualityId.Should().Be("auto");
    }

    [Fact]
    public void Manual_mode_with_unavailable_quality_throws()
    {
        var act = () => _decider.Decide(BuildSource(), Profile(), PlaybackMode.Manual, "99999p", _options);
        act.Should().Throw<LumenMedia.Application.Common.UnprocessableException>();
    }

    [Fact]
    public void Manual_fixed_rung_forces_transcode_even_when_direct_play_possible()
    {
        // Source is fully DirectPlay-compatible; user still asked for 360p.
        var result = _decider.Decide(BuildSource(), Profile(), PlaybackMode.Manual, "360p", _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("ManualQuality");
        result.SelectedQualityId.Should().Be("360p");
    }

    [Fact]
    public void Manual_original_keeps_direct_play_when_compatible()
    {
        var result = _decider.Decide(BuildSource(), Profile(), PlaybackMode.Manual, "original", _options);

        result.Method.Should().Be(PlaybackMethod.DirectPlay);
        result.SelectedQualityId.Should().Be("original");
    }

    [Fact]
    public void Hdr_without_support_forces_transcode_and_tonemap()
    {
        var result = _decider.Decide(
            BuildSource(hdr: "HDR10"),
            Profile(supportsHdr: false),
            PlaybackMode.Auto,
            null,
            _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("HdrNotSupported");
        result.ToneMapActive.Should().BeTrue();
        result.SourceHdr.Should().Be("HDR10");
    }

    [Fact]
    public void Force_hdr_to_sdr_when_device_supports_hdr()
    {
        var result = _decider.Decide(
            BuildSource(hdr: "HDR10"),
            Profile(supportsHdr: true, videoCodecs: ["h264"], supportsHevc: false),
            PlaybackMode.Auto,
            null,
            _options,
            forceHdrToSdr: true);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("ForceHdrToSdr");
        result.ToneMapActive.Should().BeTrue();
        result.AvailableHdrToneMapMethods.Select(m => m.Id).Should().Contain("hable");
        result.SelectedHdrToneMapMethod.Should().Be("hable");
    }

    [Fact]
    public void Hdr_tone_map_method_request_is_honoured_when_tonemap_active()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi", HdrToneMapMethod = "hable" };
        var result = _decider.Decide(
            BuildSource(hdr: "HDR10"),
            Profile(supportsHdr: true),
            PlaybackMode.Auto,
            null,
            opts,
            forceHdrToSdr: true,
            hdrToneMapMethod: "mobius");

        result.ToneMapActive.Should().BeTrue();
        result.SelectedHdrToneMapMethod.Should().Be("mobius");
        result.AvailableHdrToneMapMethods.Select(m => m.Id).Should().Contain(["vaapi", "hable", "mobius"]);
    }

    [Fact]
    public void Hdr_passthrough_when_supported_and_not_forced()
    {
        var result = _decider.Decide(
            BuildSource(hdr: "HDR10"),
            Profile(supportsHdr: true),
            PlaybackMode.Auto,
            null,
            _options);

        result.Method.Should().Be(PlaybackMethod.DirectPlay);
        result.ToneMapActive.Should().BeFalse();
        result.SourceHdr.Should().Be("HDR10");
    }

    [Fact]
    public void Audio_downmix_to_stereo_forces_transcode()
    {
        var result = _decider.Decide(
            BuildSource(),
            Profile(),
            PlaybackMode.Manual,
            "original",
            _options,
            audioLayout: "stereo");

        // Source is 6ch; stereo downmix requires encode.
        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("AudioDownmix");
        result.SelectedAudioLayout.Should().Be("stereo");
        result.AvailableAudioLayouts.Select(l => l.Id).Should().Contain(["mono", "stereo", "2.1", "5.1"]);
    }

    [Fact]
    public void Force_hdr_keeps_tonemap_reason_when_audio_also_needs_downmix()
    {
        var result = _decider.Decide(
            BuildSource(hdr: "HDR10"),
            Profile(supportsHdr: true),
            PlaybackMode.Manual,
            "original",
            _options,
            forceHdrToSdr: true,
            audioLayout: "stereo");

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.ToneMapActive.Should().BeTrue();
        result.Reason.Should().Be("ForceHdrToSdr");
        result.SelectedAudioLayout.Should().Be("stereo");
    }

    [Fact]
    public void Audio_layout_5_1_keeps_direct_play_when_source_is_6ch()
    {
        var result = _decider.Decide(
            BuildSource(),
            Profile(),
            PlaybackMode.Manual,
            "original",
            _options,
            audioLayout: "5.1");

        result.Method.Should().Be(PlaybackMethod.DirectPlay);
        result.SelectedAudioLayout.Should().Be("5.1");
    }

    [Fact]
    public void Transcode_when_default_audio_unsupported_even_if_sidecar_matches()
    {
        // MKV with default AC-3 + AAC commentary — browsers reject AC-3 in fMP4.
        var source = BuildSource(container: "mkv", audioCodec: "ac3");
        source.AddStream(new MediaStream(StreamKind.Audio, 2)
        {
            Codec = "aac",
            Channels = 2,
            IsDefault = false,
        });

        var result = _decider.Decide(
            source,
            Profile(containers: ["mp4", "hls"], audioCodecs: ["aac"]),
            PlaybackMode.Auto,
            null,
            _options);

        result.Method.Should().Be(PlaybackMethod.Transcode);
        result.Reason.Should().Be("AudioCodecNotSupported");
    }

    [Fact]
    public void Direct_stream_when_explicit_audio_stream_matches_profile()
    {
        var source = BuildSource(container: "mkv", audioCodec: "ac3");
        var aac = new MediaStream(StreamKind.Audio, 2)
        {
            Codec = "aac",
            Channels = 2,
            IsDefault = false,
        };
        source.AddStream(aac);

        var result = _decider.Decide(
            source,
            Profile(containers: ["mp4", "hls"], audioCodecs: ["aac"]),
            PlaybackMode.Auto,
            null,
            _options,
            audioStreamId: aac.Id);

        result.Method.Should().Be(PlaybackMethod.DirectStream);
        result.Reason.Should().Be("ContainerNotSupported");
    }
}
