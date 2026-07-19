using FreePlex.Application.Common;
using FreePlex.Application.Contracts;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Jobs;
using FreePlex.Domain.Libraries;
using FreePlex.Domain.Media;
using FreePlex.Domain.Playback;
using FreePlex.Domain.Users;

namespace FreePlex.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    void Remove(User user);

    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(Guid userId, CancellationToken ct);
}

public interface ILibraryRepository
{
    Task<Library?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Library>> ListAsync(CancellationToken ct);
    Task<int> CountItemsAsync(Guid libraryId, CancellationToken ct);
    Task AddAsync(Library library, CancellationToken ct);
    void Remove(Library library);
}

public interface IMediaRepository
{
    Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<MediaItem?> GetDetailAsync(Guid id, CancellationToken ct);
    Task<PagedResult<MediaItemSummary>> ListAsync(LibraryItemsQuery query, CancellationToken ct);
    Task<IReadOnlyList<MediaItemSummary>> SearchAsync(string term, IReadOnlyCollection<Guid> allowedLibraryIds, int limit, CancellationToken ct);
    Task<IReadOnlyList<MediaItemSummary>> GetSummariesByIdsAsync(IReadOnlyCollection<Guid> ids, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<MediaItemSummary>> GetRecentlyAddedAsync(IReadOnlyCollection<Guid> allowedLibraryIds, int limit, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<Season>> GetSeasonsAsync(Guid seriesId, CancellationToken ct);
    Task<IReadOnlyList<Episode>> GetEpisodesAsync(Guid seasonId, CancellationToken ct);
    Task<Episode?> GetEpisodeAsync(Guid episodeId, CancellationToken ct);

    /// <summary>Returns a TRACKED series (with its seasons and episodes) for scan-time reuse, or null.</summary>
    Task<Series?> FindSeriesForScanAsync(Guid libraryId, string title, CancellationToken ct);

    /// <summary>Returns a TRACKED episode by series/season/number for scan-time reuse, or null.</summary>
    Task<Episode?> FindEpisodeForScanAsync(Guid seriesId, int seasonNumber, int episodeNumber, CancellationToken ct);

    Task<MediaSource?> FindSourceByPathAsync(string path, CancellationToken ct);
    Task<MediaSource?> GetSourceByIdAsync(Guid id, CancellationToken ct);
    Task<MediaSource?> GetPrimarySourceForMediaAsync(Guid mediaId, CancellationToken ct);

    Task AddAsync(MediaItem item, CancellationToken ct);
    Task AddSeasonAsync(Season season, CancellationToken ct);
    Task AddEpisodeAsync(Episode episode, CancellationToken ct);
    Task AddMediaSourceAsync(MediaSource source, CancellationToken ct);

    /// <summary>Tracked item with genres + artworks for metadata writes.</summary>
    Task<MediaItem?> GetTrackedForMetadataAsync(Guid id, CancellationToken ct);

    /// <summary>Item ids in a library that still need metadata (no overview / no TMDB id).</summary>
    Task<IReadOnlyList<Guid>> ListIdsMissingMetadataAsync(Guid libraryId, CancellationToken ct);

    /// <summary>All movie/series item ids in a library (for full metadata refresh).</summary>
    Task<IReadOnlyList<Guid>> ListIdsForLibraryAsync(Guid libraryId, CancellationToken ct);

    /// <summary>Item ids that already have an external provider id (eligible for language re-enrich).</summary>
    Task<IReadOnlyList<Guid>> ListIdsWithExternalIdsAsync(CancellationToken ct);

    /// <summary>Item ids in a library that already have an external provider id.</summary>
    Task<IReadOnlyList<Guid>> ListIdsWithExternalIdsForLibraryAsync(Guid libraryId, CancellationToken ct);

    Task<Genre> GetOrCreateGenreAsync(string name, CancellationToken ct);
    void RemoveArtwork(Artwork artwork);
    /// <summary>
    /// Explicit INSERT — client-generated artwork keys are otherwise misclassified as UPDATE
    /// when reached only via a tracked MediaItem collection (same pattern as seasons/episodes).
    /// </summary>
    Task AddArtworkAsync(Artwork artwork, CancellationToken ct);

    void Remove(MediaItem item);
}

public interface IProgressRepository
{
    Task<PlaybackProgress?> GetAsync(Guid userId, Guid mediaId, CancellationToken ct);
    Task AddAsync(PlaybackProgress progress, CancellationToken ct);
    Task<IReadOnlyList<PlaybackProgress>> GetContinueWatchingAsync(Guid userId, int limit, CancellationToken ct);
}

public interface IJobRepository
{
    Task AddAsync(BackgroundJob job, CancellationToken ct);
    Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<PagedResult<BackgroundJob>> ListAsync(int page, int pageSize, CancellationToken ct);
    /// <summary>Current persisted state, bypassing the local change tracker.</summary>
    Task<JobState?> GetStateAsync(Guid id, CancellationToken ct);
    /// <summary>Latest Queued/Running job of the given type for a library, if any.</summary>
    Task<BackgroundJob?> FindActiveAsync(JobType type, Guid libraryId, CancellationToken ct);
    /// <summary>Marks all Queued/Running jobs as Failed (startup recovery after a restart).</summary>
    Task<int> FailUnfinishedAsync(string error, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Aggregate of repositories sharing one unit-of-work (DbContext) transaction.</summary>
public interface IUnitOfWork
{
    IUserRepository Users { get; }
    ILibraryRepository Libraries { get; }
    IMediaRepository Media { get; }
    IProgressRepository Progress { get; }
    IJobRepository Jobs { get; }
    Task<int> SaveChangesAsync(CancellationToken ct);
    /// <summary>Drops tracked entities so a failed insert cannot poison later saves in the same scope.</summary>
    void DiscardChanges();
    Task<IAppTransaction> BeginTransactionAsync(CancellationToken ct);
}

/// <summary>
/// Explicit-commit transaction: disposing without <see cref="CommitAsync"/> rolls back,
/// so an exception inside the scope never commits partial writes.
/// </summary>
public interface IAppTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
