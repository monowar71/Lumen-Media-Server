namespace LumenMedia.Domain.Media;

/// <summary>
/// A physical media file. Owner is exactly one of a movie <see cref="MediaItemId"/>
/// or an <see cref="EpisodeId"/> (enforced by a DB check constraint).
/// </summary>
public class MediaSource
{
    private readonly List<MediaStream> _streams = [];

    private MediaSource() { }

    public MediaSource(string path, string container, long sizeBytes, DateTimeOffset fileMtime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required", nameof(path));

        Id = Guid.CreateVersion7();
        Path = path;
        Container = container;
        SizeBytes = sizeBytes;
        FileMtime = fileMtime;
        AddedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid? MediaItemId { get; internal set; }
    public Guid? EpisodeId { get; internal set; }
    public string Path { get; private set; } = null!;
    public string Container { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public long? DurationMs { get; private set; }
    public int? OverallBitrateKbps { get; private set; }
    public DateTimeOffset FileMtime { get; private set; }
    public string? ContentHash { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    public IReadOnlyList<MediaStream> Streams => _streams;

    public void SetProbeInfo(long? durationMs, int? overallBitrateKbps)
    {
        DurationMs = durationMs;
        OverallBitrateKbps = overallBitrateKbps;
    }

    public void SetContentHash(string? hash) => ContentHash = hash;

    public MediaStream AddStream(MediaStream stream)
    {
        _streams.Add(stream);
        return stream;
    }

    public void OwnedByMovie(Guid mediaItemId)
    {
        MediaItemId = mediaItemId;
        EpisodeId = null;
    }

    public void OwnedByEpisode(Guid episodeId)
    {
        EpisodeId = episodeId;
        MediaItemId = null;
    }
}
