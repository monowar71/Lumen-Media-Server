namespace FreePlex.Domain.Media;

public class Episode
{
    private readonly List<MediaSource> _sources = [];

    private Episode() { }

    public Episode(Guid seriesId, Guid seasonId, int seasonNumber, int episodeNumber, DateTimeOffset now)
    {
        if (seasonNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(seasonNumber), "Season number must be >= 0");
        if (episodeNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(episodeNumber), "Episode number must be >= 0");

        Id = Guid.CreateVersion7();
        SeriesId = seriesId;
        SeasonId = seasonId;
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        AddedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid SeriesId { get; private set; }
    public Guid SeasonId { get; private set; }
    public int SeasonNumber { get; private set; }
    public int EpisodeNumber { get; private set; }
    public string? Title { get; private set; }
    public string? Overview { get; private set; }
    public DateOnly? AirDate { get; private set; }
    public long? RuntimeMs { get; private set; }
    public string? TvdbId { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    public IReadOnlyList<MediaSource> Sources => _sources;

    public void SetDetails(string? title, string? overview, DateOnly? airDate, long? runtimeMs)
    {
        Title = title;
        Overview = overview;
        AirDate = airDate;
        RuntimeMs = runtimeMs;
    }

    public MediaSource AddSource(MediaSource source)
    {
        _sources.Add(source);
        return source;
    }
}
