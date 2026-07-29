using LumenMedia.Domain.Enums;

namespace LumenMedia.Application.Common;

public static class ArtworkUrlBuilder
{
    public static string ItemArtwork(Guid id, ArtworkKind kind) => $"/api/v1/items/{id}/artwork/{kind}";

    public static string ItemTheme(Guid id) => $"/api/v1/items/{id}/theme";

    public static string SubtitleUrl(Guid itemId, Guid streamId) => $"/api/v1/items/{itemId}/subtitles/{streamId}.vtt";
}
