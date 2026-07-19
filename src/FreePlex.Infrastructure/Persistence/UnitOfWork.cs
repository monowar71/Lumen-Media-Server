using FreePlex.Application.Abstractions;
using FreePlex.Infrastructure.Persistence.Repositories;

namespace FreePlex.Infrastructure.Persistence;

public sealed class UnitOfWork(FreePlexDbContext db) : IUnitOfWork
{
    public IUserRepository Users { get; } = new UserRepository(db);
    public ILibraryRepository Libraries { get; } = new LibraryRepository(db);
    public IMediaRepository Media { get; } = new MediaRepository(db);
    public IProgressRepository Progress { get; } = new ProgressRepository(db);
    public IJobRepository Jobs { get; } = new JobRepository(db);

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public void DiscardChanges() => db.ChangeTracker.Clear();

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new TransactionScope(tx);
    }

    private sealed class TransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await tx.CommitAsync();
            await tx.DisposeAsync();
        }
    }
}
