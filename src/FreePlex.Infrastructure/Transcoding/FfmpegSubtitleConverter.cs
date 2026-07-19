using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FreePlex.Application.Abstractions;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;
using Microsoft.Extensions.Logging;

namespace FreePlex.Infrastructure.Transcoding;

/// <summary>
/// Converts text subtitles (SRT/ASS/VTT, external or embedded) to WebVTT via ffmpeg,
/// with a pure C# SRT fallback when ffmpeg is unavailable.
/// </summary>
public sealed class FfmpegSubtitleConverter(ILogger<FfmpegSubtitleConverter> logger) : ISubtitleConverter
{
    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "dvdsub", "vobsub", "xsub",
    };

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

            var fromFile = await RunFfmpegToWebVttAsync(["-i", path], ct);
            if (fromFile is not null)
                return fromFile;

            if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                return SrtToWebVtt(await File.ReadAllTextAsync(path, ct));

            return null;
        }

        if (stream.StreamIndex < 0 || !File.Exists(source.Path))
            return null;

        return await RunFfmpegToWebVttAsync(
            ["-i", source.Path, "-map", $"0:{stream.StreamIndex}"],
            ct);
    }

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
