using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Domain.Jobs;
using FreePlex.Domain.Playback;
using Microsoft.EntityFrameworkCore;

namespace FreePlex.Infrastructure.Persistence.Repositories;

public sealed class ProgressRepository(FreePlexDbContext db) : IProgressRepository
{
    public Task<PlaybackProgress?> GetAsync(Guid userId, Guid mediaId, CancellationToken ct) =>
        db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.MediaId == mediaId, ct);

    public async Task AddAsync(PlaybackProgress progress, CancellationToken ct) =>
        await db.Progress.AddAsync(progress, ct);

    public async Task<IReadOnlyList<PlaybackProgress>> GetContinueWatchingAsync(Guid userId, int limit, CancellationToken ct) =>
        await db.Progress.AsNoTracking()
            .Where(p => p.UserId == userId && !p.Watched && p.PositionMs > 0)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
}

public sealed class JobRepository(FreePlexDbContext db) : IJobRepository
{
    public async Task AddAsync(BackgroundJob job, CancellationToken ct) => await db.BackgroundJobs.AddAsync(job, ct);

    public Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.BackgroundJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<PagedResult<BackgroundJob>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.BackgroundJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<BackgroundJob>(items, page, pageSize, total);
    }
}
