using LumenMedia.Api.Hubs;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using Microsoft.AspNetCore.SignalR;

namespace LumenMedia.Api.Realtime;

/// <summary>
/// SignalR adapter for <see cref="IRealtimeNotifier"/>. Method names match api.md §8 event names.
/// </summary>
public sealed class SignalRRealtimeNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public Task NotifyJobProgressAsync(JobDto job, CancellationToken ct = default) =>
        hub.Clients.Group(NotificationsHub.JobGroup(job.Id.ToString()))
            .SendAsync("JobProgress", new { @event = "JobProgress", job }, ct);

    public Task NotifyLibraryUpdatedAsync(Guid libraryId, int added, int updated, int removed, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(
            "LibraryUpdated",
            new { @event = "LibraryUpdated", libraryId, added, updated, removed },
            ct);

    public Task NotifyPlaybackSyncAsync(
        Guid userId,
        Guid itemId,
        long positionMs,
        string state,
        string? originDeviceId,
        CancellationToken ct = default) =>
        hub.Clients.Group(NotificationsHub.UserGroup(userId.ToString()))
            .SendAsync(
                "PlaybackSync",
                new { @event = "PlaybackSync", itemId, positionMs, state, originDeviceId },
                ct);

    public Task NotifyNowPlayingAsync(
        Guid userId,
        Guid itemId,
        PlaybackMethod method,
        string sessionId,
        CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(
            "NowPlaying",
            new { @event = "NowPlaying", userId, itemId, method, sessionId },
            ct);
}
