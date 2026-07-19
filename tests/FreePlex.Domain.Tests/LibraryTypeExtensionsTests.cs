using FluentAssertions;
using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Tests;

public class LibraryTypeExtensionsTests
{
    [Theory]
    [InlineData(LibraryType.Movies, MediaKind.Movie, true)]
    [InlineData(LibraryType.Movies, MediaKind.Series, false)]
    [InlineData(LibraryType.Movies, MediaKind.Episode, false)]
    [InlineData(LibraryType.Series, MediaKind.Series, true)]
    [InlineData(LibraryType.Series, MediaKind.Movie, false)]
    [InlineData(LibraryType.Series, MediaKind.Episode, false)]
    public void Accepts_matches_library_type_to_parsed_kind(
        LibraryType libraryType,
        MediaKind kind,
        bool expected) =>
        libraryType.Accepts(kind).Should().Be(expected);

    [Fact]
    public void Shared_root_routing_movies_take_movie_kind_series_take_series_kind()
    {
        // Filename classification (RegexNameParser) → library membership without moving files.
        var movieKind = MediaKind.Movie;
        var seriesKind = MediaKind.Series;

        LibraryType.Movies.Accepts(movieKind).Should().BeTrue();
        LibraryType.Movies.Accepts(seriesKind).Should().BeFalse();
        LibraryType.Series.Accepts(seriesKind).Should().BeTrue();
        LibraryType.Series.Accepts(movieKind).Should().BeFalse();
    }
}
