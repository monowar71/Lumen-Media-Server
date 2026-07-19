using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Common;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Plex;

/// <summary>
/// Reads watch state from a Plex Media Server via its JSON library API
/// (sections → movies/episodes with viewCount/viewOffset + Guids).
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

    public async Task<IReadOnlyList<PlexWatchEntry>> FetchWatchStateAsync(
        Uri baseUrl,
        string token,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Plex");
        var root = NormalizeBaseUrl(baseUrl);

        try
        {
            var sections = await GetAsync<PlexMediaContainer<PlexDirectory>>(
                client, root, "/library/sections", token, ct);
            var libraries = sections?.MediaContainer?.Directory ?? [];
            var results = new List<PlexWatchEntry>();

            foreach (var section in libraries)
            {
                if (string.IsNullOrWhiteSpace(section.Key))
                    continue;

                // type=1 movies, type=4 episodes — covers both Movies and TV libraries.
                await CollectAsync(client, root, token, section.Key, PlexWatchKind.Movie, type: 1, results, ct);
                await CollectAsync(client, root, token, section.Key, PlexWatchKind.Episode, type: 4, results, ct);
            }

            return results;
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

    private async Task CollectAsync(
        HttpClient client,
        Uri root,
        string token,
        string sectionKey,
        PlexWatchKind kind,
        int type,
        List<PlexWatchEntry> results,
        CancellationToken ct)
    {
        var path = $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={type}&includeGuids=1";
        var payload = await GetAsync<PlexMediaContainer<PlexMetadata>>(client, root, path, token, ct);
        var items = payload?.MediaContainer?.Metadata ?? [];

        foreach (var item in items)
        {
            var entry = MapEntry(item, kind);
            if (entry is not null)
                results.Add(entry);
        }
    }

    private static PlexWatchEntry? MapEntry(PlexMetadata item, PlexWatchKind kind)
    {
        var viewCount = item.ViewCount ?? 0;
        var viewOffset = item.ViewOffset ?? 0;
        if (viewCount <= 0 && viewOffset <= 0)
            return null;

        var guids = (item.ExternalGuids ?? []).Select(g => g.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>();
        var parsed = PlexGuidParser.Parse(guids, item.GrandparentGuid);

        if (kind == PlexWatchKind.Episode)
        {
            var season = item.ParentIndex ?? parsed.SeasonNumber;
            var episode = item.Index ?? parsed.EpisodeNumber;
            if (season is null || episode is null)
                return null;
            if (string.IsNullOrWhiteSpace(parsed.TmdbId)
                && string.IsNullOrWhiteSpace(parsed.TvdbId)
                && string.IsNullOrWhiteSpace(parsed.ImdbId))
                return null;

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
                ViewedAt: ToViewedAt(item.LastViewedAt));
        }

        if (string.IsNullOrWhiteSpace(parsed.TmdbId)
            && string.IsNullOrWhiteSpace(parsed.TvdbId)
            && string.IsNullOrWhiteSpace(parsed.ImdbId))
            return null;

        return new PlexWatchEntry(
            PlexWatchKind.Movie,
            item.Title ?? "Movie",
            parsed.TmdbId,
            parsed.TvdbId,
            parsed.ImdbId,
            SeasonNumber: null,
            EpisodeNumber: null,
            Watched: viewCount > 0 && viewOffset <= 0,
            PositionMs: viewOffset,
            DurationMs: item.Duration,
            PlayCount: Math.Max(0, viewCount),
            ViewedAt: ToViewedAt(item.LastViewedAt));
    }

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
        [property: JsonPropertyName("grandparentTitle")] string? GrandparentTitle,
        [property: JsonPropertyName("grandparentGuid")] string? GrandparentGuid,
        [property: JsonPropertyName("parentIndex")] int? ParentIndex,
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("viewCount")] int? ViewCount,
        [property: JsonPropertyName("viewOffset")] long? ViewOffset,
        [property: JsonPropertyName("duration")] long? Duration,
        [property: JsonPropertyName("lastViewedAt")] long? LastViewedAt,
        [property: JsonPropertyName("Guid")] List<PlexGuid>? ExternalGuids);

    private sealed record PlexGuid([property: JsonPropertyName("id")] string? Id);
}
