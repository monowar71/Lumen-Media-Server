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

        var matchedRows = await uow.Progress.ListAllHistoryAsync(userId, ct);
        var externalRows = await uow.ExternalHistory.ListAllAsync(userId, ct);

        var timeline = new List<(DateTimeOffset UpdatedAt, bool External, PlaybackProgress? Progress, ExternalPlaybackHistory? ExternalRow)>(
            matchedRows.Count + externalRows.Count);

        foreach (var row in matchedRows)
            timeline.Add((row.UpdatedAt, false, row, null));
        foreach (var row in externalRows)
            timeline.Add((row.UpdatedAt, true, null, row));

        timeline.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        var total = timeline.Count;
        var pageSlice = timeline.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (pageSlice.Count == 0)
            return new PagedResult<HistoryEntryDto>([], page, pageSize, total);

        var pageProgress = pageSlice.Where(x => !x.External).Select(x => x.Progress!).ToList();
        var movieIds = pageProgress
            .Where(e => e.MediaKind == MediaKind.Movie)
            .Select(e => e.MediaId)
            .Distinct()
            .ToList();
        var episodeIds = pageProgress
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

        var items = new List<HistoryEntryDto>(pageSlice.Count);
        foreach (var slot in pageSlice)
        {
            if (slot.External)
            {
                var ext = slot.ExternalRow!;
                items.Add(new HistoryEntryDto
                {
                    ItemId = null,
                    Kind = ext.Kind,
                    Title = ext.Title,
                    SeriesTitle = ext.SeriesTitle,
                    SeasonNumber = ext.SeasonNumber,
                    EpisodeNumber = ext.EpisodeNumber,
                    Watched = ext.Watched,
                    PositionMs = ext.PositionMs,
                    DurationMs = ext.DurationMs,
                    UpdatedAt = ext.UpdatedAt,
                    IsExternal = true,
                    ExternalKey = ext.DedupeKey,
                });
                continue;
            }

            var entry = slot.Progress!;
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

        return new PagedResult<HistoryEntryDto>(items, page, pageSize, total);
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

        cleared += await uow.ExternalHistory.DeleteAllForUserAsync(userId, ct);

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
        var progressByMedia = new Dictionary<Guid, PlaybackProgress>();
        var externalByKey = new Dictionary<string, ExternalPlaybackHistory>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var target = await ResolveTargetAsync(entry, ct);
            var dedupeKey = ExternalPlaybackHistory.BuildDedupeKey(
                entry.Kind == PlexWatchKind.Movie ? MediaKind.Movie : MediaKind.Episode,
                entry.Title,
                entry.SeriesTitle,
                entry.SeasonNumber,
                entry.EpisodeNumber,
                entry.TmdbId,
                entry.TvdbId,
                entry.ImdbId);

            if (target is null)
            {
                unmatched++;
                if (!externalByKey.TryGetValue(dedupeKey, out var external))
                {
                    external = await uow.ExternalHistory.GetAsync(userId, dedupeKey, ct);
                    if (external is null)
                    {
                        external = new ExternalPlaybackHistory(
                            userId,
                            dedupeKey,
                            entry.Kind == PlexWatchKind.Movie ? MediaKind.Movie : MediaKind.Episode,
                            entry.Title,
                            entry.SeriesTitle,
                            entry.SeasonNumber,
                            entry.EpisodeNumber,
                            entry.ViewedAt);
                        await uow.ExternalHistory.AddAsync(external, ct);
                    }

                    externalByKey[dedupeKey] = external;
                }

                external.SetExternalIds(entry.TmdbId, entry.TvdbId, entry.ImdbId);
                var localUpdatedAt = external.UpdatedAt;
                var appliedExternal = external.TryApplyImport(
                    entry.Watched,
                    entry.PositionMs,
                    entry.DurationMs,
                    entry.PlayCount,
                    entry.ViewedAt);

                if (appliedExternal)
                    imported++;
                else if (entry.ViewedAt < localUpdatedAt)
                    skippedNewer++;
                continue;
            }

            matched++;
            var (mediaId, kind) = target.Value;
            // Promote: drop any previously unmatched row(s) for this title/ids.
            foreach (var key in ExternalPlaybackHistory.CandidateDedupeKeys(
                         kind,
                         entry.Title,
                         entry.SeriesTitle,
                         entry.SeasonNumber,
                         entry.EpisodeNumber,
                         entry.TmdbId,
                         entry.TvdbId,
                         entry.ImdbId))
            {
                await uow.ExternalHistory.DeleteAsync(userId, key, ct);
            }

            if (!progressByMedia.TryGetValue(mediaId, out var progress))
            {
                progress = await uow.Progress.GetAsync(userId, mediaId, ct);
                if (progress is null)
                {
                    progress = new PlaybackProgress(userId, mediaId, kind, entry.ViewedAt);
                    await uow.Progress.AddAsync(progress, ct);
                }

                progressByMedia[mediaId] = progress;
            }

            var progressUpdatedAt = progress.UpdatedAt;
            var applied = progress.TryApplyImport(
                entry.Watched,
                entry.PositionMs,
                entry.DurationMs,
                entry.PlayCount,
                entry.ViewedAt);

            if (applied)
                imported++;
            else if (entry.ViewedAt < progressUpdatedAt)
                skippedNewer++;
        }

        if (imported > 0 || unmatched > 0)
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
            if (movie is null && !string.IsNullOrWhiteSpace(entry.Title))
                movie = await uow.Media.FindMovieByTitleAsync(entry.Title, ct);
            return movie is null ? null : (movie.Id, MediaKind.Movie);
        }

        if (entry.SeasonNumber is null || entry.EpisodeNumber is null)
            return null;

        var series = await uow.Media.FindSeriesByExternalIdsAsync(entry.TmdbId, entry.TvdbId, entry.ImdbId, ct);
        if (series is null && !string.IsNullOrWhiteSpace(entry.SeriesTitle))
            series = await uow.Media.FindSeriesByTitleAsync(entry.SeriesTitle, ct);
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
