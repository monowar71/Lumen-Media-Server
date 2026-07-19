using FreePlex.Application.Abstractions;
using FreePlex.Application.Contracts;
using FreePlex.Application.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class HealthController(IUnitOfWork uow, SettingsService settings) : ControllerBase
{
    [HttpGet("/health")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken ct)
    {
        var databaseHealthy = await CheckDatabaseAsync(ct);
        var checks = new Dictionary<string, string>
        {
            ["database"] = databaseHealthy ? "Healthy" : "Unhealthy",
            ["ffmpeg"] = ServerInfo.FfmpegAvailable ? "Healthy" : "Unavailable",
            ["storage"] = "Healthy",
        };

        return Ok(new HealthResponse
        {
            Status = databaseHealthy ? "Healthy" : "Degraded",
            Version = ServerInfo.Version,
            UptimeSec = ServerInfo.UptimeSeconds,
            Checks = checks,
        });
    }

    [HttpGet("/api/v1/server/info")]
    [ProducesResponseType<ServerInfoResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ServerInfoResponse>> Info(CancellationToken ct)
    {
        var userCount = await uow.Users.CountAsync(ct);
        var current = settings.Get();

        return Ok(new ServerInfoResponse
        {
            Version = ServerInfo.Version,
            SetupCompleted = userCount > 0,
            Features = new ServerFeatures
            {
                HardwareAccel = current.Transcoding.HardwareAccel,
                Abr = current.Transcoding.AbrEnabled,
            },
        });
    }

    private async Task<bool> CheckDatabaseAsync(CancellationToken ct)
    {
        try
        {
            await uow.Users.CountAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
