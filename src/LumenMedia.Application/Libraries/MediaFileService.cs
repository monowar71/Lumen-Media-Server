using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Media;

namespace LumenMedia.Application.Libraries;

public sealed class MediaFileService(
    IUnitOfWork uow,
    IMediaFileDeleter fileDeleter,
    IArtworkStore artworkStore)
{
    /// <summary>
    /// Deletes on-disk media file(s) for a movie, episode, season, or series and removes DB rows.
    /// When no sources remain, removes the movie/episode; seasons and series are always removed.
    /// Admin only.
    /// </summary>
    public async Task<DeleteMediaFileResponse> DeleteFilesAsync(
        Caller caller,
        Guid mediaId,
        CancellationToken ct)
    {
        if (!caller.IsAdmin)
            throw new ForbiddenException("Only administrators can delete media files.");

        var sources = await uow.Media.GetTrackedSourcesForMediaAsync(mediaId, ct);
        if (sources.Count > 0)
            return await DeleteOwnedSourcesAsync(caller, mediaId, sources, ct);

        var seriesGraph = await uow.Media.GetTrackedSeriesGraphAsync(mediaId, ct);
        if (seriesGraph is not null)
            return await DeleteSeriesAsync(caller, seriesGraph, ct);

        var seasonMeta = await uow.Media.GetSeasonAsync(mediaId, ct);
        if (seasonMeta is not null)
            return await DeleteSeasonAsync(caller, seasonMeta, ct);

        throw new NotFoundException("No media file found for this item.");
    }

    private async Task<DeleteMediaFileResponse> DeleteOwnedSourcesAsync(
        Caller caller,
        Guid mediaId,
        IReadOnlyList<MediaSource> sources,
        CancellationToken ct)
    {
        var libraryId = await ResolveLibraryIdAsync(sources[0], ct)
                        ?? throw new NotFoundException("Media not found.");
        var roots = await RequireLibraryRootsAsync(caller, libraryId, ct);

        var ownedByMovie = sources.Any(s => s.MediaItemId == mediaId);
        var ownedByEpisode = sources.Any(s => s.EpisodeId == mediaId);
        var deletedFiles = DeleteSources(sources, roots);

        var mediaRemoved = false;
        if (ownedByMovie)
        {
            var tracked = await uow.Media.GetTrackedForMetadataAsync(mediaId, ct);
            if (tracked is not null)
            {
                await uow.Progress.DeleteForMediaIdsAsync([mediaId], ct);
                uow.Media.Remove(tracked);
                artworkStore.DeleteOwner(mediaId);
                mediaRemoved = true;
            }
        }
        else if (ownedByEpisode)
        {
            var episodeMeta = await uow.Media.GetEpisodeAsync(mediaId, ct);
            if (episodeMeta is not null)
            {
                var trackedList = await uow.Media.GetTrackedEpisodesForSeriesAsync(episodeMeta.SeriesId, ct);
                var tracked = trackedList.FirstOrDefault(e => e.Id == mediaId);
                if (tracked is not null)
                {
                    await uow.Progress.DeleteForMediaIdsAsync([mediaId], ct);
                    uow.Media.RemoveEpisode(tracked);
                    mediaRemoved = true;
                }
            }
        }

        await uow.SaveChangesAsync(ct);

        return new DeleteMediaFileResponse
        {
            DeletedFiles = deletedFiles,
            SourcesRemoved = sources.Count,
            MediaRemoved = mediaRemoved,
        };
    }

    private async Task<DeleteMediaFileResponse> DeleteSeriesAsync(
        Caller caller,
        Series series,
        CancellationToken ct)
    {
        var roots = await RequireLibraryRootsAsync(caller, series.LibraryId, ct);
        var episodes = series.Seasons.SelectMany(s => s.Episodes).ToList();
        var sources = episodes.SelectMany(e => e.Sources).ToList();
        var deletedFiles = DeleteSources(sources, roots);

        var progressIds = episodes.Select(e => e.Id).Append(series.Id).ToList();
        if (progressIds.Count > 0)
            await uow.Progress.DeleteForMediaIdsAsync(progressIds, ct);

        uow.Media.Remove(series);
        artworkStore.DeleteOwner(series.Id);
        await uow.SaveChangesAsync(ct);

        return new DeleteMediaFileResponse
        {
            DeletedFiles = deletedFiles,
            SourcesRemoved = sources.Count,
            MediaRemoved = true,
        };
    }

    private async Task<DeleteMediaFileResponse> DeleteSeasonAsync(
        Caller caller,
        Season seasonMeta,
        CancellationToken ct)
    {
        var graph = await uow.Media.GetTrackedSeriesGraphAsync(seasonMeta.SeriesId, ct)
                    ?? throw new NotFoundException("Media not found.");
        var tracked = graph.Seasons.FirstOrDefault(s => s.Id == seasonMeta.Id)
                      ?? throw new NotFoundException("Media not found.");

        var roots = await RequireLibraryRootsAsync(caller, graph.LibraryId, ct);
        var sources = tracked.Episodes.SelectMany(e => e.Sources).ToList();
        var deletedFiles = DeleteSources(sources, roots);

        var episodeIds = tracked.Episodes.Select(e => e.Id).ToList();
        if (episodeIds.Count > 0)
            await uow.Progress.DeleteForMediaIdsAsync(episodeIds, ct);

        uow.Media.RemoveSeason(tracked);
        await uow.SaveChangesAsync(ct);

        return new DeleteMediaFileResponse
        {
            DeletedFiles = deletedFiles,
            SourcesRemoved = sources.Count,
            MediaRemoved = true,
        };
    }

    private int DeleteSources(IReadOnlyList<MediaSource> sources, IReadOnlyList<string> roots)
    {
        var deletedFiles = 0;
        foreach (var source in sources)
        {
            if (fileDeleter.TryDelete(source.Path, roots))
                deletedFiles++;
            uow.Media.RemoveSource(source);
        }

        return deletedFiles;
    }

    private async Task<IReadOnlyList<string>> RequireLibraryRootsAsync(
        Caller caller,
        Guid libraryId,
        CancellationToken ct)
    {
        if (!caller.CanAccess(libraryId))
            throw new NotFoundException("Media not found.");

        var library = await uow.Libraries.GetByIdAsync(libraryId, ct)
                      ?? throw new NotFoundException("Library not found.");
        return library.Paths.Select(p => p.Path).ToList();
    }

    private async Task<Guid?> ResolveLibraryIdAsync(MediaSource source, CancellationToken ct)
    {
        if (source.MediaItemId is not null)
            return (await uow.Media.GetByIdAsync(source.MediaItemId.Value, ct))?.LibraryId;

        if (source.EpisodeId is not null)
        {
            var episode = await uow.Media.GetEpisodeAsync(source.EpisodeId.Value, ct);
            if (episode is null)
                return null;
            return (await uow.Media.GetByIdAsync(episode.SeriesId, ct))?.LibraryId;
        }

        return null;
    }
}
