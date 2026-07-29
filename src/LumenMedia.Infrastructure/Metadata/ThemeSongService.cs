using System.Diagnostics;
using System.Text.RegularExpressions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Media;
using Microsoft.Extensions.Logging;

namespace LumenMedia.Infrastructure.Metadata;

/// <summary>
/// During metadata enrich: ThemerrDB → yt-dlp (YouTube audio) → MP3 under /config/metadata.
/// Failures are non-fatal so artwork/overview enrich still succeeds.
/// </summary>
public sealed partial class ThemeSongService(
    IThemerrDbClient themerr,
    IThemeSongStore store,
    ILogger<ThemeSongService> logger) : IThemeSongService
{
    // Serial downloads: concurrent yt-dlp from the NAS often hits YouTube SSL timeouts.
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public async Task SyncFromThemerrAsync(MediaItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.TmdbId))
        {
            ClearTheme(item);
            return;
        }

        string? youtubeUrl;
        try
        {
            youtubeUrl = await themerr.GetYoutubeThemeUrlAsync(item.TmdbId, item.Kind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ThemerrDB lookup failed for item {ItemId}", item.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(youtubeUrl))
        {
            ClearTheme(item);
            return;
        }

        // Persist Themerr mapping even if the MP3 download fails (geo/SSL); retry while file missing.
        item.SetThemeYoutubeUrl(youtubeUrl);

        if (store.Exists(item.Id))
            return;

        await DownloadGate.WaitAsync(ct);
        try
        {
            if (store.Exists(item.Id))
                return;

            await DownloadAndCacheAsync(item.Id, youtubeUrl, ct);
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private void ClearTheme(MediaItem item)
    {
        if (item.ThemeYoutubeUrl is not null || store.Exists(item.Id))
        {
            store.Delete(item.Id);
            item.SetThemeYoutubeUrl(null);
        }
    }

    private async Task<bool> DownloadAndCacheAsync(Guid itemId, string youtubeUrl, CancellationToken ct)
    {
        var videoId = ExtractVideoId(youtubeUrl);
        if (videoId is null)
        {
            logger.LogDebug("Invalid Themerr YouTube URL for {ItemId}: {Url}", itemId, youtubeUrl);
            return false;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "lumenmedia-theme", itemId.ToString("N"));
        Directory.CreateDirectory(workDir);
        var watchUrl = $"https://www.youtube.com/watch?v={videoId}";

        try
        {
            var mp3Path = await RunYtDlpAsync(watchUrl, workDir, ct);
            if (mp3Path is null)
                return false;

            var info = new FileInfo(mp3Path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxMp3Bytes)
            {
                logger.LogWarning(
                    "Theme mp3 size invalid for {ItemId} ({Bytes} bytes)",
                    itemId, info.Exists ? info.Length : 0);
                return false;
            }

            await using (var mp3 = new FileStream(mp3Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                await store.SaveAsync(itemId, mp3, ct);
            }

            logger.LogInformation("Cached theme song for item {ItemId} from {Url}", itemId, youtubeUrl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to cache theme for item {ItemId} from {Url}", itemId, youtubeUrl);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<string?> RunYtDlpAsync(string watchUrl, string workDir, CancellationToken ct)
    {
        var outputTemplate = Path.Combine(workDir, "theme.%(ext)s");
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            ArgumentList =
            {
                "--no-playlist",
                "--no-progress",
                "--quiet",
                // Deno is in PATH (Dockerfile); yt-dlp enables it by default for YouTube n-sig.
                "--force-ipv4",
                "--socket-timeout", "30",
                "--retries", "3",
                "--fragment-retries", "3",
                // Prefer clients that avoid brittle web JS paths when possible.
                "--extractor-args", "youtube:player_client=android_vr,web",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "5",
                "--max-filesize", "20M",
                "-o", outputTemplate,
                watchUrl,
            },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            logger.LogWarning("Failed to start yt-dlp");
            return null;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "yt-dlp exit {Code} for {Url}: {Stderr}",
                process.ExitCode, watchUrl, Truncate(stderr, 400));
            return null;
        }

        var mp3 = Path.Combine(workDir, "theme.mp3");
        return File.Exists(mp3) ? mp3 : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>Accepts watch?v=, youtu.be/, and shorts URLs.</summary>
    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            return YoutubeIdRegex().IsMatch(id) ? id : null;
        }

        if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var v = GetQueryValue(uri.Query, "v");
            if (!string.IsNullOrWhiteSpace(v) && YoutubeIdRegex().IsMatch(v))
                return v;

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && (parts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
                && YoutubeIdRegex().IsMatch(parts[1]))
            {
                return parts[1];
            }
        }

        return null;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var span = query.AsSpan().TrimStart('?');
        while (!span.IsEmpty)
        {
            var amp = span.IndexOf('&');
            var pair = amp >= 0 ? span[..amp] : span;
            span = amp >= 0 ? span[(amp + 1)..] : [];

            var eq = pair.IndexOf('=');
            var name = eq >= 0 ? pair[..eq] : pair;
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = eq >= 0 ? pair[(eq + 1)..] : [];
            return Uri.UnescapeDataString(value.ToString());
        }

        return null;
    }

    private const long MaxMp3Bytes = 20L * 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YoutubeIdRegex();
}
