using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Metadata;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class ItemMetadataServiceTests
{
    [Fact]
    public async Task SearchCandidates_aggregates_configured_providers_by_score()
    {
        var itemId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "Matrix", DateTimeOffset.UtcNow);
        movie.SetYear(1999);

        var media = Substitute.For<IMediaRepository>();
        media.GetByIdAsync(itemId, Arg.Any<CancellationToken>()).Returns(movie);

        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(movie.LibraryId, Arg.Any<CancellationToken>()).Returns((Library?)null);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Libraries.Returns(libs);

        var tmdb = Substitute.For<IMetadataProvider>();
        tmdb.Name.Returns("Tmdb");
        tmdb.IsConfigured.Returns(true);
        tmdb.SearchAsync("Matrix", 1999, MediaKind.Movie, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns([
                new MetadataMatch("Tmdb", "603", "The Matrix", 1999, 1.1),
                new MetadataMatch("Tmdb", "604", "The Matrix Reloaded", 2003, 0.7),
            ]);

        var language = Substitute.For<IMetadataLanguageSource>();
        language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));

        var sut = new ItemMetadataService(uow, [tmdb], language, TimeProvider.System);
        var result = await sut.SearchCandidatesAsync(itemId, null, null, default);

        result.Should().HaveCount(2);
        result[0].ProviderId.Should().Be("603");
        result[0].Score.Should().Be(1.1);
    }

    [Fact]
    public async Task Update_sets_fields_and_locks_by_default()
    {
        var itemId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "Wrong Title", DateTimeOffset.UtcNow);

        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(itemId, Arg.Any<CancellationToken>()).Returns(movie);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);

        var sut = new ItemMetadataService(
            uow,
            [],
            Substitute.For<IMetadataLanguageSource>(),
            TimeProvider.System);

        await sut.UpdateAsync(
            itemId,
            new UpdateItemMetadataRequest
            {
                Title = "The Matrix",
                Year = 1999,
                Overview = "A computer hacker…",
                Tagline = "Welcome to the Real World",
            },
            default);

        movie.Title.Should().Be("The Matrix");
        movie.Year.Should().Be(1999);
        movie.Overview.Should().Be("A computer hacker…");
        movie.Tagline.Should().Be("Welcome to the Real World");
        movie.MetadataLocked.Should().BeTrue();
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_can_unlock_without_other_changes()
    {
        var itemId = Guid.CreateVersion7();
        var movie = new Movie(Guid.CreateVersion7(), "The Matrix", DateTimeOffset.UtcNow);
        movie.SetMetadataLocked(true);

        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(itemId, Arg.Any<CancellationToken>()).Returns(movie);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);

        var sut = new ItemMetadataService(
            uow,
            [],
            Substitute.For<IMetadataLanguageSource>(),
            TimeProvider.System);

        await sut.UpdateAsync(itemId, new UpdateItemMetadataRequest { MetadataLocked = false }, default);

        movie.MetadataLocked.Should().BeFalse();
    }
}
