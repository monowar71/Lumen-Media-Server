using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using LumenMedia.Application.Contracts;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using Microsoft.EntityFrameworkCore;

namespace LumenMedia.Infrastructure.Persistence.Repositories;

public sealed class MediaRepository(LumenMediaDbContext db) : IMediaRepository
{
    public Task<MediaItem?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<MediaItem?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var movie = await db.Movies
            .AsNoTracking()
            .AsSplitQuery()
            .Include(m => m.Genres)
            .Include(m => m.Artworks)
            .Include(m => m.People).ThenInclude(p => p.Person)
            .Include(m => m.Sources).ThenInclude(s => s.Streams)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (movie is not null)
            return movie;

        return await db.Series
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Genres)
            .Include(s => s.Artworks)
            .Include(s => s.People).ThenInclude(p => p.Person)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<PagedResult<MediaItemSummary>> ListAsync(LibraryItemsQuery q, CancellationToken ct)
    {
        var query = db.MediaItems.AsNoTracking().Where(m => m.LibraryId == q.LibraryId);

        if (!string.IsNullOrWhiteSpace(q.Genre))
            query = query.Where(m => m.Genres.Any(g => g.Name == q.Genre));
        if (q.Year is not null)
            query = query.Where(m => m.Year == q.Year);
        if (!string.IsNullOrWhiteSpace(q.Query))
            query = query.Where(m => EF.Functions.Like(m.Title, $"%{q.Query}%"));
        if (q.Watched is not null)
        {
            var watched = q.Watched.Value;
            query = query.Where(m => db.Progress.Any(p =>
                p.UserId == q.UserId && p.MediaId == m.Id && p.Watched == watched));
        }

        query = ApplySort(query, q.Sort, q.Desc);

        var total = await query.CountAsync(ct);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var page = Math.Max(1, q.Page);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(Row.Projection(db, q.UserId))
            .ToListAsync(ct);

        return new PagedResult<MediaItemSummary>(rows.Select(r => r.Map()).ToList(), page, pageSize, total);
    }

    public async Task<IReadOnlyList<MediaItemSummary>> SearchAsync(string term, IReadOnlyCollection<Guid> allowedLibraryIds, int limit, CancellationToken ct)
    {
        var rows = await db.MediaItems.AsNoTracking()
            .Where(m => allowedLibraryIds.Contains(m.LibraryId) && EF.Functions.Like(m.Title, $"%{term}%"))
            .OrderBy(m => m.SortTitle)
            .Take(limit)
            .Select(Row.Projection(db, Guid.Empty))
            .ToListAsync(ct);
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<IReadOnlyList<MediaItemSummary>> GetSummariesByIdsAsync(IReadOnlyCollection<Guid> ids, Guid userId, CancellationToken ct)
    {
        var rows = await db.MediaItems.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(Row.Projection(db, userId))
            .ToListAsync(ct);
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<IReadOnlyList<MediaItemSummary>> GetRecentlyAddedAsync(IReadOnlyCollection<Guid> allowedLibraryIds, int limit, Guid userId, CancellationToken ct)
    {
        var rows = await db.MediaItems.AsNoTracking()
            .Where(m => allowedLibraryIds.Contains(m.LibraryId))
            .OrderByDescending(m => m.AddedAt)
            .Take(limit)
            .Select(Row.Projection(db, userId))
            .ToListAsync(ct);
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<IReadOnlyList<Season>> GetSeasonsAsync(Guid seriesId, CancellationToken ct) =>
        await db.Seasons.AsNoTracking()
            .Where(s => s.SeriesId == seriesId)
            .OrderBy(s => s.SeasonNumber)
            .ToListAsync(ct);

    public Task<Season?> GetSeasonAsync(Guid seasonId, CancellationToken ct) =>
        db.Seasons.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seasonId, ct);

    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(Guid seasonId, CancellationToken ct) =>
        await db.Episodes.AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .OrderBy(e => e.EpisodeNumber)
            .ToListAsync(ct);

    public Task<Episode?> GetEpisodeAsync(Guid episodeId, CancellationToken ct) =>
        db.Episodes.AsNoTracking()
            .AsSplitQuery()
            .Include(e => e.Sources).ThenInclude(s => s.Streams)
            .FirstOrDefaultAsync(e => e.Id == episodeId, ct);

    public async Task<IReadOnlyList<Episode>> GetEpisodesByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        return await db.Episodes.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);
    }

    public async Task<Movie?> FindMovieByExternalIdsAsync(
        string? tmdbId, string? tvdbId, string? imdbId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            var byTmdb = await db.Movies.AsNoTracking()
                .Where(m => m.TmdbId == tmdbId)
                .OrderBy(m => m.AddedAt)
                .FirstOrDefaultAsync(ct);
            if (byTmdb is not null)
                return byTmdb;
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var byImdb = await db.Movies.AsNoTracking()
                .Where(m => m.ImdbId == imdbId)
                .OrderBy(m => m.AddedAt)
                .FirstOrDefaultAsync(ct);
            if (byImdb is not null)
                return byImdb;
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            return await db.Movies.AsNoTracking()
                .Where(m => m.TvdbId == tvdbId)
                .OrderBy(m => m.AddedAt)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    public async Task<Series?> FindSeriesByExternalIdsAsync(
        string? tmdbId, string? tvdbId, string? imdbId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            var byTmdb = await db.Series.AsNoTracking()
                .Where(s => s.TmdbId == tmdbId)
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
            if (byTmdb is not null)
                return byTmdb;
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            var byTvdb = await db.Series.AsNoTracking()
                .Where(s => s.TvdbId == tvdbId)
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
            if (byTvdb is not null)
                return byTvdb;
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            return await db.Series.AsNoTracking()
                .Where(s => s.ImdbId == imdbId)
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    public async Task<Movie?> FindMovieByTitleAsync(string title, CancellationToken ct)
    {
        var needle = title.Trim();
        if (needle.Length == 0)
            return null;

        return await db.Movies.AsNoTracking()
            .Where(m => m.Title == needle || (m.OriginalTitle != null && m.OriginalTitle == needle))
            .OrderBy(m => m.AddedAt)
            .FirstOrDefaultAsync(ct)
            ?? await db.Movies.AsNoTracking()
                .Where(m => m.Title.ToLower() == needle.ToLower()
                    || (m.OriginalTitle != null && m.OriginalTitle.ToLower() == needle.ToLower()))
                .OrderBy(m => m.AddedAt)
                .FirstOrDefaultAsync(ct);
    }

    public async Task<Series?> FindSeriesByTitleAsync(string title, CancellationToken ct)
    {
        var needle = title.Trim();
        if (needle.Length == 0)
            return null;

        return await db.Series.AsNoTracking()
            .Where(s => s.Title == needle || (s.OriginalTitle != null && s.OriginalTitle == needle))
            .OrderBy(s => s.AddedAt)
            .FirstOrDefaultAsync(ct)
            ?? await db.Series.AsNoTracking()
                .Where(s => s.Title.ToLower() == needle.ToLower()
                    || (s.OriginalTitle != null && s.OriginalTitle.ToLower() == needle.ToLower()))
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
    }

    public Task<Series?> FindSeriesForScanAsync(Guid libraryId, string title, CancellationToken ct) =>
        db.Series
            .Include(s => s.Seasons)
                .ThenInclude(s => s.Episodes)
            .FirstOrDefaultAsync(s => s.LibraryId == libraryId && s.Title == title, ct);

    public async Task<Series?> FindOtherSeriesByExternalIdAsync(
        Guid libraryId, Guid excludeId, string? tmdbId, string? tvdbId, CancellationToken ct)
    {
        // Prefer TMDB: it is the authoritative id after enrichment. Fall back to TVDB only
        // when neither side has a TMDB id (avoids false merges across providers).
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            return await db.Series
                .Where(s => s.LibraryId == libraryId && s.Id != excludeId && s.TmdbId == tmdbId)
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            return await db.Series
                .Where(s => s.LibraryId == libraryId
                            && s.Id != excludeId
                            && s.TvdbId == tvdbId
                            && s.TmdbId == null)
                .OrderBy(s => s.AddedAt)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    public Task<Series?> GetTrackedSeriesGraphAsync(Guid id, CancellationToken ct) =>
        db.Series
            .Include(s => s.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(e => e.Sources)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Episode?> FindEpisodeForScanAsync(Guid seriesId, int seasonNumber, int episodeNumber, CancellationToken ct) =>
        db.Episodes.FirstOrDefaultAsync(
            e => e.SeriesId == seriesId && e.SeasonNumber == seasonNumber && e.EpisodeNumber == episodeNumber,
            ct);

    public Task<MediaSource?> FindSourceByPathAsync(string path, CancellationToken ct) =>
        db.MediaSources.AsNoTracking().FirstOrDefaultAsync(s => s.Path == path, ct);

    public Task<MediaSource?> GetTrackedSourceByPathWithStreamsAsync(string path, CancellationToken ct) =>
        db.MediaSources
            .Include(s => s.Streams)
            .FirstOrDefaultAsync(s => s.Path == path, ct);

    public Task<MediaSource?> GetSourceByIdAsync(Guid id, CancellationToken ct) =>
        db.MediaSources.AsNoTracking()
            .Include(s => s.Streams)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<MediaSource?> GetPrimarySourceForMediaAsync(Guid mediaId, CancellationToken ct)
    {
        var sources = await db.MediaSources.AsNoTracking()
            .Include(s => s.Streams)
            .Where(s => s.MediaItemId == mediaId || s.EpisodeId == mediaId)
            .ToListAsync(ct);
        // Prefer a source whose file still exists and has been probed (DurationMs set).
        return sources
                   .Where(s => File.Exists(s.Path))
                   .OrderByDescending(s => s.DurationMs.HasValue)
                   .ThenByDescending(s => s.Streams.Count)
                   .FirstOrDefault()
               ?? sources.FirstOrDefault();
    }

    public async Task AddAsync(MediaItem item, CancellationToken ct) => await db.MediaItems.AddAsync(item, ct);

    public async Task AddSeasonAsync(Season season, CancellationToken ct) => await db.Seasons.AddAsync(season, ct);

    public async Task AddEpisodeAsync(Episode episode, CancellationToken ct) => await db.Episodes.AddAsync(episode, ct);

    public async Task AddMediaSourceAsync(MediaSource source, CancellationToken ct) =>
        await db.MediaSources.AddAsync(source, ct);

    public async Task<IReadOnlyList<MediaSource>> GetTrackedSourcesForMediaAsync(Guid mediaId, CancellationToken ct) =>
        await db.MediaSources
            .Include(s => s.Streams)
            .Where(s => s.MediaItemId == mediaId || s.EpisodeId == mediaId)
            .ToListAsync(ct);

    public void RemoveSource(MediaSource source) => db.MediaSources.Remove(source);

    public Task<MediaItem?> GetTrackedForMetadataAsync(Guid id, CancellationToken ct) =>
        db.MediaItems
            .Include(m => m.Genres)
            .Include(m => m.Artworks)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> ListIdsMissingMetadataAsync(Guid libraryId, CancellationToken ct) =>
        await db.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId && (m.Overview == null || m.TmdbId == null))
            .OrderBy(m => m.SortTitle)
            .Select(m => m.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsForLibraryAsync(Guid libraryId, CancellationToken ct) =>
        await db.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId)
            .OrderBy(m => m.SortTitle)
            .Select(m => m.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsWithExternalIdsAsync(CancellationToken ct) =>
        await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbId != null || m.TvdbId != null)
            .OrderBy(m => m.SortTitle)
            .Select(m => m.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsWithExternalIdsForLibraryAsync(Guid libraryId, CancellationToken ct) =>
        await db.MediaItems.AsNoTracking()
            .Where(m => m.LibraryId == libraryId && (m.TmdbId != null || m.TvdbId != null))
            .OrderBy(m => m.SortTitle)
            .Select(m => m.Id)
            .ToListAsync(ct);

    public async Task<Genre> GetOrCreateGenreAsync(string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var existing = await db.Genres.FirstOrDefaultAsync(g => g.Name == trimmed, ct);
        if (existing is not null)
            return existing;

        var genre = new Genre(trimmed);
        await db.Genres.AddAsync(genre, ct);
        return genre;
    }

    public async Task<Person> GetOrCreatePersonAsync(string name, string? tmdbId, string? thumbUrl, CancellationToken ct)
    {
        var trimmed = name.Trim();

        // Check local (not yet saved) entries first: one enrich pass may credit the same
        // person twice (e.g. actor + director) and a DB-only lookup would double-insert.
        var person = db.People.Local.FirstOrDefault(p =>
                         tmdbId is not null && p.TmdbId == tmdbId)
                     ?? db.People.Local.FirstOrDefault(p =>
                         p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        if (person is null && tmdbId is not null)
            person = await db.People.FirstOrDefaultAsync(p => p.TmdbId == tmdbId, ct);
        person ??= await db.People.FirstOrDefaultAsync(p => p.Name == trimmed, ct);

        if (person is null)
        {
            person = new Person(trimmed, tmdbId);
            await db.People.AddAsync(person, ct);
        }

        if (thumbUrl is not null)
            person.ThumbPath = thumbUrl;
        return person;
    }

    public Task RemovePeopleAsync(Guid mediaItemId, CancellationToken ct) =>
        db.MediaPeople.Where(mp => mp.MediaItemId == mediaItemId).ExecuteDeleteAsync(ct);

    public async Task AddMediaPersonAsync(MediaPerson link, CancellationToken ct) =>
        await db.MediaPeople.AddAsync(link, ct);

    public async Task<IReadOnlyList<Episode>> GetTrackedEpisodesForSeriesAsync(Guid seriesId, CancellationToken ct) =>
        await db.Episodes.Where(e => e.SeriesId == seriesId).ToListAsync(ct);

    public void RemoveArtwork(Artwork artwork) => db.Artworks.Remove(artwork);

    public async Task AddArtworkAsync(Artwork artwork, CancellationToken ct) =>
        await db.Artworks.AddAsync(artwork, ct);

    public void RemoveEpisode(Episode episode) => db.Episodes.Remove(episode);

    public void Remove(MediaItem item) => db.MediaItems.Remove(item);

    private static IQueryable<MediaItem> ApplySort(IQueryable<MediaItem> query, MediaSortField sort, bool desc) => sort switch
    {
        MediaSortField.Year => desc ? query.OrderByDescending(m => m.Year) : query.OrderBy(m => m.Year),
        MediaSortField.Added => desc ? query.OrderByDescending(m => m.AddedAt) : query.OrderBy(m => m.AddedAt),
        MediaSortField.Rating => desc ? query.OrderByDescending(m => m.CommunityRating) : query.OrderBy(m => m.CommunityRating),
        MediaSortField.Runtime => desc
            ? query.OrderByDescending(m => ((Movie)m).RuntimeMs)
            : query.OrderBy(m => ((Movie)m).RuntimeMs),
        _ => desc ? query.OrderByDescending(m => m.SortTitle) : query.OrderBy(m => m.SortTitle),
    };

    /// <summary>Flat, EF-translatable projection row; artwork URLs are built in memory afterwards.</summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public bool IsMovie { get; init; }
        public string Title { get; init; } = null!;
        public string? OriginalTitle { get; init; }
        public int? Year { get; init; }
        public long? RuntimeMs { get; init; }
        public double? CommunityRating { get; init; }
        public string? OfficialRating { get; init; }
        public List<string> Genres { get; init; } = [];
        public bool HasPoster { get; init; }
        public bool HasBackdrop { get; init; }
        public bool HasLogo { get; init; }
        public bool HasBanner { get; init; }
        public DateTimeOffset AddedAt { get; init; }
        public bool? Watched { get; init; }
        public long? Position { get; init; }
        public bool? Favorite { get; init; }

        public static System.Linq.Expressions.Expression<Func<MediaItem, Row>> Projection(LumenMediaDbContext db, Guid userId) =>
            m => new Row
            {
                Id = m.Id,
                IsMovie = m is Movie,
                Title = m.Title,
                OriginalTitle = m.OriginalTitle,
                Year = m.Year,
                RuntimeMs = ((Movie)m).RuntimeMs,
                CommunityRating = m.CommunityRating,
                OfficialRating = m.OfficialRating,
                Genres = m.Genres.Select(g => g.Name).ToList(),
                HasPoster = m.Artworks.Any(a => a.Kind == ArtworkKind.Poster),
                HasBackdrop = m.Artworks.Any(a => a.Kind == ArtworkKind.Backdrop),
                HasLogo = m.Artworks.Any(a => a.Kind == ArtworkKind.Logo),
                HasBanner = m.Artworks.Any(a => a.Kind == ArtworkKind.Banner),
                AddedAt = m.AddedAt,
                Watched = db.Progress.Where(p => p.UserId == userId && p.MediaId == m.Id).Select(p => (bool?)p.Watched).FirstOrDefault(),
                Position = db.Progress.Where(p => p.UserId == userId && p.MediaId == m.Id).Select(p => (long?)p.PositionMs).FirstOrDefault(),
                Favorite = db.Progress.Where(p => p.UserId == userId && p.MediaId == m.Id).Select(p => (bool?)p.IsFavorite).FirstOrDefault(),
            };

        public MediaItemSummary Map() => new()
        {
            Id = Id,
            Kind = IsMovie ? MediaKind.Movie : MediaKind.Series,
            Title = Title,
            OriginalTitle = OriginalTitle,
            Year = Year,
            RuntimeMs = IsMovie ? RuntimeMs : null,
            CommunityRating = CommunityRating,
            OfficialRating = OfficialRating,
            Genres = Genres,
            Artwork = new ArtworkUrls
            {
                Poster = HasPoster ? ArtworkUrlBuilder.ItemArtwork(Id, ArtworkKind.Poster) : null,
                Backdrop = HasBackdrop ? ArtworkUrlBuilder.ItemArtwork(Id, ArtworkKind.Backdrop) : null,
                Logo = HasLogo ? ArtworkUrlBuilder.ItemArtwork(Id, ArtworkKind.Logo) : null,
                Banner = HasBanner ? ArtworkUrlBuilder.ItemArtwork(Id, ArtworkKind.Banner) : null,
            },
            UserData = new UserDataDto
            {
                Watched = Watched ?? false,
                PlaybackPositionMs = Position ?? 0,
                IsFavorite = Favorite ?? false,
            },
            AddedAt = AddedAt,
        };
    }
}
