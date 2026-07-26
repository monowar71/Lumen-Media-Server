using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Infrastructure.Transcoding;

namespace LumenMedia.Application.Tests;

public class FfmpegArgumentBuilderTests
{
    private static TranscodeRequest Request(
        PlaybackMethod method,
        string quality,
        string reason,
        long startMs = 0) => new()
    {
        Session = new PlaybackSession
        {
            SessionId = "sess-test",
            UserId = Guid.NewGuid(),
            MediaId = Guid.NewGuid(),
            MediaSourceId = Guid.NewGuid(),
            SourcePath = "/media/show/S01E01.mkv",
            Container = "mkv",
            Method = method,
            Mode = PlaybackMode.Auto,
            SelectedQualityId = quality,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        },
        QualityId = quality,
        StartPositionMs = startMs,
        Reason = reason,
    };

    [Fact]
    public void Direct_stream_copies_video_and_audio()
    {
        var args = FfmpegArgumentBuilder.Build(Request(PlaybackMethod.DirectStream, "auto", "ContainerNotSupported"), "/tmp/out", new PlaybackOptions());

        args.Should().ContainInOrder("-c:v", "copy");
        args.Should().ContainInOrder("-c:a", "copy");
        args.Should().Contain("-hls_segment_type");
        args.Should().Contain("fmp4");
        args.Should().NotContain("libx264");
    }

    [Fact]
    public void Audio_unsupported_encodes_video_with_short_gop()
    {
        var opts = new PlaybackOptions { SegmentDurationSec = 2, InitialSegmentDurationSec = 1 };
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "auto", "AudioCodecNotSupported"),
            "/tmp/out",
            opts);

        // BluRay-length GOPs make -c:v copy unstable in HLS — always re-encode.
        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().ContainInOrder("-c:a", "aac");
        args.Should().ContainInOrder("-ac", "2");
        args.Should().ContainInOrder("-g", "60");
        args.Should().ContainInOrder("-hls_time", "2");
        args.Should().ContainInOrder("-hls_init_time", "1");
        args.Should().Contain("independent_segments");
        args.Should().NotContain(a => a.Contains("split_by_time", StringComparison.Ordinal));
        args.Should().ContainInOrder("-probesize", "1000000");
    }

    [Fact]
    public void Selected_audio_stream_index_is_mapped()
    {
        var request = Request(PlaybackMethod.Transcode, "auto", "AudioCodecNotSupported");
        request = request with { AudioStreamIndex = 3 };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", new PlaybackOptions());

        args.Should().ContainInOrder("-map", "0:3");
        args.Should().NotContain("0:a:0?");
    }

    [Fact]
    public void Ladder_rung_encodes_and_scales()
    {
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "720p", "BitrateTooHigh") with { SourceHeight = 1080, SourceWidth = 1920 },
            "/tmp/out",
            new PlaybackOptions());

        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().ContainInOrder("-vf", "scale=-2:720");
        args.Should().ContainInOrder("-b:v", "4000k");
        args.Should().ContainInOrder("-g", "60");
    }

    [Fact]
    public void Ladder_rung_clamps_scale_to_source_height()
    {
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "1080p", "ManualQuality") with { SourceHeight = 696, SourceWidth = 1920 },
            "/tmp/out",
            new PlaybackOptions());

        args.Should().ContainInOrder("-vf", "scale=-2:696");
        args.Should().ContainInOrder("-b:v", "10000k");
    }

    [Fact]
    public void Vaapi_transcode_uses_hwupload_and_scale_vaapi()
    {
        var opts = new PlaybackOptions
        {
            HardwareAccel = "vaapi",
            VaapiDevice = "/dev/dri/renderD128",
        };
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "720p", "BitrateTooHigh") with { SourceHeight = 1080, SourceWidth = 1920 },
            "/tmp/out",
            opts);

        args.Should().ContainInOrder("-init_hw_device", "vaapi=va:/dev/dri/renderD128");
        args.Should().ContainInOrder("-filter_hw_device", "va");
        args.Should().ContainInOrder("-c:v", "h264_vaapi");
        args.Should().ContainInOrder("-vf", "format=nv12,hwupload,scale_vaapi=-2:720");
        args.Should().NotContain("libx264");
        args.Should().NotContain("-preset");
        args.Should().NotContain("yuv420p");
    }

    [Fact]
    public void Vaapi_burn_in_falls_back_to_software()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi" };
        var request = Request(PlaybackMethod.Transcode, "720p", "SubtitleBurnIn") with
        {
            SubtitleBurnInIndex = 3,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().NotContain("h264_vaapi");
        args.Should().NotContain("-init_hw_device");
    }

    [Fact]
    public void Seek_adds_ss_before_input()
    {
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "original", "AudioCodecNotSupported", startMs: 12_500),
            "/tmp/out",
            new PlaybackOptions());

        var ss = args.ToList().IndexOf("-ss");
        var input = args.ToList().IndexOf("-i");
        ss.Should().BeGreaterThanOrEqualTo(0);
        input.Should().BeGreaterThan(ss);
        args[ss + 1].Should().Be("12.5");
    }

    [Fact]
    public void Never_builds_a_shell_string_path_stays_own_argv()
    {
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.DirectStream, "auto", "ContainerNotSupported"),
            "/tmp/out",
            new PlaybackOptions());

        args.Should().Contain("/media/show/S01E01.mkv");
        string.Join(' ', args).Should().NotContain("&&");
    }
}
