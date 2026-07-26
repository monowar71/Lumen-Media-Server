using LumenMedia.Api.OpenApi;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LumenMedia.Api.Errors;
using LumenMedia.Api.Realtime;
using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace LumenMedia.Api;

public static class DependencyInjection
{
    /// <summary>Applied to credential endpoints (login/refresh/setup) to slow brute force.</summary>
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
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
        // Cors:AllowedOrigins (array) restricts browsers to known origins; when it is not
        // configured we keep the permissive reflect-any-origin behavior for LAN setups.
        var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                if (allowedOrigins.Length > 0)
                    policy.WithOrigins(allowedOrigins).SetIsOriginAllowedToAllowWildcardSubdomains();
                else
                    policy.SetIsOriginAllowed(_ => true);
            });
        });

        services.AddExceptionHandler<AppExceptionHandler>();
        services.AddProblemDetails();

        // Per-IP limiter on credential endpoints: PBKDF2 alone does not stop online brute force.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(AuthRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        var jwt = new JwtOptions();
        config.GetSection(JwtOptions.SectionName).Bind(jwt);
        const int minSecretBytes = 32; // HS256 key must be at least 256 bits.
        if (string.IsNullOrWhiteSpace(jwt.Secret))
        {
            if (env.IsProduction())
            {
                throw new InvalidOperationException(
                    "Jwt:Secret is required in Production (set the JWT__SECRET environment variable, "
                    + $"at least {minSecretBytes} bytes). Refusing to generate an ephemeral key: "
                    + "all sessions would be silently invalidated on every restart.");
            }

            // Dev/test convenience: generate an ephemeral key so the app can boot.
            jwt.Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            // Persist it back so the Infrastructure token service (IOptions<JwtOptions>) SIGNS with the
            // same key we VALIDATE with here — otherwise issuing crashes / tokens don't validate.
            config[$"{JwtOptions.SectionName}:Secret"] = jwt.Secret;
        }
        else if (Encoding.UTF8.GetByteCount(jwt.Secret) < minSecretBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:Secret is too short: HS256 requires at least {minSecretBytes} bytes.");
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
                //
                // HLS / DirectPlay under /api/v1/stream/{sessionId}/… also accept the
                // unguessable session id as a capability URL (see StreamController) so native
                // players are not cut off when the 15‑minute access JWT expires mid‑playback.
                // If a client still appends an expired access_token, do not fail the request —
                // session capability auth handles those routes.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var accessToken = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && AllowsQueryToken(ctx.HttpContext.Request.Path))
                            ctx.Token = accessToken;
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/v1/stream"))
                            ctx.NoResult();
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
