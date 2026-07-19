using FreePlex.Api.Auth;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Playback;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class ProgressController(ProgressService progress) : ControllerBase
{
    [HttpPut("progress/{itemId:guid}")]
    public async Task<ActionResult<ProgressResponse>> Update(Guid itemId, [FromBody] UpdateProgressRequest request, CancellationToken ct) =>
        Ok(await progress.UpdateAsync(User.GetUserId(), itemId, request, ct));

    [HttpGet("progress/{itemId:guid}")]
    public async Task<ActionResult<ProgressResponse>> Get(Guid itemId, CancellationToken ct) =>
        Ok(await progress.GetAsync(User.GetUserId(), itemId, ct));

    [HttpGet("continue-watching")]
    public async Task<ActionResult<PagedResult<MediaItemSummary>>> ContinueWatching([FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await progress.ContinueWatchingAsync(User.GetUserId(), limit, ct));
}
