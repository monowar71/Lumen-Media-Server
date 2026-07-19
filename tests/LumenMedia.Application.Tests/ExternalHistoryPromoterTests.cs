using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class ExternalHistoryPromoterTests
{
    [Fact]
    public async Task PromoteForMovie_moves_external_row_into_playback_progress()
    {
        var userId = Guid.CreateVersion7();
        var libraryId = Guid.CreateVersion7();
        var viewedAt = DateTimeOffset.Parse("2025-06-01T00:00:00Z");
        var movie = new Movie(libraryId, "Unknown Plex Film", viewedAt);
        movie.SetExternalIds("999001", null, null);

        var external = new ExternalPlaybackHistory(
            userId,
            "m:tmdb:999001",
            MediaKind.Movie,
            "Unknown Plex Film",
            null,
            null,
            null,
            viewedAt);
        external.SetExternalIds("999001", null, null);
        external.TryApplyImport(true, 0, 7_200_000, 1, viewedAt);

        PlaybackProgress? stored = null;
        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, movie.Id, Arg.Any<CancellationToken>()).Returns(_ => stored);
        progress.When(p => p.AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PlaybackProgress>());

        var externalRepo = Substitute.For<IExternalHistoryRepository>();
        externalRepo.FindByDedupeKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([external]);
        externalRepo.DeleteAsync(userId, "m:tmdb:999001", Arg.Any<CancellationToken>()).Returns(1);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.ExternalHistory.Returns(externalRepo);

        var sut = new ExternalHistoryPromoter(uow);
        var promoted = await sut.PromoteForMovieAsync(movie, default);

        promoted.Should().Be(1);
        stored.Should().NotBeNull();
        stored!.Watched.Should().BeTrue();
        stored.MediaId.Should().Be(movie.Id);
        stored.UpdatedAt.Should().Be(viewedAt);
        await externalRepo.Received().DeleteAsync(userId, "m:tmdb:999001", Arg.Any<CancellationToken>());
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteForMovie_matches_title_fallback_key()
    {
        var userId = Guid.CreateVersion7();
        var libraryId = Guid.CreateVersion7();
        var viewedAt = DateTimeOffset.Parse("2025-06-02T00:00:00Z");
        var movie = new Movie(libraryId, "Some Title", viewedAt);

        var key = ExternalPlaybackHistory.BuildDedupeKey(
            MediaKind.Movie, "Some Title", null, null, null, null, null, null);
        var external = new ExternalPlaybackHistory(
            userId, key, MediaKind.Movie, "Some Title", null, null, null, viewedAt);
        external.TryApplyImport(true, 0, null, 1, viewedAt);

        var progress = Substitute.For<IProgressRepository>();
        progress.GetAsync(userId, movie.Id, Arg.Any<CancellationToken>())
            .Returns((PlaybackProgress?)null);

        var externalRepo = Substitute.For<IExternalHistoryRepository>();
        externalRepo.FindByDedupeKeysAsync(
                Arg.Is<IReadOnlyCollection<string>>(k => k != null && k.Contains(key)),
                Arg.Any<CancellationToken>())
            .Returns([external]);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Progress.Returns(progress);
        uow.ExternalHistory.Returns(externalRepo);

        var sut = new ExternalHistoryPromoter(uow);
        (await sut.PromoteForMovieAsync(movie, default)).Should().Be(1);
        await progress.Received(1).AddAsync(Arg.Any<PlaybackProgress>(), Arg.Any<CancellationToken>());
    }
}
