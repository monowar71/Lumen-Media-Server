using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreePlex.Application.Abstractions;
using FreePlex.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FreePlex.Infrastructure.Metadata;

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
                var resultYear = ParseYear(kind == MediaKind.Movie ? r.ReleaseDate : r.FirstAirDate);
                var score = Score(title, year, resultTitle ?? string.Empty, resultYear, r.Popularity);
                return new MetadataMatch(ProviderName, r.Id.ToString(CultureInfo.InvariantCulture), resultTitle ?? title, resultYear, score);
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
        var query = new Dictionary<string, string?>
        {
            ["language"] = language.Language,
            ["append_to_response"] = "external_ids",
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

        return new MetadataDetails(
            Provider: ProviderName,
            ProviderId: providerId,
            Title: title ?? providerId,
            OriginalTitle: original,
            Year: year,
            Overview: details.Overview,
            CommunityRating: details.VoteAverage is > 0 ? Math.Round(details.VoteAverage.Value, 1) : null,
            OfficialRating: null,
            ImdbId: details.ExternalIds?.ImdbId,
            PosterUrl: ImageUrl(details.PosterPath),
            BackdropUrl: ImageUrl(details.BackdropPath),
            Genres: details.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList() ?? [],
            Tagline: details.Tagline,
            ReleaseDate: release,
            RuntimeMs: runtimeMs);
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
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TMDB {Path} returned {Status}", path, (int)response.StatusCode);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TMDB request failed for {Path}", path);
            return default;
        }
    }

    private static string? ImageUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w780{path}";

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    public static double Score(string queryTitle, int? queryYear, string candidateTitle, int? candidateYear, double? popularity)
    {
        var q = Normalize(queryTitle);
        var c = Normalize(candidateTitle);
        double score;
        if (q.Equals(c, StringComparison.Ordinal))
            score = 1.0;
        else if (c.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(c, StringComparison.Ordinal))
            score = 0.85;
        else if (c.Contains(q, StringComparison.Ordinal) || q.Contains(c, StringComparison.Ordinal))
            score = 0.65;
        else
            score = 0.2;

        if (queryYear is not null && candidateYear is not null)
        {
            var delta = Math.Abs(queryYear.Value - candidateYear.Value);
            score += delta == 0 ? 0.15 : delta == 1 ? 0.05 : -0.1;
        }

        if (popularity is > 0)
            score += Math.Min(0.05, popularity.Value / 2000.0);

        return Math.Clamp(score, 0, 1.15);
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
        int? Runtime,
        [property: JsonPropertyName("episode_run_time")] List<int>? EpisodeRunTime,
        string? Tagline,
        List<TmdbGenre>? Genres,
        [property: JsonPropertyName("external_ids")] TmdbExternalIds? ExternalIds);

    private sealed record TmdbGenre(string? Name);
    private sealed record TmdbExternalIds([property: JsonPropertyName("imdb_id")] string? ImdbId);
}
