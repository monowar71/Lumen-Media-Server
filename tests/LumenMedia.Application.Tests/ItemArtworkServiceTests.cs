using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Metadata;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class ItemArtworkServiceTests
{
    [Fact]
    public async Task ListCandidates_orders_by_language_then_votes()
    {
        var movie = new Movie(Guid.CreateVersion7(), "Fight Club", DateTimeOffset.UtcNow);
        movie.SetExternalIds("550", null, null);

        var media = Substitute.For<IMediaRepository>();
        media.GetByIdAsync(movie.Id, Arg.Any<CancellationToken>()).Returns(movie);

        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(movie.LibraryId, Arg.Any<CancellationToken>()).Returns((Library?)null);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Libraries.Returns(libs);

        var provider = Substitute.For<IMetadataProvider>();
        provider.Name.Returns("Tmdb");
        provider.IsConfigured.Returns(true);
        provider.ListArtworkAsync("550", MediaKind.Movie, ArtworkKind.Poster, Arg.Any<MetadataLanguage>(), Arg.Any<CancellationToken>())
            .Returns([
                new ArtworkImageCandidate("Tmdb", ArtworkKind.Poster, "https://image.tmdb.org/t/p/w780/a.jpg", "https://image.tmdb.org/t/p/w185/a.jpg", "de", 500, 750, 9),
                new ArtworkImageCandidate("Tmdb", ArtworkKind.Poster, "https://image.tmdb.org/t/p/w780/b.jpg", "https://image.tmdb.org/t/p/w185/b.jpg", "ru", 500, 750, 1),
                new ArtworkImageCandidate("Tmdb", ArtworkKind.Poster, "https://image.tmdb.org/t/p/w780/c.jpg", "https://image.tmdb.org/t/p/w185/c.jpg", null, 500, 750, 5),
            ]);

        var language = Substitute.For<IMetadataLanguageSource>();
        language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));

        var sut = new ItemArtworkService(
            uow,
            [provider],
            language,
            Substitute.For<IRemoteImageFetcher>(),
            Substitute.For<IArtworkStore>(),
            TimeProvider.System);

        var result = await sut.ListCandidatesAsync(movie.Id, ArtworkKind.Poster, CancellationToken.None);

        result.Select(c => c.Url).Should().Equal(
            "https://image.tmdb.org/t/p/w780/b.jpg",
            "https://image.tmdb.org/t/p/w780/c.jpg",
            "https://image.tmdb.org/t/p/w780/a.jpg");
    }

    [Fact]
    public async Task SetAsync_rejects_disallowed_host()
    {
        var movie = new Movie(Guid.CreateVersion7(), "X", DateTimeOffset.UtcNow);
        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(movie.Id, Arg.Any<CancellationToken>()).Returns(movie);
        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);

        var sut = new ItemArtworkService(
            uow,
            [],
            Substitute.For<IMetadataLanguageSource>(),
            Substitute.For<IRemoteImageFetcher>(),
            Substitute.For<IArtworkStore>(),
            TimeProvider.System);

        var act = () => sut.SetAsync(movie.Id, ArtworkKind.Poster, "https://evil.example/x.jpg", CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task SetAsync_downloads_and_replaces_poster()
    {
        var movie = new Movie(Guid.CreateVersion7(), "X", DateTimeOffset.UtcNow);
        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedForMetadataAsync(movie.Id, Arg.Any<CancellationToken>()).Returns(movie);
        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);

        var fetcher = Substitute.For<IRemoteImageFetcher>();
        fetcher.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream([1, 2, 3, 4]));

        var store = Substitute.For<IArtworkStore>();
        store.SaveAsync(movie.Id, ArtworkKind.Poster, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns("/config/metadata/x/Poster.img");

        var sut = new ItemArtworkService(
            uow,
            [],
            Substitute.For<IMetadataLanguageSource>(),
            fetcher,
            store,
            TimeProvider.System);

        await sut.SetAsync(
            movie.Id,
            ArtworkKind.Poster,
            "https://image.tmdb.org/t/p/w780/poster.jpg",
            CancellationToken.None);

        movie.Artworks.Should().ContainSingle(a => a.Kind == ArtworkKind.Poster);
        await media.Received(1).AddArtworkAsync(Arg.Any<Artwork>(), Arg.Any<CancellationToken>());
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
