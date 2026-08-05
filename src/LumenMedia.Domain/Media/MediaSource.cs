using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Media;

/// <summary>
/// A playable media source. Owner is exactly one of a movie <see cref="MediaItemId"/>
/// or an <see cref="EpisodeId"/> (enforced by a DB check constraint).
/// Local files use a filesystem path; torrent sources use a synthetic path and stream via TorrServer.
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
        Kind = MediaSourceKind.LocalFile;
        Path = path;
        Container = container;
        SizeBytes = sizeBytes;
        FileMtime = fileMtime;
        AddedAt = now;
    }

    /// <summary>
    /// Torrent video entry. <see cref="Path"/> is <c>{torrentPath}#{fileIndex}</c> (unique);
    /// <see cref="TorrentPath"/> is the real <c>.torrent</c> on disk under library roots.
    /// </summary>
    public static MediaSource CreateTorrent(
        string torrentPath,
        string infoHash,
        int fileIndex,
        string relativePathInsideTorrent,
        string container,
        long sizeBytes,
        DateTimeOffset fileMtime,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(torrentPath))
            throw new ArgumentException("Torrent path is required", nameof(torrentPath));
        if (string.IsNullOrWhiteSpace(infoHash))
            throw new ArgumentException("Info hash is required", nameof(infoHash));
        if (fileIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(fileIndex), "TorrServer file ids are 1-based.");

        var source = new MediaSource
        {
            Id = Guid.CreateVersion7(),
            Kind = MediaSourceKind.Torrent,
            Path = $"{torrentPath}#{fileIndex}",
            TorrentPath = torrentPath,
            InfoHash = infoHash.ToLowerInvariant(),
            TorrentFileIndex = fileIndex,
            TorrentRelativePath = relativePathInsideTorrent,
            Container = container,
            SizeBytes = sizeBytes,
            FileMtime = fileMtime,
            AddedAt = now,
        };
        return source;
    }

    public Guid Id { get; private set; }
    public Guid? MediaItemId { get; internal set; }
    public Guid? EpisodeId { get; internal set; }
    public MediaSourceKind Kind { get; private set; } = MediaSourceKind.LocalFile;
    public string Path { get; private set; } = null!;
    public string Container { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public long? DurationMs { get; private set; }
    public int? OverallBitrateKbps { get; private set; }
    public DateTimeOffset FileMtime { get; private set; }
    public string? ContentHash { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    /// <summary>Absolute path to the <c>.torrent</c> file when <see cref="Kind"/> is <see cref="MediaSourceKind.Torrent"/>.</summary>
    public string? TorrentPath { get; private set; }
    public string? InfoHash { get; private set; }
    /// <summary>1-based file id matching TorrServer <c>file_stats[].id</c>.</summary>
    public int? TorrentFileIndex { get; private set; }
    public string? TorrentRelativePath { get; private set; }

    public IReadOnlyList<MediaStream> Streams => _streams;

    public bool IsTorrent => Kind == MediaSourceKind.Torrent;

    /// <summary>
    /// True when video codec is missing/placeholder — typical for torrent sources before play-time probe.
    /// </summary>
    public bool NeedsStreamProbe()
    {
        var video = _streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        if (video is null)
            return true;
        if (string.IsNullOrWhiteSpace(video.Codec))
            return true;
        return video.Codec.Equals("unknown", StringComparison.OrdinalIgnoreCase)
               || video.Codec.Equals("und", StringComparison.OrdinalIgnoreCase)
               || video.Codec.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>Replaces all streams (caller removes old EF entities before save).</summary>
    public void ReplaceStreams(IEnumerable<MediaStream> streams)
    {
        _streams.Clear();
        foreach (var stream in streams)
            _streams.Add(stream);
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
