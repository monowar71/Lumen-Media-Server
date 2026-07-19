using FluentAssertions;
using LumenMedia.Infrastructure.Metadata;

namespace LumenMedia.Application.Tests;

public class TmdbMetadataScoringTests
{
    [Theory]
    [InlineData("Dark", null, "Dark", 2017, 1.0)]
    [InlineData("Citadel Diana", null, "Citadel: Diana", null, 0.95)]
    [InlineData("Breaking Bad", 2008, "Breaking Bad", 2008, 1.15)]
    public void Scores_expected_candidates(string query, int? year, string candidate, int? candYear, double minScore)
    {
        var score = TmdbMetadataProvider.Score(query, year, candidate, candYear, popularity: 50);
        score.Should().BeGreaterThanOrEqualTo(minScore - 0.01);
    }

    [Fact]
    public void Exact_title_and_year_beats_partial()
    {
        var exact = TmdbMetadataProvider.Score("Dark", 2017, "Dark", 2017, 10);
        var partial = TmdbMetadataProvider.Score("Dark", 2017, "Dark Matter", 2015, 10);
        exact.Should().BeGreaterThan(partial);
    }

    [Fact]
    public void Original_title_matches_english_filename_when_localized_title_differs()
    {
        // Library file "Molly's Game"; TMDB ru title is «Большая игра», original is English.
        var viaLocalized = TmdbMetadataProvider.Score(
            "Molly's Game", 2017, "Большая игра", 2017, popularity: 40, originalTitle: null);
        var viaOriginal = TmdbMetadataProvider.Score(
            "Molly's Game", 2017, "Большая игра", 2017, popularity: 40, originalTitle: "Molly's Game");

        viaLocalized.Should().BeLessThan(0.70);
        viaOriginal.Should().BeGreaterThanOrEqualTo(1.0);
    }

    [Fact]
    public void Popular_exact_title_beats_obscure_same_name()
    {
        var obscure = TmdbMetadataProvider.Score(
            "Oblivion", 2013, "Oblivion", 2013, popularity: 0.5, originalTitle: "Oblivion");
        var known = TmdbMetadataProvider.Score(
            "Oblivion", 2013, "Обливион", 2013, popularity: 80, originalTitle: "Oblivion");

        known.Should().BeGreaterThan(obscure);
    }

    [Fact]
    public void Fan_edit_prefix_scores_below_exact()
    {
        var exact = TmdbMetadataProvider.Score("28 Weeks Later", 2007, "28 Weeks Later", 2007, 60);
        var fan = TmdbMetadataProvider.Score(
            "28 Weeks Later", 2007, "28 Weeks Later (Squaddies Story)", 2007, 2);

        exact.Should().BeGreaterThan(fan);
        fan.Should().BeLessThan(1.0);
    }
}
