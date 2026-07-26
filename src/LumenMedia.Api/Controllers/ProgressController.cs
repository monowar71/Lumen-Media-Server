using LumenMedia.Api.Auth;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Playback;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class ProgressController(ProgressService progress) : ControllerBase
{
    [HttpPut("progress/{itemId:guid}")]
    public async Task<ActionResult<ProgressResponse>> Update(Guid itemId, [FromBody] UpdateProgressRequest request, CancellationToken ct) =>
        Ok(await progress.UpdateAsync(User.ToCaller(), itemId, request, ct));

    [HttpGet("progress/{itemId:guid}")]
    public async Task<ActionResult<ProgressResponse>> Get(Guid itemId, CancellationToken ct) =>
        Ok(await progress.GetAsync(User.ToCaller(), itemId, ct));

    [HttpGet("continue-watching")]
    public async Task<ActionResult<PagedResult<MediaItemSummary>>> ContinueWatching([FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await progress.ContinueWatchingAsync(User.ToCaller(), limit, ct));
}
