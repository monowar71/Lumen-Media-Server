using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Metadata;

/// <summary>
/// TheTVDB API v4 provider (movies + series). Requires a free project API key
/// (and often a subscriber PIN) from https://thetvdb.com/api-information.
/// </summary>
public sealed class TvdbMetadataProvider(
    IHttpClientFactory httpClientFactory,
    IMetadataSecretsStore secrets,
    ILogger<TvdbMetadataProvider> logger) : IMetadataProvider
{
    public const string ProviderName = "Tvdb";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly Lock _tokenGate = new();
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public string Name => ProviderName;
    public bool IsConfigured => secrets.TvdbConfigured;

    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(
        string title,
        int? year,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return [];

        var type = kind == MediaKind.Movie ? "movie" : "series";
        var client = httpClientFactory.CreateClient("Tvdb");
        if (!await EnsureTokenAsync(client, ct))
            return [];

        try
        {
            var url =
                $"search?query={Uri.EscapeDataString(title)}&type={type}&language={Uri.EscapeDataString(ToTvdbLang(language.Language))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                InvalidateToken();
                if (!await EnsureTokenAsync(client, ct))
                    return [];
                using var retry = new HttpRequestMessage(HttpMethod.Get, url);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var retryResponse = await client.SendAsync(retry, ct);
                if (!retryResponse.IsSuccessStatusCode)
                    return [];
                return await ParseSearchAsync(retryResponse, title, year, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TVDB search returned {Status}", (int)response.StatusCode);
                return [];
            }

            return await ParseSearchAsync(response, title, year, ct);
        }
        // Timeout-safe: HttpClient timeout is an OCE without ct cancellation — swallow as failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TVDB search failed for {Title}", title);
            return [];
        }
    }

    public async Task<MetadataDetails?> GetDetailsAsync(
        string providerId,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(providerId))
            return null;

        var client = httpClientFactory.CreateClient("Tvdb");
        if (!await EnsureTokenAsync(client, ct))
            return null;

        var path = kind == MediaKind.Movie
            ? $"movies/{providerId}/extended"
            : $"series/{providerId}/extended";

        try
        {
            var payload = await GetAuthorizedAsync<TvdbEnvelope<TvdbExtended>>(client, path, language, ct);
            var data = payload?.Data;
            if (data is null)
                return null;

            var title = data.Name ?? data.NameTranslations?.GetValueOrDefault(ToTvdbLang(language.Language))
                        ?? data.Slug ?? providerId;
            var overview = data.Overview
                           ?? data.OverviewTranslations?.GetValueOrDefault(ToTvdbLang(language.Language))
                           ?? data.OverviewTranslations?.GetValueOrDefault(ToTvdbLang(language.FallbackLanguage));

            if (string.IsNullOrWhiteSpace(overview)
                && !string.Equals(language.Language, language.FallbackLanguage, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await GetAuthorizedAsync<TvdbEnvelope<TvdbExtended>>(
                    client, path, language with { Language = language.FallbackLanguage }, ct);
                overview = fallback?.Data?.Overview ?? overview;
            }

            var year = ParseYear(data.Year) ?? ParseYear(data.FirstAired) ?? ParseYear(data.ReleaseDate);
            DateOnly? release = null;
            if (DateOnly.TryParse(data.FirstAired ?? data.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                release = d;

            long? runtimeMs = data.Runtime is > 0 ? data.Runtime.Value * 60_000L : null;
            var poster = AbsoluteArtwork(data.Image) ?? AbsoluteArtwork(data.Poster);
            var backdrop = AbsoluteArtwork(data.Fanart) ?? AbsoluteArtwork(data.Background);

            SeriesStatus? status = null;
            int? endYear = null;
            List<string>? studios = null;
            if (kind == MediaKind.Series)
            {
                status = MapSeriesStatus(data.Status?.Name);
                endYear = ParseYear(data.LastAired);
                if (status == SeriesStatus.Continuing)
                    endYear = null;
                studios = CollectStudios(data.OriginalNetwork?.Name, data.LatestNetwork?.Name);
            }
            else
            {
                studios = CollectStudios(
                    data.Companies?
                        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                        .Select(c => c.Name!)
                        .Take(5)
                        .ToArray());
            }

            return new MetadataDetails(
                Provider: ProviderName,
                ProviderId: providerId,
                Title: title,
                OriginalTitle: data.Name,
                Year: year,
                Overview: overview,
                CommunityRating: data.Score is > 0 ? Math.Round(data.Score.Value, 1) : null,
                OfficialRating: data.ContentRating,
                ImdbId: data.RemoteIds?.FirstOrDefault(r =>
                    string.Equals(r.SourceName, "IMDB", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Type, "2", StringComparison.Ordinal))?.Id,
                PosterUrl: poster,
                BackdropUrl: backdrop,
                Genres: data.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList() ?? [],
                Tagline: null,
                ReleaseDate: release,
                RuntimeMs: runtimeMs,
                Studios: studios,
                Status: status,
                EndYear: endYear);
        }
        // Timeout-safe: HttpClient timeout is an OCE without ct cancellation — swallow as failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TVDB details failed for {Id}", providerId);
            return null;
        }
    }

    private async Task<IReadOnlyList<MetadataMatch>> ParseSearchAsync(
        HttpResponseMessage response,
        string title,
        int? year,
        CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<TvdbEnvelope<List<TvdbSearchHit>>>(JsonOptions, ct);
        if (payload?.Data is null || payload.Data.Count == 0)
            return [];

        return payload.Data
            .Where(h => h.TvdbId is not null || !string.IsNullOrWhiteSpace(h.Id))
            .Select(h =>
            {
                var id = h.TvdbId?.ToString(CultureInfo.InvariantCulture)
                         ?? h.Id!.Replace("movie-", "", StringComparison.OrdinalIgnoreCase)
                             .Replace("series-", "", StringComparison.OrdinalIgnoreCase);
                var y = ParseYear(h.Year) ?? ParseYear(h.FirstAirTime);
                var score = TmdbMetadataProvider.Score(title, year, h.Name ?? title, y, null);
                return new MetadataMatch(ProviderName, id, h.Name ?? title, y, score);
            })
            .OrderByDescending(m => m.Score)
            .Take(10)
            .ToList();
    }

    private async Task<T?> GetAuthorizedAsync<T>(
        HttpClient client,
        string path,
        MetadataLanguage language,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.AcceptLanguage.ParseAdd(ToTvdbLang(language.Language));

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            InvalidateToken();
            if (!await EnsureTokenAsync(client, ct))
                return default;
            using var retry = new HttpRequestMessage(HttpMethod.Get, path);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            retry.Headers.AcceptLanguage.ParseAdd(ToTvdbLang(language.Language));
            using var retryResponse = await client.SendAsync(retry, ct);
            if (!retryResponse.IsSuccessStatusCode)
                return default;
            return await retryResponse.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("TVDB {Path} returned {Status}", path, (int)response.StatusCode);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private async Task<bool> EnsureTokenAsync(HttpClient client, CancellationToken ct)
    {
        lock (_tokenGate)
        {
            if (!string.IsNullOrEmpty(_token) && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return true;
        }

        var apiKey = secrets.TvdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        try
        {
            var body = new Dictionary<string, string> { ["apikey"] = apiKey };
            var pin = secrets.TvdbPin;
            if (!string.IsNullOrWhiteSpace(pin))
                body["pin"] = pin;

            using var response = await client.PostAsJsonAsync("login", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TVDB login failed with {Status}", (int)response.StatusCode);
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<TvdbEnvelope<TvdbLogin>>(JsonOptions, ct);
            var token = payload?.Data?.Token;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            lock (_tokenGate)
            {
                _token = token;
                // Tokens are long-lived; refresh daily to stay safe.
                _tokenExpiresAt = DateTimeOffset.UtcNow.AddHours(20);
            }

            return true;
        }
        // Timeout-safe: HttpClient timeout is an OCE without ct cancellation — swallow as failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TVDB login request failed");
            return false;
        }
    }

    private void InvalidateToken()
    {
        lock (_tokenGate)
        {
            _token = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private static string? AbsoluteArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return path;
        return $"https://artworks.thetvdb.com{path}";
    }

    private static string ToTvdbLang(string locale)
    {
        // TVDB uses short codes: eng, rus, …
        var primary = locale.Split('-', '_')[0].ToLowerInvariant();
        return primary switch
        {
            "en" => "eng",
            "ru" => "rus",
            "de" => "deu",
            "fr" => "fra",
            "es" => "spa",
            "it" => "ita",
            "ja" => "jpn",
            "ko" => "kor",
            "zh" => "zho",
            "pt" => "por",
            "pl" => "pol",
            "uk" => "ukr",
            _ => primary.Length == 3 ? primary : "eng",
        };
    }

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
            return null;
        return int.TryParse(value.AsSpan(0, 4), out var y) ? y : null;
    }

    public static SeriesStatus? MapSeriesStatus(string? statusName)
    {
        if (string.IsNullOrWhiteSpace(statusName))
            return null;
        var s = statusName.Trim();
        if (s.Equals("Ended", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
            return SeriesStatus.Ended;
        if (s.Equals("Continuing", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Upcoming", StringComparison.OrdinalIgnoreCase))
            return SeriesStatus.Continuing;
        return null;
    }

    private static List<string>? CollectStudios(params string?[]? names)
    {
        if (names is null || names.Length == 0)
            return null;
        var result = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = raw.Trim();
            if (result.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(name);
            if (result.Count >= 5)
                break;
        }
        return result.Count == 0 ? null : result;
    }

    private sealed record TvdbEnvelope<T>(T? Data);

    private sealed record TvdbLogin(string? Token);

    private sealed record TvdbSearchHit(
        string? Id,
        [property: JsonPropertyName("tvdb_id")] int? TvdbId,
        string? Name,
        string? Year,
        [property: JsonPropertyName("first_air_time")] string? FirstAirTime);

    private sealed record TvdbExtended(
        string? Name,
        string? Slug,
        string? Overview,
        string? Year,
        [property: JsonPropertyName("firstAired")] string? FirstAired,
        [property: JsonPropertyName("lastAired")] string? LastAired,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
        int? Runtime,
        double? Score,
        string? Image,
        string? Poster,
        string? Fanart,
        string? Background,
        [property: JsonPropertyName("contentRating")] string? ContentRating,
        TvdbStatus? Status,
        [property: JsonPropertyName("originalNetwork")] TvdbCompany? OriginalNetwork,
        [property: JsonPropertyName("latestNetwork")] TvdbCompany? LatestNetwork,
        List<TvdbCompany>? Companies,
        List<TvdbGenre>? Genres,
        [property: JsonPropertyName("remoteIds")] List<TvdbRemoteId>? RemoteIds,
        [property: JsonPropertyName("nameTranslations")] Dictionary<string, string>? NameTranslations,
        [property: JsonPropertyName("overviewTranslations")] Dictionary<string, string>? OverviewTranslations);

    private sealed record TvdbStatus(string? Name);
    private sealed record TvdbCompany(string? Name);

    private sealed record TvdbGenre(string? Name);

    private sealed record TvdbRemoteId(
        string? Id,
        string? Type,
        [property: JsonPropertyName("sourceName")] string? SourceName);
}
