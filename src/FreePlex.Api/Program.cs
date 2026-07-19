using FreePlex.Api;
using FreePlex.Api.Hubs;
using FreePlex.Application;
using FreePlex.Infrastructure;
using FreePlex.Infrastructure.Configuration;
using FreePlex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApi(builder.Configuration);

var app = builder.Build();

EnsureStorageDirectories(app);
ApplyMigrations(app);

app.UseExceptionHandler();
app.UseCors();
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
    return "freeplex.db";
}

static void ApplyMigrations(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<FreePlexDbContext>();
    db.Database.Migrate();
}

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
