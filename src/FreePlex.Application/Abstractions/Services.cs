using FreePlex.Domain.Enums;
using FreePlex.Domain.Users;

namespace FreePlex.Application.Abstractions;

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
