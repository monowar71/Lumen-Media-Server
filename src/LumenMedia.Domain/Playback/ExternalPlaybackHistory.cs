using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Playback;

/// <summary>
/// Watch history row imported from an external source (e.g. Plex) that could not be
/// matched to local library media. Display-only until matching content appears.
/// </summary>
public sealed class ExternalPlaybackHistory
{
    private ExternalPlaybackHistory()
    {
        DedupeKey = string.Empty;
        Title = string.Empty;
    }

    public ExternalPlaybackHistory(
        Guid userId,
        string dedupeKey,
        MediaKind kind,
        string title,
        string? seriesTitle,
        int? seasonNumber,
        int? episodeNumber,
        DateTimeOffset viewedAt)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
            throw new ArgumentException("Dedupe key is required.", nameof(dedupeKey));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        UserId = userId;
        DedupeKey = dedupeKey.Trim();
        Kind = kind;
        Title = title.Trim();
        SeriesTitle = string.IsNullOrWhiteSpace(seriesTitle) ? null : seriesTitle.Trim();
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        ViewedAt = viewedAt;
        UpdatedAt = viewedAt;
    }

    public Guid UserId { get; private set; }
    public string DedupeKey { get; private set; }
    public MediaKind Kind { get; private set; }
    public string Title { get; private set; }
    public string? SeriesTitle { get; private set; }
    public int? SeasonNumber { get; private set; }
    public int? EpisodeNumber { get; private set; }
    public bool Watched { get; private set; }
    public long PositionMs { get; private set; }
    public long? DurationMs { get; private set; }
    public int PlayCount { get; private set; }
    public DateTimeOffset ViewedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? TmdbId { get; private set; }
    public string? TvdbId { get; private set; }
    public string? ImdbId { get; private set; }

    public void SetExternalIds(string? tmdbId, string? tvdbId, string? imdbId)
    {
        TmdbId = string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId.Trim();
        TvdbId = string.IsNullOrWhiteSpace(tvdbId) ? null : tvdbId.Trim();
        ImdbId = string.IsNullOrWhiteSpace(imdbId) ? null : imdbId.Trim();
    }

    /// <summary>
    /// Applies an external watch snapshot. Skips when local state is newer.
    /// Equal timestamps still apply so resume offsets can refresh.
    /// </summary>
    public bool TryApplyImport(
        bool watched,
        long positionMs,
        long? durationMs,
        int playCount,
        DateTimeOffset viewedAt)
    {
        if (viewedAt < UpdatedAt)
            return false;

        var nextPosition = watched ? 0L : (positionMs < 0 ? 0 : positionMs);
        var nextPlayCount = watched
            ? Math.Max(PlayCount, Math.Max(1, playCount))
            : Math.Max(PlayCount, playCount);
        var nextDuration = durationMs ?? DurationMs;

        if (Watched == watched
            && PositionMs == nextPosition
            && PlayCount == nextPlayCount
            && DurationMs == nextDuration
            && UpdatedAt == viewedAt)
        {
            return false;
        }

        if (durationMs is not null)
            DurationMs = durationMs;

        if (watched)
        {
            Watched = true;
            PositionMs = 0;
            PlayCount = nextPlayCount;
        }
        else
        {
            Watched = false;
            PositionMs = nextPosition;
            PlayCount = nextPlayCount;
        }

        ViewedAt = viewedAt;
        UpdatedAt = viewedAt;
        return true;
    }

    /// <summary>Stable key for upsert / promotion when a local match appears later.</summary>
    public static string BuildDedupeKey(
        MediaKind kind,
        string title,
        string? seriesTitle,
        int? seasonNumber,
        int? episodeNumber,
        string? tmdbId,
        string? tvdbId,
        string? imdbId)
    {
        // Prefer the richest identifier available — same priority as CandidateDedupeKeys.
        return CandidateDedupeKeys(kind, title, seriesTitle, seasonNumber, episodeNumber, tmdbId, tvdbId, imdbId)[0];
    }

    /// <summary>
    /// All dedupe keys that may have been used when the external row was stored
    /// (ids and/or title), so promotion can find rows regardless of which id was present at import.
    /// </summary>
    public static IReadOnlyList<string> CandidateDedupeKeys(
        MediaKind kind,
        string title,
        string? seriesTitle,
        int? seasonNumber,
        int? episodeNumber,
        string? tmdbId,
        string? tvdbId,
        string? imdbId)
    {
        var keys = new List<string>(4);
        if (kind == MediaKind.Movie)
        {
            if (!string.IsNullOrWhiteSpace(tmdbId))
                keys.Add($"m:tmdb:{tmdbId.Trim()}");
            if (!string.IsNullOrWhiteSpace(imdbId))
                keys.Add($"m:imdb:{imdbId.Trim()}");
            if (!string.IsNullOrWhiteSpace(tvdbId))
                keys.Add($"m:tvdb:{tvdbId.Trim()}");
            keys.Add($"m:title:{NormalizeTitle(title)}");
            return keys;
        }

        var season = seasonNumber ?? 0;
        var episode = episodeNumber ?? 0;
        if (!string.IsNullOrWhiteSpace(tmdbId))
            keys.Add($"e:tmdb:{tmdbId.Trim()}:{season}:{episode}");
        if (!string.IsNullOrWhiteSpace(tvdbId))
            keys.Add($"e:tvdb:{tvdbId.Trim()}:{season}:{episode}");
        if (!string.IsNullOrWhiteSpace(imdbId))
            keys.Add($"e:imdb:{imdbId.Trim()}:{season}:{episode}");
        var series = string.IsNullOrWhiteSpace(seriesTitle) ? title : seriesTitle;
        keys.Add($"e:title:{NormalizeTitle(series)}:{season}:{episode}");
        return keys;
    }

    public static string NormalizeTitle(string title) =>
        string.Join(' ', title.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
