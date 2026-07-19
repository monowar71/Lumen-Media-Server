using FreePlex.Application.Jobs;
using FreePlex.Application.Libraries;
using FreePlex.Application.Metadata;
using FreePlex.Application.Playback;
using FreePlex.Application.Settings;
using FreePlex.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace FreePlex.Application;

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
        services.AddScoped<PlaybackService>();
        services.AddScoped<ProgressService>();
        services.AddScoped<HomeService>();
        services.AddScoped<JobService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<MetadataJobService>();
        services.AddScoped<ItemMetadataService>();

        return services;
    }
}
