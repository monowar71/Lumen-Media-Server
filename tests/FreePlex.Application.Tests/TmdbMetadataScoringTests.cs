using FluentAssertions;
using FreePlex.Infrastructure.Metadata;

namespace FreePlex.Application.Tests;

public class TmdbMetadataScoringTests
{
    [Theory]
    [InlineData("Dark", null, "Dark", 2017, 1.0)]
    [InlineData("Citadel Diana", null, "Citadel: Diana", null, 0.65)]
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
}
