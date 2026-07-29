using FluentAssertions;
using LumenMedia.Infrastructure.Metadata;

namespace LumenMedia.Application.Tests;

public sealed class ThemeSongServiceTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=SLBACEP6LsI", "SLBACEP6LsI")]
    [InlineData("https://youtu.be/SLBACEP6LsI", "SLBACEP6LsI")]
    [InlineData("https://www.youtube.com/shorts/SLBACEP6LsI", "SLBACEP6LsI")]
    [InlineData("https://www.youtube.com/embed/SLBACEP6LsI", "SLBACEP6LsI")]
    [InlineData("https://www.youtube.com/watch?v=SLBACEP6LsI&list=PLxxx", "SLBACEP6LsI")]
    public void ExtractVideoId_parses_common_youtube_urls(string url, string expected) =>
        ThemeSongService.ExtractVideoId(url).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://example.com/watch?v=SLBACEP6LsI")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    public void ExtractVideoId_rejects_invalid(string url) =>
        ThemeSongService.ExtractVideoId(url).Should().BeNull();
}
