using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Domain.Users;

namespace LumenMedia.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public sealed record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresInSec, DateTimeOffset RefreshExpiresAt);

public interface ITokenService
{
    IssuedTokens Issue(User user);

    /// <summary>Hashes a raw refresh token for storage/lookup (never store the raw value).</summary>
    string HashRefreshToken(string rawToken);
}

public sealed record ArtworkResult(Stream Content, string ContentType, string ETag);

public interface IArtworkStore
{
    Task<ArtworkResult?> GetAsync(string localPath, int? width, int? height, int? quality, CancellationToken ct);
    Task<string> SaveAsync(Guid ownerId, ArtworkKind kind, Stream content, CancellationToken ct);
    /// <summary>Deletes the on-disk artwork directory for an owner (best-effort).</summary>
    void DeleteOwner(Guid ownerId);
}

public sealed record ThemeSongResult(Stream Content, string ContentType, string ETag);

/// <summary>Disk cache for ThemerrDB theme songs under <c>/config/metadata/{id}/theme.mp3</c>.</summary>
public interface IThemeSongStore
{
    bool Exists(Guid itemId);
    Task SaveAsync(Guid itemId, Stream mp3Content, CancellationToken ct);
    Task<ThemeSongResult?> OpenAsync(Guid itemId, CancellationToken ct);
    void Delete(Guid itemId);
}

/// <summary>Looks up YouTube theme URLs in the public ThemerrDB JSON database.</summary>
public interface IThemerrDbClient
{
    Task<string?> GetYoutubeThemeUrlAsync(string tmdbId, MediaKind kind, CancellationToken ct);
}

/// <summary>
/// Syncs ambient theme audio for a library item during metadata enrichment:
/// ThemerrDB lookup → YouTube download → MP3 cache.
/// </summary>
public interface IThemeSongService
{
    Task SyncFromThemerrAsync(MediaItem item, CancellationToken ct);
}

public sealed record ScanResult(int Added, int Updated, int Removed);

public interface IMediaScanner
{
    Task<ScanResult> ScanAsync(Guid libraryId, IProgress<double>? progress, CancellationToken ct);
}

public sealed record ImportResult(bool Success, Guid? MediaItemId, string? Error);

public interface IFileImporter
{
    Task<ImportResult> ImportAsync(string sourcePath, CancellationToken ct);
}
