using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Plex;

/// <summary>
/// Reads watch state from a Plex Media Server via its JSON library API
/// (sections → movies/episodes with viewCount/viewOffset + Guids) and
/// the full session history feed for all-time watched items.
/// </summary>
public sealed class PlexHistoryClient(
    IHttpClientFactory httpClientFactory,
    ILogger<PlexHistoryClient> logger) : IPlexHistoryClient
{
    // Case-sensitive: Plex emits both string "guid" (plex://…) and array "Guid"
    // (external ids). Case-insensitive binding would map "guid" onto List<PlexGuid>.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private const int HistoryPageSize = 200;

    public async Task<IReadOnlyList<PlexWatchEntry>> FetchWatchStateAsync(
        Uri baseUrl,
        string token,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Plex");
        var root = NormalizeBaseUrl(baseUrl);
        var showGuidCache = new Dictionary<string, ParsedShowGuids>(StringComparer.Ordinal);
        // Library rows win over history for the same logical item (they carry resume offset).
        var byKey = new Dictionary<string, PlexWatchEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var sections = await GetAsync<PlexMediaContainer<PlexDirectory>>(
                client, root, "/library/sections", token, ct);
            var libraries = sections?.MediaContainer?.Directory ?? [];

            foreach (var section in libraries)
            {
                if (string.IsNullOrWhiteSpace(section.Key))
                    continue;

                // type=1 movies, type=4 episodes — covers both Movies and TV libraries.
                await CollectLibraryAsync(
                    client, root, token, section.Key, PlexWatchKind.Movie, type: 1, byKey, showGuidCache, ct);
                await CollectLibraryAsync(
                    client, root, token, section.Key, PlexWatchKind.Episode, type: 4, byKey, showGuidCache, ct);
            }

            await CollectHistoryAsync(client, root, token, byKey, ct);

            // Values are stored under multiple lookup keys; Distinct keeps one row per entry.
            return byKey.Values.Distinct().ToList();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to reach Plex at {BaseUrl}", root);
            throw new UnprocessableException($"Could not reach Plex server: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Plex request timed out at {BaseUrl}", root);
            throw new UnprocessableException("Plex server request timed out.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid JSON from Plex at {BaseUrl}", root);
            throw new UnprocessableException("Plex server returned an unexpected response.");
        }
    }

    private async Task CollectLibraryAsync(
        HttpClient client,
        Uri root,
        string token,
        string sectionKey,
        PlexWatchKind kind,
        int type,
        Dictionary<string, PlexWatchEntry> byKey,
        Dictionary<string, ParsedShowGuids> showGuidCache,
        CancellationToken ct)
    {
        var path = $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={type}&includeGuids=1";
        var payload = await GetAsync<PlexMediaContainer<PlexMetadata>>(client, root, path, token, ct);
        var items = payload?.MediaContainer?.Metadata ?? [];

        foreach (var item in items)
        {
            var entry = await MapLibraryEntryAsync(client, root, token, item, kind, showGuidCache, ct);
            if (entry is null)
                continue;

            foreach (var key in BuildKeys(entry))
                byKey[key] = entry;
        }
    }

    private async Task CollectHistoryAsync(
        HttpClient client,
        Uri root,
        string token,
        Dictionary<string, PlexWatchEntry> byKey,
        CancellationToken ct)
    {
        var start = 0;
        while (true)
        {
            var path =
                $"/status/sessions/history/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={HistoryPageSize}";
            var payload = await GetAsync<PlexMediaContainer<PlexHistoryItem>>(client, root, path, token, ct);
            var items = payload?.MediaContainer?.Metadata ?? [];
            if (items.Count == 0)
                break;

            foreach (var item in items)
            {
                var entry = MapHistoryEntry(item);
                if (entry is null)
                    continue;

                // Do not overwrite library rows that already carry resume position / Guids.
                var keys = BuildKeys(entry);
                if (keys.Any(byKey.ContainsKey))
                    continue;

                foreach (var key in keys)
                    byKey[key] = entry;
            }

            start += items.Count;
            if (items.Count < HistoryPageSize)
                break;
        }
    }

    private async Task<PlexWatchEntry?> MapLibraryEntryAsync(
        HttpClient client,
        Uri root,
        string token,
        PlexMetadata item,
        PlexWatchKind kind,
        Dictionary<string, ParsedShowGuids> showGuidCache,
        CancellationToken ct)
    {
        var viewCount = item.ViewCount ?? 0;
        var viewOffset = item.ViewOffset ?? 0;
        if (viewCount <= 0 && viewOffset <= 0)
            return null;

        if (kind == PlexWatchKind.Episode)
        {
            var season = item.ParentIndex;
            var episode = item.Index;
            if (season is null || episode is null)
                return null;

            var showGuids = await ResolveShowGuidsAsync(
                client, root, token, item.GrandparentRatingKey, showGuidCache, ct);
            var parsed = PlexGuidParser.Parse(showGuids.Guids, showGuids.PrimaryGuid);
            if (string.IsNullOrWhiteSpace(parsed.TmdbId)
                && string.IsNullOrWhiteSpace(parsed.TvdbId)
                && string.IsNullOrWhiteSpace(parsed.ImdbId))
            {
                // Keep title-based fallback even without external ids.
            }

            return new PlexWatchEntry(
                PlexWatchKind.Episode,
                item.Title ?? item.GrandparentTitle ?? "Episode",
                parsed.TmdbId,
                parsed.TvdbId,
                parsed.ImdbId,
                season,
                episode,
                Watched: viewCount > 0 && viewOffset <= 0,
                PositionMs: viewOffset,
                DurationMs: item.Duration,
                PlayCount: Math.Max(0, viewCount),
                ViewedAt: ToViewedAt(item.LastViewedAt),
                SeriesTitle: item.GrandparentTitle);
        }

        var movieGuids = (item.ExternalGuids ?? [])
            .Select(g => g.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>();
        var movieParsed = PlexGuidParser.Parse(movieGuids);
        if (string.IsNullOrWhiteSpace(movieParsed.TmdbId)
            && string.IsNullOrWhiteSpace(movieParsed.TvdbId)
            && string.IsNullOrWhiteSpace(movieParsed.ImdbId)
            && string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }

        return new PlexWatchEntry(
            PlexWatchKind.Movie,
            item.Title ?? "Movie",
            movieParsed.TmdbId,
            movieParsed.TvdbId,
            movieParsed.ImdbId,
            SeasonNumber: null,
            EpisodeNumber: null,
            Watched: viewCount > 0 && viewOffset <= 0,
            PositionMs: viewOffset,
            DurationMs: item.Duration,
            PlayCount: Math.Max(0, viewCount),
            ViewedAt: ToViewedAt(item.LastViewedAt),
            SeriesTitle: null);
    }

    private static PlexWatchEntry? MapHistoryEntry(PlexHistoryItem item)
    {
        if (string.Equals(item.Type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length < 2)
                return null;

            return new PlexWatchEntry(
                PlexWatchKind.Movie,
                item.Title.Trim(),
                TmdbId: null,
                TvdbId: null,
                ImdbId: null,
                SeasonNumber: null,
                EpisodeNumber: null,
                Watched: true,
                PositionMs: 0,
                DurationMs: null,
                PlayCount: 1,
                ViewedAt: ToViewedAt(item.ViewedAt),
                SeriesTitle: null);
        }

        if (!string.Equals(item.Type, "episode", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrWhiteSpace(item.GrandparentTitle)
            || item.ParentIndex is null
            || item.Index is null)
        {
            return null;
        }

        return new PlexWatchEntry(
            PlexWatchKind.Episode,
            item.Title ?? $"S{item.ParentIndex:00}E{item.Index:00}",
            TmdbId: null,
            TvdbId: null,
            ImdbId: null,
            item.ParentIndex,
            item.Index,
            Watched: true,
            PositionMs: 0,
            DurationMs: null,
            PlayCount: 1,
            ViewedAt: ToViewedAt(item.ViewedAt),
            SeriesTitle: item.GrandparentTitle.Trim());
    }

    private async Task<ParsedShowGuids> ResolveShowGuidsAsync(
        HttpClient client,
        Uri root,
        string token,
        string? grandparentRatingKey,
        Dictionary<string, ParsedShowGuids> cache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(grandparentRatingKey))
            return ParsedShowGuids.Empty;

        if (cache.TryGetValue(grandparentRatingKey, out var cached))
            return cached;

        try
        {
            var path = $"/library/metadata/{Uri.EscapeDataString(grandparentRatingKey)}?includeGuids=1";
            var payload = await GetAsync<PlexMediaContainer<PlexMetadata>>(client, root, path, token, ct);
            var show = payload?.MediaContainer?.Metadata?.FirstOrDefault();
            var guids = (show?.ExternalGuids ?? [])
                .Select(g => g.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
            var parsed = new ParsedShowGuids(guids, show?.Guid);
            cache[grandparentRatingKey] = parsed;
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or UnprocessableException or JsonException)
        {
            logger.LogDebug(ex, "Failed to resolve Plex show Guids for {RatingKey}", grandparentRatingKey);
            cache[grandparentRatingKey] = ParsedShowGuids.Empty;
            return ParsedShowGuids.Empty;
        }
    }

    private static IEnumerable<string> BuildKeys(PlexWatchEntry entry)
    {
        if (entry.Kind == PlexWatchKind.Movie)
        {
            if (!string.IsNullOrWhiteSpace(entry.TmdbId))
                yield return $"m:tmdb:{entry.TmdbId}";
            if (!string.IsNullOrWhiteSpace(entry.ImdbId))
                yield return $"m:imdb:{entry.ImdbId}";
            if (!string.IsNullOrWhiteSpace(entry.TvdbId))
                yield return $"m:tvdb:{entry.TvdbId}";
            if (!string.IsNullOrWhiteSpace(entry.Title))
                yield return $"m:title:{NormalizeTitle(entry.Title)}";
            yield break;
        }

        var season = entry.SeasonNumber ?? 0;
        var episode = entry.EpisodeNumber ?? 0;
        if (!string.IsNullOrWhiteSpace(entry.TmdbId))
            yield return $"e:tmdb:{entry.TmdbId}:{season}:{episode}";
        if (!string.IsNullOrWhiteSpace(entry.TvdbId))
            yield return $"e:tvdb:{entry.TvdbId}:{season}:{episode}";
        if (!string.IsNullOrWhiteSpace(entry.ImdbId))
            yield return $"e:imdb:{entry.ImdbId}:{season}:{episode}";
        if (!string.IsNullOrWhiteSpace(entry.SeriesTitle))
            yield return $"e:title:{NormalizeTitle(entry.SeriesTitle)}:{season}:{episode}";
    }

    private static string NormalizeTitle(string title) =>
        string.Join(' ', title.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset ToViewedAt(long? unixSeconds) =>
        unixSeconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
            : DateTimeOffset.UtcNow;

    private static Uri NormalizeBaseUrl(Uri baseUrl)
    {
        var builder = new UriBuilder(baseUrl) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    private static async Task<T?> GetAsync<T>(
        HttpClient client,
        Uri root,
        string pathAndQuery,
        string token,
        CancellationToken ct)
    {
        var uri = new Uri(root, pathAndQuery.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "LumenMedia");
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "lumenmedia-server");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnprocessableException("Plex rejected the token (unauthorized).");

        if (!response.IsSuccessStatusCode)
        {
            throw new UnprocessableException(
                $"Plex returned {(int)response.StatusCode} {response.ReasonPhrase} for {pathAndQuery}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private readonly record struct ParsedShowGuids(IReadOnlyList<string> Guids, string? PrimaryGuid)
    {
        public static ParsedShowGuids Empty { get; } = new([], null);
    }

    private sealed record PlexMediaContainer<T>([property: JsonPropertyName("MediaContainer")] PlexContainer<T>? MediaContainer);

    private sealed record PlexContainer<T>(
        [property: JsonPropertyName("Directory")] List<T>? Directory,
        [property: JsonPropertyName("Metadata")] List<T>? Metadata);

    private sealed record PlexDirectory(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title);

    private sealed record PlexMetadata(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("guid")] string? Guid,
        [property: JsonPropertyName("grandparentTitle")] string? GrandparentTitle,
        [property: JsonPropertyName("grandparentGuid")] string? GrandparentGuid,
        [property: JsonPropertyName("grandparentRatingKey")] string? GrandparentRatingKey,
        [property: JsonPropertyName("parentIndex")] int? ParentIndex,
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("viewCount")] int? ViewCount,
        [property: JsonPropertyName("viewOffset")] long? ViewOffset,
        [property: JsonPropertyName("duration")] long? Duration,
        [property: JsonPropertyName("lastViewedAt")] long? LastViewedAt,
        [property: JsonPropertyName("Guid")] List<PlexGuid>? ExternalGuids);

    private sealed record PlexHistoryItem(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("grandparentTitle")] string? GrandparentTitle,
        [property: JsonPropertyName("parentIndex")] int? ParentIndex,
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("viewedAt")] long? ViewedAt);

    private sealed record PlexGuid([property: JsonPropertyName("id")] string? Id);
}
