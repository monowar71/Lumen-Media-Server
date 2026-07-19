using System.Security.Cryptography;
using System.Text;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Users;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LumenMedia.Infrastructure.Security;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public IssuedTokens Issue(User user)
    {
        var now = clock.GetUtcNow();
        var accessExpires = now.AddMinutes(_options.AccessTokenMinutes);

        object libs = user.Role == UserRole.Admin || user.LibraryAccessAll
            ? "*"
            : user.AllowedLibraryIds.Select(id => id.ToString()).ToArray();

        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["name"] = user.Username,
            ["role"] = user.Role.ToString(),
            ["libs"] = libs,
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);
        var refreshToken = GenerateRefreshToken();

        return new IssuedTokens(
            accessToken,
            refreshToken,
            _options.AccessTokenMinutes * 60,
            now.AddDays(_options.RefreshTokenDays));
    }

    public string HashRefreshToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }
}
