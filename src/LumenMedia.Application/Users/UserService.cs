using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Users;

namespace LumenMedia.Application.Users;

public sealed class UserService(IUnitOfWork uow, IPasswordHasher passwordHasher, TimeProvider clock)
{
    // Keep in sync with AuthService.Validate (first-run setup).
    private const int MinPasswordLength = 8;
    private const string PasswordLengthError = "Password must be at least 8 characters.";

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct)
    {
        var users = await uow.Users.ListAsync(ct);
        return users.Select(UserMapper.Map).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length < 3)
            errors["username"] = ["Username must be at least 3 characters."];
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPasswordLength)
            errors["password"] = [PasswordLengthError];
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var existing = await uow.Users.GetByUsernameAsync(request.Username.Trim(), ct);
        if (existing is not null)
            throw new ConflictException($"User '{request.Username}' already exists.");

        var now = clock.GetUtcNow();
        var user = new User(request.Username.Trim(), passwordHasher.Hash(request.Password), request.Role, now);

        if (request.Role != UserRole.Admin)
            user.SetLibraryAccess(false, request.LibraryAccess ?? [], now);

        user.SetTranscoding(request.AllowTranscoding, user.MaxBitrateRemoteKbps, now);
        if (!string.IsNullOrWhiteSpace(request.Pin))
            user.SetPin(passwordHasher.Hash(request.Pin), now);

        await uow.Users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);
        return UserMapper.Map(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await uow.Users.GetByIdAsync(id, ct)
                   ?? throw new NotFoundException("User not found.");
        var now = clock.GetUtcNow();
        var revokeSessions = false;

        if (request.Role is not null && request.Role.Value != user.Role)
        {
            user.SetRole(request.Role.Value, now);
            revokeSessions = true; // old refresh tokens must not keep minting the old role
        }

        if (request.LibraryAccessAll is not null || request.LibraryAccess is not null)
        {
            var all = request.LibraryAccessAll ?? user.LibraryAccessAll;
            user.SetLibraryAccess(all, request.LibraryAccess ?? user.AllowedLibraryIds, now);
        }

        if (request.AllowTranscoding is not null || request.MaxBitrateKbpsRemote is not null)
        {
            user.SetTranscoding(
                request.AllowTranscoding ?? user.AllowTranscoding,
                request.MaxBitrateKbpsRemote ?? user.MaxBitrateRemoteKbps,
                now);
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < MinPasswordLength)
                throw new ValidationException("password", PasswordLengthError);
            user.SetPassword(passwordHasher.Hash(request.Password), now);
            revokeSessions = true; // a stolen refresh token must not survive a password change
        }

        if (request.Pin is not null)
            user.SetPin(string.IsNullOrWhiteSpace(request.Pin) ? null : passwordHasher.Hash(request.Pin), now);

        if (revokeSessions)
        {
            foreach (var token in await uow.Users.GetActiveRefreshTokensAsync(user.Id, ct))
                token.Revoke(now);
        }

        await uow.SaveChangesAsync(ct);
        return UserMapper.Map(user);
    }

    public async Task DeleteAsync(Guid id, Guid currentUserId, CancellationToken ct)
    {
        if (id == currentUserId)
            throw new ConflictException("You cannot delete your own account.");
        var user = await uow.Users.GetByIdAsync(id, ct)
                   ?? throw new NotFoundException("User not found.");
        uow.Users.Remove(user);
        await uow.SaveChangesAsync(ct);
    }
}
