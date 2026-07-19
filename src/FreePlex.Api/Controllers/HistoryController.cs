using FreePlex.Api.Auth;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Playback;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1/history")]
public sealed class HistoryController(HistoryService history) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<HistoryEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<HistoryEntryDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        Ok(await history.ListAsync(User.GetUserId(), page, pageSize, ct));

    [HttpDelete]
    [ProducesResponseType<ClearHistoryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ClearHistoryResponse>> Clear(CancellationToken ct) =>
        Ok(await history.ClearAsync(User.GetUserId(), ct));

    [HttpPost("import/plex")]
    [ProducesResponseType<ImportPlexHistoryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImportPlexHistoryResponse>> ImportPlex(
        [FromBody] ImportPlexHistoryRequest request,
        CancellationToken ct) =>
        Ok(await history.ImportFromPlexAsync(User.GetUserId(), request, ct));
}
