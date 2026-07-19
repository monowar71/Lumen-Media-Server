using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

/// <summary>
/// Import queue/history. The FolderWatcher + import pipeline is a later phase (P3);
/// these endpoints return correct shapes but no import records exist yet.
/// </summary>
[ApiController]
[Route("api/v1/imports")]
[Authorize(Policy = "Admin")]
public sealed class ImportsController : ControllerBase
{
    public sealed record ResolveRequest(string Provider, string ProviderId, Guid TargetLibraryId);

    [HttpGet]
    public ActionResult<PagedResult<ImportJobDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(new PagedResult<ImportJobDto>([], Math.Max(1, page), Math.Clamp(pageSize, 1, 200), 0));

    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Resolve(Guid id, [FromBody] ResolveRequest request) =>
        throw new NotFoundException($"Import '{id}' not found.");
}
