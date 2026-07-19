using FreePlex.Application.Abstractions;
using FreePlex.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace FreePlex.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(FreePlexDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => db.Users.CountAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct) => await db.Users.AddAsync(user, ct);

    public void Remove(User user) => db.Users.Remove(user);

    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct) =>
        await db.RefreshTokens.AddAsync(token, ct);

    public Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(Guid userId, CancellationToken ct) =>
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
}
