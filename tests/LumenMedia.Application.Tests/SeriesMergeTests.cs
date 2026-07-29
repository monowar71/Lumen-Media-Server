using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;
using LumenMedia.Infrastructure.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class SeriesMergeTests
{
    private static (
        MetadataEnricher Sut,
        IMediaRepository Media,
        IProgressRepository Progress,
        IArtworkStore Artwork) CreateSut(Series primary, MetadataDetails details)
    {
        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(primary.Id, Arg.Any<CancellationToken>()).Returns(primary);
        media.GetOrCreatePersonAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new Person(call.ArgAt<string>(0), call.ArgAt<string?>(1)));
        media.GetTrackedEpisodesForSeriesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        media.GetTrackedSeriesGraphAsync(primary.Id, Arg.Any<CancellationToken>()).Returns(primary);

        var progress = Substitute.For<IProgressRepository>();
        var artwork = Substitute.For<IArtworkStore>();
        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Library?)null);

        var external = Substitute.For<IExternalHistoryRepository>();
        external.FindByDedupeKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tx = Substitute.For<IAppTransaction>();
        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Libraries.Returns(libs);
        uow.Progress.Returns(progress);
        uow.ExternalHistory.Returns(external);
        uow.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(tx);

        var provider = Substitute.For<IMetadataProvider>();
        provider.Name.Returns(details.Provider);
        provider.IsConfigured.Returns(true);
        provider.GetDetailsAsync(details.ProviderId, primary.Kind, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns(details);

        var language = Substitute.For<IMetadataLanguageSource>();
        language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));

        var sut = new MetadataEnricher(
            uow,
            [provider],
            artwork,
            Substitute.For<IRemoteImageFetcher>(),
            language,
            TimeProvider.System,
            new ExternalHistoryPromoter(uow),
            Substitute.For<IThemeSongService>(),
            NullLogger<MetadataEnricher>.Instance);
        return (sut, media, progress, artwork);
    }

    private static MetadataDetails AndorDetails() =>
        new(
            Provider: "Tmdb",
            ProviderId: "83867",
            Title: "Andor",
            OriginalTitle: "Andor",
            Year: 2022,
            Overview: "Rebel spy.",
            CommunityRating: 8.4,
            OfficialRating: null,
            ImdbId: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: [],
            People: null,
            TrailerUrl: null);

    private static Series BuildSeries(Guid libraryId, string title, DateTimeOffset addedAt, params int[] seasonNumbers)
    {
        // Series.AddedAt is set in the ctor from `now`; use distinct timestamps for canonical pick.
        var series = new Series(libraryId, title, addedAt);
        foreach (var seasonNumber in seasonNumbers)
        {
            var season = new Season(series.Id, seasonNumber);
            series.AddSeason(season);
            var episode = new Episode(series.Id, season.Id, seasonNumber, 1, addedAt);
            var source = new MediaSource(
                $"/media/{title}/S{seasonNumber:00}E01.mkv",
                "mkv",
                1024,
                addedAt,
                addedAt);
            source.OwnedByEpisode(episode.Id);
            episode.AddSource(source);
            season.AddEpisode(episode);
        }

        return series;
    }

    [Fact]
    public async Task Enrich_merges_disjoint_seasons_into_series_with_more_episodes()
    {
        var libraryId = Guid.CreateVersion7();
        var earlier = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var later = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

        // S1 has 1 ep, S2 has 1 ep — but we make S1 side "canonical" by giving it more episodes.
        var s1 = BuildSeries(libraryId, "Andor", earlier, 1);
        // Add a second episode to S1 so it wins PickCanonical by count.
        var s1Season = s1.Seasons[0];
        var extra = new Episode(s1.Id, s1Season.Id, 1, 2, earlier);
        s1Season.AddEpisode(extra);

        var s2 = BuildSeries(libraryId, "Star Wars Andor", later, 2);
        s1.SetExternalIds("83867", null, null);
        s2.SetExternalIds("83867", null, null);

        var (sut, media, progress, artwork) = CreateSut(s2, AndorDetails());
        media.GetTrackedSeriesGraphAsync(s2.Id, Arg.Any<CancellationToken>()).Returns(s2);
        media.FindOtherSeriesByExternalIdAsync(libraryId, s2.Id, "83867", null, Arg.Any<CancellationToken>())
            .Returns(s1);
        media.GetTrackedSeriesGraphAsync(s1.Id, Arg.Any<CancellationToken>()).Returns(s1);

        var ok = await sut.EnrichAsync(s2.Id, "Tmdb", "83867", default);

        ok.Should().BeTrue();
        s1.Seasons.Select(s => s.SeasonNumber).Should().BeEquivalentTo([1, 2]);
        s1.Seasons.First(s => s.SeasonNumber == 2).Episodes.Should().ContainSingle()
            .Which.SeriesId.Should().Be(s1.Id);
        s2.Seasons.Should().BeEmpty();
        media.Received(1).Remove(s2);
        artwork.Received(1).DeleteOwner(s2.Id);
        await progress.DidNotReceive()
            .DeleteForMediaIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrich_merges_overlapping_episode_by_moving_sources()
    {
        var libraryId = Guid.CreateVersion7();
        var earlier = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var later = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

        var keeper = BuildSeries(libraryId, "Andor", earlier, 1);
        var donor = BuildSeries(libraryId, "Andor Dup", later, 1);
        keeper.SetExternalIds("83867", null, null);
        donor.SetExternalIds("83867", null, null);

        var keeperEp = keeper.Seasons[0].Episodes[0];
        var donorEp = donor.Seasons[0].Episodes[0];
        var donorSource = donorEp.Sources[0];

        var (sut, media, progress, _) = CreateSut(donor, AndorDetails());
        media.GetTrackedSeriesGraphAsync(donor.Id, Arg.Any<CancellationToken>()).Returns(donor);
        media.FindOtherSeriesByExternalIdAsync(libraryId, donor.Id, "83867", null, Arg.Any<CancellationToken>())
            .Returns(keeper);
        media.GetTrackedSeriesGraphAsync(keeper.Id, Arg.Any<CancellationToken>()).Returns(keeper);

        await sut.EnrichAsync(donor.Id, "Tmdb", "83867", default);

        donorSource.EpisodeId.Should().Be(keeperEp.Id);
        media.Received(1).RemoveEpisode(donorEp);
        media.Received(1).Remove(donor);
        await progress.Received(1).DeleteForMediaIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids != null && ids.Count == 1 && ids.Contains(donorEp.Id)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrich_skips_merge_when_no_duplicate_exists()
    {
        var series = BuildSeries(Guid.CreateVersion7(), "Andor", DateTimeOffset.UtcNow, 1);
        var (sut, media, _, artwork) = CreateSut(series, AndorDetails());
        media.GetTrackedSeriesGraphAsync(series.Id, Arg.Any<CancellationToken>()).Returns(series);
        media.FindOtherSeriesByExternalIdAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Series?)null);

        await sut.EnrichAsync(series.Id, "Tmdb", "83867", default);

        media.DidNotReceive().Remove(Arg.Any<MediaItem>());
        artwork.DidNotReceive().DeleteOwner(Arg.Any<Guid>());
    }
}
