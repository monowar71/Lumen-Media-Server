using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Infrastructure.ArtworkStorage;
using LumenMedia.Infrastructure.Configuration;
using LumenMedia.Infrastructure.Import;
using LumenMedia.Infrastructure.Jobs;
using LumenMedia.Infrastructure.Metadata;
using LumenMedia.Infrastructure.Persistence;
using LumenMedia.Infrastructure.Plex;
using LumenMedia.Infrastructure.Scanning;
using LumenMedia.Infrastructure.Security;
using LumenMedia.Infrastructure.Settings;
using LumenMedia.Infrastructure.Transcoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LumenMedia.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
        services.Configure<PathsOptions>(config.GetSection(PathsOptions.SectionName));
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.Configure<ImportOptions>(config.GetSection(ImportOptions.SectionName));
        services.Configure<AutoScanOptions>(config.GetSection(AutoScanOptions.SectionName));
        services.Configure<JobWorkerOptions>(config.GetSection(JobWorkerOptions.SectionName));
        services.Configure<PlaybackOptions>(config.GetSection(PlaybackOptions.SectionName));
        services.Configure<MetadataOptions>(config.GetSection(MetadataOptions.SectionName));

        services.TryAddTimeProvider();

        var connectionString = config.GetSection(DatabaseOptions.SectionName)["ConnectionString"]
                               ?? new DatabaseOptions().ConnectionString;

        services.AddDbContext<LumenMediaDbContext>(opt =>
        {
            opt.UseSqlite(connectionString, o => o.MigrationsAssembly(typeof(LumenMediaDbContext).Assembly.GetName().Name));
            opt.AddInterceptors(new SqlitePragmaInterceptor());
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<INameParser, RegexNameParser>();
        services.AddSingleton<FfprobeClient>();
        services.AddSingleton<IArtworkStore, LocalArtworkStore>();
        services.AddSingleton<IPlaybackSessionStore, InMemoryPlaybackSessionStore>();
        services.AddSingleton<ITranscoder, FfmpegTranscoder>();
        services.AddSingleton<ISubtitleConverter, FfmpegSubtitleConverter>();
        services.AddSingleton<ISettingsStore, InMemorySettingsStore>();
        services.AddSingleton<IMetadataSecretsStore, FileMetadataSecretsStore>();
        services.AddSingleton<IMetadataLanguageSource, SettingsMetadataLanguageSource>();
        services.AddSingleton<IRemoteImageFetcher, HttpRemoteImageFetcher>();
        services.AddSingleton<IMetadataProvider, TmdbMetadataProvider>();
        services.AddSingleton<IMetadataProvider, TvMazeMetadataProvider>();
        services.AddSingleton<IMetadataProvider, TvdbMetadataProvider>();
        services.AddScoped<IMetadataEnricher, MetadataEnricher>();

        services.AddHttpClient("Tmdb", client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenMedia/0.1");
        });
        services.AddHttpClient("TmdbImages", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenMedia/0.1");
        });
        services.AddHttpClient("TvMaze", client =>
        {
            client.BaseAddress = new Uri("https://api.tvmaze.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenMedia/0.1");
        });
        services.AddHttpClient("Tvdb", client =>
        {
            client.BaseAddress = new Uri("https://api4.thetvdb.com/v4/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenMedia/0.1");
        });
        // Base address is per-request (user-supplied Plex URL); keep a generous timeout for large libraries.
        services.AddHttpClient("Plex", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LumenMedia/0.1");
        });
        services.AddSingleton<IPlexHistoryClient, PlexHistoryClient>();

        services.AddScoped<IMediaScanner, FileSystemScanner>();
        services.AddScoped<IFileImporter, HardlinkImporter>();
        services.AddSingleton<IMediaFileDeleter, Storage.MediaFileDeleter>();
        services.AddHostedService<LibraryAutoScanHostedService>();

        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddHostedService<JobWorker>();
        services.AddHostedService<TranscodeSessionJanitor>();

        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);
    }
}
