using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LumenMedia.Api.Hubs;

/// <summary>
/// Real-time notifications hub (JobProgress, LibraryUpdated, PlaybackSync, NowPlaying).
/// Authenticated with the same JWT. Clients are auto-added to their per-user group.
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        await base.OnConnectedAsync();
    }

    public Task SubscribeJob(string jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, JobGroup(jobId));

    public Task UnsubscribeJob(string jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroup(jobId));

    public static string UserGroup(string userId) => $"user:{userId}";

    public static string JobGroup(string jobId) => $"job:{jobId}";
}
