using FluentAssertions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Tests;

public class TorrentDirectPlayTests
{
    [Fact]
    public void ApplyTorrentPlaybackGuard_forces_transcode_when_unprobed()
    {
        var source = MediaSource.CreateTorrent(
            "/t/a.torrent", "abc", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0));

        var decision = new PlaybackDecisionResult
        {
            Method = PlaybackMethod.DirectPlay,
            Reason = "DirectPlay",
            SelectedQualityId = "original",
            AvailableQualities = [],
            SelectedAudioLayout = "stereo",
        };

        var guarded = PlaybackService.ApplyTorrentPlaybackGuard(source, decision);
        guarded.Method.Should().Be(PlaybackMethod.Transcode);
        guarded.Reason.Should().Be("TorrentStream");
    }

    [Fact]
    public void ApplyTorrentPlaybackGuard_remuxes_mkv_after_probe()
    {
        var source = MediaSource.CreateTorrent(
            "/t/a.torrent", "abc", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0) { Codec = "h264", Width = 1920, Height = 1080 });

        var decision = new PlaybackDecisionResult
        {
            Method = PlaybackMethod.DirectPlay,
            Reason = "DirectPlay",
            SelectedQualityId = "original",
            AvailableQualities = [],
            SelectedAudioLayout = "stereo",
        };

        var guarded = PlaybackService.ApplyTorrentPlaybackGuard(source, decision);
        guarded.Method.Should().Be(PlaybackMethod.DirectStream);
        guarded.Reason.Should().Be("TorrentRemux");
    }

    [Fact]
    public void ApplyTorrentPlaybackGuard_allows_direct_play_for_mp4_after_probe()
    {
        var source = MediaSource.CreateTorrent(
            "/t/a.torrent", "abc", 1, "ep.mp4", "mp4", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0) { Codec = "h264", Width = 1920, Height = 1080 });

        var decision = new PlaybackDecisionResult
        {
            Method = PlaybackMethod.DirectPlay,
            Reason = "DirectPlay",
            SelectedQualityId = "original",
            AvailableQualities = [],
            SelectedAudioLayout = "stereo",
        };

        var guarded = PlaybackService.ApplyTorrentPlaybackGuard(source, decision);
        guarded.Method.Should().Be(PlaybackMethod.DirectPlay);
        guarded.Reason.Should().Be("DirectPlay");
    }

    [Fact]
    public void ApplyTorrentPlaybackGuard_keeps_direct_stream_without_probe()
    {
        var source = MediaSource.CreateTorrent(
            "/t/a.torrent", "abc", 1, "ep.mkv", "mkv", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        source.AddStream(new MediaStream(StreamKind.Video, 0));

        var decision = new PlaybackDecisionResult
        {
            Method = PlaybackMethod.DirectStream,
            Reason = "ContainerNotSupported",
            SelectedQualityId = "original",
            AvailableQualities = [],
            SelectedAudioLayout = "stereo",
        };

        var guarded = PlaybackService.ApplyTorrentPlaybackGuard(source, decision);
        guarded.Method.Should().Be(PlaybackMethod.DirectStream);
    }
}
