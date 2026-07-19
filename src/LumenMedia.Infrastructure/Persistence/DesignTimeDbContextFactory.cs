using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LumenMedia.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time so migrations can be generated without
/// booting the web host. The connection string here is never used at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LumenMediaDbContext>
{
    public LumenMediaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LumenMediaDbContext>()
            .UseSqlite(
                "Data Source=lumenmedia-design.db",
                o => o.MigrationsAssembly(typeof(LumenMediaDbContext).Assembly.GetName().Name))
            .Options;
        return new LumenMediaDbContext(options);
    }
}
