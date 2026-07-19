using FreePlex.Domain.Enums;

namespace FreePlex.Application.Abstractions;

public sealed record MetadataMatch(string Provider, string ProviderId, string Title, int? Year, double Score);

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
    long? RuntimeMs = null);

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
