using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Metadata;

/// <summary>
/// Free TVMaze provider (no API key). Covers series only — used when TMDB is not configured,
/// and as an extra search candidate when it is.
/// </summary>
public sealed partial class TvMazeMetadataProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<TvMazeMetadataProvider> logger) : IMetadataProvider
{
    public const string ProviderName = "TvMaze";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Name => ProviderName;
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<MetadataMatch>> SearchAsync(
        string title,
        int? year,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        _ = language; // TVMaze has no language parameter; returns provider-default text.
        if (kind != MediaKind.Series || string.IsNullOrWhiteSpace(title))
            return [];

        var client = httpClientFactory.CreateClient("TvMaze");
        try
        {
            var url = $"search/shows?q={Uri.EscapeDataString(title)}";
            var rows = await client.GetFromJsonAsync<List<TvMazeSearchRow>>(url, JsonOptions, ct);
            if (rows is null || rows.Count == 0)
                return [];

            return rows
                .Where(r => r.Show is not null)
                .Select(r =>
                {
                    var show = r.Show!;
                    var y = ParseYear(show.Premiered);
                    var score = TmdbMetadataProvider.Score(title, year, show.Name ?? title, y, show.Weight);
                    return new MetadataMatch(
                        ProviderName,
                        show.Id.ToString(CultureInfo.InvariantCulture),
                        show.Name ?? title,
                        y,
                        score);
                })
                .OrderByDescending(m => m.Score)
                .Take(10)
                .ToList();
        }
        // Timeout-safe: HttpClient timeout is an OCE without ct cancellation — swallow as failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TVMaze search failed for {Title}", title);
            return [];
        }
    }

    public async Task<MetadataDetails?> GetDetailsAsync(
        string providerId,
        MediaKind kind,
        MetadataLanguage language,
        CancellationToken ct)
    {
        _ = language;
        if (kind != MediaKind.Series || string.IsNullOrWhiteSpace(providerId))
            return null;

        var client = httpClientFactory.CreateClient("TvMaze");
        try
        {
            var show = await client.GetFromJsonAsync<TvMazeShow>($"shows/{providerId}", JsonOptions, ct);
            if (show is null)
                return null;

            var poster = show.Image?.Original ?? show.Image?.Medium;
            double? rating = show.Rating?.Average is > 0 ? Math.Round(show.Rating.Average.Value, 1) : null;
            var status = MapSeriesStatus(show.Status);
            var endYear = ParseYear(show.Ended);
            if (status == SeriesStatus.Continuing)
                endYear = null;
            var studios = CollectStudios(show.Network?.Name, show.WebChannel?.Name);

            return new MetadataDetails(
                Provider: ProviderName,
                ProviderId: providerId,
                Title: show.Name ?? providerId,
                OriginalTitle: show.Name,
                Year: ParseYear(show.Premiered),
                Overview: StripHtml(show.Summary),
                CommunityRating: rating,
                OfficialRating: null,
                ImdbId: show.Externals?.Imdb,
                PosterUrl: poster,
                BackdropUrl: poster,
                Genres: show.Genres ?? [],
                ReleaseDate: DateOnly.TryParse(show.Premiered, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                RuntimeMs: show.Runtime is > 0 ? show.Runtime.Value * 60_000L : null,
                Studios: studios,
                Status: status,
                EndYear: endYear);
        }
        // Timeout-safe: HttpClient timeout is an OCE without ct cancellation — swallow as failure.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TVMaze details failed for {Id}", providerId);
            return null;
        }
    }

    private static int? ParseYear(string? date) =>
        !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;

    public static SeriesStatus? MapSeriesStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;
        return status.Trim() switch
        {
            "Ended" => SeriesStatus.Ended,
            "Running" or "To Be Determined" or "In Development" => SeriesStatus.Continuing,
            _ => null,
        };
    }

    private static List<string>? CollectStudios(params string?[] names)
    {
        var result = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = raw.Trim();
            if (result.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(name);
        }
        return result.Count == 0 ? null : result;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var text = HtmlTagRegex().Replace(html, " ");
        return WhitespaceRegex().Replace(System.Net.WebUtility.HtmlDecode(text) ?? text, " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record TvMazeSearchRow(TvMazeShow? Show);
    private sealed record TvMazeShow(
        int Id,
        string? Name,
        string? Summary,
        string? Premiered,
        string? Ended,
        string? Status,
        int? Runtime,
        double? Weight,
        List<string>? Genres,
        TvMazeRating? Rating,
        TvMazeImage? Image,
        TvMazeExternals? Externals,
        TvMazeNetwork? Network,
        TvMazeNetwork? WebChannel);
    private sealed record TvMazeRating(double? Average);
    private sealed record TvMazeImage(string? Medium, string? Original);
    private sealed record TvMazeExternals(string? Imdb);
    private sealed record TvMazeNetwork(string? Name);
}
