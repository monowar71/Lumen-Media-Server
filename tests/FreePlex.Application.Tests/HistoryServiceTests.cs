using FluentAssertions;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Playback;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;
using FreePlex.Domain.Playback;
using NSubstitute;

namespace FreePlex.Application.Tests;

public sealed class HistoryServiceTests
{
    [Fact]
    public async Task Clear_removes_non_favorite_history_and_keeps_favorite_flag()
    {
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var clock = new FakeTimeProvider(now);

        var plain = new PlaybackProgress(userId, Guid.CreateVersion7(), MediaKind.Movie, now.AddDays(-1));
        plain.SetWatched(true, now.AddDays(-1));

        var favorite = new PlaybackProgress(userId, Guid.CreateVersion7(), MediaKind.Movie, now.AddDays(-2));
        favorite.SetWatched(true, now.AddDays(-2));
        favorite.SetFavorite(true, now.AddDays(-2));

        var removed = new List<PlaybackProgress>();
        var progress = Substitute.For<IProgressRepository>();
        progress.ListHistoryForClearAsync(userId, Arg.Any<CancellationToken>())
            .Returns([plain, favorite]);
        progress.When(p => p.Remove(Arg.Any<PlaybackProgress>()))
            .Do(ci =>
            {
                var item = ci.ArgAt<PlaybackProgress>(0);
                removed.Add(item);
            });

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);

        var sut = new HistoryService(uow, clock, Substitute.For<IPlexHistoryClient>());
        var result = await sut.ClearAsync(userId, default);

        result.ClearedCount.Should().Be(2);
        removed.Should().ContainSingle().Which.Should().BeSameAs(plain);
        favorite.Watched.Should().BeFalse();
        favorite.IsFavorite.Should().BeTrue();
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportFromPlex_matches_movie_by_tmdb_and_applies_watched()
    {
        var userId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "The Matrix", DateTimeOffset.UtcNow);
        movie.SetExternalIds("603", null, "tt0133093");
        var viewedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z");

        var plex = Substitute.For<IPlexHistoryClient>();
        plex.FetchWatchStateAsync(Arg.Any<Uri>(), "token", Arg.Any<CancellationToken>())
            .Returns([
                new PlexWatchEntry(
                    PlexWatchKind.Movie,
                    "The Matrix",
                    "603",
                    null,
                    "tt0133093",
                    null,
                    null,
                    Watched: true,
                    PositionMs: 0,
                    DurationMs: 8_160_000,
                    PlayCount: 2,
                    ViewedAt: viewedAt),
            ]);

        PlaybackProgress? stored = null;
        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, movie.Id, Arg.Any<CancellationToken>()).Returns(_ => stored);
        progress.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PlaybackProgress>());

        var media = Substitute.For<IMediaRepository>();
        media.FindMovieByExternalIdsAsync("603", null, "tt0133093", Arg.Any<CancellationToken>())
            .Returns(movie);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.Media.Returns(media);

        var sut = new HistoryService(uow, TimeProvider.System, plex);
        var result = await sut.ImportFromPlexAsync(
            userId,
            new ImportPlexHistoryRequest { BaseUrl = "http://192.168.0.10:32400", Token = "token" },
            default);

        result.Scanned.Should().Be(1);
        result.Matched.Should().Be(1);
        result.Imported.Should().Be(1);
        result.Unmatched.Should().Be(0);
        stored.Should().NotBeNull();
        stored!.Watched.Should().BeTrue();
        stored.PlayCount.Should().Be(2);
        stored.UpdatedAt.Should().Be(viewedAt);
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportFromPlex_skips_when_local_progress_is_newer()
    {
        var userId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "The Matrix", DateTimeOffset.UtcNow);
        movie.SetExternalIds("603", null, null);

        var localUpdated = DateTimeOffset.Parse("2025-06-01T00:00:00Z");
        var plexViewed = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var existing = new PlaybackProgress(userId, movie.Id, MediaKind.Movie, localUpdated);
        existing.SetWatched(true, localUpdated);

        var plex = Substitute.For<IPlexHistoryClient>();
        plex.FetchWatchStateAsync(Arg.Any<Uri>(), "token", Arg.Any<CancellationToken>())
            .Returns([
                new PlexWatchEntry(
                    PlexWatchKind.Movie,
                    "The Matrix",
                    "603",
                    null,
                    null,
                    null,
                    null,
                    Watched: true,
                    PositionMs: 0,
                    DurationMs: null,
                    PlayCount: 1,
                    ViewedAt: plexViewed),
            ]);

        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, movie.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var media = Substitute.For<IMediaRepository>();
        media.FindMovieByExternalIdsAsync("603", null, null, Arg.Any<CancellationToken>()).Returns(movie);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.Media.Returns(media);

        var sut = new HistoryService(uow, TimeProvider.System, plex);
        var result = await sut.ImportFromPlexAsync(
            userId,
            new ImportPlexHistoryRequest { BaseUrl = "http://plex.local:32400", Token = "token" },
            default);

        result.Matched.Should().Be(1);
        result.Imported.Should().Be(0);
        result.SkippedNewer.Should().Be(1);
        await uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportFromPlex_rejects_invalid_url()
    {
        var sut = new HistoryService(
            Substitute.For<IUnitOfWork>(),
            TimeProvider.System,
            Substitute.For<IPlexHistoryClient>());

        var act = () => sut.ImportFromPlexAsync(
            Guid.CreateVersion7(),
            new ImportPlexHistoryRequest { BaseUrl = "not-a-url", Token = "token" },
            default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
