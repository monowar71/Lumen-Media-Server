using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Media;

/// <summary>
/// Base type for library items (TPH root, discriminator = <see cref="Kind"/>).
/// Concrete kinds: <see cref="Movie"/> and <see cref="Series"/>.
/// </summary>
public abstract class MediaItem
{
    private readonly List<Genre> _genres = [];
    private readonly List<MediaPerson> _people = [];
    private readonly List<Artwork> _artworks = [];

    protected MediaItem() { }

    protected MediaItem(Guid libraryId, string title, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required", nameof(title));

        Id = Guid.CreateVersion7();
        LibraryId = libraryId;
        Title = title;
        SortTitle = ComputeSortTitle(title);
        AddedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; protected set; }
    public abstract MediaKind Kind { get; }
    public Guid LibraryId { get; protected set; }
    public string Title { get; protected set; } = null!;
    public string? OriginalTitle { get; protected set; }
    public string SortTitle { get; protected set; } = null!;
    public int? Year { get; protected set; }
    public string? Overview { get; protected set; }
    public double? CommunityRating { get; protected set; }
    public string? OfficialRating { get; protected set; }
    public string? TmdbId { get; protected set; }
    public string? TvdbId { get; protected set; }
    public string? ImdbId { get; protected set; }
    /// <summary>Remote trailer URL (usually YouTube) from the metadata provider.</summary>
    public string? TrailerUrl { get; protected set; }
    /// <summary>
    /// YouTube theme URL from ThemerrDB that was successfully cached under
    /// <c>/config/metadata/{id}/theme.mp3</c>. Null when no theme is cached.
    /// </summary>
    public string? ThemeYoutubeUrl { get; protected set; }
    public bool MetadataLocked { get; protected set; }
    public DateTimeOffset AddedAt { get; protected set; }
    public DateTimeOffset UpdatedAt { get; protected set; }

    public IReadOnlyList<Genre> Genres => _genres;
    public IReadOnlyList<MediaPerson> People => _people;
    public IReadOnlyList<Artwork> Artworks => _artworks;

    public void SetYear(int? year) => Year = year;
    public void SetOverview(string? overview) => Overview = overview;
    public void SetSortTitle(string sortTitle) => SortTitle = sortTitle;
    public void SetOriginalTitle(string? originalTitle) => OriginalTitle = originalTitle;

    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required", nameof(title));
        Title = title.Trim();
        SortTitle = ComputeSortTitle(Title);
    }

    public void SetRatings(double? communityRating, string? officialRating)
    {
        CommunityRating = communityRating;
        OfficialRating = officialRating;
    }

    public void SetExternalIds(string? tmdb, string? tvdb, string? imdb)
    {
        TmdbId = tmdb;
        TvdbId = tvdb;
        ImdbId = imdb;
    }

    public void SetTrailerUrl(string? url) => TrailerUrl = url;

    public void SetThemeYoutubeUrl(string? url) => ThemeYoutubeUrl = url;

    /// <summary>
    /// When locked, automatic metadata refresh skips this item; explicit match still applies.
    /// </summary>
    public void SetMetadataLocked(bool locked) => MetadataLocked = locked;

    public void AddGenre(Genre genre)
    {
        if (_genres.All(g => g.Id != genre.Id && !g.Name.Equals(genre.Name, StringComparison.OrdinalIgnoreCase)))
            _genres.Add(genre);
    }

    public void AddArtwork(Artwork artwork) => _artworks.Add(artwork);

    public void AddPerson(MediaPerson credit)
    {
        if (_people.All(p => p.PersonId != credit.PersonId || p.Type != credit.Type))
            _people.Add(credit);
    }

    public void ClearPeople() => _people.Clear();

    public void RemoveArtworksOfKind(ArtworkKind kind)
    {
        for (var i = _artworks.Count - 1; i >= 0; i--)
        {
            if (_artworks[i].Kind == kind)
                _artworks.RemoveAt(i);
        }
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    /// <summary>"The Matrix" → "Matrix, The" for natural alphabetical ordering.</summary>
    public static string ComputeSortTitle(string title)
    {
        var trimmed = title.Trim();
        string[] articles = ["The ", "A ", "An "];
        foreach (var article in articles)
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return $"{trimmed[article.Length..]}, {trimmed[..(article.Length - 1)]}";
        }
        return trimmed;
    }
}
