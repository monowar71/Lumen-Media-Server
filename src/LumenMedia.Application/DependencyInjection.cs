using LumenMedia.Application.Jobs;
using LumenMedia.Application.Libraries;
using LumenMedia.Application.Metadata;
using LumenMedia.Application.Playback;
using LumenMedia.Application.Settings;
using LumenMedia.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace LumenMedia.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Pure decision engine — stateless, safe as a singleton.
        services.AddSingleton<PlaybackDecider>();

        // Use-case services (scoped: one unit-of-work per request/job scope).
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<LibraryService>();
        services.AddScoped<MediaQueryService>();
        services.AddScoped<MediaFileService>();
        services.AddScoped<PlaybackService>();
        services.AddScoped<ProgressService>();
        services.AddScoped<HistoryService>();
        services.AddScoped<ExternalHistoryPromoter>();
        services.AddScoped<HomeService>();
        services.AddScoped<JobService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<MetadataJobService>();
        services.AddScoped<ItemMetadataService>();

        return services;
    }
}
