namespace LumenMedia.Infrastructure.Transcoding;

/// <summary>
/// Reads HLS fragment files only after size stabilizes, then snapshots an exact byte
/// length so Kestrel never emits a Content-Length that drifts while ffmpeg appends.
/// </summary>
public static class StableFileSnapshot
{
    /// <summary>
    /// Waits until <paramref name="path"/> has a stable non-zero size, then returns a
    /// fixed byte copy. Returns <c>null</c> on timeout or if the file never settles.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(
        string path,
        TimeSpan timeout,
        CancellationToken ct,
        int stableSamples = 2,
        int pollMs = 50)
    {
        if (!await WaitUntilStableAsync(path, timeout, ct, stableSamples, pollMs).ConfigureAwait(false))
            return null;

        // Retry a few times: ffmpeg may append between "stable" and the open.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? snapshot;
            long snapLen;
            try
            {
                await using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapLen = fs.Length;
                if (snapLen <= 0)
                    return null;
                if (snapLen > int.MaxValue)
                    return null;

                snapshot = new byte[snapLen];
                var read = 0;
                while (read < snapLen)
                {
                    var n = await fs.ReadAsync(snapshot.AsMemory(read, (int)snapLen - read), ct)
                        .ConfigureAwait(false);
                    if (n == 0)
                        break;
                    read += n;
                }

                if (read != snapLen)
                {
                    await Task.Delay(pollMs, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (IOException)
            {
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
                continue;
            }

            // If the on-disk size grew, the writer was still active — wait and retry.
            try
            {
                var nowLen = new FileInfo(path).Length;
                if (nowLen != snapLen)
                {
                    await Task.Delay(pollMs, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (IOException)
            {
                await Task.Delay(pollMs, ct).ConfigureAwait(false);
                continue;
            }

            return snapshot;
        }

        return null;
    }

    /// <summary>
    /// True when the file exists and its size is unchanged for <paramref name="stableSamples"/>
    /// consecutive polls. Does <b>not</b> return true for an unstable file after timeout.
    /// </summary>
    public static async Task<bool> WaitUntilStableAsync(
        string path,
        TimeSpan timeout,
        CancellationToken ct,
        int stableSamples = 2,
        int pollMs = 50)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastLen = -1;
        var stableCount = 0;
        var samplesNeeded = Math.Max(2, stableSamples);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                long len;
                try
                {
                    len = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    len = -1;
                }

                if (len > 0 && len == lastLen)
                {
                    stableCount++;
                    if (stableCount >= samplesNeeded)
                        return true;
                }
                else
                {
                    stableCount = 0;
                    lastLen = len;
                }
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        return false;
    }
}
