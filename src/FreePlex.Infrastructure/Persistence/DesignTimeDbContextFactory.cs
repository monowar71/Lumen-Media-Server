using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FreePlex.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time so migrations can be generated without
/// booting the web host. The connection string here is never used at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FreePlexDbContext>
{
    public FreePlexDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FreePlexDbContext>()
            .UseSqlite(
                "Data Source=freeplex-design.db",
                o => o.MigrationsAssembly(typeof(FreePlexDbContext).Assembly.GetName().Name))
            .Options;
        return new FreePlexDbContext(options);
    }
}
