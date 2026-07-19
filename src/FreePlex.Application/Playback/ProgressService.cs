using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Libraries;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;
using FreePlex.Domain.Playback;

namespace FreePlex.Application.Playback;

public sealed class ProgressService(IUnitOfWork uow, TimeProvider clock, IRealtimeNotifier notifier)
{
    public async Task<ProgressResponse> UpdateAsync(Guid userId, Guid itemId, UpdateProgressRequest request, CancellationToken ct)
    {
        if (request.Watched is { } watched)
            return await SetWatchedAsync(userId, itemId, watched, ct);

        var kind = await ResolvePlayableKindAsync(itemId, ct)
                   ?? throw new NotFoundException("Item not found.");

        var now = clock.GetUtcNow();
        var progress = await uow.Progress.GetAsync(userId, itemId, ct);
        if (progress is null)
        {
            progress = new PlaybackProgress(userId, itemId, kind, now);
            await uow.Progress.AddAsync(progress, ct);
        }

        var stopped = string.Equals(request.State, "stopped", StringComparison.OrdinalIgnoreCase);
        progress.Update(request.PositionMs, request.DurationMs, stopped, now);
        await uow.SaveChangesAsync(ct);

        await notifier.NotifyPlaybackSyncAsync(
            userId,
            itemId,
            progress.PositionMs,
            request.State,
            originDeviceId: null,
            ct);

        return ToResponse(itemId, progress);
    }

    public async Task<ProgressResponse> GetAsync(Guid userId, Guid itemId, CancellationToken ct)
    {
        var progress = await uow.Progress.GetAsync(userId, itemId, ct);
        return new ProgressResponse
        {
            ItemId = itemId,
            PositionMs = progress?.PositionMs ?? 0,
            Watched = progress?.Watched ?? false,
            UpdatedAt = progress?.UpdatedAt ?? clock.GetUtcNow(),
        };
    }

    public async Task<PagedResult<MediaItemSummary>> ContinueWatchingAsync(Guid userId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MediaQueryService.MaxPageSize);
        // Fetch extra rows so episode→series rollup / dedupe still fills the page.
        var entries = await uow.Progress.GetContinueWatchingAsync(userId, limit * 3, ct);

        var movieIds = entries.Where(e => e.MediaKind == MediaKind.Movie).Select(e => e.MediaId).Distinct().ToList();
        var movieSummaries = movieIds.Count > 0
            ? await uow.Media.GetSummariesByIdsAsync(movieIds, userId, ct)
            : [];
        var movieById = movieSummaries.ToDictionary(s => s.Id);

        var items = new List<MediaItemSummary>();
        var seenSeries = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (items.Count >= limit)
                break;

            if (entry.MediaKind == MediaKind.Movie)
            {
                if (!movieById.TryGetValue(entry.MediaId, out var movie))
                    continue;
                items.Add(movie with
                {
                    UserData = movie.UserData with
                    {
                        PlaybackPositionMs = entry.PositionMs,
                        Watched = entry.Watched,
                    },
                });
                continue;
            }

            if (entry.MediaKind != MediaKind.Episode)
                continue;

            var episode = await uow.Media.GetEpisodeAsync(entry.MediaId, ct);
            if (episode is null)
                continue;
            if (!seenSeries.Add(episode.SeriesId))
                continue;

            var seriesList = await uow.Media.GetSummariesByIdsAsync([episode.SeriesId], userId, ct);
            var series = seriesList.FirstOrDefault();
            if (series is null)
                continue;

            var nextUp = MediaMapper.MapEpisodeSummary(episode, entry);
            items.Add(series with
            {
                RuntimeMs = episode.RuntimeMs,
                UserData = series.UserData with
                {
                    PlaybackPositionMs = entry.PositionMs,
                    Watched = false,
                    NextUp = nextUp,
                },
            });
        }

        return new PagedResult<MediaItemSummary>(items, 1, limit, items.Count);
    }

    private async Task<ProgressResponse> SetWatchedAsync(Guid userId, Guid itemId, bool watched, CancellationToken ct)
    {
        var targets = await ResolveWatchedTargetsAsync(itemId, ct);
        if (targets.Count == 0)
            throw new NotFoundException("Item not found.");

        var now = clock.GetUtcNow();
        PlaybackProgress? primary = null;

        foreach (var (mediaId, kind) in targets)
        {
            var progress = await uow.Progress.GetAsync(userId, mediaId, ct);
            if (progress is null)
            {
                progress = new PlaybackProgress(userId, mediaId, kind, now);
                await uow.Progress.AddAsync(progress, ct);
            }

            progress.SetWatched(watched, now);
            if (mediaId == itemId || primary is null)
                primary = progress;
        }

        await uow.SaveChangesAsync(ct);

        // Cascade targets (series/season) are not playable themselves — sync the first episode.
        var syncId = targets[0].MediaId;
        var syncProgress = await uow.Progress.GetAsync(userId, syncId, ct)
                           ?? primary!;

        await notifier.NotifyPlaybackSyncAsync(
            userId,
            syncId,
            syncProgress.PositionMs,
            state: watched ? "stopped" : "paused",
            originDeviceId: null,
            ct);

        // For series/season the itemId is not a progress row — report the requested flag.
        if (targets.Count == 1 && targets[0].MediaId == itemId)
            return ToResponse(itemId, syncProgress);

        return new ProgressResponse
        {
            ItemId = itemId,
            PositionMs = 0,
            Watched = watched,
            UpdatedAt = now,
        };
    }

    private async Task<IReadOnlyList<(Guid MediaId, MediaKind Kind)>> ResolveWatchedTargetsAsync(
        Guid itemId,
        CancellationToken ct)
    {
        var item = await uow.Media.GetByIdAsync(itemId, ct);
        if (item is Movie)
            return [(itemId, MediaKind.Movie)];

        if (item is Series series)
        {
            var result = new List<(Guid, MediaKind)>();
            foreach (var season in await uow.Media.GetSeasonsAsync(series.Id, ct))
            {
                foreach (var ep in await uow.Media.GetEpisodesAsync(season.Id, ct))
                    result.Add((ep.Id, MediaKind.Episode));
            }
            return result;
        }

        var singleEpisode = await uow.Media.GetEpisodeAsync(itemId, ct);
        if (singleEpisode is not null)
            return [(singleEpisode.Id, MediaKind.Episode)];

        var seasonEntity = await uow.Media.GetSeasonAsync(itemId, ct);
        if (seasonEntity is not null)
        {
            var episodes = await uow.Media.GetEpisodesAsync(seasonEntity.Id, ct);
            return episodes.Select(e => (e.Id, MediaKind.Episode)).ToList();
        }

        return [];
    }

    private async Task<MediaKind?> ResolvePlayableKindAsync(Guid itemId, CancellationToken ct)
    {
        var item = await uow.Media.GetByIdAsync(itemId, ct);
        if (item is Movie)
            return MediaKind.Movie;
        var episode = await uow.Media.GetEpisodeAsync(itemId, ct);
        return episode is not null ? MediaKind.Episode : null;
    }

    private static ProgressResponse ToResponse(Guid itemId, PlaybackProgress progress) => new()
    {
        ItemId = itemId,
        PositionMs = progress.PositionMs,
        Watched = progress.Watched,
        UpdatedAt = progress.UpdatedAt,
    };
}
