using LumenMedia.Api.Auth;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1/playback")]
public sealed class PlaybackController(PlaybackService playback) : ControllerBase
{
    [HttpPost("decision")]
    [ProducesResponseType<PlaybackDecisionResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PlaybackDecisionResponse>> Decision([FromBody] PlaybackDecisionRequest request, CancellationToken ct)
    {
        var response = await playback.CreateDecisionAsync(User.ToCaller(), request, ct);
        if (!string.IsNullOrEmpty(response.Reason))
            Response.Headers["X-Playback-Reason"] = response.Reason;
        return Created(response.StreamUrl, response);
    }

    [HttpPost("{sessionId}/set-quality")]
    [ProducesResponseType<PlaybackDecisionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaybackDecisionResponse>> SetQuality(string sessionId, [FromBody] SetQualityRequest request, CancellationToken ct)
    {
        var response = await playback.SetQualityAsync(User.ToCaller(), sessionId, request, ct);
        if (!string.IsNullOrEmpty(response.Reason))
            Response.Headers["X-Playback-Reason"] = response.Reason;
        return Ok(response);
    }

    [HttpPost("{sessionId}/seek")]
    [ProducesResponseType<PlaybackDecisionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PlaybackDecisionResponse>> Seek(string sessionId, [FromBody] SeekRequest request, CancellationToken ct)
    {
        var response = await playback.SeekAsync(User.ToCaller(), sessionId, request, ct);
        if (!string.IsNullOrEmpty(response.Reason))
            Response.Headers["X-Playback-Reason"] = response.Reason;
        return Ok(response);
    }

    [HttpPost("{sessionId}/ping")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Ping(string sessionId, CancellationToken ct)
    {
        await playback.PingAsync(User.ToCaller(), sessionId, ct);
        return NoContent();
    }

    [HttpPost("{sessionId}/stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Stop(string sessionId, CancellationToken ct)
    {
        await playback.StopAsync(User.ToCaller(), sessionId, ct);
        return NoContent();
    }
}
