using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Playback;

/// <summary>
/// Per-user watch state for a movie or an episode. Composite key (UserId, MediaId).
/// Intentionally not FK-bound to two tables — polymorphism handled via <see cref="MediaKind"/>.
/// </summary>
public class PlaybackProgress
{
    private PlaybackProgress() { }

    public PlaybackProgress(Guid userId, Guid mediaId, MediaKind mediaKind, DateTimeOffset now)
    {
        UserId = userId;
        MediaId = mediaId;
        MediaKind = mediaKind;
        UpdatedAt = now;
    }

    public Guid UserId { get; private set; }
    public Guid MediaId { get; private set; }
    public MediaKind MediaKind { get; private set; }
    public long PositionMs { get; private set; }
    public long? DurationMs { get; private set; }
    public bool Watched { get; private set; }
    public bool IsFavorite { get; private set; }
    public int PlayCount { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private const double WatchedThreshold = 0.9;

    /// <summary>
    /// Applies a progress update. When stopped at &gt;= 90% of duration the item is marked
    /// watched and the position is reset to 0 (per api.md §6.10).
    /// </summary>
    public void Update(long positionMs, long? durationMs, bool stopped, DateTimeOffset now)
    {
        if (durationMs is not null)
            DurationMs = durationMs;

        var effectiveDuration = durationMs ?? DurationMs;
        var reachedEnd = effectiveDuration is > 0 && positionMs >= effectiveDuration * WatchedThreshold;

        if (stopped && reachedEnd)
        {
            if (!Watched)
                PlayCount++;
            Watched = true;
            PositionMs = 0;
        }
        else
        {
            PositionMs = positionMs < 0 ? 0 : positionMs;
            if (!reachedEnd)
                Watched = false;
        }

        UpdatedAt = now;
    }

    public void SetFavorite(bool favorite, DateTimeOffset now)
    {
        IsFavorite = favorite;
        UpdatedAt = now;
    }

    /// <summary>
    /// Explicitly marks the item watched or unwatched (manual toggle from clients).
    /// Watched resets resume position; first transition to watched bumps play count.
    /// </summary>
    public void SetWatched(bool watched, DateTimeOffset now)
    {
        if (watched)
        {
            if (!Watched)
                PlayCount++;
            Watched = true;
            PositionMs = 0;
        }
        else
        {
            Watched = false;
            PositionMs = 0;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Clears watch/resume state while preserving favorite flag (used by "clear history").
    /// </summary>
    public void ClearWatchHistory(DateTimeOffset now)
    {
        Watched = false;
        PositionMs = 0;
        PlayCount = 0;
        DurationMs = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Applies an external watch snapshot (e.g. Plex import). Skips when local state is newer.
    /// Equal timestamps still apply so resume offsets from Plex can refresh local progress.
    /// Returns whether the row was updated.
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

        // Idempotent no-op when nothing would change (avoids inflated "imported" counts).
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

        UpdatedAt = viewedAt;
        return true;
    }
}
