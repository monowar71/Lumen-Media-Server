using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Libraries;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;

namespace LumenMedia.Application.Playback;

public sealed class HistoryService(
    IUnitOfWork uow,
    TimeProvider clock,
    IPlexHistoryClient plex)
{
    public async Task<PagedResult<HistoryEntryDto>> ListAsync(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MediaQueryService.MaxPageSize);

        var pageResult = await uow.Progress.GetHistoryAsync(userId, page, pageSize, ct);
        if (pageResult.Items.Count == 0)
            return new PagedResult<HistoryEntryDto>([], page, pageSize, pageResult.Total);

        var movieIds = pageResult.Items
            .Where(e => e.MediaKind == MediaKind.Movie)
            .Select(e => e.MediaId)
            .Distinct()
            .ToList();
        var episodeIds = pageResult.Items
            .Where(e => e.MediaKind == MediaKind.Episode)
            .Select(e => e.MediaId)
            .Distinct()
            .ToList();

        var movies = movieIds.Count > 0
            ? (await uow.Media.GetSummariesByIdsAsync(movieIds, userId, ct)).ToDictionary(m => m.Id)
            : new Dictionary<Guid, MediaItemSummary>();

        var episodes = episodeIds.Count > 0
            ? await uow.Media.GetEpisodesByIdsAsync(episodeIds, ct)
            : [];
        var episodeById = episodes.ToDictionary(e => e.Id);

        var seriesIds = episodes.Select(e => e.SeriesId).Distinct().ToList();
        var seriesSummaries = seriesIds.Count > 0
            ? (await uow.Media.GetSummariesByIdsAsync(seriesIds, userId, ct)).ToDictionary(s => s.Id)
            : new Dictionary<Guid, MediaItemSummary>();

        var items = new List<HistoryEntryDto>(pageResult.Items.Count);
        foreach (var entry in pageResult.Items)
        {
            if (entry.MediaKind == MediaKind.Movie)
            {
                if (!movies.TryGetValue(entry.MediaId, out var movie))
                    continue;
                items.Add(new HistoryEntryDto
                {
                    ItemId = movie.Id,
                    Kind = MediaKind.Movie,
                    Title = movie.Title,
                    Year = movie.Year,
                    Artwork = movie.Artwork,
                    Watched = entry.Watched,
                    PositionMs = entry.PositionMs,
                    DurationMs = entry.DurationMs ?? movie.RuntimeMs,
                    UpdatedAt = entry.UpdatedAt,
                });
                continue;
            }

            if (!episodeById.TryGetValue(entry.MediaId, out var episode))
                continue;

            seriesSummaries.TryGetValue(episode.SeriesId, out var series);
            items.Add(new HistoryEntryDto
            {
                ItemId = episode.Id,
                Kind = MediaKind.Episode,
                Title = episode.Title ?? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
                SeriesTitle = series?.Title,
                SeriesId = episode.SeriesId,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                Year = series?.Year,
                Artwork = series?.Artwork ?? new ArtworkUrls
                {
                    Thumb = ArtworkUrlBuilder.ItemArtwork(episode.Id, ArtworkKind.Thumb),
                },
                Watched = entry.Watched,
                PositionMs = entry.PositionMs,
                DurationMs = entry.DurationMs ?? episode.RuntimeMs,
                UpdatedAt = entry.UpdatedAt,
            });
        }

        return new PagedResult<HistoryEntryDto>(items, page, pageSize, pageResult.Total);
    }

    public async Task<ClearHistoryResponse> ClearAsync(Guid userId, CancellationToken ct)
    {
        var rows = await uow.Progress.ListHistoryForClearAsync(userId, ct);
        var now = clock.GetUtcNow();
        var cleared = 0;

        foreach (var row in rows)
        {
            if (row.IsFavorite)
            {
                row.ClearWatchHistory(now);
            }
            else
            {
                uow.Progress.Remove(row);
            }

            cleared++;
        }

        if (cleared > 0)
            await uow.SaveChangesAsync(ct);

        return new ClearHistoryResponse { ClearedCount = cleared };
    }

    public async Task<ImportPlexHistoryResponse> ImportFromPlexAsync(
        Guid userId,
        ImportPlexHistoryRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            throw new ValidationException("baseUrl", "Plex server URL is required.");
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new ValidationException("token", "Plex token is required.");

        if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ValidationException("baseUrl", "Plex server URL must be an absolute http(s) URL.");
        }

        var entries = await plex.FetchWatchStateAsync(baseUrl, request.Token.Trim(), ct);

        var scanned = entries.Count;
        var matched = 0;
        var imported = 0;
        var skippedNewer = 0;
        var unmatched = 0;

        foreach (var entry in entries)
        {
            var target = await ResolveTargetAsync(entry, ct);
            if (target is null)
            {
                unmatched++;
                continue;
            }

            matched++;
            var (mediaId, kind) = target.Value;
            var progress = await uow.Progress.GetAsync(userId, mediaId, ct);
            if (progress is null)
            {
                progress = new PlaybackProgress(userId, mediaId, kind, entry.ViewedAt);
                await uow.Progress.AddAsync(progress, ct);
                // New row starts with UpdatedAt = viewedAt from ctor; ApplyImport still needed for state.
                // Ctor sets UpdatedAt but leaves Watched=false — force apply by using viewedAt.
            }

            var applied = progress.TryApplyImport(
                entry.Watched,
                entry.PositionMs,
                entry.DurationMs,
                entry.PlayCount,
                entry.ViewedAt);

            if (applied)
                imported++;
            else
                skippedNewer++;
        }

        if (imported > 0)
            await uow.SaveChangesAsync(ct);

        return new ImportPlexHistoryResponse
        {
            Scanned = scanned,
            Matched = matched,
            Imported = imported,
            SkippedNewer = skippedNewer,
            Unmatched = unmatched,
        };
    }

    private async Task<(Guid MediaId, MediaKind Kind)?> ResolveTargetAsync(PlexWatchEntry entry, CancellationToken ct)
    {
        if (entry.Kind == PlexWatchKind.Movie)
        {
            var movie = await uow.Media.FindMovieByExternalIdsAsync(entry.TmdbId, entry.TvdbId, entry.ImdbId, ct);
            return movie is null ? null : (movie.Id, MediaKind.Movie);
        }

        if (entry.SeasonNumber is null || entry.EpisodeNumber is null)
            return null;

        var series = await uow.Media.FindSeriesByExternalIdsAsync(entry.TmdbId, entry.TvdbId, entry.ImdbId, ct);
        if (series is null)
            return null;

        var episode = await uow.Media.FindEpisodeForScanAsync(
            series.Id,
            entry.SeasonNumber.Value,
            entry.EpisodeNumber.Value,
            ct);
        return episode is null ? null : (episode.Id, MediaKind.Episode);
    }
}
