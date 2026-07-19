using FluentAssertions;
using FreePlex.Domain.Enums;
using FreePlex.Infrastructure.Import;

namespace FreePlex.Application.Tests;

public class RegexNameParserTests
{
    private readonly RegexNameParser _parser = new();

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", "The Matrix", 1999)]
    [InlineData("Inception (2010) [1080p].mp4", "Inception", 2010)]
    [InlineData("The.Dark.Knight.2008.1080p.BluRay.x264.mkv", "The Dark Knight", 2008)]
    [InlineData("Dune.Part.Two.2024.UHD.BluRay.mkv", "Dune Part Two", 2024)]
    public void Parses_movies(string fileName, string expectedTitle, int expectedYear)
    {
        var parsed = _parser.Parse(fileName);

        parsed.Kind.Should().Be(MediaKind.Movie);
        parsed.Title.Should().Be(expectedTitle);
        parsed.Year.Should().Be(expectedYear);
    }

    [Theory]
    [InlineData("Breaking.Bad.S03E07.720p.HDTV.x264.mkv", "Breaking Bad", 3, 7)]
    [InlineData("The.Wire.S01E01.mkv", "The Wire", 1, 1)]
    [InlineData("Friends.Season.2.Episode.10.mkv", "Friends", 2, 10)]
    [InlineData("Lost.3x14.HDTV.mkv", "Lost", 3, 14)]
    [InlineData("Dark.S01E01.1080p.LostFilm.TV.mkv", "Dark", 1, 1)]
    [InlineData("Citadel.Diana.S01E04.1080p.rus.LostFilm.TV.mkv", "Citadel Diana", 1, 4)]
    public void Parses_series(string fileName, string expectedTitle, int season, int episode)
    {
        var parsed = _parser.Parse(fileName);

        parsed.Kind.Should().Be(MediaKind.Series);
        parsed.Title.Should().Be(expectedTitle);
        parsed.Season.Should().Be(season);
        parsed.Episode.Should().Be(episode);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", "1080p", "x264")]
    [InlineData("Movie.2020.2160p.x265.mkv", "2160p", "x265")]
    [InlineData("Show.2020.4K.HEVC.mkv", "2160p", "hevc")]
    public void Extracts_quality_and_codec(string fileName, string quality, string codec)
    {
        var parsed = _parser.Parse(fileName);

        parsed.Quality.Should().Be(quality);
        parsed.Codec.Should().Be(codec);
    }

    [Fact]
    public void Extracts_release_group()
    {
        var parsed = _parser.Parse("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv");
        parsed.ReleaseGroup.Should().Be("GRP");
    }

    [Fact]
    public void Empty_name_degrades_gracefully()
    {
        var parsed = _parser.Parse("");
        parsed.Title.Should().Be("Unknown");
        parsed.Kind.Should().Be(MediaKind.Movie);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", LibraryType.Movies, true)]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GRP.mkv", LibraryType.Series, false)]
    [InlineData("Breaking.Bad.S03E07.720p.HDTV.x264.mkv", LibraryType.Series, true)]
    [InlineData("Breaking.Bad.S03E07.720p.HDTV.x264.mkv", LibraryType.Movies, false)]
    public void Parsed_kind_routes_to_matching_library_only(
        string fileName,
        LibraryType libraryType,
        bool shouldAccept)
    {
        var parsed = _parser.Parse(fileName);
        libraryType.Accepts(parsed.Kind).Should().Be(shouldAccept);
    }
}
