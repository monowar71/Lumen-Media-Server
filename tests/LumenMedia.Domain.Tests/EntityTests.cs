using FluentAssertions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Jobs;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;
using LumenMedia.Domain.Users;

namespace LumenMedia.Domain.Tests;

public class EntityTests
{
    [Theory]
    [InlineData("The Matrix", "Matrix, The")]
    [InlineData("A Beautiful Mind", "Beautiful Mind, A")]
    [InlineData("An American Tail", "American Tail, An")]
    [InlineData("Inception", "Inception")]
    public void ComputeSortTitle_moves_leading_article_to_the_end(string title, string expected) =>
        MediaItem.ComputeSortTitle(title).Should().Be(expected);

    [Fact]
    public void Admin_can_access_any_library()
    {
        var now = DateTimeOffset.UtcNow;
        var admin = new User("root", "hash", UserRole.Admin, now);
        admin.CanAccessLibrary(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void Restricted_user_only_accesses_allowed_libraries()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User("kate", "hash", UserRole.User, now);
        var allowed = Guid.NewGuid();
        user.SetLibraryAccess(false, [allowed], now);

        user.CanAccessLibrary(allowed).Should().BeTrue();
        user.CanAccessLibrary(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void Progress_marks_watched_and_resets_position_past_ninety_percent_when_stopped()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new PlaybackProgress(Guid.NewGuid(), Guid.NewGuid(), MediaKind.Movie, now);

        progress.Update(positionMs: 9500, durationMs: 10000, stopped: true, now);

        progress.Watched.Should().BeTrue();
        progress.PositionMs.Should().Be(0);
        progress.PlayCount.Should().Be(1);
    }

    [Fact]
    public void Progress_SetWatched_clears_position_and_bumps_play_count_once()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new PlaybackProgress(Guid.NewGuid(), Guid.NewGuid(), MediaKind.Movie, now);
        progress.Update(positionMs: 4000, durationMs: 10000, stopped: false, now);

        progress.SetWatched(true, now);

        progress.Watched.Should().BeTrue();
        progress.PositionMs.Should().Be(0);
        progress.PlayCount.Should().Be(1);

        progress.SetWatched(true, now);
        progress.PlayCount.Should().Be(1);

        progress.SetWatched(false, now);
        progress.Watched.Should().BeFalse();
        progress.PositionMs.Should().Be(0);
    }

    [Fact]
    public void Progress_ClearWatchHistory_preserves_favorite()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new PlaybackProgress(Guid.NewGuid(), Guid.NewGuid(), MediaKind.Movie, now);
        progress.SetWatched(true, now);
        progress.SetFavorite(true, now);

        progress.ClearWatchHistory(now.AddMinutes(1));

        progress.Watched.Should().BeFalse();
        progress.PositionMs.Should().Be(0);
        progress.PlayCount.Should().Be(0);
        progress.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public void Progress_TryApplyImport_skips_older_snapshot()
    {
        var older = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var newer = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var progress = new PlaybackProgress(Guid.NewGuid(), Guid.NewGuid(), MediaKind.Movie, newer);
        progress.Update(positionMs: 1000, durationMs: 10_000, stopped: false, newer);

        var applied = progress.TryApplyImport(
            watched: true,
            positionMs: 0,
            durationMs: 10_000,
            playCount: 3,
            viewedAt: older);

        applied.Should().BeFalse();
        progress.Watched.Should().BeFalse();
        progress.PositionMs.Should().Be(1000);
    }

    [Fact]
    public void Progress_keeps_position_when_not_finished()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new PlaybackProgress(Guid.NewGuid(), Guid.NewGuid(), MediaKind.Movie, now);

        progress.Update(positionMs: 4000, durationMs: 10000, stopped: false, now);

        progress.Watched.Should().BeFalse();
        progress.PositionMs.Should().Be(4000);
    }

    [Fact]
    public void Job_can_be_cancelled_while_queued_or_running()
    {
        var now = DateTimeOffset.UtcNow;

        var queued = new BackgroundJob(JobType.ScanLibrary, now);
        queued.Cancel(now).Should().BeTrue();
        queued.State.Should().Be(JobState.Cancelled);

        var running = new BackgroundJob(JobType.ScanLibrary, now);
        running.Start(now);
        running.Cancel(now).Should().BeTrue();
        running.State.Should().Be(JobState.Cancelled);
    }

    [Fact]
    public void Job_cancel_does_not_overwrite_finished_states()
    {
        var now = DateTimeOffset.UtcNow;

        var succeeded = new BackgroundJob(JobType.ScanLibrary, now);
        succeeded.Start(now);
        succeeded.Succeed(now);
        succeeded.Cancel(now).Should().BeFalse();
        succeeded.State.Should().Be(JobState.Succeeded);

        var failed = new BackgroundJob(JobType.ScanLibrary, now);
        failed.Start(now);
        failed.Fail("boom", now);
        failed.Cancel(now).Should().BeFalse();
        failed.State.Should().Be(JobState.Failed);
    }

    [Fact]
    public void Season_and_episode_can_be_reassigned_for_series_merge()
    {
        var now = DateTimeOffset.UtcNow;
        var series = new Series(Guid.CreateVersion7(), "Andor", now);
        var season = new Season(series.Id, 1);
        var episode = new Episode(series.Id, season.Id, 1, 1, now);
        series.AddSeason(season);
        season.AddEpisode(episode);

        var targetSeriesId = Guid.CreateVersion7();
        var targetSeasonId = Guid.CreateVersion7();

        series.DetachSeason(season).Should().BeTrue();
        season.ReassignSeries(targetSeriesId);
        season.DetachEpisode(episode).Should().BeTrue();
        episode.Reassign(targetSeriesId, targetSeasonId);

        series.Seasons.Should().BeEmpty();
        season.SeriesId.Should().Be(targetSeriesId);
        season.Episodes.Should().BeEmpty();
        episode.SeriesId.Should().Be(targetSeriesId);
        episode.SeasonId.Should().Be(targetSeasonId);
    }
}
