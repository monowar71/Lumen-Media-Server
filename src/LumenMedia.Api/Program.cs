using LumenMedia.Api;
using LumenMedia.Api.Hubs;
using LumenMedia.Application;
using LumenMedia.Infrastructure;
using LumenMedia.Infrastructure.Configuration;
using LumenMedia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApi(builder.Configuration, builder.Environment);

var app = builder.Build();

EnsureStorageDirectories(app);
ApplyMigrations(app);
await RecoverInterruptedJobsAsync(app);
WarnIfCorsIsPermissive(app);

app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOpenApi().AllowAnonymous();
app.MapHub<NotificationsHub>("/hubs/notifications");

app.Run();
return;

static void EnsureStorageDirectories(WebApplication app)
{
    var paths = app.Services.GetRequiredService<IOptions<PathsOptions>>().Value;
    var db = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    foreach (var dir in new[] { paths.Config, paths.Transcodes })
    {
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    var dataSource = ExtractDataSource(db.ConnectionString);
    var dbDir = Path.GetDirectoryName(dataSource);
    if (!string.IsNullOrWhiteSpace(dbDir))
        Directory.CreateDirectory(dbDir);
}

static string ExtractDataSource(string connectionString)
{
    foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        if (kv.Length == 2 && kv[0].Trim().Replace(" ", "").Equals("DataSource", StringComparison.OrdinalIgnoreCase))
            return kv[1].Trim();
    }
    return "lumenmedia.db";
}

static void ApplyMigrations(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LumenMediaDbContext>();
    db.Database.Migrate();
}

static void WarnIfCorsIsPermissive(WebApplication app)
{
    if (app.Environment.IsProduction()
        && (app.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).Length == 0)
    {
        app.Services.GetRequiredService<ILogger<Program>>().LogWarning(
            "Cors:AllowedOrigins is not configured — any browser origin is allowed. "
            + "Set it when the server is reachable from the internet.");
    }
}

// The job queue is in-memory: jobs left Queued/Running by a previous process are lost
// and would otherwise stay "running" in the journal forever.
static async Task RecoverInterruptedJobsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var uow = scope.ServiceProvider.GetRequiredService<LumenMedia.Application.Abstractions.IUnitOfWork>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var recovered = await uow.Jobs.FailUnfinishedAsync(
        "Interrupted by server restart.",
        clock.GetUtcNow(),
        CancellationToken.None);
    if (recovered > 0)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning("Marked {Count} interrupted job(s) as Failed after restart", recovered);
    }
}

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
