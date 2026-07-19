using FreePlex.Application.Abstractions;
using FreePlex.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace FreePlex.Infrastructure.Persistence.Repositories;

public sealed class LibraryRepository(FreePlexDbContext db) : ILibraryRepository
{
    public Task<Library?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Libraries.Include(l => l.Paths).FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<Library>> ListAsync(CancellationToken ct) =>
        await db.Libraries.AsNoTracking().Include(l => l.Paths).OrderBy(l => l.Name).ToListAsync(ct);

    public Task<int> CountItemsAsync(Guid libraryId, CancellationToken ct) =>
        db.MediaItems.CountAsync(m => m.LibraryId == libraryId, ct);

    public async Task AddAsync(Library library, CancellationToken ct) => await db.Libraries.AddAsync(library, ct);

    public void Remove(Library library) => db.Libraries.Remove(library);
}
