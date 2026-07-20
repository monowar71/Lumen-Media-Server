using FluentAssertions;
using LumenMedia.Application.Libraries;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;

namespace LumenMedia.Application.Tests;

public sealed class SeriesNextUpTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
    private static readonly Guid SeriesId = Guid.CreateVersion7();
    private static readonly Guid Season1 = Guid.CreateVersion7();
    private static readonly Guid Season0 = Guid.CreateVersion7();

    private static Episode Ep(Guid seasonId, int season, int number) =>
        new(SeriesId, seasonId, season, number, Now);

    private static PlaybackProgress Progress(Guid episodeId, bool watched, long positionMs = 0)
    {
        var p = new PlaybackProgress(Guid.CreateVersion7(), episodeId, MediaKind.Episode, Now);
        if (watched)
            p.SetWatched(true, Now);
        else if (positionMs > 0)
            p.Update(positionMs, durationMs: 40 * 60 * 1000, stopped: false, now: Now);
        return p;
    }

    [Fact]
    public void Select_returns_first_unwatched_regular_episode()
    {
        var e1 = Ep(Season1, 1, 1);
        var e2 = Ep(Season1, 1, 2);
        var list = new[]
        {
            new SeriesNextUp.Candidate(e1, Progress(e1.Id, watched: true)),
            new SeriesNextUp.Candidate(e2, null),
        };

        var next = SeriesNextUp.Select(list);
        next!.Episode.Id.Should().Be(e2.Id);
    }

    [Fact]
    public void Select_prefers_in_progress_over_later_unwatched()
    {
        var e1 = Ep(Season1, 1, 1);
        var e2 = Ep(Season1, 1, 2);
        var list = new[]
        {
            new SeriesNextUp.Candidate(e1, Progress(e1.Id, watched: false, positionMs: 5 * 60 * 1000)),
            new SeriesNextUp.Candidate(e2, null),
        };

        var next = SeriesNextUp.Select(list);
        next!.Episode.Id.Should().Be(e1.Id);
        next.Progress!.PositionMs.Should().Be(5 * 60 * 1000);
    }

    [Fact]
    public void Select_skips_specials_when_regular_episodes_exist()
    {
        var special = Ep(Season0, 0, 1);
        var e1 = Ep(Season1, 1, 1);
        var list = new[]
        {
            new SeriesNextUp.Candidate(special, null),
            new SeriesNextUp.Candidate(e1, null),
        };

        SeriesNextUp.Select(list)!.Episode.Id.Should().Be(e1.Id);
    }

    [Fact]
    public void Select_uses_specials_when_only_specials()
    {
        var special = Ep(Season0, 0, 1);
        var list = new[] { new SeriesNextUp.Candidate(special, null) };
        SeriesNextUp.Select(list)!.Episode.Id.Should().Be(special.Id);
    }

    [Fact]
    public void Select_returns_null_when_all_watched()
    {
        var e1 = Ep(Season1, 1, 1);
        var list = new[]
        {
            new SeriesNextUp.Candidate(e1, Progress(e1.Id, watched: true)),
        };
        SeriesNextUp.Select(list).Should().BeNull();
    }
}
