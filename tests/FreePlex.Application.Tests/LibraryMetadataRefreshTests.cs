using FluentAssertions;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Libraries;
using FreePlex.Application.Metadata;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Jobs;
using FreePlex.Domain.Libraries;
using NSubstitute;

namespace FreePlex.Application.Tests;

public sealed class LibraryMetadataRefreshTests
{
    [Fact]
    public async Task RefreshMetadata_missing_mode_enqueues_only_missing_ids()
    {
        var (sut, lib, media, jobs, queue) = CreateSut();
        var missing = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        media.ListIdsMissingMetadataAsync(lib.Id, Arg.Any<CancellationToken>()).Returns(missing);

        var result = await sut.RefreshMetadataAsync(
            lib.Id,
            new RefreshLibraryMetadataRequest { Mode = MetadataRefreshMode.Missing },
            default);

        result.EnqueuedCount.Should().Be(2);
        result.Mode.Should().Be(MetadataRefreshMode.Missing);
        result.LibraryId.Should().Be(lib.Id);
        await media.Received(1).ListIdsMissingMetadataAsync(lib.Id, Arg.Any<CancellationToken>());
        await media.DidNotReceive().ListIdsForLibraryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await jobs.Received(2).AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await queue.Received(2).EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshMetadata_all_mode_enqueues_every_library_item()
    {
        var (sut, lib, media, jobs, queue) = CreateSut();
        var all = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        media.ListIdsForLibraryAsync(lib.Id, Arg.Any<CancellationToken>()).Returns(all);

        var result = await sut.RefreshMetadataAsync(
            lib.Id,
            new RefreshLibraryMetadataRequest { Mode = MetadataRefreshMode.All },
            default);

        result.EnqueuedCount.Should().Be(3);
        result.Mode.Should().Be(MetadataRefreshMode.All);
        await media.Received(1).ListIdsForLibraryAsync(lib.Id, Arg.Any<CancellationToken>());
        await jobs.Received(3).AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await queue.Received(3).EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshMetadata_updates_language_then_enqueues_without_double_pass()
    {
        var (sut, lib, media, jobs, queue) = CreateSut(preferredLanguage: "ru-RU");
        var matched = new[] { Guid.CreateVersion7() };
        media.ListIdsWithExternalIdsForLibraryAsync(lib.Id, Arg.Any<CancellationToken>()).Returns(matched);

        var result = await sut.RefreshMetadataAsync(
            lib.Id,
            new RefreshLibraryMetadataRequest
            {
                Mode = MetadataRefreshMode.Matched,
                PreferredLanguage = "en-US",
            },
            default);

        result.PreferredLanguage.Should().Be("en-US");
        result.EnqueuedCount.Should().Be(1);
        lib.PreferredLanguage.Should().Be("en-US");
        // Matched mode only once — language change must not trigger a second enqueue pass.
        await media.Received(1).ListIdsWithExternalIdsForLibraryAsync(lib.Id, Arg.Any<CancellationToken>());
        await jobs.Received(1).AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }

    private static (
        LibraryService Sut,
        Library Lib,
        IMediaRepository Media,
        IJobRepository Jobs,
        IJobQueue Queue) CreateSut(string preferredLanguage = "ru-RU")
    {
        var uow = Substitute.For<IUnitOfWork>();
        var libraries = Substitute.For<ILibraryRepository>();
        var media = Substitute.For<IMediaRepository>();
        var jobs = Substitute.For<IJobRepository>();
        var queue = Substitute.For<IJobQueue>();
        uow.Libraries.Returns(libraries);
        uow.Media.Returns(media);
        uow.Jobs.Returns(jobs);

        var lib = new Library("Movies", LibraryType.Movies, ["/media"], DateTimeOffset.UtcNow, preferredLanguage);
        libraries.GetByIdAsync(lib.Id, Arg.Any<CancellationToken>()).Returns(lib);
        libraries.CountItemsAsync(lib.Id, Arg.Any<CancellationToken>()).Returns(0);

        var language = Substitute.For<IMetadataLanguageSource>();
        language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));
        var metadataJobs = new MetadataJobService(uow, queue, TimeProvider.System);
        var sut = new LibraryService(uow, queue, language, metadataJobs, TimeProvider.System);

        return (sut, lib, media, jobs, queue);
    }
}
