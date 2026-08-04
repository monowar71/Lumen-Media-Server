using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Metadata;

/// <summary>TMDB v3 metadata provider (search + details + image URLs).</summary>
public sealed class TmdbMetadataProvider(
    IHttpClientFactory httpClientFactory,
    IMetadataSecretsStore secrets,
    ILogger<TmdbMetadataProvider> logger) : IMetadataProvider
{
    public const string ProviderName = "Tmdb";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string Name => ProviderName;

    public bool IsConfigured => secrets.TmdbConfigured;

    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(
        string title,
        int? year,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return [];

        var client = httpClientFactory.CreateClient("Tmdb");
        var path = kind == MediaKind.Movie ? "search/movie" : "search/tv";
        var query = new Dictionary<string, string?>
        {
            ["query"] = title,
            ["include_adult"] = "false",
            ["language"] = language.Language,
        };
        if (year is not null)
        {
            if (kind == MediaKind.Movie)
                query["year"] = year.Value.ToString(CultureInfo.InvariantCulture);
            else
                query["first_air_date_year"] = year.Value.ToString(CultureInfo.InvariantCulture);
        }

        var payload = await GetAsync<TmdbSearchResponse>(client, path, query, ct);
        if (payload?.Results is null || payload.Results.Count == 0)
            return [];

        return payload.Results
            .Select(r =>
            {
                var resultTitle = kind == MediaKind.Movie ? r.Title : r.Name;
                var originalTitle = kind == MediaKind.Movie ? r.OriginalTitle : r.OriginalName;
                var resultYear = ParseYear(kind == MediaKind.Movie ? r.ReleaseDate : r.FirstAirDate);
                var score = Score(
                    title,
                    year,
                    resultTitle ?? string.Empty,
                    resultYear,
                    r.Popularity,
                    originalTitle);
                return new MetadataMatch(
                    ProviderName,
                    r.Id.ToString(CultureInfo.InvariantCulture),
                    resultTitle ?? title,
                    resultYear,
                    score);
            })
            .OrderByDescending(m => m.Score)
            .Take(10)
            .ToList();
    }

    public async Task<MetadataDetails?> GetDetailsAsync(
        string providerId,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(providerId))
            return null;

        var client = httpClientFactory.CreateClient("Tmdb");
        var path = kind == MediaKind.Movie ? $"movie/{providerId}" : $"tv/{providerId}";
        var append = kind == MediaKind.Movie
            ? "external_ids,credits,videos,release_dates"
            : "external_ids,credits,videos,content_ratings";
        var query = new Dictionary<string, string?>
        {
            ["language"] = language.Language,
            // credits/videos/ratings ride along on the details call — no extra requests per item.
            ["append_to_response"] = append,
            // Videos are language-tagged; without this a ru-RU request hides EN-only trailers.
            ["include_video_language"] = $"{ShortLang(language.Language)},{ShortLang(language.FallbackLanguage)},null",
        };

        var details = await GetAsync<TmdbDetailsResponse>(client, path, query, ct);
        if (details is null)
            return null;

        // Fallback overview when the preferred language is empty.
        if (string.IsNullOrWhiteSpace(details.Overview)
            && !string.Equals(language.Language, language.FallbackLanguage, StringComparison.OrdinalIgnoreCase))
        {
            query["language"] = language.FallbackLanguage;
            var fallback = await GetAsync<TmdbDetailsResponse>(client, path, query, ct);
            if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.Overview))
                details = details with { Overview = fallback.Overview };
        }

        var title = kind == MediaKind.Movie ? details.Title : details.Name;
        var original = kind == MediaKind.Movie ? details.OriginalTitle : details.OriginalName;
        var year = ParseYear(kind == MediaKind.Movie ? details.ReleaseDate : details.FirstAirDate);
        long? runtimeMs = null;
        if (details.Runtime is > 0)
            runtimeMs = details.Runtime.Value * 60_000L;
        else if (details.EpisodeRunTime is { Count: > 0 } times && times[0] > 0)
            runtimeMs = times[0] * 60_000L;

        DateOnly? release = null;
        if (DateOnly.TryParse(details.ReleaseDate ?? details.FirstAirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            release = d;

        SeriesStatus? status = null;
        int? endYear = null;
        IReadOnlyList<string>? studios;
        if (kind == MediaKind.Movie)
        {
            studios = MapNamedList(details.ProductionCompanies, max: 5);
        }
        else
        {
            studios = MapNamedList(details.Networks, max: 5);
            status = MapSeriesStatus(details.Status);
            endYear = ParseYear(details.LastAirDate);
            // Ongoing shows should not advertise an end year from the latest air date.
            if (status == SeriesStatus.Continuing)
                endYear = null;
        }

        var official = kind == MediaKind.Movie
            ? PickMovieCertification(details.ReleaseDates?.Results, language)
            : PickTvContentRating(details.ContentRatings?.Results, language);

        return new MetadataDetails(
            Provider: ProviderName,
            ProviderId: providerId,
            Title: title ?? providerId,
            OriginalTitle: original,
            Year: year,
            Overview: details.Overview,
            CommunityRating: details.VoteAverage is > 0 ? Math.Round(details.VoteAverage.Value, 1) : null,
            OfficialRating: official,
            ImdbId: details.ExternalIds?.ImdbId,
            PosterUrl: ImageUrl(details.PosterPath),
            BackdropUrl: ImageUrl(details.BackdropPath),
            Genres: details.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList() ?? [],
            Tagline: details.Tagline,
            ReleaseDate: release,
            RuntimeMs: runtimeMs,
            People: MapCredits(details.Credits),
            TrailerUrl: PickTrailerUrl(details.Videos?.Results),
            Studios: studios,
            Status: status,
            EndYear: endYear);
    }

    public async Task<IReadOnlyList<EpisodeMetadata>> GetSeasonEpisodesAsync(
        string providerId,
        int seasonNumber,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(providerId))
            return [];

        var client = httpClientFactory.CreateClient("Tmdb");
        var path = $"tv/{providerId}/season/{seasonNumber}";
        var query = new Dictionary<string, string?> { ["language"] = language.Language };

        var season = await GetAsync<TmdbSeasonResponse>(client, path, query, ct);
        if (season?.Episodes is null || season.Episodes.Count == 0)
            return [];

        // One fallback fetch fills gaps when the preferred language has no episode texts yet.
        Dictionary<int, TmdbEpisode>? fallbackByNumber = null;
        if (!string.Equals(language.Language, language.FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            && season.Episodes.Any(e => string.IsNullOrWhiteSpace(e.Overview) || string.IsNullOrWhiteSpace(e.Name)))
        {
            query["language"] = language.FallbackLanguage;
            var fallback = await GetAsync<TmdbSeasonResponse>(client, path, query, ct);
            fallbackByNumber = fallback?.Episodes?
                .Where(e => e.EpisodeNumber is not null)
                .ToDictionary(e => e.EpisodeNumber!.Value);
        }

        var result = new List<EpisodeMetadata>(season.Episodes.Count);
        foreach (var ep in season.Episodes)
        {
            if (ep.EpisodeNumber is null)
                continue;

            var fb = fallbackByNumber?.GetValueOrDefault(ep.EpisodeNumber.Value);
            // TMDB pads missing localized titles with "Episode N" — prefer the fallback text then.
            var epName = ep.Name;
            if (string.IsNullOrWhiteSpace(epName) || IsPlaceholderEpisodeTitle(epName, ep.EpisodeNumber.Value))
                epName = fb?.Name ?? epName;
            var overview = string.IsNullOrWhiteSpace(ep.Overview) ? fb?.Overview : ep.Overview;

            DateOnly? air = null;
            if (DateOnly.TryParse(ep.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var airDate))
                air = airDate;

            result.Add(new EpisodeMetadata(
                SeasonNumber: season.SeasonNumber ?? seasonNumber,
                EpisodeNumber: ep.EpisodeNumber.Value,
                Title: string.IsNullOrWhiteSpace(epName) ? null : epName.Trim(),
                Overview: string.IsNullOrWhiteSpace(overview) ? null : overview.Trim(),
                AirDate: air,
                RuntimeMs: ep.Runtime is > 0 ? ep.Runtime.Value * 60_000L : null));
        }

        return result;
    }

    public async Task<IReadOnlyList<ArtworkImageCandidate>> ListArtworkAsync(
        string providerId,
        MediaKind mediaKind,
        ArtworkKind artworkKind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(providerId))
            return [];
        if (artworkKind is not (ArtworkKind.Poster or ArtworkKind.Backdrop))
            return [];

        var client = httpClientFactory.CreateClient("Tmdb");
        var path = mediaKind == MediaKind.Movie
            ? $"movie/{providerId}/images"
            : $"tv/{providerId}/images";
        // Empty language includes all localizations + textless images.
        var payload = await GetAsync<TmdbImagesResponse>(
            client,
            path,
            new Dictionary<string, string?> { ["include_image_language"] = "en,null,ru,uk,de,fr,es,it,ja,ko,zh" },
            ct);
        if (payload is null)
            return [];

        var entries = artworkKind == ArtworkKind.Poster ? payload.Posters : payload.Backdrops;
        if (entries is null || entries.Count == 0)
            return [];

        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.FilePath))
            .Select(e => new ArtworkImageCandidate(
                ProviderName,
                artworkKind,
                ImageUrl(e.FilePath, fullSize: true)!,
                ImageUrl(e.FilePath, fullSize: false)!,
                string.IsNullOrWhiteSpace(e.Iso6391) ? null : e.Iso6391,
                e.Width,
                e.Height,
                e.VoteAverage))
            .ToList();
    }

    /// <summary>"Episode 5" / "Эпизод 5" auto-generated stubs are not real localized titles.</summary>
    private static bool IsPlaceholderEpisodeTitle(string title, int episodeNumber) =>
        title.Trim().Equals($"Episode {episodeNumber}", StringComparison.OrdinalIgnoreCase)
        || title.Trim().Equals($"Эпизод {episodeNumber}", StringComparison.OrdinalIgnoreCase);

    private static List<PersonCredit> MapCredits(TmdbCredits? credits)
    {
        if (credits is null)
            return [];

        var people = new List<PersonCredit>();
        foreach (var c in (credits.Cast ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)).Take(MaxCastMembers))
        {
            people.Add(new PersonCredit(
                c.Name!.Trim(),
                PersonType.Actor,
                string.IsNullOrWhiteSpace(c.Character) ? null : c.Character.Trim(),
                c.Order ?? people.Count,
                ProfileUrl(c.ProfilePath),
                c.Id?.ToString(CultureInfo.InvariantCulture)));
        }

        var order = 100; // crew is sorted after the cast block
        foreach (var c in (credits.Crew ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            PersonType? type = c.Job switch
            {
                "Director" => PersonType.Director,
                "Writer" or "Screenplay" or "Novel" => PersonType.Writer,
                _ => null,
            };
            if (type is null)
                continue;

            people.Add(new PersonCredit(
                c.Name!.Trim(),
                type.Value,
                c.Job,
                order++,
                ProfileUrl(c.ProfilePath),
                c.Id?.ToString(CultureInfo.InvariantCulture)));
        }

        return people;
    }

    private static List<string> MapNamedList(IReadOnlyList<TmdbNamed>? items, int max)
    {
        if (items is null || items.Count == 0)
            return [];
        var result = new List<string>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;
            var name = item.Name.Trim();
            if (result.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(name);
            if (result.Count >= max)
                break;
        }
        return result;
    }

    /// <summary>TMDB TV status → domain Continuing/Ended.</summary>
    public static SeriesStatus? MapSeriesStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;
        return status.Trim() switch
        {
            "Ended" or "Canceled" or "Cancelled" => SeriesStatus.Ended,
            "Returning Series" or "In Production" or "Planned" or "Pilot" => SeriesStatus.Continuing,
            _ => null,
        };
    }

    /// <summary>
    /// Prefer certification for the UI language country, then US, then first non-empty.
    /// </summary>
    public static string? PickMovieCertification(
        IReadOnlyList<TmdbReleaseDateCountry>? countries,
        MetadataLanguage language)
    {
        if (countries is null || countries.Count == 0)
            return null;

        var preferred = CountryFromLanguage(language.Language);
        var fallback = CountryFromLanguage(language.FallbackLanguage);
        foreach (var iso in new[] { preferred, fallback, "US" })
        {
            var hit = FirstCertification(countries, iso);
            if (hit is not null)
                return hit;
        }

        return countries
            .SelectMany(c => c.ReleaseDates ?? [])
            .Select(r => r.Certification)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
            ?.Trim();
    }

    public static string? PickTvContentRating(
        IReadOnlyList<TmdbContentRating>? ratings,
        MetadataLanguage language)
    {
        if (ratings is null || ratings.Count == 0)
            return null;

        var preferred = CountryFromLanguage(language.Language);
        var fallback = CountryFromLanguage(language.FallbackLanguage);
        foreach (var iso in new[] { preferred, fallback, "US" })
        {
            var hit = ratings.FirstOrDefault(r =>
                string.Equals(r.Iso31661, iso, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.Rating));
            if (hit?.Rating is { } rating)
                return rating.Trim();
        }

        return ratings
            .Select(r => r.Rating)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            ?.Trim();
    }

    private static string? FirstCertification(IReadOnlyList<TmdbReleaseDateCountry> countries, string iso)
    {
        var country = countries.FirstOrDefault(c =>
            string.Equals(c.Iso31661, iso, StringComparison.OrdinalIgnoreCase));
        return country?.ReleaseDates?
            .Select(r => r.Certification)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
            ?.Trim();
    }

    /// <summary>ru-RU → RU; en → US when only language code is given.</summary>
    public static string CountryFromLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "US";
        var parts = language.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return parts[^1].ToUpperInvariant();
        return parts[0].ToUpperInvariant() switch
        {
            "EN" => "US",
            "JA" => "JP",
            "KO" => "KR",
            "ZH" => "CN",
            var code => code,
        };
    }

    /// <summary>
    /// Best trailer: official YouTube "Trailer" first, then any YouTube trailer, then a teaser.
    /// </summary>
    public static string? PickTrailerUrl(IReadOnlyList<TmdbVideo>? videos)
    {
        if (videos is null || videos.Count == 0)
            return null;

        var candidates = videos
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(v.Key))
            .ToList();
        var best = candidates
            .Where(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Official == true)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault(v => string.Equals(v.Type, "Teaser", StringComparison.OrdinalIgnoreCase));

        return best is null ? null : $"https://www.youtube.com/watch?v={best.Key}";
    }

    private const int MaxCastMembers = 20;

    /// <summary>"ru-RU" → "ru" (include_video_language expects ISO 639-1 codes).</summary>
    private static string ShortLang(string language)
    {
        var dash = language.IndexOf('-');
        return dash > 0 ? language[..dash] : language;
    }

    private async Task<T?> GetAsync<T>(HttpClient client, string path, Dictionary<string, string?> query, CancellationToken ct)
    {
        var apiKey = secrets.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return default;
        query["api_key"] = apiKey;

        var qs = string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var url = $"{path}?{qs}";

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var response = await client.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    // Bulk refresh (episodes fetch per season) can trip TMDB's rate limit;
                    // honour Retry-After with a sane cap instead of dropping the item.
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    if (delay > MaxRetryAfter)
                        delay = MaxRetryAfter;
                    logger.LogInformation("TMDB rate limited on {Path}; retrying in {Delay}s", path, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("TMDB {Path} returned {Status}", path, (int)response.StatusCode);
                    return default;
                }

                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            }
        }
        // HttpClient timeouts throw TaskCanceledException without ct being cancelled;
        // treat them as provider failures, propagate only real cancellation.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TMDB request failed for {Path}", path);
            return default;
        }
    }

    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    private static string? ImageUrl(string? path, bool fullSize = true) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : $"https://image.tmdb.org/t/p/{(fullSize ? "w780" : "w185")}{path}";

    private static string? ProfileUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w185{path}";

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    /// <summary>
    /// Scores a TMDB candidate against the library title. Uses the better of localized
    /// vs original title so EN filenames still match when the UI language is ru-RU.
    /// Popularity breaks ties so obscure same-name entries lose to the known title.
    /// </summary>
    public static double Score(
        string queryTitle,
        int? queryYear,
        string candidateTitle,
        int? candidateYear,
        double? popularity,
        string? originalTitle = null)
    {
        var titleScore = TitleSimilarity(queryTitle, candidateTitle);
        if (!string.IsNullOrWhiteSpace(originalTitle))
            titleScore = Math.Max(titleScore, TitleSimilarity(queryTitle, originalTitle));

        var score = titleScore;

        if (queryYear is not null && candidateYear is not null)
        {
            var delta = Math.Abs(queryYear.Value - candidateYear.Value);
            score += delta == 0 ? 0.15 : delta == 1 ? 0.05 : -0.15;
        }

        // log-ish boost: popularity 5 ≈ +0.04, 50 ≈ +0.13, 500 ≈ +0.22 (cap 0.25)
        if (popularity is > 0)
            score += Math.Min(0.25, Math.Log10(popularity.Value + 1.0) * 0.1);

        return Math.Clamp(score, 0, 1.4);
    }

    private static double TitleSimilarity(string queryTitle, string candidateTitle)
    {
        var q = Normalize(queryTitle);
        var c = Normalize(candidateTitle);
        if (q.Length == 0 || c.Length == 0)
            return 0;

        if (q.Equals(c, StringComparison.Ordinal))
            return 1.0;

        // Prefix with extra words (fan edits / "Title (Something)") — weaker than exact.
        if (c.StartsWith(q + " ", StringComparison.Ordinal) || q.StartsWith(c + " ", StringComparison.Ordinal))
            return 0.72;

        if (c.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(c, StringComparison.Ordinal))
            return 0.8;

        if (c.Contains(q, StringComparison.Ordinal) || q.Contains(c, StringComparison.Ordinal))
            return 0.5;

        return 0.1;
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant()
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace(':', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record TmdbSearchResponse(List<TmdbSearchResult>? Results);

    private sealed record TmdbSearchResult(
        int Id,
        string? Title,
        string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        double? Popularity);

    private sealed record TmdbDetailsResponse(
        string? Title,
        string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        string? Overview,
        [property: JsonPropertyName("vote_average")] double? VoteAverage,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("last_air_date")] string? LastAirDate,
        string? Status,
        int? Runtime,
        [property: JsonPropertyName("episode_run_time")] List<int>? EpisodeRunTime,
        string? Tagline,
        List<TmdbGenre>? Genres,
        [property: JsonPropertyName("production_companies")] List<TmdbNamed>? ProductionCompanies,
        List<TmdbNamed>? Networks,
        [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds,
        TmdbCredits? Credits,
        TmdbVideos? Videos,
        [property: JsonPropertyName("release_dates")] TmdbReleaseDates? ReleaseDates,
        [property: JsonPropertyName("content_ratings")] TmdbContentRatings? ContentRatings);

    private sealed record TmdbGenre(string? Name);
    private sealed record TmdbNamed(string? Name);
    private sealed record TmdbExternalIds([property: JsonPropertyName("imdb_id")] string? ImdbId);

    private sealed record TmdbReleaseDates(List<TmdbReleaseDateCountry>? Results);
    public sealed record TmdbReleaseDateCountry(
        [property: JsonPropertyName("iso_3166_1")] string? Iso31661,
        [property: JsonPropertyName("release_dates")] List<TmdbReleaseDateEntry>? ReleaseDates);
    public sealed record TmdbReleaseDateEntry(string? Certification);

    private sealed record TmdbContentRatings(List<TmdbContentRating>? Results);
    public sealed record TmdbContentRating(
        [property: JsonPropertyName("iso_3166_1")] string? Iso31661,
        string? Rating);

    private sealed record TmdbCredits(List<TmdbCastMember>? Cast, List<TmdbCrewMember>? Crew);

    private sealed record TmdbCastMember(
        int? Id,
        string? Name,
        string? Character,
        int? Order,
        [property: JsonPropertyName("profile_path")] string? ProfilePath);

    private sealed record TmdbCrewMember(
        int? Id,
        string? Name,
        string? Job,
        [property: JsonPropertyName("profile_path")] string? ProfilePath);

    private sealed record TmdbVideos(List<TmdbVideo>? Results);

    public sealed record TmdbVideo(string? Site, string? Type, string? Key, bool? Official);

    private sealed record TmdbSeasonResponse(
        [property: JsonPropertyName("season_number")] int? SeasonNumber,
        List<TmdbEpisode>? Episodes);

    private sealed record TmdbEpisode(
        [property: JsonPropertyName("episode_number")] int? EpisodeNumber,
        string? Name,
        string? Overview,
        [property: JsonPropertyName("air_date")] string? AirDate,
        int? Runtime);

    private sealed record TmdbImagesResponse(
        List<TmdbImageEntry>? Posters,
        List<TmdbImageEntry>? Backdrops);

    private sealed record TmdbImageEntry(
        [property: JsonPropertyName("file_path")] string? FilePath,
        [property: JsonPropertyName("iso_639_1")] string? Iso6391,
        int? Width,
        int? Height,
        [property: JsonPropertyName("vote_average")] double? VoteAverage);
}
