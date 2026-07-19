using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;

namespace LumenMedia.Application.Playback;

/// <summary>
/// Promotes unmatched external watch history into normal <see cref="PlaybackProgress"/>
/// when the corresponding library item appears (scan / metadata match). After promotion the
/// row looks like a native LumenMedia watch — not a grey external stub.
/// </summary>
public sealed class ExternalHistoryPromoter(IUnitOfWork uow)
{
    public Task<int> PromoteForMovieAsync(Movie movie, CancellationToken ct)
    {
        var keys = ExternalPlaybackHistory.CandidateDedupeKeys(
            MediaKind.Movie,
            movie.Title,
            seriesTitle: null,
            seasonNumber: null,
            episodeNumber: null,
            movie.TmdbId,
            movie.TvdbId,
            movie.ImdbId);
        return PromoteByKeysAsync(movie.Id, MediaKind.Movie, keys, ct);
    }

    public Task<int> PromoteForEpisodeAsync(Episode episode, Series series, CancellationToken ct)
    {
        var keys = ExternalPlaybackHistory.CandidateDedupeKeys(
            MediaKind.Episode,
            episode.Title ?? series.Title,
            series.Title,
            episode.SeasonNumber,
            episode.EpisodeNumber,
            series.TmdbId,
            series.TvdbId,
            series.ImdbId);
        return PromoteByKeysAsync(episode.Id, MediaKind.Episode, keys, ct);
    }

    /// <summary>Promotes every unmatched episode row that maps to an existing episode of this series.</summary>
    public async Task<int> PromoteForSeriesAsync(Series series, CancellationToken ct)
    {
        // Collect candidate keys for every local episode (ids + title fallback).
        var graph = await uow.Media.GetTrackedSeriesGraphAsync(series.Id, ct) ?? series;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var episodeByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var season in graph.Seasons)
        {
            foreach (var episode in season.Episodes)
            {
                var episodeKeys = ExternalPlaybackHistory.CandidateDedupeKeys(
                    MediaKind.Episode,
                    episode.Title ?? series.Title,
                    series.Title,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    series.TmdbId,
                    series.TvdbId,
                    series.ImdbId);
                foreach (var key in episodeKeys)
                {
                    keys.Add(key);
                    episodeByKey.TryAdd(key, episode.Id);
                }
            }
        }

        if (keys.Count == 0)
            return 0;

        var rows = await uow.ExternalHistory.FindByDedupeKeysAsync(keys, ct);
        if (rows.Count == 0)
            return 0;

        var promoted = 0;
        foreach (var row in rows)
        {
            if (!episodeByKey.TryGetValue(row.DedupeKey, out var episodeId))
            {
                // Row may match via alternate key (e.g. stored as tmdb, looked up via title).
                episodeId = ResolveEpisodeId(row, episodeByKey, series);
                if (episodeId == Guid.Empty)
                    continue;
            }

            if (await ApplyRowAsync(row, episodeId, MediaKind.Episode, ct))
                promoted++;
        }

        if (promoted > 0)
            await uow.SaveChangesAsync(ct);

        return promoted;
    }

    private async Task<int> PromoteByKeysAsync(
        Guid mediaId,
        MediaKind kind,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        if (keys.Count == 0)
            return 0;

        var rows = await uow.ExternalHistory.FindByDedupeKeysAsync(keys, ct);
        if (rows.Count == 0)
            return 0;

        var promoted = 0;
        foreach (var row in rows)
        {
            if (await ApplyRowAsync(row, mediaId, kind, ct))
                promoted++;
        }

        if (promoted > 0)
            await uow.SaveChangesAsync(ct);

        return promoted;
    }

    private async Task<bool> ApplyRowAsync(
        ExternalPlaybackHistory row,
        Guid mediaId,
        MediaKind kind,
        CancellationToken ct)
    {
        var progress = await uow.Progress.GetAsync(row.UserId, mediaId, ct);
        if (progress is null)
        {
            progress = new PlaybackProgress(row.UserId, mediaId, kind, row.UpdatedAt);
            await uow.Progress.AddAsync(progress, ct);
        }

        // Native progress: same fields as a local watch / Plex-matched import.
        progress.TryApplyImport(
            row.Watched,
            row.PositionMs,
            row.DurationMs,
            row.PlayCount,
            row.UpdatedAt);

        await uow.ExternalHistory.DeleteAsync(row.UserId, row.DedupeKey, ct);
        return true;
    }

    private static Guid ResolveEpisodeId(
        ExternalPlaybackHistory row,
        Dictionary<string, Guid> episodeByKey,
        Series series)
    {
        if (row.SeasonNumber is null || row.EpisodeNumber is null)
            return Guid.Empty;

        foreach (var key in ExternalPlaybackHistory.CandidateDedupeKeys(
                     MediaKind.Episode,
                     row.Title,
                     row.SeriesTitle ?? series.Title,
                     row.SeasonNumber,
                     row.EpisodeNumber,
                     series.TmdbId ?? row.TmdbId,
                     series.TvdbId ?? row.TvdbId,
                     series.ImdbId ?? row.ImdbId))
        {
            if (episodeByKey.TryGetValue(key, out var id))
                return id;
        }

        return Guid.Empty;
    }
}
