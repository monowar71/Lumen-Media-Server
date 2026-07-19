using FreePlex.Domain.Enums;

namespace FreePlex.Application.Contracts;

public sealed record ArtworkUrls
{
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Logo { get; init; }
    public string? Thumb { get; init; }
    public string? Banner { get; init; }
}

public sealed record UserDataDto
{
    public bool Watched { get; init; }
    public long PlaybackPositionMs { get; init; }
    public bool IsFavorite { get; init; }
    /// <summary>For series continue-watching cards — the episode to resume.</summary>
    public EpisodeSummary? NextUp { get; init; }
}

public sealed record ExternalIds
{
    public string? Tmdb { get; init; }
    public string? Tvdb { get; init; }
    public string? Imdb { get; init; }
}

public sealed record MediaItemSummary
{
    public required Guid Id { get; init; }
    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int? Year { get; init; }
    public long? RuntimeMs { get; init; }
    public double? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public ArtworkUrls Artwork { get; init; } = new();
    public UserDataDto UserData { get; init; } = new();
    public DateTimeOffset AddedAt { get; init; }
}

public sealed record PersonDto
{
    public required string Name { get; init; }
    public string? Role { get; init; }
    public required string Type { get; init; }
    public int Order { get; init; }
    public string? Thumb { get; init; }
}

public sealed record MediaStreamDto
{
    public required Guid Id { get; init; }
    public required StreamKind Kind { get; init; }
    public int Index { get; init; }
    public string? Codec { get; init; }
    public string? Profile { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public int? BitrateKbps { get; init; }
    public string? Hdr { get; init; }
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public bool IsExternal { get; init; }
    public string? Format { get; init; }
}

public sealed record MediaSourceDto
{
    public required Guid Id { get; init; }
    public string? Path { get; init; }
    public required string Container { get; init; }
    public long SizeBytes { get; init; }
    public long? DurationMs { get; init; }
    public int? OverallBitrateKbps { get; init; }
    public IReadOnlyList<MediaStreamDto> Streams { get; init; } = [];
}

public sealed record MovieDetail
{
    public required Guid Id { get; init; }
    public MediaKind Kind => MediaKind.Movie;
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public required string SortTitle { get; init; }
    public int? Year { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public string? Overview { get; init; }
    public string? Tagline { get; init; }
    public long? RuntimeMs { get; init; }
    public double? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<PersonDto> People { get; init; } = [];
    /// <summary>Remote trailer URL (usually YouTube), when the metadata provider has one.</summary>
    public string? TrailerUrl { get; init; }
    public ExternalIds ExternalIds { get; init; } = new();
    public bool MetadataLocked { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
    public IReadOnlyList<MediaSourceDto> MediaSources { get; init; } = [];
    public UserDataDto UserData { get; init; } = new();
    public required Guid LibraryId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SeriesUserData
{
    public int UnwatchedEpisodeCount { get; init; }
    public EpisodeSummary? NextUp { get; init; }
}

public sealed record SeriesDetail
{
    public required Guid Id { get; init; }
    public MediaKind Kind => MediaKind.Series;
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int? Year { get; init; }
    public int? EndYear { get; init; }
    public string? Status { get; init; }
    public string? Overview { get; init; }
    public double? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<PersonDto> People { get; init; } = [];
    /// <summary>Remote trailer URL (usually YouTube), when the metadata provider has one.</summary>
    public string? TrailerUrl { get; init; }
    public ExternalIds ExternalIds { get; init; } = new();
    public bool MetadataLocked { get; init; }
    public int SeasonCount { get; init; }
    public int EpisodeCount { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
    public SeriesUserData UserData { get; init; } = new();
    public required Guid LibraryId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

/// <summary>Partial update of library-item metadata fields (admin).</summary>
public sealed record UpdateItemMetadataRequest
{
    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int? Year { get; init; }
    public string? Overview { get; init; }
    public string? Tagline { get; init; }
    public double? CommunityRating { get; init; }
    public string? OfficialRating { get; init; }
    /// <summary>When null and other fields change, the item is locked automatically.</summary>
    public bool? MetadataLocked { get; init; }
}

public sealed record MetadataMatchCandidateDto
{
    public required string Provider { get; init; }
    public required string ProviderId { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public double Score { get; init; }
}

public sealed record SeasonDto
{
    public required Guid Id { get; init; }
    public required Guid SeriesId { get; init; }
    public int SeasonNumber { get; init; }
    public string? Name { get; init; }
    public int EpisodeCount { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
}

public sealed record EpisodeSummary
{
    public required Guid Id { get; init; }
    public MediaKind Kind => MediaKind.Episode;
    public required Guid SeriesId { get; init; }
    public required Guid SeasonId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public DateOnly? AirDate { get; init; }
    public long? RuntimeMs { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
    public UserDataDto UserData { get; init; } = new();
}

public sealed record EpisodeDetail
{
    public required Guid Id { get; init; }
    public MediaKind Kind => MediaKind.Episode;
    public required Guid SeriesId { get; init; }
    public required Guid SeasonId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public DateOnly? AirDate { get; init; }
    public long? RuntimeMs { get; init; }
    public ArtworkUrls Artwork { get; init; } = new();
    public IReadOnlyList<MediaSourceDto> MediaSources { get; init; } = [];
    public UserDataDto UserData { get; init; } = new();
}
