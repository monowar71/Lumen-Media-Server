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
        var external = Substitute.For<IExternalHistoryRepository>();
        external.DeleteAllForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(0);
        uow.ExternalHistory.Returns(external);

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
        var external = Substitute.For<IExternalHistoryRepository>();
        external.DeleteAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        uow.ExternalHistory.Returns(external);

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
        var external = Substitute.For<IExternalHistoryRepository>();
        external.DeleteAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        uow.ExternalHistory.Returns(external);

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

    [Fact]
    public async Task ImportFromPlex_matches_episode_by_series_tmdb_and_applies_resume()
    {
        var userId = Guid.CreateVersion7();
        var series = new Series(Guid.CreateVersion7(), "Основание", DateTimeOffset.UtcNow);
        series.SetExternalIds("93740", null, null);
        var episode = new Episode(series.Id, Guid.CreateVersion7(), 2, 10, DateTimeOffset.UtcNow);
        var viewedAt = DateTimeOffset.Parse("2025-01-15T00:00:00Z");

        var plex = Substitute.For<IPlexHistoryClient>();
        plex.FetchWatchStateAsync(Arg.Any<Uri>(), "token", Arg.Any<CancellationToken>())
            .Returns([
                new PlexWatchEntry(
                    PlexWatchKind.Episode,
                    "Мифы о сотворении",
                    "93740",
                    null,
                    null,
                    SeasonNumber: 2,
                    EpisodeNumber: 10,
                    Watched: false,
                    PositionMs: 1_412_091,
                    DurationMs: 3_235_136,
                    PlayCount: 1,
                    ViewedAt: viewedAt,
                    SeriesTitle: "Основание"),
            ]);

        PlaybackProgress? stored = null;
        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, episode.Id, Arg.Any<CancellationToken>()).Returns(_ => stored);
        progress.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PlaybackProgress>());

        var media = Substitute.For<IMediaRepository>();
        media.FindSeriesByExternalIdsAsync("93740", null, null, Arg.Any<CancellationToken>()).Returns(series);
        media.FindEpisodeForScanAsync(series.Id, 2, 10, Arg.Any<CancellationToken>()).Returns(episode);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.Media.Returns(media);
        var external = Substitute.For<IExternalHistoryRepository>();
        external.DeleteAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        uow.ExternalHistory.Returns(external);

        var sut = new HistoryService(uow, TimeProvider.System, plex);
        var result = await sut.ImportFromPlexAsync(
            userId,
            new ImportPlexHistoryRequest { BaseUrl = "http://192.168.0.10:32400", Token = "token" },
            default);

        result.Matched.Should().Be(1);
        result.Imported.Should().Be(1);
        stored.Should().NotBeNull();
        stored!.Watched.Should().BeFalse();
        stored.PositionMs.Should().Be(1_412_091);
    }

    [Fact]
    public async Task ImportFromPlex_matches_episode_by_series_title_fallback()
    {
        var userId = Guid.CreateVersion7();
        var series = new Series(Guid.CreateVersion7(), "Тьма", DateTimeOffset.UtcNow);
        var episode = new Episode(series.Id, Guid.CreateVersion7(), 1, 5, DateTimeOffset.UtcNow);

        var plex = Substitute.For<IPlexHistoryClient>();
        plex.FetchWatchStateAsync(Arg.Any<Uri>(), "token", Arg.Any<CancellationToken>())
            .Returns([
                new PlexWatchEntry(
                    PlexWatchKind.Episode,
                    "Правда",
                    TmdbId: null,
                    TvdbId: null,
                    ImdbId: null,
                    SeasonNumber: 1,
                    EpisodeNumber: 5,
                    Watched: false,
                    PositionMs: 2_554_758,
                    DurationMs: 2_737_472,
                    PlayCount: 0,
                    ViewedAt: DateTimeOffset.Parse("2025-02-01T00:00:00Z"),
                    SeriesTitle: "Тьма"),
            ]);

        PlaybackProgress? stored = null;
        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, episode.Id, Arg.Any<CancellationToken>()).Returns(_ => stored);
        progress.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PlaybackProgress>());

        var media = Substitute.For<IMediaRepository>();
        media.FindSeriesByExternalIdsAsync(null, null, null, Arg.Any<CancellationToken>()).Returns((Series?)null);
        media.FindSeriesByTitleAsync("Тьма", Arg.Any<CancellationToken>()).Returns(series);
        media.FindEpisodeForScanAsync(series.Id, 1, 5, Arg.Any<CancellationToken>()).Returns(episode);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.Media.Returns(media);
        var external = Substitute.For<IExternalHistoryRepository>();
        external.DeleteAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        uow.ExternalHistory.Returns(external);

        var sut = new HistoryService(uow, TimeProvider.System, plex);
        var result = await sut.ImportFromPlexAsync(
            userId,
            new ImportPlexHistoryRequest { BaseUrl = "http://192.168.0.10:32400", Token = "token" },
            default);

        result.Matched.Should().Be(1);
        result.Imported.Should().Be(1);
        stored!.PositionMs.Should().Be(2_554_758);
        await media.Received(1).FindSeriesByTitleAsync("Тьма", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportFromPlex_persists_unmatched_as_external_history()
    {
        var userId = Guid.CreateVersion7();
        var viewedAt = DateTimeOffset.Parse("2025-03-01T00:00:00Z");

        var plex = Substitute.For<IPlexHistoryClient>();
        plex.FetchWatchStateAsync(Arg.Any<Uri>(), "token", Arg.Any<CancellationToken>())
            .Returns([
                new PlexWatchEntry(
                    PlexWatchKind.Movie,
                    "Unknown Plex Film",
                    "999001",
                    null,
                    null,
                    SeasonNumber: null,
                    EpisodeNumber: null,
                    Watched: true,
                    PositionMs: 0,
                    DurationMs: 7_200_000,
                    PlayCount: 1,
                    ViewedAt: viewedAt),
            ]);

        var progress = Substitute.For<IProgressRepository>();
        var media = Substitute.For<IMediaRepository>();
        media.FindMovieByExternalIdsAsync("999001", null, null, Arg.Any<CancellationToken>())
            .Returns((Movie?)null);
        media.FindMovieByTitleAsync("Unknown Plex Film", Arg.Any<CancellationToken>())
            .Returns((Movie?)null);

        ExternalPlaybackHistory? stored = null;
        var external = Substitute.For<IExternalHistoryRepository>();
        external.GetAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => stored);
        external.When(e => e.AddAsync(Arg.Any<ExternalPlaybackHistory>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<ExternalPlaybackHistory>());

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.Media.Returns(media);
        uow.ExternalHistory.Returns(external);

        var sut = new HistoryService(uow, TimeProvider.System, plex);
        var result = await sut.ImportFromPlexAsync(
            userId,
            new ImportPlexHistoryRequest { BaseUrl = "http://192.168.0.10:32400", Token = "token" },
            default);

        result.Scanned.Should().Be(1);
        result.Matched.Should().Be(0);
        result.Unmatched.Should().Be(1);
        result.Imported.Should().Be(1);
        stored.Should().NotBeNull();
        stored!.Title.Should().Be("Unknown Plex Film");
        stored.Watched.Should().BeTrue();
        stored.TmdbId.Should().Be("999001");
        await progress.DidNotReceive().AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>());
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_merges_matched_and_external_rows_by_updated_at()
    {
        var userId = Guid.CreateVersion7();
        var movieId = Guid.CreateVersion7();
        var matchedAt = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        var externalAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z");

        var progressRow = new PlaybackProgress(userId, movieId, MediaKind.Movie, matchedAt);
        progressRow.SetWatched(true, matchedAt);

        var externalRow = new ExternalPlaybackHistory(
            userId,
            "m:tmdb:42",
            MediaKind.Movie,
            "External Only",
            null,
            null,
            null,
            externalAt);
        externalRow.TryApplyImport(true, 0, null, 1, externalAt);

        var progress = Substitute.For<IProgressRepository>();
        progress.ListAllHistoryAsync(userId, Arg.Any<CancellationToken>()).Returns([progressRow]);

        var external = Substitute.For<IExternalHistoryRepository>();
        external.ListAllAsync(userId, Arg.Any<CancellationToken>()).Returns([externalRow]);

        var media = Substitute.For<IMediaRepository>();
        media.GetSummariesByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), userId, Arg.Any<CancellationToken>())
            .Returns([
                new MediaItemSummary
                {
                    Id = movieId,
                    Kind = MediaKind.Movie,
                    Title = "Local Movie",
                    Year = 1999,
                    Artwork = new ArtworkUrls(),
                },
            ]);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.ExternalHistory.Returns(external);
        uow.Media.Returns(media);

        var sut = new HistoryService(uow, TimeProvider.System, Substitute.For<IPlexHistoryClient>());
        var result = await sut.ListAsync(userId, 1, 50, default);

        result.Total.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].Title.Should().Be("External Only");
        result.Items[0].IsExternal.Should().BeTrue();
        result.Items[0].ItemId.Should().BeNull();
        result.Items[1].Title.Should().Be("Local Movie");
        result.Items[1].IsExternal.Should().BeFalse();
        result.Items[1].ItemId.Should().Be(movieId);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
