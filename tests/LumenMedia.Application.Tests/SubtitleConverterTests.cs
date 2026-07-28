using FluentAssertions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Configuration;
using LumenMedia.Infrastructure.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LumenMedia.Application.Tests;

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

    [Fact]
    public async Task External_srt_is_cached_on_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumen-subcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var srtPath = Path.Combine(root, "movie.srt");
            await File.WriteAllTextAsync(
                srtPath,
                """
                1
                00:00:01,000 --> 00:00:02,000
                Cached
                """);

            var now = DateTimeOffset.UtcNow;
            var source = new MediaSource(srtPath, "mkv", 100, now, now);
            var stream = new MediaStream(StreamKind.Subtitle, 0)
            {
                Codec = "subrip",
                SubtitleFormat = "srt",
                IsExternal = true,
                ExternalPath = srtPath,
            };
            source.AddStream(stream);

            var paths = Options.Create(new PathsOptions
            {
                Config = root,
                Subtitles = Path.Combine(root, "subtitles"),
                Transcodes = Path.Combine(root, "transcodes"),
            });
            var converter = new FfmpegSubtitleConverter(paths, NullLogger<FfmpegSubtitleConverter>.Instance);

            var first = await converter.ToWebVttAsync(source, stream, CancellationToken.None);
            first.Should().Contain("Cached");

            var cacheFile = Directory.EnumerateFiles(Path.Combine(root, "subtitles"), "*.vtt", SearchOption.AllDirectories)
                .Single();
            File.Exists(cacheFile).Should().BeTrue();

            // Corrupt the source so a cache miss would fail / differ; hit must still return cache.
            await File.WriteAllTextAsync(srtPath, "not-srt");
            File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddMinutes(1));

            var second = await converter.ToWebVttAsync(source, stream, CancellationToken.None);
            second.Should().Be(first);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
