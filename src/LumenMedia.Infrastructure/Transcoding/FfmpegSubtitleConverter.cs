using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;
using LumenMedia.Domain.Media;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Transcoding;

/// <summary>
/// Converts text subtitles (SRT/ASS/VTT, external or embedded) to WebVTT via ffmpeg,
/// with a pure C# SRT fallback when ffmpeg is unavailable. Results are cached under
/// <see cref="PathsOptions.Subtitles"/> so repeat GETs do not re-scan multi‑GB containers.
/// </summary>
public sealed class FfmpegSubtitleConverter(
    IOptions<PathsOptions> paths,
    ILogger<FfmpegSubtitleConverter> logger) : ISubtitleConverter
{
    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "dvdsub", "vobsub", "xsub",
    };

    /// <summary>One in-flight conversion per cache key — avoids N parallel ffmpeg on the same MKV.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<string?> ToWebVttAsync(MediaSource source, MediaStream stream, CancellationToken ct)
    {
        if (stream.Kind != StreamKind.Subtitle)
            return null;

        var codec = stream.Codec ?? stream.SubtitleFormat ?? string.Empty;
        if (BitmapCodecs.Contains(codec))
            return null;

        if (stream.IsExternal && !string.IsNullOrWhiteSpace(stream.ExternalPath))
        {
            var path = stream.ExternalPath;
            if (!File.Exists(path))
                return null;

            if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                return await File.ReadAllTextAsync(path, ct);

            var cachedExternal = await TryReadCacheAsync(source, stream, path, ct);
            if (cachedExternal is not null)
                return cachedExternal;

            var fromFile = await RunExclusiveAsync(source, stream, path, async () =>
            {
                var converted = await RunFfmpegToWebVttAsync(["-i", path], ct);
                if (converted is not null)
                    return converted;

                if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                    return SrtToWebVtt(await File.ReadAllTextAsync(path, ct));

                return null;
            }, ct);
            return fromFile;
        }

        if (stream.StreamIndex < 0 || !File.Exists(source.Path))
            return null;

        var cached = await TryReadCacheAsync(source, stream, source.Path, ct);
        if (cached is not null)
            return cached;

        return await RunExclusiveAsync(source, stream, source.Path, async () =>
            await RunFfmpegToWebVttAsync(["-i", source.Path, "-map", $"0:{stream.StreamIndex}"], ct), ct);
    }

    private async Task<string?> RunExclusiveAsync(
        MediaSource source,
        MediaStream stream,
        string inputPath,
        Func<Task<string?>> convert,
        CancellationToken ct)
    {
        var key = CacheKey(source, stream);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var again = await TryReadCacheAsync(source, stream, inputPath, ct);
            if (again is not null)
                return again;

            var vtt = await convert();
            if (vtt is not null)
                await TryWriteCacheAsync(source, stream, inputPath, vtt, ct);
            return vtt;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> TryReadCacheAsync(
        MediaSource source,
        MediaStream stream,
        string inputPath,
        CancellationToken ct)
    {
        var cachePath = CachePath(source, stream);
        if (cachePath is null || !File.Exists(cachePath))
            return null;

        try
        {
            var inputInfo = new FileInfo(inputPath);
            var cacheInfo = new FileInfo(cachePath);
            if (!inputInfo.Exists || cacheInfo.LastWriteTimeUtc < inputInfo.LastWriteTimeUtc)
                return null;

            var text = await File.ReadAllTextAsync(cachePath, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed reading subtitle cache {Path}", cachePath);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(
        MediaSource source,
        MediaStream stream,
        string inputPath,
        string vtt,
        CancellationToken ct)
    {
        var cachePath = CachePath(source, stream);
        if (cachePath is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, vtt, Encoding.UTF8, ct);
            File.Move(tmp, cachePath, overwrite: true);

            // Align cache mtime with input so a later source replace invalidates correctly.
            try
            {
                var inputMtime = File.GetLastWriteTimeUtc(inputPath);
                File.SetLastWriteTimeUtc(cachePath, inputMtime);
            }
            catch
            {
                // Best-effort; content is still valid for this generation.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed writing subtitle cache {Path}", cachePath);
        }
    }

    private string? CachePath(MediaSource source, MediaStream stream)
    {
        var root = paths.Value.Subtitles;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(paths.Value.Config, "subtitles");
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, source.Id.ToString("N"), $"{stream.Id:N}.vtt"));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string CacheKey(MediaSource source, MediaStream stream) =>
        $"{source.Id:N}:{stream.Id:N}";

    private async Task<string?> RunFfmpegToWebVttAsync(IReadOnlyList<string> inputArgs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in inputArgs)
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("webvtt");
            psi.ArgumentList.Add("pipe:1");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            _ = await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            return stdout.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                ? stdout
                : "WEBVTT\n\n" + stdout;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "ffmpeg subtitle conversion failed");
            return null;
        }
    }

    /// <summary>Minimal SRT → WebVTT for when ffmpeg is missing.</summary>
    public static string SrtToWebVtt(string srt)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        var blocks = Regex.Split(srt.Replace("\r\n", "\n"), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
                continue;

            var start = 0;
            if (Regex.IsMatch(lines[0], @"^\d+$"))
                start = 1;
            if (start >= lines.Length)
                continue;

            var timing = lines[start].Replace(',', '.');
            if (!timing.Contains("-->", StringComparison.Ordinal))
                continue;

            sb.AppendLine(timing);
            for (var i = start + 1; i < lines.Length; i++)
                sb.AppendLine(lines[i]);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
