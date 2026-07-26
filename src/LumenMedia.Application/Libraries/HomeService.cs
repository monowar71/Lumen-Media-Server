using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;

namespace LumenMedia.Application.Libraries;

public sealed class HomeService(IUnitOfWork uow, ProgressService progressService)
{
    public async Task<HomeResponse> GetAsync(Caller caller, CancellationToken ct)
    {
        var allowed = caller.IsAdmin || caller.AllLibraries
            ? (await uow.Libraries.ListAsync(ct)).Select(l => l.Id).ToList()
            : caller.LibraryIds.ToList();

        var continueWatching = await progressService.ContinueWatchingAsync(caller, 20, ct);
        var recentlyAdded = await uow.Media.GetRecentlyAddedAsync(allowed, 20, caller.UserId, ct);

        var sections = new List<HomeSection>
        {
            new() { Id = "continue", Title = "Continue Watching", Items = continueWatching.Items },
            new() { Id = "recentlyAdded", Title = "Recently Added", Items = recentlyAdded },
            new() { Id = "recommended", Title = "Recommended", Items = recentlyAdded.Take(10).ToList() },
        };

        return new HomeResponse { Sections = sections };
    }
}
