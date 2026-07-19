using FluentAssertions;
using FreePlex.Application.Abstractions;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Libraries;
using FreePlex.Domain.Media;
using FreePlex.Infrastructure.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FreePlex.Application.Tests;

public sealed class MetadataEnrichmentTests
{
    private static (MetadataEnricher Sut, IMediaRepository Media, IMetadataProvider Provider) CreateSut(
        MediaItem item,
        MetadataDetails details)
    {
        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        media.GetOrCreatePersonAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new Person(call.ArgAt<string>(0), call.ArgAt<string?>(1)));

        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Library?)null);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Libraries.Returns(libs);

        var provider = Substitute.For<IMetadataProvider>();
        provider.Name.Returns(details.Provider);
        provider.IsConfigured.Returns(true);
        provider.GetDetailsAsync(details.ProviderId, item.Kind, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns(details);

        var language = Substitute.For<IMetadataLanguageSource>();
        language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));

        var sut = new MetadataEnricher(
            uow,
            [provider],
            Substitute.For<IArtworkStore>(),
            Substitute.For<IRemoteImageFetcher>(),
            language,
            TimeProvider.System,
            NullLogger<MetadataEnricher>.Instance);
        return (sut, media, provider);
    }

    private static MetadataDetails Details(
        IReadOnlyList<PersonCredit>? people = null,
        string? trailerUrl = null) =>
        new(
            Provider: "Tmdb",
            ProviderId: "603",
            Title: "The Matrix",
            OriginalTitle: null,
            Year: 1999,
            Overview: "Overview",
            CommunityRating: 8.2,
            OfficialRating: null,
            ImdbId: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: [],
            People: people,
            TrailerUrl: trailerUrl);

    [Fact]
    public async Task Enrich_applies_trailer_and_people()
    {
        var movie = new Movie(Guid.CreateVersion7(), "Matrix", DateTimeOffset.UtcNow);
        var details = Details(
            people:
            [
                new PersonCredit("Keanu Reeves", PersonType.Actor, "Neo", 0, "https://img/keanu.jpg", "6384"),
                new PersonCredit("Lana Wachowski", PersonType.Director, "Director", 100, null, "9339"),
            ],
            trailerUrl: "https://www.youtube.com/watch?v=vKQi3bBA1y8");

        var (sut, media, _) = CreateSut(movie, details);

        var ok = await sut.EnrichAsync(movie.Id, "Tmdb", "603", default);

        ok.Should().BeTrue();
        movie.TrailerUrl.Should().Be("https://www.youtube.com/watch?v=vKQi3bBA1y8");
        await media.Received(1).RemovePeopleAsync(movie.Id, Arg.Any<CancellationToken>());
        await media.Received(2).AddMediaPersonAsync(Arg.Any<MediaPerson>(), Arg.Any<CancellationToken>());
        await media.Received(1).AddMediaPersonAsync(
            Arg.Is<MediaPerson>(mp => mp != null && mp.Type == PersonType.Actor && mp.Role == "Neo"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrich_without_people_keeps_existing_credits()
    {
        var movie = new Movie(Guid.CreateVersion7(), "Matrix", DateTimeOffset.UtcNow);
        var (sut, media, _) = CreateSut(movie, Details(people: null, trailerUrl: null));

        await sut.EnrichAsync(movie.Id, "Tmdb", "603", default);

        await media.DidNotReceive().RemovePeopleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        movie.TrailerUrl.Should().BeNull();
    }

    [Fact]
    public async Task Enrich_deduplicates_same_person_and_type()
    {
        var movie = new Movie(Guid.CreateVersion7(), "Matrix", DateTimeOffset.UtcNow);
        var details = Details(people:
        [
            new PersonCredit("Keanu Reeves", PersonType.Actor, "Neo", 0, null, "6384"),
            new PersonCredit("Keanu Reeves", PersonType.Actor, "Neo (double)", 1, null, "6384"),
        ]);

        var (sut, media, _) = CreateSut(movie, details);
        // Same provider person id must resolve to the same Person entity.
        var keanu = new Person("Keanu Reeves", "6384");
        media.GetOrCreatePersonAsync("Keanu Reeves", "6384", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(keanu);

        await sut.EnrichAsync(movie.Id, "Tmdb", "603", default);

        await media.Received(1).AddMediaPersonAsync(Arg.Any<MediaPerson>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrich_series_applies_episode_titles_per_season()
    {
        var series = new Series(Guid.CreateVersion7(), "Dark", DateTimeOffset.UtcNow);
        var season = new Season(series.Id, 1);
        var ep1 = new Episode(series.Id, season.Id, 1, 1, DateTimeOffset.UtcNow);
        var ep2 = new Episode(series.Id, season.Id, 1, 2, DateTimeOffset.UtcNow);

        var details = Details() with { ProviderId = "70523" };
        var (sut, media, provider) = CreateSut(series, details);
        media.GetTrackedEpisodesForSeriesAsync(series.Id, Arg.Any<CancellationToken>())
            .Returns([ep1, ep2]);
        provider.GetSeasonEpisodesAsync("70523", 1, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns([
                new EpisodeMetadata(1, 1, "Secrets", "Winden, 2019…", new DateOnly(2017, 12, 1), 51 * 60_000L),
                new EpisodeMetadata(1, 2, "Lies", null, null, null),
            ]);

        await sut.EnrichAsync(series.Id, "Tmdb", "70523", default);

        ep1.Title.Should().Be("Secrets");
        ep1.Overview.Should().Be("Winden, 2019…");
        ep1.AirDate.Should().Be(new DateOnly(2017, 12, 1));
        ep1.RuntimeMs.Should().Be(51 * 60_000L);
        ep2.Title.Should().Be("Lies");
        ep2.Overview.Should().BeNull();
        await provider.Received(1).GetSeasonEpisodesAsync("70523", 1, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enrich_series_keeps_existing_episode_fields_when_provider_returns_null()
    {
        var series = new Series(Guid.CreateVersion7(), "Dark", DateTimeOffset.UtcNow);
        var season = new Season(series.Id, 1);
        var ep = new Episode(series.Id, season.Id, 1, 1, DateTimeOffset.UtcNow);
        ep.SetDetails("Manual title", "Manual overview", null, 42L);

        var details = Details() with { ProviderId = "70523" };
        var (sut, media, provider) = CreateSut(series, details);
        media.GetTrackedEpisodesForSeriesAsync(series.Id, Arg.Any<CancellationToken>())
            .Returns([ep]);
        provider.GetSeasonEpisodesAsync("70523", 1, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns([new EpisodeMetadata(1, 1, null, null, null, null)]);

        await sut.EnrichAsync(series.Id, "Tmdb", "70523", default);

        ep.Title.Should().Be("Manual title");
        ep.Overview.Should().Be("Manual overview");
        ep.RuntimeMs.Should().Be(42L);
    }

    [Fact]
    public void PickTrailerUrl_prefers_official_youtube_trailer()
    {
        var url = TmdbMetadataProvider.PickTrailerUrl(
        [
            new TmdbMetadataProvider.TmdbVideo("YouTube", "Teaser", "teaser1", true),
            new TmdbMetadataProvider.TmdbVideo("YouTube", "Trailer", "fanmade", false),
            new TmdbMetadataProvider.TmdbVideo("YouTube", "Trailer", "official1", true),
            new TmdbMetadataProvider.TmdbVideo("Vimeo", "Trailer", "vimeo1", true),
        ]);

        url.Should().Be("https://www.youtube.com/watch?v=official1");
    }

    [Fact]
    public void PickTrailerUrl_falls_back_to_teaser_and_handles_empty()
    {
        TmdbMetadataProvider.PickTrailerUrl(
            [new TmdbMetadataProvider.TmdbVideo("YouTube", "Teaser", "teaser1", null)])
            .Should().Be("https://www.youtube.com/watch?v=teaser1");

        TmdbMetadataProvider.PickTrailerUrl([]).Should().BeNull();
        TmdbMetadataProvider.PickTrailerUrl(null).Should().BeNull();
        TmdbMetadataProvider.PickTrailerUrl(
            [new TmdbMetadataProvider.TmdbVideo("Vimeo", "Trailer", "v1", true)])
            .Should().BeNull();
    }
}
