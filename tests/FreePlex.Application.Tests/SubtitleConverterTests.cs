using FluentAssertions;
using FreePlex.Infrastructure.Transcoding;

namespace FreePlex.Application.Tests;

public class SubtitleConverterTests
{
    [Fact]
    public void Srt_to_webvtt_rewrites_comma_timecodes()
    {
        const string srt =
            """
            1
            00:00:01,000 --> 00:00:04,000
            Hello

            2
            00:00:05,500 --> 00:00:07,000
            World
            """;

        var vtt = FfmpegSubtitleConverter.SrtToWebVtt(srt);

        vtt.Should().StartWith("WEBVTT");
        vtt.Should().Contain("00:00:01.000 --> 00:00:04.000");
        vtt.Should().Contain("Hello");
        vtt.Should().Contain("World");
        vtt.Should().NotContain(",");
    }
}
