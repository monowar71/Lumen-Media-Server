using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Metadata;

namespace LumenMedia.Application.Tests;

public class TmdbMetadataMappingTests
{
    private static readonly MetadataLanguage Lang = new("ru-RU", "en-US");

    [Theory]
    [InlineData("Ended", SeriesStatus.Ended)]
    [InlineData("Canceled", SeriesStatus.Ended)]
    [InlineData("Returning Series", SeriesStatus.Continuing)]
    [InlineData("In Production", SeriesStatus.Continuing)]
    [InlineData("Unknown", null)]
    public void MapSeriesStatus_maps_known_values(string input, SeriesStatus? expected) =>
        TmdbMetadataProvider.MapSeriesStatus(input).Should().Be(expected);

    [Fact]
    public void CountryFromLanguage_uses_region_or_sensible_default()
    {
        TmdbMetadataProvider.CountryFromLanguage("ru-RU").Should().Be("RU");
        TmdbMetadataProvider.CountryFromLanguage("en").Should().Be("US");
        TmdbMetadataProvider.CountryFromLanguage("ja").Should().Be("JP");
    }

    [Fact]
    public void PickMovieCertification_prefers_ui_country_then_us()
    {
        var countries = new List<TmdbMetadataProvider.TmdbReleaseDateCountry>
        {
            new("DE", [new("FSK 16")]),
            new("US", [new("R")]),
            new("RU", [new("16+")]),
        };

        TmdbMetadataProvider.PickMovieCertification(countries, Lang).Should().Be("16+");
        TmdbMetadataProvider.PickMovieCertification(
                [new("DE", [new("FSK 16")]), new("US", [new("PG-13")])],
                Lang)
            .Should().Be("PG-13");
    }

    [Fact]
    public void PickTvContentRating_prefers_ui_country()
    {
        var ratings = new List<TmdbMetadataProvider.TmdbContentRating>
        {
            new("US", "TV-MA"),
            new("RU", "18+"),
        };

        TmdbMetadataProvider.PickTvContentRating(ratings, Lang).Should().Be("18+");
    }
}

public class SeriesStatusProviderMappingTests
{
    [Theory]
    [InlineData("Ended", SeriesStatus.Ended)]
    [InlineData("Continuing", SeriesStatus.Continuing)]
    [InlineData("Canceled", SeriesStatus.Ended)]
    public void Tvdb_maps_status(string name, SeriesStatus expected) =>
        TvdbMetadataProvider.MapSeriesStatus(name).Should().Be(expected);

    [Theory]
    [InlineData("Ended", SeriesStatus.Ended)]
    [InlineData("Running", SeriesStatus.Continuing)]
    [InlineData("To Be Determined", SeriesStatus.Continuing)]
    public void TvMaze_maps_status(string name, SeriesStatus expected) =>
        TvMazeMetadataProvider.MapSeriesStatus(name).Should().Be(expected);
}
