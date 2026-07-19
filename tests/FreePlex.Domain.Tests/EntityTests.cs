using FluentAssertions;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Jobs;
using FreePlex.Domain.Media;
using FreePlex.Domain.Playback;
using FreePlex.Domain.Users;

namespace FreePlex.Domain.Tests;

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
}
