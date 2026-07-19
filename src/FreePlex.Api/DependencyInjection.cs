using FreePlex.Api.OpenApi;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreePlex.Api.Errors;
using FreePlex.Api.Realtime;
using FreePlex.Application.Abstractions;
using FreePlex.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FreePlex.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddOpenApi(options => options.AddSchemaTransformer<StringEnumSchemaTransformer>());
        services.AddSignalR().AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

        // Web client (Vite) and other browsers cannot call the API without CORS.
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .SetIsOriginAllowed(_ => true));
        });

        services.AddExceptionHandler<AppExceptionHandler>();
        services.AddProblemDetails();

        var jwt = new JwtOptions();
        config.GetSection(JwtOptions.SectionName).Bind(jwt);
        if (string.IsNullOrWhiteSpace(jwt.Secret))
        {
            // Never ship a default in production; generate an ephemeral dev key so the app can boot.
            jwt.Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            // Persist it back so the Infrastructure token service (IOptions<JwtOptions>) SIGNS with the
            // same key we VALIDATE with here — otherwise issuing crashes / tokens don't validate.
            config[$"{JwtOptions.SectionName}:Secret"] = jwt.Secret;
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = signingKey,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // Browsers cannot set an Authorization header for SignalR (WS) or for media
                // delivered to native elements: <video src> (DirectPlay / native HLS),
                // <track> subtitles and <img> artwork. For those routes only, accept the
                // token via the query string (short-lived access token; same pattern as api.md §8).
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var accessToken = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && AllowsQueryToken(ctx.HttpContext.Request.Path))
                            ctx.Token = accessToken;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
            // Everything requires authentication unless explicitly marked [AllowAnonymous].
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    // Query-string token is accepted ONLY for endpoints consumed by native browser
    // elements that cannot send an Authorization header.
    private static bool AllowsQueryToken(PathString path)
    {
        if (path.StartsWithSegments("/hubs") || path.StartsWithSegments("/api/v1/stream"))
            return true;

        if (!path.StartsWithSegments("/api/v1/items"))
            return false;

        var value = path.Value ?? string.Empty;
        return value.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/subtitles/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/artwork/", StringComparison.OrdinalIgnoreCase);
    }
}
