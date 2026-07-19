using System.Text.RegularExpressions;

namespace FreePlex.Infrastructure.Plex;

/// <summary>Parses Plex Guid strings into TMDB/TVDB/IMDB identifiers.</summary>
public static partial class PlexGuidParser
{
    public sealed record ParsedIds(
        string? TmdbId,
        string? TvdbId,
        string? ImdbId,
        int? SeasonNumber,
        int? EpisodeNumber);

    public static ParsedIds Parse(IEnumerable<string> guids, string? grandparentGuid = null)
    {
        string? tmdbId = null;
        string? tvdbId = null;
        string? imdbId = null;
        int? season = null;
        int? episode = null;

        foreach (var guid in guids)
        {
            if (string.IsNullOrWhiteSpace(guid))
                continue;

            var episodeMatch = TmdbEpisodeGuidRegex().Match(guid);
            if (episodeMatch.Success)
            {
                tmdbId ??= episodeMatch.Groups[1].Value;
                if (int.TryParse(episodeMatch.Groups[2].Value, out var s))
                    season ??= s;
                if (int.TryParse(episodeMatch.Groups[3].Value, out var e))
                    episode ??= e;
                continue;
            }

            if (TryParseProviderId(guid, "tmdb", out var tmdb) || TryParseProviderId(guid, "themoviedb", out tmdb))
                tmdbId ??= tmdb;
            else if (TryParseProviderId(guid, "tvdb", out var tvdb) || TryParseProviderId(guid, "thetvdb", out tvdb))
                tvdbId ??= tvdb;
            else if (TryParseProviderId(guid, "imdb", out var imdb))
                imdbId ??= imdb;
        }

        if (string.IsNullOrWhiteSpace(tmdbId) && !string.IsNullOrWhiteSpace(grandparentGuid))
            TryParseProviderId(grandparentGuid, "tmdb", out tmdbId);
        if (string.IsNullOrWhiteSpace(tvdbId) && !string.IsNullOrWhiteSpace(grandparentGuid))
            TryParseProviderId(grandparentGuid, "tvdb", out tvdbId);
        if (string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(grandparentGuid))
            TryParseProviderId(grandparentGuid, "imdb", out imdbId);

        return new ParsedIds(tmdbId, tvdbId, imdbId, season, episode);
    }

    private static bool TryParseProviderId(string guid, string provider, out string? id)
    {
        id = null;
        // Accept: tmdb://603, com.plexapp.agents.themoviedb://603?lang=en, agents.imdb://tt0133093
        var pattern = $@"(?:^|[./]){Regex.Escape(provider)}://([^/?#]+)";
        var match = Regex.Match(guid, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var raw = match.Groups[1].Value;
        if (raw.Contains('/'))
            return false;

        id = Uri.UnescapeDataString(raw);
        return !string.IsNullOrWhiteSpace(id);
    }

    [GeneratedRegex(@"tmdb://(\d+)/(\d+)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbEpisodeGuidRegex();
}
