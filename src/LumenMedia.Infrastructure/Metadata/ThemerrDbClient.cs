using System.Text.Json;
using System.Text.Json.Serialization;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Metadata;

/// <summary>
/// Reads public ThemerrDB JSON (no API key). 404 = no theme for that TMDB id.
/// </summary>
public sealed class ThemerrDbClient(
    IHttpClientFactory httpClientFactory,
    ILogger<ThemerrDbClient> logger) : IThemerrDbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<string?> GetYoutubeThemeUrlAsync(string tmdbId, MediaKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
            return null;

        var mediaType = kind switch
        {
            MediaKind.Movie => "movies",
            MediaKind.Series => "tv_shows",
            _ => null,
        };
        if (mediaType is null)
            return null;

        var path = $"{mediaType}/themoviedb/{Uri.EscapeDataString(tmdbId.Trim())}.json";
        var client = httpClientFactory.CreateClient("ThemerrDb");

        try
        {
            using var response = await client.GetAsync(path, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("ThemerrDB {Path} returned {Status}", path, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<ThemerrEntry>(stream, JsonOptions, ct);
            var url = dto?.YoutubeThemeUrl?.Trim();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ThemerrDB lookup failed for {Path}", path);
            return null;
        }
    }

    private sealed class ThemerrEntry
    {
        [JsonPropertyName("youtube_theme_url")]
        public string? YoutubeThemeUrl { get; set; }
    }
}
