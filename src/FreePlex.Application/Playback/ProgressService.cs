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
        var kind = await ResolveKindAsync(itemId, ct)
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

        return new ProgressResponse
        {
            ItemId = itemId,
            PositionMs = progress.PositionMs,
            Watched = progress.Watched,
            UpdatedAt = progress.UpdatedAt,
        };
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

    private async Task<MediaKind?> ResolveKindAsync(Guid itemId, CancellationToken ct)
    {
        var item = await uow.Media.GetByIdAsync(itemId, ct);
        if (item is Movie)
            return MediaKind.Movie;
        var episode = await uow.Media.GetEpisodeAsync(itemId, ct);
        return episode is not null ? MediaKind.Episode : null;
    }
}
