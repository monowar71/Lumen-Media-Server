using System.Threading.Channels;
using LumenMedia.Application.Abstractions;

namespace LumenMedia.Infrastructure.Jobs;

/// <summary>
/// Event-driven background queue backed by an unbounded <see cref="Channel{T}"/>.
/// Workers block on <see cref="DequeueAllAsync"/> (no busy-wait / polling).
/// </summary>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobRequest> _channel = Channel.CreateUnbounded<JobRequest>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ValueTask EnqueueAsync(JobRequest request, CancellationToken ct) =>
        _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<JobRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
