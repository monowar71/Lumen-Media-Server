using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Contracts;

public sealed record SetupRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? ServerName { get; init; }
}

public sealed record SetupResponse
{
    public required Guid UserId { get; init; }
    public required UserRole Role { get; init; }
}

public sealed record LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
}

public sealed record TokenResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresInSec { get; init; }
    public UserDto? User { get; init; }
}

public sealed record RefreshRequest
{
    public required string RefreshToken { get; init; }
}

public sealed record LogoutRequest
{
    public string? RefreshToken { get; init; }
}

public sealed record UserDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required UserRole Role { get; init; }
    public required object LibraryAccess { get; init; }
    public bool AllowTranscoding { get; init; }
    public int? MaxBitrateKbpsRemote { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record CreateUserRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public UserRole Role { get; init; } = UserRole.User;
    public IReadOnlyList<Guid>? LibraryAccess { get; init; }
    public bool AllowTranscoding { get; init; } = true;
    public string? Pin { get; init; }
}

public sealed record UpdateUserRequest
{
    public UserRole? Role { get; init; }
    public IReadOnlyList<Guid>? LibraryAccess { get; init; }
    public bool? LibraryAccessAll { get; init; }
    public bool? AllowTranscoding { get; init; }
    public int? MaxBitrateKbpsRemote { get; init; }
    public string? Password { get; init; }
    public string? Pin { get; init; }
}
