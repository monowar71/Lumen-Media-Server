using FluentAssertions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Libraries;
using LumenMedia.Application.Metadata;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Jobs;
using LumenMedia.Domain.Libraries;
using NSubstitute;

namespace LumenMedia.Application.Tests;

public class LibraryScanDedupTests
{
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ILibraryRepository _libraries = Substitute.For<ILibraryRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IJobQueue _queue = Substitute.For<IJobQueue>();
    private readonly IMetadataLanguageSource _language = Substitute.For<IMetadataLanguageSource>();

    public LibraryScanDedupTests()
    {
        _uow.Libraries.Returns(_libraries);
        _uow.Jobs.Returns(_jobs);
        _language.Get().Returns(new MetadataLanguage("ru-RU", "en-US"));
    }

    private LibraryService CreateSut() => new(
        _uow,
        _queue,
        _language,
        new MetadataJobService(_uow, _queue, TimeProvider.System),
        TimeProvider.System);

    [Fact]
    public async Task Scan_returns_active_job_instead_of_enqueueing_a_duplicate()
    {
        var library = new Library("Movies", LibraryType.Movies, ["/media/movies"], DateTimeOffset.UtcNow);
        _libraries.GetByIdAsync(library.Id, Arg.Any<CancellationToken>()).Returns(library);
        var active = new BackgroundJob(JobType.ScanLibrary, DateTimeOffset.UtcNow, library.Id);
        _jobs.FindActiveAsync(JobType.ScanLibrary, library.Id, Arg.Any<CancellationToken>()).Returns(active);

        var result = await CreateSut().ScanAsync(library.Id, default);

        result.Id.Should().Be(active.Id);
        await _jobs.DidNotReceive().AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scan_enqueues_when_no_active_job_exists()
    {
        var library = new Library("Movies", LibraryType.Movies, ["/media/movies"], DateTimeOffset.UtcNow);
        _libraries.GetByIdAsync(library.Id, Arg.Any<CancellationToken>()).Returns(library);
        _jobs.FindActiveAsync(JobType.ScanLibrary, library.Id, Arg.Any<CancellationToken>())
            .Returns((BackgroundJob?)null);

        var result = await CreateSut().ScanAsync(library.Id, default);

        result.State.Should().Be(JobState.Queued);
        await _jobs.Received(1).AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }
}
