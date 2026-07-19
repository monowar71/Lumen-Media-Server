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
    /// Deletes on-disk media file(s) for a movie or episode and removes DB source rows.
    /// When no sources remain, removes the movie/episode (and its progress). Admin only.
    /// </summary>
    public async Task<DeleteMediaFileResponse> DeleteFilesAsync(
        Caller caller,
        Guid mediaId,
        CancellationToken ct)
    {
        if (!caller.IsAdmin)
            throw new ForbiddenException("Only administrators can delete media files.");

        var sources = await uow.Media.GetTrackedSourcesForMediaAsync(mediaId, ct);
        if (sources.Count == 0)
            throw new NotFoundException("No media file found for this item.");

        var libraryId = await ResolveLibraryIdAsync(sources[0], ct)
                        ?? throw new NotFoundException("Media not found.");
        if (!caller.CanAccess(libraryId))
            throw new NotFoundException("Media not found.");

        var library = await uow.Libraries.GetByIdAsync(libraryId, ct)
                      ?? throw new NotFoundException("Library not found.");
        var roots = library.Paths.Select(p => p.Path).ToList();

        var deletedFiles = 0;
        var ownedByMovie = sources.Any(s => s.MediaItemId == mediaId);
        var ownedByEpisode = sources.Any(s => s.EpisodeId == mediaId);

        foreach (var source in sources)
        {
            if (fileDeleter.TryDelete(source.Path, roots))
                deletedFiles++;
            uow.Media.RemoveSource(source);
        }

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
