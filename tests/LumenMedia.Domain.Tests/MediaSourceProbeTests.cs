using FluentAssertions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Domain.Tests;

public class MediaSourceProbeTests
{
    [Fact]
    public void NeedsStreamProbe_true_when_video_codec_missing_or_unknown()
    {
        var empty = MediaSource.CreateTorrent(
            "/t/a.torrent", "abc", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        empty.AddStream(new MediaStream(StreamKind.Video, 0));
        empty.NeedsStreamProbe().Should().BeTrue();

        var unknown = MediaSource.CreateTorrent(
            "/t/b.torrent", "def", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        unknown.AddStream(new MediaStream(StreamKind.Video, 0) { Codec = "unknown" });
        unknown.NeedsStreamProbe().Should().BeTrue();
    }

    [Fact]
    public void NeedsStreamProbe_false_after_real_codec()
    {
        var source = MediaSource.CreateTorrent(
            "/t/c.torrent", "ghi", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0) { Codec = "hevc", Width = 1920, Height = 1080 });
        source.NeedsStreamProbe().Should().BeFalse();
    }

    [Fact]
    public void ReplaceStreams_swaps_collection()
    {
        var source = MediaSource.CreateTorrent(
            "/t/d.torrent", "jkl", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0) { Codec = "unknown" });
        source.ReplaceStreams(
        [
            new MediaStream(StreamKind.Video, 0) { Codec = "hevc", Width = 3840, Height = 2160 },
            new MediaStream(StreamKind.Audio, 1) { Codec = "eac3", Channels = 6 },
        ]);
        source.Streams.Should().HaveCount(2);
        source.NeedsStreamProbe().Should().BeFalse();
        source.Streams[0].Codec.Should().Be("hevc");
    }
}
