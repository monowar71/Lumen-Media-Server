using FluentAssertions;
using LumenMedia.Domain.Enums;
using LumenMedia.Infrastructure.Scanning;

namespace LumenMedia.Application.Tests;

public class FfprobeClientTests
{
    [Fact]
    public void Parse_maps_audio_and_subtitle_titles_and_disposition()
    {
        // Shape mirrors The.Creator BluRay release (MovieDalen / LostFilm / Forced subs).
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                {
                  "index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6,
                  "tags": { "language": "rus", "title": "MovieDalen" },
                  "disposition": { "default": 1, "forced": 0 }
                },
                {
                  "index": 2, "codec_type": "audio", "codec_name": "ac3", "channels": 6,
                  "tags": { "language": "rus", "title": "LostFilm" },
                  "disposition": { "default": 0, "forced": 0 }
                },
                {
                  "index": 7, "codec_type": "subtitle", "codec_name": "subrip",
                  "tags": { "language": "rus", "title": "Russian (Forced)" },
                  "disposition": { "default": 1, "forced": 1 }
                }
              ]
            }
            """;

        var result = FfprobeClient.ParseForTests(json);

        result.Streams.Should().HaveCount(4);

        var movieDalen = result.Streams.Single(s => s.Title == "MovieDalen");
        movieDalen.Kind.Should().Be(StreamKind.Audio);
        movieDalen.Language.Should().Be("rus");
        movieDalen.IsDefault.Should().BeTrue();
        movieDalen.IsForced.Should().BeFalse();

        var lostFilm = result.Streams.Single(s => s.Title == "LostFilm");
        lostFilm.Kind.Should().Be(StreamKind.Audio);
        lostFilm.IsDefault.Should().BeFalse();

        var forced = result.Streams.Single(s => s.Title == "Russian (Forced)");
        forced.Kind.Should().Be(StreamKind.Subtitle);
        forced.SubtitleFormat.Should().Be("subrip");
        forced.IsDefault.Should().BeTrue();
        forced.IsForced.Should().BeTrue();
    }

    [Fact]
    public void Parse_trims_blank_title_to_null()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 1, "codec_type": "audio", "codec_name": "aac",
                  "tags": { "language": "eng", "title": "   " }
                }
              ]
            }
            """;

        var stream = FfprobeClient.ParseForTests(json).Streams.Single();
        stream.Title.Should().BeNull();
        stream.Language.Should().Be("eng");
    }

    [Fact]
    public void Parse_detects_hdr10_from_color_transfer()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "hevc",
                  "width": 3840, "height": 2160,
                  "color_transfer": "smpte2084",
                  "color_primaries": "bt2020"
                }
              ]
            }
            """;

        var video = FfprobeClient.ParseForTests(json).Streams.Single();
        video.Hdr.Should().Be("HDR10");
    }

    [Fact]
    public void Parse_detects_hlg()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "hevc",
                  "color_transfer": "arib-std-b67"
                }
              ]
            }
            """;

        FfprobeClient.ParseForTests(json).Streams.Single().Hdr.Should().Be("HLG");
    }

    [Fact]
    public void Parse_detects_hdr10_plus_from_side_data()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "hevc",
                  "color_transfer": "smpte2084",
                  "side_data_list": [
                    { "side_data_type": "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)" }
                  ]
                }
              ]
            }
            """;

        FfprobeClient.ParseForTests(json).Streams.Single().Hdr.Should().Be("HDR10+");
    }

    [Fact]
    public void Parse_detects_dolby_vision_from_side_data()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "hevc",
                  "side_data_list": [
                    { "side_data_type": "DOVI configuration record" }
                  ]
                }
              ]
            }
            """;

        FfprobeClient.ParseForTests(json).Streams.Single().Hdr.Should().Be("DolbyVision");
    }

    [Fact]
    public void Parse_leaves_sdr_hdr_null()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0, "codec_type": "video", "codec_name": "h264",
                  "color_transfer": "bt709"
                }
              ]
            }
            """;

        FfprobeClient.ParseForTests(json).Streams.Single().Hdr.Should().BeNull();
    }
}
