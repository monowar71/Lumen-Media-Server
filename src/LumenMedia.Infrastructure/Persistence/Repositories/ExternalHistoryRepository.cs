using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Playback;
using Microsoft.EntityFrameworkCore;

namespace LumenMedia.Infrastructure.Persistence.Repositories;

public sealed class ExternalHistoryRepository(LumenMediaDbContext db) : IExternalHistoryRepository
{
    public Task<ExternalPlaybackHistory?> GetAsync(Guid userId, string dedupeKey, CancellationToken ct) =>
        db.ExternalPlaybackHistory.FirstOrDefaultAsync(x => x.UserId == userId && x.DedupeKey == dedupeKey, ct);

    public async Task AddAsync(ExternalPlaybackHistory row, CancellationToken ct) =>
        await db.ExternalPlaybackHistory.AddAsync(row, ct);

    public async Task<IReadOnlyList<ExternalPlaybackHistory>> ListAllAsync(Guid userId, CancellationToken ct) =>
        await db.ExternalPlaybackHistory.AsNoTracking()
            .Where(x => x.UserId == userId && (x.Watched || x.PositionMs > 0))
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExternalPlaybackHistory>> FindByDedupeKeysAsync(
        IReadOnlyCollection<string> dedupeKeys,
        CancellationToken ct)
    {
        if (dedupeKeys.Count == 0)
            return [];

        return await db.ExternalPlaybackHistory.AsNoTracking()
            .Where(x => dedupeKeys.Contains(x.DedupeKey))
            .ToListAsync(ct);
    }

    public Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct) =>
        db.ExternalPlaybackHistory.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

    public Task<int> DeleteAsync(Guid userId, string dedupeKey, CancellationToken ct) =>
        db.ExternalPlaybackHistory
            .Where(x => x.UserId == userId && x.DedupeKey == dedupeKey)
            .ExecuteDeleteAsync(ct);
}
