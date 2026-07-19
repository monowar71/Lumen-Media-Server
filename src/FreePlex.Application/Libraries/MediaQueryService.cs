using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Domain.Media;

namespace FreePlex.Application.Libraries;

public sealed class MediaQueryService(IUnitOfWork uow)
{
    public const int MaxPageSize = 200;

    public async Task<PagedResult<MediaItemSummary>> ListItemsAsync(
        Guid libraryId,
        Caller caller,
        LibraryItemsQuery query,
        CancellationToken ct)
    {
        var lib = await uow.Libraries.GetByIdAsync(libraryId, ct)
                  ?? throw new NotFoundException("Library not found.");
        if (!caller.CanAccess(lib.Id))
            throw new NotFoundException("Library not found.");

        return await uow.Media.ListAsync(query, ct);
    }

    public async Task<object> GetItemDetailAsync(Guid id, Caller caller, CancellationToken ct)
    {
        var item = await uow.Media.GetDetailAsync(id, ct)
                   ?? throw new NotFoundException("Item not found.");
        if (!caller.CanAccess(item.LibraryId))
            throw new NotFoundException("Item not found.");

        var progress = await uow.Progress.GetAsync(caller.UserId, item.Id, ct);

        return item switch
        {
            Movie movie => MediaMapper.MapMovieDetail(movie, progress, caller.IsAdmin),
            Series series => await MapSeriesAsync(series, ct),
            _ => throw new NotFoundException("Item not found."),
        };
    }

    private async Task<SeriesDetail> MapSeriesAsync(Series series, CancellationToken ct)
    {
        var seasons = await uow.Media.GetSeasonsAsync(series.Id, ct);
        var episodeCount = 0;
        foreach (var season in seasons)
            episodeCount += (await uow.Media.GetEpisodesAsync(season.Id, ct)).Count;
        return MediaMapper.MapSeriesDetail(series, seasons.Count, episodeCount, episodeCount);
    }

    public async Task<PagedResult<SeasonDto>> GetSeasonsAsync(Guid seriesId, Caller caller, CancellationToken ct)
    {
        var series = await uow.Media.GetByIdAsync(seriesId, ct)
                     ?? throw new NotFoundException("Series not found.");
        if (!caller.CanAccess(series.LibraryId))
            throw new NotFoundException("Series not found.");

        var seasons = await uow.Media.GetSeasonsAsync(seriesId, ct);
        var dtos = new List<SeasonDto>();
        foreach (var season in seasons)
        {
            var count = (await uow.Media.GetEpisodesAsync(season.Id, ct)).Count;
            dtos.Add(MediaMapper.MapSeason(season, count));
        }
        return new PagedResult<SeasonDto>(dtos, 1, dtos.Count == 0 ? 1 : dtos.Count, dtos.Count);
    }

    public async Task<PagedResult<EpisodeSummary>> GetEpisodesAsync(Guid seasonId, Caller caller, CancellationToken ct)
    {
        var episodes = await uow.Media.GetEpisodesAsync(seasonId, ct);
        var dtos = new List<EpisodeSummary>();
        foreach (var e in episodes)
        {
            // Access is enforced via the owning series' library.
            var series = await uow.Media.GetByIdAsync(e.SeriesId, ct);
            if (series is null || !caller.CanAccess(series.LibraryId))
                continue;
            var progress = await uow.Progress.GetAsync(caller.UserId, e.Id, ct);
            dtos.Add(MediaMapper.MapEpisodeSummary(e, progress));
        }
        return new PagedResult<EpisodeSummary>(dtos, 1, dtos.Count == 0 ? 1 : dtos.Count, dtos.Count);
    }

    public async Task<EpisodeDetail> GetEpisodeAsync(Guid episodeId, Caller caller, CancellationToken ct)
    {
        var episode = await uow.Media.GetEpisodeAsync(episodeId, ct)
                      ?? throw new NotFoundException("Episode not found.");
        var series = await uow.Media.GetByIdAsync(episode.SeriesId, ct);
        if (series is null || !caller.CanAccess(series.LibraryId))
            throw new NotFoundException("Episode not found.");

        var progress = await uow.Progress.GetAsync(caller.UserId, episode.Id, ct);
        return MediaMapper.MapEpisodeDetail(episode, progress, caller.IsAdmin);
    }

    public async Task<SearchResponse> SearchAsync(string term, Caller caller, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term))
            return new SearchResponse();

        limit = Math.Clamp(limit, 1, 100);
        var allowed = await ResolveAllowedLibrariesAsync(caller, ct);
        var results = await uow.Media.SearchAsync(term.Trim(), allowed, limit, ct);

        return new SearchResponse
        {
            Movies = results.Where(r => r.Kind == Domain.Enums.MediaKind.Movie).ToList(),
            Series = results.Where(r => r.Kind == Domain.Enums.MediaKind.Series).ToList(),
            Episodes = [],
        };
    }

    private async Task<IReadOnlyList<Guid>> ResolveAllowedLibrariesAsync(Caller caller, CancellationToken ct)
    {
        if (caller.IsAdmin || caller.AllLibraries)
            return (await uow.Libraries.ListAsync(ct)).Select(l => l.Id).ToList();
        return caller.LibraryIds;
    }
}
