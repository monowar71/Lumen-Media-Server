using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Persistence.Repositories;

namespace LumenMedia.Infrastructure.Persistence;

public sealed class UnitOfWork(LumenMediaDbContext db) : IUnitOfWork
{
    public IUserRepository Users { get; } = new UserRepository(db);
    public ILibraryRepository Libraries { get; } = new LibraryRepository(db);
    public IMediaRepository Media { get; } = new MediaRepository(db);
    public IProgressRepository Progress { get; } = new ProgressRepository(db);
    public IExternalHistoryRepository ExternalHistory { get; } = new ExternalHistoryRepository(db);
    public IJobRepository Jobs { get; } = new JobRepository(db);

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public void DiscardChanges() => db.ChangeTracker.Clear();

    public async Task<IAppTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new TransactionScope(tx);
    }

    private sealed class TransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : IAppTransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            await tx.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // Dispose without an explicit commit means the scope failed — roll back.
            if (!_committed)
                await tx.RollbackAsync();
            await tx.DisposeAsync();
        }
    }
}
