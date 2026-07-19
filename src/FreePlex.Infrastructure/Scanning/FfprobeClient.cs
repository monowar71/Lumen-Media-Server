using System.Diagnostics;
using System.Text.Json;
using FreePlex.Domain.Enums;
using FreePlex.Domain.Media;
using Microsoft.Extensions.Logging;

namespace FreePlex.Infrastructure.Scanning;

public sealed record ProbeResult(long? DurationMs, int? OverallBitrateKbps, IReadOnlyList<MediaStream> Streams);

/// <summary>
/// Thin wrapper over the <c>ffprobe</c> binary. Arguments are passed as an array
/// (never a shell string) to avoid command injection via file names. When the binary
/// is absent the probe degrades gracefully to <c>null</c> instead of throwing.
/// </summary>
public sealed class FfprobeClient(ILogger<FfprobeClient> logger)
{
    private bool _unavailable;

    public async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct)
    {
        if (_unavailable)
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-print_format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-show_format");
            psi.ArgumentList.Add("-show_streams");
            psi.ArgumentList.Add(path);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return null;

            return Parse(json);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ffprobe binary not found — remember and stop trying for this run.
            _unavailable = true;
            logger.LogWarning("ffprobe binary not found; media stream metadata will be minimal.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ffprobe failed for {Path}", path);
            return null;
        }
    }

    private static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<MediaStream>();

        long? durationMs = null;
        int? overallBitrate = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), out var seconds))
                durationMs = (long)(seconds * 1000);
            if (format.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bits))
                overallBitrate = (int)(bits / 1000);
        }

        if (root.TryGetProperty("streams", out var streamArray))
        {
            var index = 0;
            foreach (var s in streamArray.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                var kind = type switch
                {
                    "video" => StreamKind.Video,
                    "audio" => StreamKind.Audio,
                    "subtitle" => StreamKind.Subtitle,
                    _ => (StreamKind?)null,
                };
                if (kind is null)
                {
                    index++;
                    continue;
                }

                var stream = new MediaStream(kind.Value, s.TryGetProperty("index", out var idx) ? idx.GetInt32() : index)
                {
                    Codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null,
                    Profile = s.TryGetProperty("profile", out var p) ? p.GetString() : null,
                    Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : null,
                    Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : null,
                    Channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : null,
                };

                if (kind == StreamKind.Subtitle)
                    stream.SubtitleFormat = stream.Codec;

                if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang))
                    stream.Language = lang.GetString();

                streams.Add(stream);
                index++;
            }
        }

        return new ProbeResult(durationMs, overallBitrate, streams);
    }
}
