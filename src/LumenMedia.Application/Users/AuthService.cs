using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Users;

namespace LumenMedia.Application.Users;

public sealed class AuthService(
    IUnitOfWork uow,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    TimeProvider clock)
{
    public async Task<SetupResponse> SetupAsync(SetupRequest request, CancellationToken ct)
    {
        Validate(request.Username, request.Password);

        await using var tx = await uow.BeginTransactionAsync(ct);

        var existing = await uow.Users.CountAsync(ct);
        if (existing > 0)
            throw new ConflictException("Setup already completed: an administrator already exists.");

        var now = clock.GetUtcNow();
        var user = new User(request.Username.Trim(), passwordHasher.Hash(request.Password), UserRole.Admin, now);
        await uow.Users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new SetupResponse { UserId = user.Id, Role = user.Role };
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await uow.Users.GetByUsernameAsync(request.Username.Trim(), ct);
        if (user is null || !passwordHasher.Verify(user.PasswordHash, request.Password))
            throw new UnauthorizedException("Invalid username or password.");

        var tokens = await IssueAndStoreAsync(user, request.DeviceId, request.DeviceName, ct);
        return new TokenResponse
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresInSec = tokens.ExpiresInSec,
            User = UserMapper.Map(user),
        };
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new UnauthorizedException("Refresh token is required.");

        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var stored = await uow.Users.GetRefreshTokenByHashAsync(hash, ct);
        var now = clock.GetUtcNow();
        if (stored is null)
            throw new UnauthorizedException("Refresh token is invalid, expired or revoked.");

        if (!stored.IsActive(now))
        {
            // Reuse of a rotated/revoked refresh token → revoke the whole session family.
            if (stored.RevokedAt is not null)
            {
                foreach (var token in await uow.Users.GetActiveRefreshTokensAsync(stored.UserId, ct))
                    token.Revoke(now);
                await uow.SaveChangesAsync(ct);
            }

            throw new UnauthorizedException("Refresh token is invalid, expired or revoked.");
        }

        var user = await uow.Users.GetByIdAsync(stored.UserId, ct)
                   ?? throw new UnauthorizedException("User no longer exists.");

        // Rotate: revoke the presented token and issue a fresh pair.
        stored.Revoke(now);
        var tokens = await IssueAndStoreAsync(user, stored.DeviceId, stored.DeviceName, ct);

        return new TokenResponse
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresInSec = tokens.ExpiresInSec,
            User = UserMapper.Map(user),
        };
    }

    public async Task LogoutAsync(Guid userId, string? refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = tokenService.HashRefreshToken(refreshToken);
            var stored = await uow.Users.GetRefreshTokenByHashAsync(hash, ct);
            if (stored is not null && stored.UserId == userId)
                stored.Revoke(now);
        }
        else
        {
            foreach (var token in await uow.Users.GetActiveRefreshTokensAsync(userId, ct))
                token.Revoke(now);
        }

        await uow.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await uow.Users.GetByIdAsync(userId, ct)
                   ?? throw new NotFoundException("User not found.");
        return UserMapper.Map(user);
    }

    private async Task<IssuedTokens> IssueAndStoreAsync(User user, string? deviceId, string? deviceName, CancellationToken ct)
    {
        var tokens = tokenService.Issue(user);
        var now = clock.GetUtcNow();
        var refresh = new RefreshToken(
            user.Id,
            tokenService.HashRefreshToken(tokens.RefreshToken),
            tokens.RefreshExpiresAt,
            now,
            deviceId,
            deviceName);
        await uow.Users.AddRefreshTokenAsync(refresh, ct);
        await uow.SaveChangesAsync(ct);
        return tokens;
    }

    private static void Validate(string username, string password)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 3)
            errors["username"] = ["Username must be at least 3 characters."];
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            errors["password"] = ["Password must be at least 8 characters."];
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }
}
