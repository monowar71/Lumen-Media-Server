using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Libraries;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Libraries;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Users;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public sealed class MediaFileServiceTests
{
    [Fact]
    public async Task DeleteFiles_removes_mock_file_and_movie_row()
    {
        var userId = Guid.CreateVersion7();
        var library = new Library("Movies", LibraryType.Movies, ["/media"], DateTimeOffset.UtcNow);
        var movie = new Movie(library.Id, "Mock Delete Me", DateTimeOffset.UtcNow);
        var source = new MediaSource(
            "/media/mock/delete-me.mkv",
            "mkv",
            1024,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        source.OwnedByMovie(movie.Id);

        var media = Substitute.For<IMediaRepository>();
        media.GetTrackedSourcesForMediaAsync(movie.Id, Arg.Any<CancellationToken>()).Returns([source]);
        media.GetByIdAsync(movie.Id, Arg.Any<CancellationToken>()).Returns(movie);
        media.GetTrackedForMetadataAsync(movie.Id, Arg.Any<CancellationToken>()).Returns(movie);

        var libs = Substitute.For<ILibraryRepository>();
        libs.GetByIdAsync(library.Id, Arg.Any<CancellationToken>()).Returns(library);

        var progress = Substitute.For<IProgressRepository>();
        progress.DeleteForMediaIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var uow = Substitute.For<IUnitOfWork>();
        uow.Media.Returns(media);
        uow.Libraries.Returns(libs);
        uow.Progress.Returns(progress);

        var deleter = Substitute.For<IMediaFileDeleter>();
        deleter.TryDelete("/media/mock/delete-me.mkv", Arg.Any<IReadOnlyList<string>>()).Returns(true);

        var artwork = Substitute.For<IArtworkStore>();
        var sut = new MediaFileService(uow, deleter, artwork);

        var caller = new Caller(userId, UserRole.Admin, AllLibraries: true, LibraryIds: []);
        var result = await sut.DeleteFilesAsync(caller, movie.Id, default);

        result.DeletedFiles.Should().Be(1);
        result.SourcesRemoved.Should().Be(1);
        result.MediaRemoved.Should().BeTrue();
        media.Received(1).RemoveSource(source);
        media.Received(1).Remove(movie);
        artwork.Received(1).DeleteOwner(movie.Id);
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteFiles_rejects_non_admin()
    {
        var sut = new MediaFileService(
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IMediaFileDeleter>(),
            Substitute.For<IArtworkStore>());

        var caller = new Caller(Guid.CreateVersion7(), UserRole.User, false, []);
        var act = () => sut.DeleteFilesAsync(caller, Guid.CreateVersion7(), default);
        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
