namespace FreePlex.Domain.Media;

public class Season
{
    private readonly List<Episode> _episodes = [];

    private Season() { }

    public Season(Guid seriesId, int seasonNumber, string? name = null, string? overview = null)
    {
        if (seasonNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(seasonNumber), "Season number must be >= 0");

        Id = Guid.CreateVersion7();
        SeriesId = seriesId;
        SeasonNumber = seasonNumber;
        Name = name ?? $"Season {seasonNumber}";
        Overview = overview;
    }

    public Guid Id { get; private set; }
    public Guid SeriesId { get; private set; }
    public int SeasonNumber { get; private set; }
    public string? Name { get; private set; }
    public string? Overview { get; private set; }

    public IReadOnlyList<Episode> Episodes => _episodes;

    public Episode AddEpisode(Episode episode)
    {
        _episodes.Add(episode);
        return episode;
    }
}
