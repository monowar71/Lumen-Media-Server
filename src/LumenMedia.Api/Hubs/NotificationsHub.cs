using LumenMedia.Api.Auth;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LumenMedia.Api.Hubs;

/// <summary>
/// Real-time notifications hub (JobProgress, LibraryUpdated, PlaybackSync, NowPlaying).
/// Authenticated with the same JWT. Clients are auto-added to their per-user and library groups.
/// </summary>
[Authorize]
public sealed class NotificationsHub(IUnitOfWork uow) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var caller = user.ToCaller();
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(caller.UserId.ToString()));

            if (caller.IsAdmin || caller.AllLibraries)
                await Groups.AddToGroupAsync(Context.ConnectionId, LibrariesAllGroup);
            else
            {
                foreach (var libraryId in caller.LibraryIds)
                    await Groups.AddToGroupAsync(Context.ConnectionId, LibraryGroup(libraryId.ToString()));
            }
        }

        await base.OnConnectedAsync();
    }

    public async Task SubscribeJob(string jobId)
    {
        if (Context.User?.Identity?.IsAuthenticated != true)
            throw new HubException("Unauthorized.");

        var caller = Context.User.ToCaller();
        if (!caller.IsAdmin)
            throw new HubException("Forbidden.");

        if (!Guid.TryParse(jobId, out var id))
            throw new HubException("Job not found.");

        var job = await uow.Jobs.GetByIdAsync(id, Context.ConnectionAborted);
        if (job is null)
            throw new HubException("Job not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, JobGroup(jobId));
    }

    public Task UnsubscribeJob(string jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroup(jobId));

    public static string UserGroup(string userId) => $"user:{userId}";

    public static string JobGroup(string jobId) => $"job:{jobId}";

    public static string LibraryGroup(string libraryId) => $"library:{libraryId}";

    public const string LibrariesAllGroup = "libraries:all";
}
