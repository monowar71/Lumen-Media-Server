using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FreePlex.Application.Abstractions;
using FreePlex.Application.Playback;
using FreePlex.Domain.Enums;
using FreePlex.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FreePlex.Infrastructure.Transcoding;

/// <summary>
/// Launches ffmpeg as a child process (argument array only — never a shell string) and
/// writes HLS fMP4 into the session's transcode directory. See docs/transcoding.md.
/// </summary>
public sealed class FfmpegTranscoder(
    IOptions<PlaybackOptions> playbackOptions,
    IOptions<PathsOptions> pathsOptions,
    ILogger<FfmpegTranscoder> logger) : ITranscoder, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunningSession> _sessions = new();

    public int ActiveSessionCount => _sessions.Count;

    public async Task StartAsync(TranscodeRequest request, CancellationToken ct)
    {
        await StopAsync(request.Session.SessionId, ct);

        var outputDir = Path.Combine(pathsOptions.Value.Transcodes, request.Session.SessionId);
        Directory.CreateDirectory(outputDir);
        WriteMasterPlaylist(outputDir, request.QualityId);

        var args = FfmpegArgumentBuilder.Build(request, outputDir, playbackOptions.Value);
        logger.LogInformation(
            "Starting ffmpeg for session {SessionId} ({Method}/{Quality}): {Args}",
            request.Session.SessionId,
            request.Session.Method,
            request.QualityId,
            string.Join(' ', args.Select(QuoteForLog)));

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start ffmpeg process.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffmpeg failed to start for session {SessionId}", request.Session.SessionId);
            throw;
        }

        // Prefer lower CPU priority so API stays responsive under load.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Not supported on all platforms / permissions.
        }

        var running = new RunningSession(process, outputDir, playbackOptions.Value);
        _sessions[request.Session.SessionId] = running;

        _ = DrainAsync(process.StandardError, request.Session.SessionId, "stderr", running.Cts.Token);
        _ = DrainAsync(process.StandardOutput, request.Session.SessionId, "stdout", running.Cts.Token);
        _ = WatchExitAsync(request.Session.SessionId, running);
        if (playbackOptions.Value.Throttle)
            _ = ThrottleLoopAsync(request.Session.SessionId, running);
    }

    public Task StopAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryRemove(sessionId, out var running))
        {
            TryDeleteDir(Path.Combine(pathsOptions.Value.Transcodes, sessionId));
            return Task.CompletedTask;
        }

        running.Cts.Cancel();
        try
        {
            if (!running.Process.HasExited)
            {
                TryResume(running); // ensure kill works if we had SIGSTOP'd
                running.Process.Kill(entireProcessTree: true);
                running.Process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop ffmpeg for session {SessionId}", sessionId);
        }
        finally
        {
            running.Process.Dispose();
            running.Cts.Dispose();
            TryDeleteDir(running.OutputDir);
        }

        return Task.CompletedTask;
    }

    public void NotifySegmentRequested(string sessionId, string segmentFileName)
    {
        if (!_sessions.TryGetValue(sessionId, out var running))
            return;

        var idx = ParseSegmentIndex(segmentFileName);
        if (idx is not null)
            Interlocked.Exchange(ref running.LastRequestedSegment, idx.Value);
        TryResume(running);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray())
            await StopAsync(id, CancellationToken.None);
    }

    private async Task ThrottleLoopAsync(string sessionId, RunningSession running)
    {
        try
        {
            while (!running.Cts.IsCancellationRequested && !running.Process.HasExited)
            {
                await Task.Delay(500, running.Cts.Token);
                var written = CountMediaSegments(running.OutputDir);
                var requested = Volatile.Read(ref running.LastRequestedSegment);
                var ahead = written - (requested + 1);
                if (ahead >= running.MaxAheadSegments)
                    TryPause(running);
                else
                    TryResume(running);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Throttle loop ended for {SessionId}", sessionId);
        }
    }

    private async Task WatchExitAsync(string sessionId, RunningSession running)
    {
        try
        {
            await running.Process.WaitForExitAsync(running.Cts.Token);
            logger.LogInformation(
                "ffmpeg exited for session {SessionId} with code {Code}",
                sessionId,
                running.Process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // StopAsync cancelled the watch.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error waiting for ffmpeg exit ({SessionId})", sessionId);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private async Task DrainAsync(StreamReader reader, string sessionId, string stream, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
                if (buffer.Length < 4000)
                    buffer.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ffmpeg {Stream} drain ended for {SessionId}", stream, sessionId);
        }

        if (buffer.Length > 0)
            logger.LogDebug("ffmpeg {Stream} ({SessionId}): {Output}", stream, sessionId, buffer.ToString());
    }

    private static void WriteMasterPlaylist(string outputDir, string qualityId)
    {
        // Single-variant master until multi-rung ABR ffmpeg fan-out lands (P3.5).
        var bandwidth = qualityId switch
        {
            "360p" => 700_000,
            "480p" => 1_500_000,
            "720p" => 4_000_000,
            "1080p" => 10_000_000,
            _ => 8_000_000,
        };
        var body =
            "#EXTM3U\n" +
            "#EXT-X-VERSION:7\n" +
            $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth}\n" +
            "index.m3u8\n";
        File.WriteAllText(Path.Combine(outputDir, "master.m3u8"), body);
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up transcode dir {Dir}", dir);
        }
    }

    private static int CountMediaSegments(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "segment*.m4s").Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int? ParseSegmentIndex(string fileName)
    {
        // segment0.m4s → 0
        if (!fileName.StartsWith("segment", StringComparison.OrdinalIgnoreCase))
            return null;
        var digits = new string(fileName.Skip("segment".Length).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    private void TryPause(RunningSession running)
    {
        if (running.Paused || running.Process.HasExited || OperatingSystem.IsWindows())
            return;
        if (SendSignal(running.Process.Id, stop: true))
        {
            running.Paused = true;
            logger.LogDebug("Throttled (paused) ffmpeg pid {Pid}", running.Process.Id);
        }
    }

    private void TryResume(RunningSession running)
    {
        if (!running.Paused || running.Process.HasExited || OperatingSystem.IsWindows())
            return;
        if (SendSignal(running.Process.Id, stop: false))
        {
            running.Paused = false;
            logger.LogDebug("Resumed ffmpeg pid {Pid}", running.Process.Id);
        }
    }

    private static bool SendSignal(int pid, bool stop)
    {
        try
        {
            // SIGSTOP=19 / SIGCONT=18 on Linux; macOS uses 17/19 — `kill -STOP/-CONT` is portable.
            var psi = new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { stop ? "-STOP" : "-CONT", pid.ToString(CultureInfo.InvariantCulture) },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(2));
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteForLog(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg;

    private sealed class RunningSession(Process process, string outputDir, PlaybackOptions opts)
    {
        public Process Process { get; } = process;
        public string OutputDir { get; } = outputDir;
        public CancellationTokenSource Cts { get; } = new();
        public int MaxAheadSegments { get; } = Math.Max(3, opts.MaxAheadSegments);
        public int LastRequestedSegment; // field for Interlocked
        public bool Paused;
    }
}

/// <summary>Pure ffmpeg argv builder (array form) — unit-tested, no process I/O.</summary>
public static class FfmpegArgumentBuilder
{
    public static IReadOnlyList<string> Build(TranscodeRequest request, string outputDir, PlaybackOptions opts)
    {
        var method = request.Session.Method;
        var qualityId = request.QualityId;
        var rung = opts.Ladder.FirstOrDefault(r => r.Id == qualityId);
        var downscale = rung is not null
                        && !qualityId.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        && !qualityId.Equals("original", StringComparison.OrdinalIgnoreCase);

        var encodeVideo = method == PlaybackMethod.Transcode && (
            downscale
            || ContainsReason(request.Reason, "VideoCodecNotSupported")
            || ContainsReason(request.Reason, "HdrNotSupported")
            || ContainsReason(request.Reason, "ResolutionTooHigh")
            || ContainsReason(request.Reason, "BitrateTooHigh")
            || ContainsReason(request.Reason, "NoVideoStream")
            || ContainsReason(request.Reason, "SubtitleBurnIn"));

        // Browser MSE is unreliable with AC3/EAC3 in fMP4; always AAC on Transcode.
        // DirectStream keeps audio copy (codecs already accepted by the profile).
        var encodeAudio = method != PlaybackMethod.DirectStream;

        // Burn-in (bitmap overlay) stays on software encode — VAAPI + overlay is fragile.
        int? burnInIndex = request.SubtitleBurnInIndex is int idx && idx >= 0 ? idx : null;
        var burnIn = burnInIndex is not null;
        if (burnIn)
            encodeVideo = true;

        var useVaapi = encodeVideo
                       && !burnIn
                       && opts.HardwareAccel.Equals("vaapi", StringComparison.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            // Bound demux probe so large MKVs do not stall before the first packet.
            "-probesize", "1000000",
            "-analyzeduration", "1000000",
        };

        if (useVaapi)
        {
            var device = string.IsNullOrWhiteSpace(opts.VaapiDevice)
                ? "/dev/dri/renderD128"
                : opts.VaapiDevice;
            args.Add("-init_hw_device");
            args.Add($"vaapi=va:{device}");
            args.Add("-filter_hw_device");
            args.Add("va");
        }

        if (request.StartPositionMs > 0)
        {
            args.Add("-ss");
            args.Add((request.StartPositionMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(request.Session.SourcePath);

        if (burnInIndex is int burnIdx)
        {
            // Bitmap overlay forces a video encode path below.
            args.Add("-filter_complex");
            args.Add($"[0:v:0][0:{burnIdx}]overlay[v]");
            args.Add("-map");
            args.Add("[v]");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v:0");
        }

        if (request.AudioStreamIndex is int audioIdx && audioIdx >= 0)
        {
            args.Add("-map");
            args.Add($"0:{audioIdx}");
        }
        else
        {
            args.Add("-map");
            args.Add("0:a:0?");
        }

        if (encodeVideo)
        {
            var encoder = useVaapi ? "h264_vaapi" : SelectVideoEncoder(opts.HardwareAccel);
            // Burn-in disables useVaapi above, but SelectVideoEncoder would still return
            // h264_vaapi — which fails without an initialized hw device. Software fallback.
            if (!useVaapi && encoder == "h264_vaapi")
                encoder = "libx264";
            args.Add("-c:v");
            args.Add(encoder);

            if (useVaapi)
            {
                // Upload to VAAPI surface; optional HW scale. Bitrate (not CRF) for vaapi.
                var vf = downscale && rung is not null
                    ? $"format=nv12,hwupload,scale_vaapi=-2:{rung.Height}"
                    : "format=nv12,hwupload";
                args.Add("-vf");
                args.Add(vf);
                var kbps = rung?.VideoBitrateKbps > 0 ? rung.VideoBitrateKbps : 8000;
                args.Add("-b:v");
                args.Add($"{kbps}k");
                args.Add("-maxrate");
                args.Add($"{kbps}k");
                args.Add("-bufsize");
                args.Add($"{kbps * 2}k");
            }
            else
            {
                args.Add("-preset");
                args.Add("veryfast");
                if (encoder == "libx264")
                {
                    args.Add("-tune");
                    args.Add("zerolatency");
                }

                args.Add("-pix_fmt");
                args.Add("yuv420p");
                if (downscale && rung is not null && !burnIn)
                {
                    args.Add("-vf");
                    args.Add($"scale=-2:{rung.Height}");
                    args.Add("-b:v");
                    args.Add($"{rung.VideoBitrateKbps}k");
                    args.Add("-maxrate");
                    args.Add($"{rung.VideoBitrateKbps}k");
                    args.Add("-bufsize");
                    args.Add($"{rung.VideoBitrateKbps * 2}k");
                }
                else if (downscale && rung is not null && burnIn)
                {
                    // scale cannot be combined with filter_complex overlay easily — bitrate-cap only.
                    args.Add("-b:v");
                    args.Add($"{rung.VideoBitrateKbps}k");
                    args.Add("-maxrate");
                    args.Add($"{rung.VideoBitrateKbps}k");
                    args.Add("-bufsize");
                    args.Add($"{rung.VideoBitrateKbps * 2}k");
                }
                else
                {
                    args.Add("-crf");
                    args.Add("20");
                }
            }
        }
        else
        {
            args.Add("-c:v");
            args.Add("copy");
        }

        if (encodeAudio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-aac_coder");
            args.Add("fast");
            args.Add("-ac");
            args.Add("2");
            args.Add("-b:a");
            args.Add("128k");
            // Flush packets promptly so hls.js can pull segments without long stalls.
            args.Add("-flush_packets");
            args.Add("1");
            args.Add("-max_delay");
            args.Add("0");
        }
        else
        {
            args.Add("-c:a");
            args.Add("copy");
        }

        var segmentSec = Math.Max(1, opts.SegmentDurationSec);
        var initSec = Math.Clamp(opts.InitialSegmentDurationSec <= 0 ? 1 : opts.InitialSegmentDurationSec, 1, segmentSec);

        args.Add("-f");
        args.Add("hls");
        args.Add("-hls_time");
        args.Add(segmentSec.ToString(CultureInfo.InvariantCulture));
        args.Add("-hls_init_time");
        args.Add(initSec.ToString(CultureInfo.InvariantCulture));
        args.Add("-hls_playlist_type");
        args.Add("event");
        // Never use split_by_time with -c:v copy: mid-GOP cuts produce fMP4 fragments that
        // Chrome MSE cannot append, and hls.js then reloads the same 1–2 segments forever.
        // Cut on keyframes only (hls_time is a target; real duration follows the GOP).
        args.Add("-hls_flags");
        args.Add("independent_segments");
        args.Add("-hls_segment_type");
        args.Add("fmp4");
        args.Add("-hls_fmp4_init_filename");
        args.Add("init.mp4");
        args.Add("-hls_segment_filename");
        args.Add(Path.Combine(outputDir, "segment%d.m4s"));
        args.Add(Path.Combine(outputDir, "index.m3u8"));
        return args;
    }

    private static string SelectVideoEncoder(string hardwareAccel) =>
        hardwareAccel.ToLowerInvariant() switch
        {
            "vaapi" => "h264_vaapi",
            "qsv" => "h264_qsv",
            "nvenc" => "h264_nvenc",
            "videotoolbox" => "h264_videotoolbox",
            _ => "libx264",
        };

    private static bool ContainsReason(string? reason, string token) =>
        !string.IsNullOrEmpty(reason)
        && reason.Contains(token, StringComparison.OrdinalIgnoreCase);
}
