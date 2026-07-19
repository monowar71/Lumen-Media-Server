using FreePlex.Application.Contracts;
using FreePlex.Domain.Enums;

namespace FreePlex.Application.Abstractions;

/// <summary>
/// Port for pushing real-time events to connected clients (SignalR in the Api layer).
/// </summary>
public interface IRealtimeNotifier
{
    Task NotifyJobProgressAsync(JobDto job, CancellationToken ct = default);

    Task NotifyLibraryUpdatedAsync(Guid libraryId, int added, int updated, int removed, CancellationToken ct = default);

    Task NotifyPlaybackSyncAsync(
        Guid userId,
        Guid itemId,
        long positionMs,
        string state,
        string? originDeviceId,
        CancellationToken ct = default);

    Task NotifyNowPlayingAsync(
        Guid userId,
        Guid itemId,
        PlaybackMethod method,
        string sessionId,
        CancellationToken ct = default);
}
