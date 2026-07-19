using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Abstractions;

/// <summary>A unit of background work enqueued for the worker pool.</summary>
public sealed record JobRequest
{
    public required Guid JobId { get; init; }
    public required JobType Type { get; init; }
    public Guid? LibraryId { get; init; }
    public string? PayloadJson { get; init; }
}

/// <summary>
/// Event-driven background job queue (System.Threading.Channels under the hood).
/// Enqueue returns immediately; workers block on the channel (no busy-wait).
/// </summary>
public interface IJobQueue
{
    ValueTask EnqueueAsync(JobRequest request, CancellationToken ct);
    IAsyncEnumerable<JobRequest> DequeueAllAsync(CancellationToken ct);
}
