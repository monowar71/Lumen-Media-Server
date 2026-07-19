using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Media;

public class Artwork
{
    private Artwork() { }

    public Artwork(ArtworkKind kind, string localPath, Guid? mediaItemId = null, Guid? episodeId = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("Local path is required", nameof(localPath));

        Id = Guid.CreateVersion7();
        Kind = kind;
        LocalPath = localPath;
        MediaItemId = mediaItemId;
        EpisodeId = episodeId;
    }

    public Guid Id { get; private set; }
    public Guid? MediaItemId { get; internal set; }
    public Guid? EpisodeId { get; internal set; }
    public ArtworkKind Kind { get; private set; }
    public string? SourceUrl { get; set; }
    public string LocalPath { get; private set; } = null!;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool IsPrimary { get; set; }
}
