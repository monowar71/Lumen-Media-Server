using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Playback;

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
}
