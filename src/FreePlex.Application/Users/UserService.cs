using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Users;

namespace FreePlex.Application.Users;

public sealed class UserService(IUnitOfWork uow, IPasswordHasher passwordHasher, TimeProvider clock)
{
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
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 6)
            errors["password"] = ["Password must be at least 6 characters."];
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

        if (request.Role is not null)
            user.SetRole(request.Role.Value, now);

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
            if (request.Password.Length < 6)
                throw new ValidationException("password", "Password must be at least 6 characters.");
            user.SetPassword(passwordHasher.Hash(request.Password), now);
        }

        if (request.Pin is not null)
            user.SetPin(string.IsNullOrWhiteSpace(request.Pin) ? null : passwordHasher.Hash(request.Pin), now);

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
