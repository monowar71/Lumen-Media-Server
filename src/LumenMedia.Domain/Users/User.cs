using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Users;

/// <summary>
/// Account able to authenticate and access libraries.
/// Per-library access is expressed either by <see cref="LibraryAccessAll"/> (everything)
/// or by the explicit <see cref="AllowedLibraryIds"/> list.
/// </summary>
public class User
{
    private readonly List<Guid> _allowedLibraryIds = [];

    private User() { }

    public User(string username, string passwordHash, UserRole role, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required", nameof(username));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("Password hash is required", nameof(passwordHash));

        Id = Guid.CreateVersion7();
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
        LibraryAccessAll = role == UserRole.Admin;
        AllowTranscoding = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Username { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public UserRole Role { get; private set; }
    public string? PinHash { get; private set; }
    public bool LibraryAccessAll { get; private set; }
    public bool AllowTranscoding { get; private set; }
    public int? MaxBitrateRemoteKbps { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Guid> AllowedLibraryIds => _allowedLibraryIds;

    public bool CanAccessLibrary(Guid libraryId) =>
        Role == UserRole.Admin || LibraryAccessAll || _allowedLibraryIds.Contains(libraryId);

    public void SetPassword(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        UpdatedAt = now;
    }

    public void SetPin(string? pinHash, DateTimeOffset now)
    {
        PinHash = pinHash;
        UpdatedAt = now;
    }

    public void SetLibraryAccess(bool all, IEnumerable<Guid> libraryIds, DateTimeOffset now)
    {
        LibraryAccessAll = all;
        _allowedLibraryIds.Clear();
        if (!all)
            _allowedLibraryIds.AddRange(libraryIds.Distinct());
        UpdatedAt = now;
    }

    public void SetTranscoding(bool allow, int? maxBitrateRemoteKbps, DateTimeOffset now)
    {
        AllowTranscoding = allow;
        MaxBitrateRemoteKbps = maxBitrateRemoteKbps;
        UpdatedAt = now;
    }

    public void SetRole(UserRole role, DateTimeOffset now)
    {
        Role = role;
        UpdatedAt = now;
    }
}
