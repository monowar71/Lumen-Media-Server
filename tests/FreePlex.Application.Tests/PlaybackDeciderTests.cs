using FluentAssertions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Playback;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;

namespace FreePlex.Application.Tests;

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
        result.AvailableQualities.Select(q => q.Id).Should().NotContain("1080p");
        result.AvailableQualities.Select(q => q.Id).Should().NotContain("720p");
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
        act.Should().Throw<FreePlex.Application.Common.UnprocessableException>();
    }
}
