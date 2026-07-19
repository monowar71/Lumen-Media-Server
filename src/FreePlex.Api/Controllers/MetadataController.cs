using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

/// <summary>Manual metadata matching / refresh. Enqueues a FetchMetadata job.</summary>
[ApiController]
[Route("api/v1/items")]
[Authorize(Policy = "Admin")]
public sealed class MetadataController(IUnitOfWork uow, MetadataJobService metadataJobs) : ControllerBase
{
    public sealed record MatchRequest(string Provider, string ProviderId);

    [HttpPost("{id:guid}/match")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDto>> Match(Guid id, [FromBody] MatchRequest request, CancellationToken ct)
    {
        _ = await uow.Media.GetByIdAsync(id, ct)
            ?? throw new NotFoundException("Item not found.");
        var job = await metadataJobs.EnqueueItemAsync(id, request.Provider, request.ProviderId, ct);
        return Accepted(job);
    }

    [HttpPost("{id:guid}/refresh-metadata")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDto>> Refresh(Guid id, CancellationToken ct)
    {
        _ = await uow.Media.GetByIdAsync(id, ct)
            ?? throw new NotFoundException("Item not found.");
        var job = await metadataJobs.EnqueueItemAsync(id, provider: null, providerId: null, ct);
        return Accepted(job);
    }
}
