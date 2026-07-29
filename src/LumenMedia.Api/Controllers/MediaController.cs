using LumenMedia.Api.Auth;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Application.Libraries;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace LumenMedia.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class MediaController(
    MediaQueryService media,
    MediaFileService files,
    IUnitOfWork uow,
    IArtworkStore artwork,
    IThemeSongStore themes) : ControllerBase
{
    [HttpGet("items/{id:guid}")]
    [ProducesResponseType(typeof(MovieDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SeriesDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> Item(Guid id, CancellationToken ct) =>
        Ok(await media.GetItemDetailAsync(id, User.ToCaller(), ct));

    [HttpGet("series/{id:guid}/seasons")]
    public async Task<ActionResult<PagedResult<SeasonDto>>> Seasons(Guid id, CancellationToken ct) =>
        Ok(await media.GetSeasonsAsync(id, User.ToCaller(), ct));

    [HttpGet("seasons/{id:guid}/episodes")]
    public async Task<ActionResult<PagedResult<EpisodeSummary>>> Episodes(Guid id, CancellationToken ct) =>
        Ok(await media.GetEpisodesAsync(id, User.ToCaller(), ct));

    [HttpGet("episodes/{id:guid}")]
    public async Task<ActionResult<EpisodeDetail>> Episode(Guid id, CancellationToken ct) =>
        Ok(await media.GetEpisodeAsync(id, User.ToCaller(), ct));

    [HttpGet("search")]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 20,
        CancellationToken ct = default) =>
        Ok(await media.SearchAsync(q, User.ToCaller(), limit, ct));

    [HttpGet("items/{id:guid}/artwork/{kind}")]
    public async Task<IActionResult> Artwork(
        Guid id,
        ArtworkKind kind,
        [FromQuery] int? w = null,
        [FromQuery] int? h = null,
        [FromQuery] int? quality = null,
        CancellationToken ct = default)
    {
        var caller = User.ToCaller();
        var localPath = await ResolveArtworkPathAsync(id, kind, caller, ct);
        if (localPath is null)
            return NotFound();

        var result = await artwork.GetAsync(localPath, w, h, quality, ct);
        if (result is null)
            return NotFound();

        var requestETag = Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == result.ETag)
        {
            await result.Content.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = result.ETag;
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=604800";
        return File(result.Content, result.ContentType);
    }

    /// <summary>Cached ambient theme song (MP3) from ThemerrDB, when metadata enrich found one.</summary>
    [HttpGet("items/{id:guid}/theme")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Theme(Guid id, CancellationToken ct)
    {
        var caller = User.ToCaller();
        var item = await uow.Media.GetByIdAsync(id, ct);
        if (item is null || !caller.CanAccess(item.LibraryId))
            return NotFound();

        var result = await themes.OpenAsync(id, ct);
        if (result is null)
            return NotFound();

        var requestETag = Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(requestETag) && requestETag == result.ETag)
        {
            await result.Content.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = result.ETag;
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=604800";
        return File(result.Content, result.ContentType);
    }

    /// <summary>Deletes the on-disk video file(s) for a movie or episode (admin).</summary>
    [HttpDelete("items/{id:guid}/file")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(typeof(DeleteMediaFileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteMediaFileResponse>> DeleteFile(Guid id, CancellationToken ct) =>
        Ok(await files.DeleteFilesAsync(User.ToCaller(), id, ct));

    private async Task<string?> ResolveArtworkPathAsync(Guid id, ArtworkKind kind, Caller caller, CancellationToken ct)
    {
        var item = await uow.Media.GetDetailAsync(id, ct);
        if (item is not null)
        {
            if (!caller.CanAccess(item.LibraryId))
                return null;
            return item.Artworks.FirstOrDefault(a => a.Kind == kind)?.LocalPath
                ?? PreferSeriesStill(item.Artworks, kind);
        }

        var episode = await uow.Media.GetEpisodeAsync(id, ct);
        if (episode is null)
            return null;

        // Episode-specific thumbs are not enriched yet; fall back to the parent series poster
        // so <img> requests from the episode list do not 404.
        var series = await uow.Media.GetDetailAsync(episode.SeriesId, ct);
        if (series is null || !caller.CanAccess(series.LibraryId))
            return null;

        return series.Artworks.FirstOrDefault(a => a.Kind == kind)?.LocalPath
            ?? PreferSeriesStill(series.Artworks, kind);
    }

    /// <summary>
    /// When the requested kind (usually Thumb) is missing, prefer Poster then Backdrop.
    /// </summary>
    private static string? PreferSeriesStill(IReadOnlyList<Artwork> artworks, ArtworkKind requested)
    {
        if (requested is ArtworkKind.Thumb or ArtworkKind.Poster or ArtworkKind.Backdrop)
        {
            return artworks.FirstOrDefault(a => a.Kind == ArtworkKind.Poster)?.LocalPath
                ?? artworks.FirstOrDefault(a => a.Kind == ArtworkKind.Backdrop)?.LocalPath;
        }

        return null;
    }
}
