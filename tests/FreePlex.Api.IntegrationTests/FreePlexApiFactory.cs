using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace FreePlex.Api.IntegrationTests;

/// <summary>
/// Boots the real API against a throwaway on-disk SQLite database (migrations applied on startup)
/// with a deterministic JWT secret so tokens validate inside the test host.
/// </summary>
public sealed class FreePlexApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"freeplex-it-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_root, "freeplex.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);

        builder.UseEnvironment("Development");
        builder.UseSetting("FreePlex:Database:ConnectionString", $"Data Source={DbPath}");
        builder.UseSetting("FreePlex:Paths:Config", _root);
        builder.UseSetting("FreePlex:Paths:Transcodes", Path.Combine(_root, "transcodes"));
        builder.UseSetting("FreePlex:Paths:Downloads", Path.Combine(_root, "downloads"));
        builder.UseSetting("Jwt:Secret", "integration-test-signing-key-that-is-long-enough-1234567890");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temp database directory.
            }
        }
    }
}
