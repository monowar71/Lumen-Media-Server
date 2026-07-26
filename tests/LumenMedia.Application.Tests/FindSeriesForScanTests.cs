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
/// Regression: after metadata localizes Title (e.g. "Укрытие") while files still parse as
/// OriginalTitle ("Silo"), scan must attach episodes to the existing series — not spawn a duplicate.
/// </summary>
public sealed class FindSeriesForScanTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly LumenMediaDbContext _db;
    private readonly MediaRepository _sut;

    public FindSeriesForScanTests()
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
    public async Task Matches_localized_series_by_original_title()
    {
        var library = await SeedLibraryAsync();
        var now = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var series = new Series(library.Id, "Укрытие", now);
        series.SetOriginalTitle("Silo");
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var found = await _sut.FindSeriesForScanAsync(library.Id, "Silo", default);

        found.Should().NotBeNull();
        found!.Id.Should().Be(series.Id);
        found.Title.Should().Be("Укрытие");
    }

    [Fact]
    public async Task Matches_by_title_when_original_title_differs()
    {
        var library = await SeedLibraryAsync();
        var series = new Series(library.Id, "Silo", DateTimeOffset.UtcNow);
        series.SetOriginalTitle("Silo");
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var found = await _sut.FindSeriesForScanAsync(library.Id, "Silo", default);

        found.Should().NotBeNull();
        found!.Id.Should().Be(series.Id);
    }

    [Fact]
    public async Task Prefers_oldest_when_duplicate_title_and_original_title_both_match()
    {
        var library = await SeedLibraryAsync();
        var older = new Series(library.Id, "Укрытие", DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        older.SetOriginalTitle("Silo");
        var newer = new Series(library.Id, "Silo", DateTimeOffset.Parse("2025-06-01T00:00:00Z"));
        _db.Series.AddRange(older, newer);
        await _db.SaveChangesAsync();

        var found = await _sut.FindSeriesForScanAsync(library.Id, "Silo", default);

        found.Should().NotBeNull();
        found!.Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task Is_case_insensitive_for_original_title()
    {
        var library = await SeedLibraryAsync();
        var series = new Series(library.Id, "Укрытие", DateTimeOffset.UtcNow);
        series.SetOriginalTitle("Silo");
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var found = await _sut.FindSeriesForScanAsync(library.Id, "silo", default);

        found.Should().NotBeNull();
        found!.Id.Should().Be(series.Id);
    }

    [Fact]
    public async Task Does_not_match_series_in_another_library()
    {
        var libA = await SeedLibraryAsync("TV A");
        var libB = await SeedLibraryAsync("TV B");
        var series = new Series(libA.Id, "Укрытие", DateTimeOffset.UtcNow);
        series.SetOriginalTitle("Silo");
        _db.Series.Add(series);
        await _db.SaveChangesAsync();

        var found = await _sut.FindSeriesForScanAsync(libB.Id, "Silo", default);

        found.Should().BeNull();
    }

    private async Task<Library> SeedLibraryAsync(string name = "TV")
    {
        var library = new Library(name, LibraryType.Series, ["/media/tv"], DateTimeOffset.UtcNow);
        _db.Libraries.Add(library);
        await _db.SaveChangesAsync();
        return library;
    }
}
