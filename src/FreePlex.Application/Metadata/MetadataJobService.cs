using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Jobs;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Jobs;

namespace FreePlex.Application.Metadata;

/// <summary>Enqueues FetchMetadata jobs (manual refresh/match and post-scan enrichment).</summary>
public sealed class MetadataJobService(IUnitOfWork uow, IJobQueue jobQueue, TimeProvider clock)
{
    public async Task<JobDto> EnqueueItemAsync(Guid itemId, string? provider, string? providerId, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            itemId,
            provider,
            providerId,
        });
        var job = new BackgroundJob(JobType.FetchMetadata, clock.GetUtcNow(), payloadJson: payload);
        await uow.Jobs.AddAsync(job, ct);
        await uow.SaveChangesAsync(ct);
        await jobQueue.EnqueueAsync(
            new JobRequest
            {
                JobId = job.Id,
                Type = JobType.FetchMetadata,
                PayloadJson = payload,
            },
            ct);
        return JobMapper.Map(job);
    }

    public async Task EnqueueMissingForLibraryAsync(Guid libraryId, CancellationToken ct)
    {
        var ids = await uow.Media.ListIdsMissingMetadataAsync(libraryId, ct);
        foreach (var id in ids)
            await EnqueueItemAsync(id, provider: null, providerId: null, ct);
    }

    /// <summary>Enqueue FetchMetadata for a library according to <paramref name="mode"/>.</summary>
    public async Task<int> EnqueueForLibraryAsync(Guid libraryId, MetadataRefreshMode mode, CancellationToken ct)
    {
        var ids = mode switch
        {
            MetadataRefreshMode.Matched => await uow.Media.ListIdsWithExternalIdsForLibraryAsync(libraryId, ct),
            MetadataRefreshMode.All => await uow.Media.ListIdsForLibraryAsync(libraryId, ct),
            _ => await uow.Media.ListIdsMissingMetadataAsync(libraryId, ct),
        };

        foreach (var id in ids)
            await EnqueueItemAsync(id, provider: null, providerId: null, ct);

        return ids.Count;
    }

    /// <summary>Re-fetch metadata for every item that already has an external id (language change).</summary>
    public async Task EnqueueRefreshAllAsync(CancellationToken ct)
    {
        var ids = await uow.Media.ListIdsWithExternalIdsAsync(ct);
        foreach (var id in ids)
            await EnqueueItemAsync(id, provider: null, providerId: null, ct);
    }

    /// <summary>Re-fetch metadata for matched items in one library (preferred-language change).</summary>
    public async Task EnqueueRefreshForLibraryAsync(Guid libraryId, CancellationToken ct)
    {
        var ids = await uow.Media.ListIdsWithExternalIdsForLibraryAsync(libraryId, ct);
        foreach (var id in ids)
            await EnqueueItemAsync(id, provider: null, providerId: null, ct);
    }
}
