using FreePlex.Domain.Enums;

namespace FreePlex.Domain.Media;

public class Series : MediaItem
{
    private readonly List<Season> _seasons = [];

    private Series() { }

    public Series(Guid libraryId, string title, DateTimeOffset now)
        : base(libraryId, title, now)
    {
    }

    public override MediaKind Kind => MediaKind.Series;

    public int? EndYear { get; private set; }
    public SeriesStatus? Status { get; private set; }

    public IReadOnlyList<Season> Seasons => _seasons;

    public void SetSeriesDetails(int? endYear, SeriesStatus? status)
    {
        EndYear = endYear;
        Status = status;
    }

    public Season AddSeason(Season season)
    {
        _seasons.Add(season);
        return season;
    }
}
