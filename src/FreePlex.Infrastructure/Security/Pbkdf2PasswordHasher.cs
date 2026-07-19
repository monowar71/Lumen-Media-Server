using FreePlex.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace FreePlex.Infrastructure.Security;

/// <summary>Wraps ASP.NET Core Identity's PBKDF2-based <see cref="PasswordHasher{T}"/>.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _inner = new();

    public string Hash(string password) => _inner.HashPassword(Subject, password);

    public bool Verify(string hash, string password) =>
        _inner.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
}
