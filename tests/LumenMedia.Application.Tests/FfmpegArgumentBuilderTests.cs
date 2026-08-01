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
        args.Should().ContainInOrder("-b:v", "4000k");
        args.Should().ContainInOrder("-avoid_negative_ts", "make_zero");
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
    public void Vaapi_transcode_uses_hwaccel_decode_and_scale_vaapi()
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
        args.Should().ContainInOrder("-hwaccel", "vaapi");
        args.Should().ContainInOrder("-hwaccel_device", "va");
        args.Should().ContainInOrder("-hwaccel_output_format", "vaapi");
        args.Should().ContainInOrder("-c:v", "h264_vaapi");
        args.Should().ContainInOrder("-vf", "scale_vaapi=-2:720:format=nv12");
        args.Should().NotContain(a => a.Contains("hwupload", StringComparison.Ordinal));
        args.Should().NotContain("libx264");
        args.Should().NotContain("-preset");
        args.Should().NotContain("yuv420p");

        var hwaccel = args.ToList().IndexOf("-hwaccel");
        var input = args.ToList().IndexOf("-i");
        hwaccel.Should().BeGreaterThanOrEqualTo(0);
        input.Should().BeGreaterThan(hwaccel);
    }

    [Fact]
    public void Vaapi_transcode_at_source_resolution_converts_format_on_gpu()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi" };
        var args = FfmpegArgumentBuilder.Build(
            Request(PlaybackMethod.Transcode, "auto", "VideoCodecNotSupported") with { SourceHeight = 2160, SourceWidth = 3840 },
            "/tmp/out",
            opts);

        args.Should().ContainInOrder("-hwaccel", "vaapi");
        args.Should().ContainInOrder("-vf", "scale_vaapi=format=nv12");
        args.Should().ContainInOrder("-c:v", "h264_vaapi");
    }

    [Fact]
    public void Vaapi_burn_in_uses_overlay_vaapi()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi" };
        var request = Request(PlaybackMethod.Transcode, "720p", "SubtitleBurnIn") with
        {
            SubtitleBurnInIndex = 3,
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "h264_vaapi");
        args.Should().ContainInOrder("-hwaccel", "vaapi");
        args.Should().Contain("-filter_complex");
        var fc = args[args.ToList().IndexOf("-filter_complex") + 1];
        fc.Should().Contain("overlay_vaapi=w=main_w:h=main_h");
        fc.Should().Contain("[0:3]format=bgra,hwupload[sub]");
        fc.Should().Contain("scale_vaapi=-2:720:format=nv12");
        args.Should().NotContain("-vf");
        args.Should().NotContain("libx264");
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
    public void Original_transcode_clamps_to_profile_max_height()
    {
        var request = Request(PlaybackMethod.Transcode, "original", "ResolutionTooHigh") with
        {
            SourceHeight = 2160,
            SourceWidth = 3840,
            MaxOutputHeight = 1080,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", new PlaybackOptions());

        args.Should().ContainInOrder("-vf", "scale=-2:1080");
        args.Should().ContainInOrder("-b:v", "10000k");
    }

    [Fact]
    public void Auto_vaapi_transcode_clamps_to_profile_max_height()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi" };
        var request = Request(PlaybackMethod.Transcode, "auto", "ResolutionTooHigh") with
        {
            SourceHeight = 2160,
            SourceWidth = 3840,
            MaxOutputHeight = 1080,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-vf", "scale_vaapi=-2:1080:format=nv12");
        args.Should().ContainInOrder("-b:v", "10000k");
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

    [Fact]
    public void Tone_map_with_vaapi_uses_tonemap_vaapi()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi", HdrToneMapMethod = "mobius" };
        var request = Request(PlaybackMethod.Transcode, "720p", "HdrNotSupported") with
        {
            ToneMap = true,
            HdrToneMapMethod = "vaapi",
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "h264_vaapi");
        args.Should().ContainInOrder("-hwaccel", "vaapi");
        args.Should().ContainInOrder("-hwaccel_output_format", "vaapi");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        vf.Should().Contain("tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709");
        vf.Should().Contain("scale_vaapi=w=-2:h=720");
        // Session vaapi method ignores admin software algorithm.
        vf.Should().NotContain("tonemap=mobius");
        args.Should().NotContain("libx264");
    }

    [Fact]
    public void Tone_map_software_method_forces_libx264_even_when_vaapi_configured()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi", HdrToneMapMethod = "hable" };
        var request = Request(PlaybackMethod.Transcode, "720p", "ForceHdrToSdr") with
        {
            ToneMap = true,
            HdrToneMapMethod = "mobius",
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().NotContain("-hwaccel");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        vf.Should().Contain("tonemap=mobius");
        vf.Should().NotContain("tonemap_vaapi");
    }

    [Fact]
    public void Tone_map_without_vaapi_uses_software_filter_and_scales_in_zscale()
    {
        var opts = new PlaybackOptions { HardwareAccel = "none", HdrToneMapMethod = "mobius" };
        var request = Request(PlaybackMethod.Transcode, "720p", "HdrNotSupported") with
        {
            ToneMap = true,
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "libx264");
        args.Should().NotContain("h264_vaapi");
        args.Should().NotContain("-hwaccel");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        vf.Should().Contain("tonemap=mobius");
        vf.Should().Contain("zscale=w=-2:h=720:t=linear:npl=100");
        vf.Should().NotContain("scale=-2:720");
    }

    [Fact]
    public void Tone_map_with_burn_in_stays_on_vaapi()
    {
        var opts = new PlaybackOptions { HardwareAccel = "vaapi" };
        var request = Request(PlaybackMethod.Transcode, "720p", "ForceHdrToSdr") with
        {
            ToneMap = true,
            HdrToneMapMethod = "vaapi",
            SubtitleBurnInIndex = 3,
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "h264_vaapi");
        var fc = args[args.ToList().IndexOf("-filter_complex") + 1];
        fc.Should().Contain("tonemap_vaapi=");
        fc.Should().Contain("overlay_vaapi=");
        fc.Should().NotContain("tonemap=hable");
        args.Should().NotContain("libx264");
    }

    [Fact]
    public void Burn_in_without_vaapi_uses_software_overlay()
    {
        var opts = new PlaybackOptions { HardwareAccel = "none" };
        var request = Request(PlaybackMethod.Transcode, "720p", "SubtitleBurnIn") with
        {
            SubtitleBurnInIndex = 3,
            SourceHeight = 1080,
            SourceWidth = 1920,
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", opts);

        args.Should().ContainInOrder("-c:v", "libx264");
        var fc = args[args.ToList().IndexOf("-filter_complex") + 1];
        fc.Should().Contain("[0:3]overlay[v]");
        fc.Should().NotContain("overlay_vaapi");
    }

    [Fact]
    public void Audio_layout_5_1_sets_channels_and_bitrate()
    {
        var request = Request(PlaybackMethod.Transcode, "auto", "AudioDownmix") with
        {
            AudioLayout = "5.1",
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", new PlaybackOptions());

        args.Should().ContainInOrder("-ac", "6");
        args.Should().ContainInOrder("-channel_layout", "5.1");
        args.Should().ContainInOrder("-b:a", "384k");
    }

    [Fact]
    public void Audio_layout_2_1_sets_three_channels()
    {
        var request = Request(PlaybackMethod.Transcode, "auto", "AudioDownmix") with
        {
            AudioLayout = "2.1",
        };
        var args = FfmpegArgumentBuilder.Build(request, "/tmp/out", new PlaybackOptions());

        args.Should().ContainInOrder("-ac", "3");
        args.Should().ContainInOrder("-channel_layout", "2.1");
        args.Should().ContainInOrder("-b:a", "192k");
    }
}
