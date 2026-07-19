using System.Text.Json;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Application.Jobs;
using LumenMedia.Application.Metadata;
using LumenMedia.Domain.Jobs;
using LumenMedia.Domain.Libraries;

namespace LumenMedia.Application.Libraries;

public sealed class LibraryService(
    IUnitOfWork uow,
    IJobQueue jobQueue,
    IMetadataLanguageSource languageSource,
    MetadataJobService metadataJobs,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<LibraryDto>> ListAsync(Caller caller, CancellationToken ct)
    {
        var libraries = await uow.Libraries.ListAsync(ct);
        var accessible = libraries.Where(l => caller.CanAccess(l.Id));
        var result = new List<LibraryDto>();
        foreach (var lib in accessible)
            result.Add(await MapAsync(lib, ct));
        return result;
    }

    public async Task<LibraryDto> GetAsync(Guid id, Caller caller, CancellationToken ct)
    {
        var lib = await GetAccessibleAsync(id, caller, ct);
        return await MapAsync(lib, ct);
    }

    public async Task<LibraryDto> CreateAsync(CreateLibraryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("name", "Library name is required.");
        if (request.Paths.Count == 0)
            throw new ValidationException("paths", "At least one path is required.");

        var now = clock.GetUtcNow();
        var serverLanguage = languageSource.Get().Language;
        var settings = request.Settings;
        var preferredLanguage = string.IsNullOrWhiteSpace(settings?.PreferredLanguage)
            ? serverLanguage
            : settings.PreferredLanguage;
        var library = new Library(
            request.Name.Trim(),
            request.Type,
            request.Paths,
            now,
            preferredLanguage,
            settings?.MetadataProviders,
            settings?.AutoScan ?? true);

        await uow.Libraries.AddAsync(library, ct);
        await uow.SaveChangesAsync(ct);
        return await MapAsync(library, ct);
    }

    public async Task<LibraryDto> UpdateAsync(Guid id, UpdateLibraryRequest request, CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Library not found.");

        var previousLanguage = lib.PreferredLanguage;
        lib.Update(
            request.Name,
            request.Settings?.PreferredLanguage,
            request.Settings?.MetadataProviders,
            request.Settings?.AutoScan);

        if (request.Paths is not null)
        {
            if (request.Paths.Count == 0)
                throw new ValidationException("paths", "At least one path is required.");
            lib.ReplacePaths(request.Paths);
        }

        await uow.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(request.Settings?.PreferredLanguage)
            && !string.Equals(previousLanguage, lib.PreferredLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await metadataJobs.EnqueueRefreshForLibraryAsync(lib.Id, ct);
        }

        return await MapAsync(lib, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Library not found.");
        uow.Libraries.Remove(lib);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<JobDto> ScanAsync(Guid id, CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Library not found.");

        // Concurrent scans of one library race on series creation; return the active job instead.
        var active = await uow.Jobs.FindActiveAsync(JobType.ScanLibrary, lib.Id, ct);
        if (active is not null)
            return JobMapper.Map(active);

        var now = clock.GetUtcNow();
        var payload = JsonSerializer.Serialize(new { libraryId = lib.Id });
        var job = new BackgroundJob(JobType.ScanLibrary, now, lib.Id, payload);
        await uow.Jobs.AddAsync(job, ct);
        await uow.SaveChangesAsync(ct);

        await jobQueue.EnqueueAsync(
            new JobRequest { JobId = job.Id, Type = JobType.ScanLibrary, LibraryId = lib.Id, PayloadJson = payload },
            ct);

        return JobMapper.Map(job);
    }

    /// <summary>
    /// Optionally updates preferred language, then enqueues FetchMetadata jobs for the library.
    /// Language change here does not trigger a separate matched-only refresh (avoids double enqueue).
    /// </summary>
    public async Task<LibraryMetadataRefreshAccepted> RefreshMetadataAsync(
        Guid id,
        RefreshLibraryMetadataRequest request,
        CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Library not found.");

        if (!string.IsNullOrWhiteSpace(request.PreferredLanguage)
            && !string.Equals(lib.PreferredLanguage, request.PreferredLanguage.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            lib.Update(name: null, preferredLanguage: request.PreferredLanguage.Trim(), metadataProviders: null, autoScan: null);
            await uow.SaveChangesAsync(ct);
        }

        var enqueued = await metadataJobs.EnqueueForLibraryAsync(lib.Id, request.Mode, ct);
        return new LibraryMetadataRefreshAccepted
        {
            LibraryId = lib.Id,
            Mode = request.Mode,
            EnqueuedCount = enqueued,
            PreferredLanguage = lib.PreferredLanguage,
        };
    }

    private async Task<Library> GetAccessibleAsync(Guid id, Caller caller, CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(id, ct)
                  ?? throw new NotFoundException("Library not found.");
        if (!caller.CanAccess(lib.Id))
            throw new NotFoundException("Library not found."); // hide existence
        return lib;
    }

    private async Task<LibraryDto> MapAsync(Library lib, CancellationToken ct)
    {
        var itemCount = await uow.Libraries.CountItemsAsync(lib.Id, ct);
        return new LibraryDto
        {
            Id = lib.Id,
            Name = lib.Name,
            Type = lib.Type,
            Paths = lib.Paths.Select(p => p.Path).ToList(),
            ItemCount = itemCount,
            Settings = new LibrarySettingsDto
            {
                PreferredLanguage = lib.PreferredLanguage,
                MetadataProviders = lib.MetadataProviders.ToList(),
                AutoScan = lib.AutoScan,
            },
            LastScanAt = lib.LastScanAt,
        };
    }
}
