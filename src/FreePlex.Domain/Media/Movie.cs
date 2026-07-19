using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Media;

public class Movie : MediaItem
{
    private readonly List<MediaSource> _sources = [];

    private Movie() { }

    public Movie(Guid libraryId, string title, DateTimeOffset now)
        : base(libraryId, title, now)
    {
    }

    public override MediaKind Kind => MediaKind.Movie;

    public string? Tagline { get; private set; }
    public DateOnly? ReleaseDate { get; private set; }
    public long? RuntimeMs { get; private set; }

    public IReadOnlyList<MediaSource> Sources => _sources;

    public void SetMovieDetails(string? tagline, DateOnly? releaseDate, long? runtimeMs)
    {
        Tagline = tagline;
        ReleaseDate = releaseDate;
        RuntimeMs = runtimeMs;
    }

    public MediaSource AddSource(MediaSource source)
    {
        _sources.Add(source);
        return source;
    }
}
