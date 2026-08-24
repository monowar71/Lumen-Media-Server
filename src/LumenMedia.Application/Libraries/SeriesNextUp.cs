using LumenMedia.Domain.Media;
using LumenMedia.Domain.Playback;

namespace LumenMedia.Application.Libraries;

/// <summary>
/// Picks the episode a "Play" button on a series detail page should open.
/// Prefers an in-progress episode; otherwise the first unwatched regular episode.
/// </summary>
public static class SeriesNextUp
{
    public sealed record Candidate(Episode Episode, PlaybackProgress? Progress);

    /// <param name="episodes">Season/episode ordered list (specials SeasonNumber 0 included last preference).</param>
    public static Candidate? Select(IReadOnlyList<Candidate> episodes)
    {
        if (episodes.Count == 0)
            return null;

        // Prefer regular seasons (S1+) over specials (S0) for the primary play action.
        var regular = episodes.Where(c => c.Episode.SeasonNumber > 0).ToList();
        var pool = regular.Count > 0 ? regular : episodes;

        Candidate? inProgress = null;
        foreach (var c in pool)
        {
            var p = c.Progress;
            if (p is null || p.Watched)
                continue;
            if (p.PositionMs > 0)
            {
                inProgress = c;
                break;
            }
        }

        if (inProgress is not null)
            return inProgress;

        foreach (var c in pool)
        {
            if (c.Progress is null || !c.Progress.Watched)
                return c;
        }

        return null;
    }

    /// <summary>
    /// Chronological next episode after <paramref name="current"/> (S1+ first, specials last).
    /// </summary>
    public static Episode? NextAfter(Episode current, IReadOnlyList<Episode> all)
    {
        if (all.Count == 0)
            return null;

        var ordered = all
            .OrderBy(e => e.SeasonNumber == 0 ? int.MaxValue : e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .ToList();

        var idx = ordered.FindIndex(e => e.Id == current.Id);
        if (idx < 0 || idx + 1 >= ordered.Count)
            return null;

        return ordered[idx + 1];
    }
}
