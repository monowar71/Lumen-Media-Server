using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Contracts;

public sealed record UpdateProgressRequest
{
    /// <summary>Playback position. Ignored when <see cref="Watched"/> is set.</summary>
    public long PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public string? SessionId { get; init; }
    public string State { get; init; } = "playing";

    /// <summary>
    /// When set, explicitly marks the item (and cascaded episodes for series/season) watched
    /// or unwatched. Position/state fields are ignored in that case.
    /// </summary>
    public bool? Watched { get; init; }
}

public sealed record ProgressResponse
{
    public required Guid ItemId { get; init; }
    public long PositionMs { get; init; }
    public bool Watched { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record HistoryEntryDto
{
    public required Guid ItemId { get; init; }
    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public string? SeriesTitle { get; init; }
    public Guid? SeriesId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? Year { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
    public bool Watched { get; init; }
    public long PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ClearHistoryResponse
{
    public int ClearedCount { get; init; }
}

public sealed record ImportPlexHistoryRequest
{
    /// <summary>Plex Media Server base URL, e.g. http://192.168.0.10:32400</summary>
    public required string BaseUrl { get; init; }
    /// <summary>X-Plex-Token for the account whose watch state should be imported.</summary>
    public required string Token { get; init; }
}

public sealed record ImportPlexHistoryResponse
{
    public int Scanned { get; init; }
    public int Matched { get; init; }
    public int Imported { get; init; }
    public int SkippedNewer { get; init; }
    public int Unmatched { get; init; }
}

public sealed record HomeSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<MediaItemSummary> Items { get; init; } = [];
}

public sealed record HomeResponse
{
    public IReadOnlyList<HomeSection> Sections { get; init; } = [];
}
