using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace LumenMedia.Application.Tests;

/// <summary>
/// Regression: Plex JSON has both string "guid" and array "Guid". Case-insensitive
/// System.Text.Json binding maps the string onto List&lt;PlexGuid&gt; and throws.
/// </summary>
public sealed class PlexMetadataJsonTests
{
    private static readonly JsonSerializerOptions CaseSensitive = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private const string Sample = """
        {
          "MediaContainer": {
            "Metadata": [
              {
                "type": "movie",
                "title": "Sample",
                "guid": "plex://movie/abc",
                "viewCount": 1,
                "viewOffset": 0,
                "duration": 1000,
                "lastViewedAt": 1700000000,
                "Guid": [
                  { "id": "tmdb://603" },
                  { "id": "imdb://tt0133093" }
                ]
              }
            ]
          }
        }
        """;

    [Fact]
    public void Case_sensitive_binding_reads_Guid_array_and_ignores_guid_string()
    {
        var payload = JsonSerializer.Deserialize<PlexMediaContainer<PlexMetadata>>(Sample, CaseSensitive);
        var item = payload!.MediaContainer!.Metadata!.Single();
        item.ExternalGuids.Should().HaveCount(2);
        item.ExternalGuids![0].Id.Should().Be("tmdb://603");
        item.ViewCount.Should().Be(1);
    }

    [Fact]
    public void Case_insensitive_binding_fails_on_real_plex_payload()
    {
        var act = () => JsonSerializer.Deserialize<PlexMediaContainer<PlexMetadata>>(Sample, CaseInsensitive);
        act.Should().Throw<JsonException>();
    }

    private sealed record PlexMediaContainer<T>([property: JsonPropertyName("MediaContainer")] PlexContainer<T>? MediaContainer);

    private sealed record PlexContainer<T>([property: JsonPropertyName("Metadata")] List<T>? Metadata);

    private sealed record PlexMetadata(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("viewCount")] int? ViewCount,
        [property: JsonPropertyName("viewOffset")] long? ViewOffset,
        [property: JsonPropertyName("duration")] long? Duration,
        [property: JsonPropertyName("lastViewedAt")] long? LastViewedAt,
        [property: JsonPropertyName("Guid")] List<PlexGuid>? ExternalGuids);

    private sealed record PlexGuid([property: JsonPropertyName("id")] string? Id);
}
