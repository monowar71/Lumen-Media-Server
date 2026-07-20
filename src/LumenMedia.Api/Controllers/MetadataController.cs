using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Metadata;
using LumenMedia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LumenMedia.Api.Controllers;

/// <summary>Manual metadata matching, editing, artwork, and refresh (admin).</summary>
[ApiController]
[Route("api/v1/items")]
[Authorize(Policy = "Admin")]
public sealed class MetadataController(
    IUnitOfWork uow,
    MetadataJobService metadataJobs,
    ItemMetadataService itemMetadata,
    ItemArtworkService itemArtwork) : ControllerBase
{
    public sealed record MatchRequest(string Provider, string ProviderId);

    [HttpGet("{id:guid}/match-candidates")]
    [ProducesResponseType<IReadOnlyList<MetadataMatchCandidateDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MetadataMatchCandidateDto>>> Candidates(
        Guid id,
        [FromQuery] string? q,
        [FromQuery] int? year,
        CancellationToken ct)
    {
        var candidates = await itemMetadata.SearchCandidatesAsync(id, q, year, ct);
        return Ok(candidates);
    }

    [HttpGet("{id:guid}/artwork-candidates")]
    [ProducesResponseType<IReadOnlyList<ArtworkCandidateDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ArtworkCandidateDto>>> ArtworkCandidates(
        Guid id,
        [FromQuery] ArtworkKind kind = ArtworkKind.Poster,
        CancellationToken ct = default)
    {
        var candidates = await itemArtwork.ListCandidatesAsync(id, kind, ct);
        return Ok(candidates);
    }

    [HttpPut("{id:guid}/artwork/{kind}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetArtwork(
        Guid id,
        ArtworkKind kind,
        [FromBody] SetItemArtworkRequest request,
        CancellationToken ct)
    {
        await itemArtwork.SetAsync(id, kind, request.Url, ct);
        return NoContent();
    }

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

    [HttpPatch("{id:guid}/metadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateMetadata(
        Guid id,
        [FromBody] UpdateItemMetadataRequest request,
        CancellationToken ct)
    {
        await itemMetadata.UpdateAsync(id, request, ct);
        return NoContent();
    }
}
