using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController(JobService jobs) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<JobDto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        return Ok(await jobs.ListAsync(page, pageSize, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JobDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await jobs.GetAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted(await jobs.CancelAsync(id, ct));
}
