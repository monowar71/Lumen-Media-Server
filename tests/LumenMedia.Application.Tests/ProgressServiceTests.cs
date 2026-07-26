using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class ProgressServiceTests
{
    private static Caller TestCaller(Guid userId) =>
        new(userId, UserRole.User, AllLibraries: true, LibraryIds: []);

    [Fact]
    public async Task Update_with_watched_marks_movie()
    {
        var userId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "Matrix", DateTimeOffset.UtcNow);
        var movieId = movie.Id;

        var media = Substitute.For<IMediaRepository>();
        media.GetByIdAsync(movieId, Arg.Any<CancellationToken>()).Returns(movie);

        PlaybackProgress? stored = null;
        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, movieId, Arg.Any<CancellationToken>())
            .Returns(_ => stored);
        progress.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PlaybackProgress>());

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Progress.Returns(progress);

        var notifier = Substitute.For<IRealtimeNotifier>();
        var sut = new ProgressService(uow, TimeProvider.System, notifier);

        var result = await sut.UpdateAsync(
            TestCaller(userId),
            movieId,
            new UpdateProgressRequest { Watched = true },
            default);

        result.Watched.Should().BeTrue();
        result.PositionMs.Should().Be(0);
        stored.Should().NotBeNull();
        stored!.Watched.Should().BeTrue();
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_with_watched_cascades_to_season_episodes()
    {
        var userId = Guid.CreateVersion7();
        var seriesId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var series = new Series(seriesId, "Show", now);
        var season = new Season(seriesId, 1);
        var ep1 = new Episode(seriesId, season.Id, 1, 1, now);
        var ep2 = new Episode(seriesId, season.Id, 1, 2, now);

        var media = Substitute.For<IMediaRepository>();
        media.GetByIdAsync(season.Id, Arg.Any<CancellationToken>()).Returns((MediaItem?)null);
        media.GetByIdAsync(seriesId, Arg.Any<CancellationToken>()).Returns(series);
        media.GetEpisodeAsync(season.Id, Arg.Any<CancellationToken>()).Returns((Episode?)null);
        media.GetSeasonAsync(season.Id, Arg.Any<CancellationToken>()).Returns(season);
        media.GetEpisodesAsync(season.Id, Arg.Any<CancellationToken>()).Returns([ep1, ep2]);

        var added = new List<PlaybackProgress>();
        var progressRepo = Substitute.For<IProgressRepository>();
        progressRepo.GetAsync(userId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((PlaybackProgress?)null);
        progressRepo.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => added.Add(ci.ArgAt<PlaybackProgress>(0)));

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Progress.Returns(progressRepo);

        var sut = new ProgressService(uow, TimeProvider.System, Substitute.For<IRealtimeNotifier>());
        var result = await sut.UpdateAsync(
            TestCaller(userId),
            season.Id,
            new UpdateProgressRequest { Watched = true },
            default);

        result.ItemId.Should().Be(season.Id);
        result.Watched.Should().BeTrue();
        added.Should().HaveCount(2);
        added.Select(p => p.MediaId).Should().BeEquivalentTo([ep1.Id, ep2.Id]);
        added.Should().OnlyContain(p => p.Watched);
    }

    [Fact]
    public async Task Update_with_watched_false_unmarks_episode()
    {
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var seriesId = Guid.CreateVersion7();
        var series = new Series(seriesId, "Show", now);
        var episode = new Episode(seriesId, Guid.CreateVersion7(), 1, 1, now);
        var stored = new PlaybackProgress(userId, episode.Id, MediaKind.Episode, now);
        stored.SetWatched(true, now);

        var media = Substitute.For<IMediaRepository>();
        media.GetByIdAsync(episode.Id, Arg.Any<CancellationToken>()).Returns((MediaItem?)null);
        media.GetByIdAsync(seriesId, Arg.Any<CancellationToken>()).Returns(series);
        media.GetEpisodeAsync(episode.Id, Arg.Any<CancellationToken>()).Returns(episode);

        var progressRepo = Substitute.For<IProgressRepository>();
        progressRepo.GetAsync(userId, episode.Id, Arg.Any<CancellationToken>()).Returns(stored);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Progress.Returns(progressRepo);

        var sut = new ProgressService(uow, TimeProvider.System, Substitute.For<IRealtimeNotifier>());
        var result = await sut.UpdateAsync(
            TestCaller(userId),
            episode.Id,
            new UpdateProgressRequest { Watched = false },
            default);

        result.Watched.Should().BeFalse();
        stored.Watched.Should().BeFalse();
    }
}
