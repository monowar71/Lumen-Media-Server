using FluentAssertions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Persistence;
using LumenMedia.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LumenMedia.Application.Tests;

/// <summary>
/// Post-scan Missing enqueue must re-fetch matched series when new episodes arrived without titles.
/// </summary>
public sealed class ListIdsMissingMetadataTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly LumenMediaDbContext _db;
    private readonly MediaRepository _sut;

    public ListIdsMissingMetadataTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LumenMediaDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new LumenMediaDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new MediaRepository(_db);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Includes_matched_series_with_untitled_episode()
    {
        var library = await SeedLibraryAsync();
        var now = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var series = new Series(library.Id, "Укрытие", now);
        series.SetOverview("Already enriched");
        series.SetExternalIds("125988", null, null);
        var season = series.AddSeason(new Season(series.Id, 3));
        season.AddEpisode(new Episode(series.Id, season.Id, 3, 10, now));
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var ids = await _sut.ListIdsMissingMetadataAsync(library.Id, default);

        ids.Should().Contain(series.Id);
    }

    [Fact]
    public async Task Skips_matched_series_when_all_episodes_have_titles()
    {
        var library = await SeedLibraryAsync();
        var now = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var series = new Series(library.Id, "Укрытие", now);
        series.SetOverview("Already enriched");
        series.SetExternalIds("125988", null, null);
        var season = series.AddSeason(new Season(series.Id, 1));
        var episode = new Episode(series.Id, season.Id, 1, 1, now);
        episode.SetDetails("Freedom Day", "Overview", null, null);
        season.AddEpisode(episode);
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var ids = await _sut.ListIdsMissingMetadataAsync(library.Id, default);

        ids.Should().NotContain(series.Id);
    }

    [Fact]
    public async Task Still_includes_items_without_tmdb_or_overview()
    {
        var library = await SeedLibraryAsync();
        var bare = new Series(library.Id, "Unmatched", DateTimeOffset.UtcNow);
        _db.Series.Add(bare);
        await _db.SaveChangesAsync();

        var ids = await _sut.ListIdsMissingMetadataAsync(library.Id, default);

        ids.Should().Contain(bare.Id);
    }

    private async Task<Library> SeedLibraryAsync(string name = "TV")
    {
        var library = new Library(name, LibraryType.Series, ["/media/tv"], DateTimeOffset.UtcNow);
        _db.Libraries.Add(library);
        await _db.SaveChangesAsync();
        return library;
    }
}
