namespace LumenMedia.Application.Abstractions;

/// <summary>Outbound adapter that reads watch state from a Plex Media Server.</summary>
public interface IPlexHistoryClient
{
    /// <summary>
    /// Fetches movies/episodes that have been watched or partially watched on the given Plex server.
    /// Throws <see cref="Common.ValidationException"/> / <see cref="Common.UnprocessableException"/> on bad input or Plex errors.
    /// </summary>
    Task<IReadOnlyList<PlexWatchEntry>> FetchWatchStateAsync(Uri baseUrl, string token, CancellationToken ct);
}

/// <summary>Normalized Plex watch-state row ready for matching against LumenMedia media.</summary>
public sealed record PlexWatchEntry(
    PlexWatchKind Kind,
    string Title,
    string? TmdbId,
    string? TvdbId,
    string? ImdbId,
    int? SeasonNumber,
    int? EpisodeNumber,
    bool Watched,
    long PositionMs,
    long? DurationMs,
    int PlayCount,
    DateTimeOffset ViewedAt,
    string? SeriesTitle = null);

public enum PlexWatchKind
{
    Movie,
    Episode
}
