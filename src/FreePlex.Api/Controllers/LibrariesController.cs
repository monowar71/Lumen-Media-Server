using FreePlex.Api.Auth;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Application.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FreePlex.Api.Controllers;

[ApiController]
[Route("api/v1/libraries")]
public sealed class LibrariesController(LibraryService libraries, MediaQueryService media) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LibraryDto>>> List(CancellationToken ct) =>
        Ok(await libraries.ListAsync(User.ToCaller(), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LibraryDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await libraries.GetAsync(id, User.ToCaller(), ct));

    [HttpPost]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<LibraryDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<LibraryDto>> Create([FromBody] CreateLibraryRequest request, CancellationToken ct)
    {
        var lib = await libraries.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = lib.Id }, lib);
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<LibraryDto>> Update(Guid id, [FromBody] UpdateLibraryRequest request, CancellationToken ct) =>
        Ok(await libraries.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await libraries.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/scan")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<JobDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<JobDto>> Scan(Guid id, CancellationToken ct)
    {
        var job = await libraries.ScanAsync(id, ct);
        return Accepted(job);
    }

    /// <summary>
    /// Enqueue metadata enrichment for items in the library.
    /// Optional body: mode (Missing|Matched|All) and preferredLanguage.
    /// </summary>
    [HttpPost("{id:guid}/refresh-metadata")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<LibraryMetadataRefreshAccepted>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<LibraryMetadataRefreshAccepted>> RefreshMetadata(
        Guid id,
        [FromBody] RefreshLibraryMetadataRequest? request,
        CancellationToken ct)
    {
        var result = await libraries.RefreshMetadataAsync(id, request ?? new RefreshLibraryMetadataRequest(), ct);
        return Accepted(result);
    }

    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<PagedResult<MediaItemSummary>>> Items(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string sort = "title",
        [FromQuery] string order = "asc",
        [FromQuery] string? genre = null,
        [FromQuery] int? year = null,
        [FromQuery] bool? watched = null,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        var caller = User.ToCaller();

        if (pageSize is < 1 or > MediaQueryService.MaxPageSize)
            throw new ValidationException("pageSize", $"Must be between 1 and {MediaQueryService.MaxPageSize}.");
        if (page < 1)
            throw new ValidationException("page", "Must be >= 1.");

        var query = new LibraryItemsQuery
        {
            LibraryId = id,
            UserId = caller.UserId,
            Page = page,
            PageSize = pageSize,
            Sort = ParseSort(sort),
            Desc = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase),
            Genre = genre,
            Year = year,
            Watched = watched,
            Query = q,
        };

        return Ok(await media.ListItemsAsync(id, caller, query, ct));
    }

    private static MediaSortField ParseSort(string sort) => sort.ToLowerInvariant() switch
    {
        "title" => MediaSortField.Title,
        "year" => MediaSortField.Year,
        "added" => MediaSortField.Added,
        "rating" => MediaSortField.Rating,
        "runtime" => MediaSortField.Runtime,
        _ => throw new ValidationException("sort", "Must be one of: title, year, added, rating, runtime."),
    };
}
