using FluentAssertions;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Metadata;
using FreePlex.Application.Playback;
using FreePlex.Application.Settings;
using FreePlex.Domain.Jobs;
using FreePlex.Infrastructure.Configuration;
using FreePlex.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FreePlex.Application.Tests;

public sealed class MetadataLanguageSettingsTests
{
    [Fact]
    public void Settings_store_seeds_metadata_language_from_options()
    {
        var playback = Options.Create(new PlaybackOptions());
        var import = Options.Create(new ImportOptions());
        var metadata = Options.Create(new MetadataOptions
        {
            Language = "ru-RU",
            FallbackLanguage = "en-US",
        });

        var store = new InMemorySettingsStore(playback, import, metadata);

        store.Get().Metadata.Language.Should().Be("ru-RU");
        store.Get().Metadata.FallbackLanguage.Should().Be("en-US");
    }

    [Fact]
    public void Language_source_reads_live_settings_not_frozen_options()
    {
        var store = new InMemorySettingsStore(
            Options.Create(new PlaybackOptions()),
            Options.Create(new ImportOptions()),
            Options.Create(new MetadataOptions { Language = "ru-RU", FallbackLanguage = "en-US" }));
        var source = new SettingsMetadataLanguageSource(store);

        source.Get().Language.Should().Be("ru-RU");

        var next = store.Get() with
        {
            Metadata = store.Get().Metadata with { Language = "de-DE" },
        };
        store.Update(next);

        source.Get().Language.Should().Be("de-DE");
    }

    [Fact]
    public async Task Settings_update_enqueues_refresh_when_language_changes()
    {
        var store = Substitute.For<ISettingsStore>();
        var previous = new ServerSettingsDto
        {
            Metadata = new MetadataSettingsDto { Language = "ru-RU", FallbackLanguage = "en-US" },
        };
        var updated = new ServerSettingsDto
        {
            Metadata = new MetadataSettingsDto { Language = "en-US", FallbackLanguage = "en-US" },
        };
        store.Get().Returns(previous);
        store.Update(Arg.Any<ServerSettingsDto>()).Returns(updated);

        var uow = Substitute.For<IUnitOfWork>();
        var media = Substitute.For<IMediaRepository>();
        var jobs = Substitute.For<IJobRepository>();
        uow.Media.Returns(media);
        uow.Jobs.Returns(jobs);
        media.ListIdsWithExternalIdsAsync(Arg.Any<CancellationToken>())
            .Returns([Guid.CreateVersion7(), Guid.CreateVersion7()]);

        var queue = Substitute.For<IJobQueue>();
        var metadataJobs = new MetadataJobService(uow, queue, TimeProvider.System);
        var sut = new SettingsService(store, metadataJobs);

        await sut.UpdateAsync(updated, default);

        await media.Received(1).ListIdsWithExternalIdsAsync(Arg.Any<CancellationToken>());
        await jobs.Received(2).AddAsync(Arg.Any<BackgroundJob>(), Arg.Any<CancellationToken>());
        await queue.Received(2).EnqueueAsync(Arg.Any<JobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Settings_update_skips_refresh_when_language_unchanged()
    {
        var store = Substitute.For<ISettingsStore>();
        var settings = new ServerSettingsDto
        {
            Metadata = new MetadataSettingsDto { Language = "ru-RU", FallbackLanguage = "en-US" },
        };
        store.Get().Returns(settings);
        store.Update(Arg.Any<ServerSettingsDto>()).Returns(settings);

        var uow = Substitute.For<IUnitOfWork>();
        var media = Substitute.For<IMediaRepository>();
        uow.Media.Returns(media);

        var metadataJobs = new MetadataJobService(uow, Substitute.For<IJobQueue>(), TimeProvider.System);
        var sut = new SettingsService(store, metadataJobs);

        await sut.UpdateAsync(settings, default);

        await media.DidNotReceive().ListIdsWithExternalIdsAsync(Arg.Any<CancellationToken>());
    }
}
