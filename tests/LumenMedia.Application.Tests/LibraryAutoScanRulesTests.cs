using FluentAssertions;
using LumenMedia.Infrastructure.Scanning;

namespace LumenMedia.Application.Tests;

public sealed class LibraryAutoScanRulesTests
{
    [Theory]
    [InlineData("/hdd/Lucky.S01E01.mkv", true)]
    [InlineData("/hdd/film.mp4", true)]
    [InlineData("/hdd/note.txt", false)]
    [InlineData("/hdd/film.mkv.part", false)]
    [InlineData("/hdd/film.mkv.!qB", false)]
    [InlineData("", false)]
    public void IsVideoFile_filters_extensions_and_incomplete(string path, bool expected) =>
        LibraryAutoScanRules.IsVideoFile(path).Should().Be(expected);

    [Fact]
    public void LibrariesForPath_matches_roots()
    {
        var movies = Guid.CreateVersion7();
        var series = Guid.CreateVersion7();
        var libs = new List<(Guid, IReadOnlyList<string>)>
        {
            (movies, ["/media/movies"]),
            (series, ["/hdd"]),
        };

        LibraryAutoScanRules.LibrariesForPath("/hdd/Lucky.S01E01.mkv", libs)
            .Should().Equal(series);
        LibraryAutoScanRules.LibrariesForPath("/media/movies/x.mkv", libs)
            .Should().Equal(movies);
        LibraryAutoScanRules.LibrariesForPath("/other/x.mkv", libs)
            .Should().BeEmpty();
    }

    [Fact]
    public void LibrariesForPath_shared_root_matches_all()
    {
        var movies = Guid.CreateVersion7();
        var series = Guid.CreateVersion7();
        var libs = new List<(Guid, IReadOnlyList<string>)>
        {
            (movies, ["/hdd"]),
            (series, ["/hdd"]),
        };

        LibraryAutoScanRules.LibrariesForPath("/hdd/Show.S01E01.mkv", libs)
            .Should().BeEquivalentTo([movies, series]);
    }
}
