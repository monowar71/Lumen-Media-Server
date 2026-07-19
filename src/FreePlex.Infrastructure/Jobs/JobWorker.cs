using System.Text.Json;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Jobs;
using FreePlex.Application.Metadata;
using FreePlex.Domain.Enums;
using FreePlex.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FreePlex.Infrastructure.Jobs;

/// <summary>
/// Background worker pool. Consumers block on the channel until work arrives (idle ≈ 0 CPU).
/// Each job runs in its own DI scope so it gets a fresh unit-of-work.
/// </summary>
public sealed class JobWorker(
    IJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<JobWorkerOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, options.Value.WorkerCount);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var request in queue.DequeueAllAsync(ct))
        {
            try
            {
                await HandleAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job {JobId} ({Type}) failed", request.JobId, request.Type);
                await MarkFailedAsync(request.JobId, ex.Message, ct);
            }
        }
    }

    private async Task HandleAsync(JobRequest request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var notifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();

        var job = await uow.Jobs.GetByIdAsync(request.JobId, ct);
        if (job is null || job.State == JobState.Cancelled)
            return;

        job.Start(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        await SafeNotifyJobAsync(notifier, job, ct);

        ScanResult? scanResult = null;
        Guid? enrichedLibraryId = null;
        switch (request.Type)
        {
            case JobType.ScanLibrary when request.LibraryId is not null:
                var scanner = scope.ServiceProvider.GetRequiredService<IMediaScanner>();
                var progress = new Progress<double>(p =>
                {
                    job.Report(p, $"Scanning… {p:P0}");
                    _ = SafeNotifyJobAsync(notifier, job, ct);
                });
                scanResult = await scanner.ScanAsync(request.LibraryId.Value, progress, ct);
                job.Succeed(clock.GetUtcNow(), $"Added {scanResult.Added}, updated {scanResult.Updated}, removed {scanResult.Removed}.");
                break;

            case JobType.FetchMetadata:
            {
                // Separate DI scope so enrichment's DbContext does not fight with the
                // tracked BackgroundJob entity in this worker scope (SQLite concurrency).
                using var enrichScope = scopeFactory.CreateScope();
                var enricher = enrichScope.ServiceProvider.GetRequiredService<IMetadataEnricher>();
                var (itemId, provider, providerId) = ParseMetadataPayload(request.PayloadJson);
                if (itemId is null)
                {
                    job.Fail("FetchMetadata payload missing itemId.", clock.GetUtcNow());
                    break;
                }

                var ok = await enricher.EnrichAsync(itemId.Value, provider, providerId, ct);
                var item = await uow.Media.GetByIdAsync(itemId.Value, ct);
                enrichedLibraryId = item?.LibraryId;
                job.Succeed(clock.GetUtcNow(), ok ? "Metadata applied." : "No metadata match.");
                break;
            }

            case JobType.CleanupTranscodes:
            {
                var sessions = scope.ServiceProvider.GetRequiredService<IPlaybackSessionStore>();
                var transcoderSvc = scope.ServiceProvider.GetRequiredService<ITranscoder>();
                var paths = scope.ServiceProvider.GetRequiredService<IOptions<PathsOptions>>();
                var opts = scope.ServiceProvider.GetRequiredService<IOptions<FreePlex.Application.Playback.PlaybackOptions>>();
                var now = clock.GetUtcNow();
                var idle = TimeSpan.FromSeconds(Math.Max(30, opts.Value.IdleTimeoutSec));
                var cleaned = 0;
                foreach (var session in sessions.ActiveSessions.ToArray())
                {
                    if (session.ExpiresAt > now && now - session.LastAccess <= idle)
                        continue;
                    await transcoderSvc.StopAsync(session.SessionId, ct);
                    sessions.Remove(session.SessionId);
                    cleaned++;
                }

                var root = paths.Value.Transcodes;
                if (Directory.Exists(root))
                {
                    var active = sessions.ActiveSessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var name = Path.GetFileName(dir);
                        if (name is "bench" || active.Contains(name) || !name.StartsWith("sess-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                            cleaned++;
                        }
                        catch
                        {
                            // best-effort
                        }
                    }
                }

                job.Succeed(clock.GetUtcNow(), $"Cleaned {cleaned} session(s)/dir(s).");
                break;
            }

            default:
                job.Succeed(clock.GetUtcNow(), "No-op (handler not implemented in this phase).");
                break;
        }

        await uow.SaveChangesAsync(ct);
        await SafeNotifyJobAsync(notifier, job, ct);

        if (scanResult is not null && request.LibraryId is not null)
        {
            await SafeNotifyAsync(
                () => notifier.NotifyLibraryUpdatedAsync(
                    request.LibraryId.Value,
                    scanResult.Added,
                    scanResult.Updated,
                    scanResult.Removed,
                    ct),
                "LibraryUpdated",
                request.JobId);

            // Kick off metadata for items that still lack overview / TMDB id.
            try
            {
                var metaJobs = scope.ServiceProvider.GetRequiredService<MetadataJobService>();
                await metaJobs.EnqueueMissingForLibraryAsync(request.LibraryId.Value, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to enqueue metadata jobs after scan {JobId}", request.JobId);
            }
        }

        if (enrichedLibraryId is not null)
        {
            await SafeNotifyAsync(
                () => notifier.NotifyLibraryUpdatedAsync(enrichedLibraryId.Value, 0, 1, 0, ct),
                "LibraryUpdated",
                request.JobId);
        }

        logger.LogInformation("Job {JobId} ({Type}) completed", request.JobId, request.Type);
    }

    private static (Guid? ItemId, string? Provider, string? ProviderId) ParseMetadataPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? itemId = null;
            if (root.TryGetProperty("itemId", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(idEl.GetString(), out var g))
                itemId = g;
            var provider = root.TryGetProperty("provider", out var p) ? p.GetString() : null;
            var providerId = root.TryGetProperty("providerId", out var pid) ? pid.GetString() : null;
            return (itemId, provider, providerId);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private async Task MarkFailedAsync(Guid jobId, string error, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            var notifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
            var job = await uow.Jobs.GetByIdAsync(jobId, ct);
            if (job is not null)
            {
                job.Fail(error, clock.GetUtcNow());
                await uow.SaveChangesAsync(ct);
                await SafeNotifyJobAsync(notifier, job, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark job {JobId} as failed", jobId);
        }
    }

    private async Task SafeNotifyJobAsync(IRealtimeNotifier notifier, Domain.Jobs.BackgroundJob job, CancellationToken ct)
    {
        await SafeNotifyAsync(() => notifier.NotifyJobProgressAsync(JobMapper.Map(job), ct), "JobProgress", job.Id);
    }

    private async Task SafeNotifyAsync(Func<Task> notify, string eventName, Guid jobId)
    {
        try
        {
            await notify();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to broadcast {Event} for job {JobId}", eventName, jobId);
        }
    }
}
