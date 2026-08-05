namespace LumenMedia.Application.Abstractions;

/// <summary>Parsed contents of a <c>.torrent</c> file (no TorrServer required).</summary>
public sealed record TorrentMetadata(
    string InfoHash,
    string Name,
    IReadOnlyList<TorrentFileEntry> Files);

public sealed record TorrentFileEntry(
    /// <summary>1-based index matching TorrServer <c>file_stats[].id</c>.</summary>
    int Index,
    string Path,
    long Length);

public interface ITorrentMetadataParser
{
    TorrentMetadata Parse(Stream torrentStream);
    TorrentMetadata ParseFile(string torrentPath);
}

/// <summary>Lazy lifecycle for the embedded TorrServer binary (idle = not running).</summary>
public interface ITorrServerProcess
{
    bool IsRunning { get; }
    Task EnsureRunningAsync(CancellationToken ct);
    /// <summary>Release one playback lease; process stops after idle grace when leases hit zero.</summary>
    void ReleaseLease();
    /// <summary>Acquire a lease while a torrent playback session is active.</summary>
    void AcquireLease();
    Task StopAsync(CancellationToken ct);
}

public sealed record TorrServerFileStat(int Id, string Path, long Length);

public sealed record TorrServerTorrentStatus(
    string Hash,
    string? Name,
    int Stat,
    string? StatString,
    IReadOnlyList<TorrServerFileStat> FileStats,
    int ConnectedSeeders = 0,
    int TotalPeers = 0,
    int ActivePeers = 0,
    double DownloadSpeedBytesPerSec = 0);

public interface ITorrServerClient
{
    Task<string> EchoAsync(CancellationToken ct);
    Task<TorrServerTorrentStatus> UploadTorrentAsync(string torrentFilePath, CancellationToken ct);
    Task<TorrServerTorrentStatus?> GetAsync(string infoHash, CancellationToken ct);
    Task DropAsync(string infoHash, CancellationToken ct);
    string BuildPlayUrl(string infoHash, int fileIndex);
}

/// <summary>Starts TorrServer (if managed), loads the torrent, returns HTTP play URL.</summary>
public interface ITorrentPlaybackResolver
{
    Task<string> ResolvePlayUrlAsync(
        string torrentPath,
        string expectedInfoHash,
        int fileIndex,
        CancellationToken ct);
}
