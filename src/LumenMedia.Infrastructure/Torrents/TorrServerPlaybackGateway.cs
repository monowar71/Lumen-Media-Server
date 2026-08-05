using LumenMedia.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Torrents;

/// <summary>
/// Ensures a .torrent is loaded in TorrServer and returns an HTTP play URL for a file index.
/// Caller owns <see cref="ITorrServerProcess"/> leases around the playback session.
/// </summary>
public sealed class TorrServerPlaybackGateway(
    ITorrServerProcess process,
    ITorrServerClient client,
    ILogger<TorrServerPlaybackGateway> logger) : ITorrentPlaybackResolver
{
    public async Task<string> ResolvePlayUrlAsync(
        string torrentPath,
        string expectedInfoHash,
        int fileIndex,
        CancellationToken ct)
    {
        await process.EnsureRunningAsync(ct);

        var status = await client.GetAsync(expectedInfoHash, ct);
        if (status is null || status.FileStats.Count == 0)
        {
            status = await client.UploadTorrentAsync(torrentPath, ct);
            var hash = string.IsNullOrWhiteSpace(status.Hash) ? expectedInfoHash : status.Hash;
            status = await WaitForFilesAsync(hash, ct) ?? status;
        }

        var playHash = string.IsNullOrWhiteSpace(status.Hash) ? expectedInfoHash : status.Hash;
        if (status.FileStats.Count > 0 && status.FileStats.All(f => f.Id != fileIndex))
        {
            logger.LogWarning(
                "TorrServer file index {Index} not in file_stats for {Hash}; proceeding anyway",
                fileIndex,
                playHash);
        }

        return client.BuildPlayUrl(playHash, fileIndex);
    }

    private async Task<TorrServerTorrentStatus?> WaitForFilesAsync(string hash, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(300);
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            var status = await client.GetAsync(hash, ct);
            if (status is { FileStats.Count: > 0 })
                return status;
            await Task.Delay(delay, ct);
            if (delay < TimeSpan.FromSeconds(2))
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 1.5);
        }

        return await client.GetAsync(hash, ct);
    }
}
