using FluentAssertions;
using FreePlex.Infrastructure.Plex;

namespace FreePlex.Application.Tests;

public sealed class PlexGuidParserTests
{
    [Fact]
    public void Parse_reads_modern_movie_guids()
    {
        var parsed = PlexGuidParser.Parse([
            "plex://movie/5d776b9aad5437001f79c6f8",
            "tmdb://603",
            "imdb://tt0133093",
        ]);

        parsed.TmdbId.Should().Be("603");
        parsed.ImdbId.Should().Be("tt0133093");
        parsed.SeasonNumber.Should().BeNull();
    }

    [Fact]
    public void Parse_reads_episode_tmdb_path_and_grandparent()
    {
        var parsed = PlexGuidParser.Parse(
            ["tmdb://1396/1/5", "tvdb://349232"],
            grandparentGuid: "tmdb://1396");

        parsed.TmdbId.Should().Be("1396");
        parsed.SeasonNumber.Should().Be(1);
        parsed.EpisodeNumber.Should().Be(5);
        parsed.TvdbId.Should().Be("349232");
    }

    [Fact]
    public void Parse_reads_legacy_agent_guids()
    {
        var parsed = PlexGuidParser.Parse([
            "com.plexapp.agents.themoviedb://603?lang=en",
            "com.plexapp.agents.imdb://tt0133093?lang=en",
        ]);

        parsed.TmdbId.Should().Be("603");
        parsed.ImdbId.Should().Be("tt0133093");
    }
}
