using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Abstractions;

public sealed record MetadataMatch(string Provider, string ProviderId, string Title, int? Year, double Score);

/// <summary>Actor/director/writer credit as returned by a metadata provider.</summary>
public sealed record PersonCredit(
    string Name,
    PersonType Type,
    string? Role,
    int Order,
    string? ThumbUrl,
    string? ProviderPersonId);

/// <summary>Episode-level metadata (one season fetch returns all its episodes).</summary>
public sealed record EpisodeMetadata(
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? Overview,
    DateOnly? AirDate,
    long? RuntimeMs);

public sealed record MetadataDetails(
    string Provider,
    string ProviderId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    double? CommunityRating,
    string? OfficialRating,
    string? ImdbId,
    string? PosterUrl,
    string? BackdropUrl,
    IReadOnlyList<string> Genres,
    string? Tagline = null,
    DateOnly? ReleaseDate = null,
    long? RuntimeMs = null,
    IReadOnlyList<PersonCredit>? People = null,
    string? TrailerUrl = null);

/// <summary>Locale pair used when fetching localized metadata from providers.</summary>
public sealed record MetadataLanguage(string Language, string FallbackLanguage);

/// <summary>Current server-wide metadata language (from settings, not a frozen IOptions snapshot).</summary>
public interface IMetadataLanguageSource
{
    MetadataLanguage Get();
}

public interface IMetadataProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<MetadataMatch>> SearchAsync(
        string title,
        int? year,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct);
    Task<MetadataDetails?> GetDetailsAsync(
        string providerId,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct);

    /// <summary>
    /// Episode titles/overviews for one season. Default: not supported by this provider.
    /// </summary>
    Task<IReadOnlyList<EpisodeMetadata>> GetSeasonEpisodesAsync(
        string providerId,
        int seasonNumber,
        MetadataLanguage language,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<EpisodeMetadata>>([]);
}

/// <summary>Downloads a remote image into the local artwork cache.</summary>
public interface IRemoteImageFetcher
{
    Task<Stream> OpenReadAsync(string url, CancellationToken ct);
}

public interface IMetadataEnricher
{
    /// <summary>
    /// Search (unless provider/providerId given), apply overview/ids/ratings/genres,
    /// and download poster/backdrop. Returns false when nothing could be matched.
    /// </summary>
    Task<bool> EnrichAsync(Guid itemId, string? provider, string? providerId, CancellationToken ct);
}
