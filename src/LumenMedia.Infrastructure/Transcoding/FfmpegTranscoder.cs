using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LumenMedia.Application.Abstractions;
using LumenMedia.Application.Playback;
using LumenMedia.Domain.Enums;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Transcoding;

/// <summary>
/// Launches ffmpeg as a child process (argument array only — never a shell string) and
/// writes HLS fMP4 into the session's transcode directory. See docs/transcoding.md.
/// </summary>
public sealed class FfmpegTranscoder(
    IOptions<PlaybackOptions> playbackOptions,
    IOptions<PathsOptions> pathsOptions,
    ISettingsStore settingsStore,
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

        var opts = ResolvePlaybackOptions();
        var args = FfmpegArgumentBuilder.Build(request, outputDir, opts);
        logger.LogInformation(
            "ffmpeg start {SessionId} method={Method} quality={Quality} posMs={PosMs} hw={Hw} tonemap={ToneMap}",
            request.Session.SessionId,
            request.Session.Method,
            request.QualityId,
            request.StartPositionMs,
            opts.HardwareAccel,
            request.ToneMap);
        logger.LogDebug(
            "ffmpeg argv {SessionId}: {Args}",
            request.Session.SessionId,
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

        var running = new RunningSession(process, outputDir, opts);
        _sessions[request.Session.SessionId] = running;

        _ = DrainAsync(process.StandardError, request.Session.SessionId, "stderr", running, running.Cts.Token);
        _ = DrainAsync(process.StandardOutput, request.Session.SessionId, "stdout", running, running.Cts.Token);
        _ = WatchExitAsync(request.Session.SessionId, running);
        if (opts.Throttle)
            _ = ThrottleLoopAsync(request.Session.SessionId, running);
    }

    /// <summary>Merges live admin settings (tonemap method, etc.) onto config-bound options.</summary>
    private PlaybackOptions ResolvePlaybackOptions()
    {
        var baseOpts = playbackOptions.Value;
        var live = settingsStore.Get().Transcoding;
        var method = string.IsNullOrWhiteSpace(live.HdrToneMapMethod)
            ? baseOpts.HdrToneMapMethod
            : live.HdrToneMapMethod;
        if (string.Equals(method, baseOpts.HdrToneMapMethod, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(live.HardwareAccel))
            return baseOpts;

        return new PlaybackOptions
        {
            HardwareAccel = string.IsNullOrWhiteSpace(live.HardwareAccel) ? baseOpts.HardwareAccel : live.HardwareAccel,
            VaapiDevice = baseOpts.VaapiDevice,
            MaxConcurrentSessions = live.MaxConcurrentSessions > 0 ? live.MaxConcurrentSessions : baseOpts.MaxConcurrentSessions,
            SegmentDurationSec = live.SegmentDurationSec > 0 ? live.SegmentDurationSec : baseOpts.SegmentDurationSec,
            InitialSegmentDurationSec = baseOpts.InitialSegmentDurationSec,
            AbrEnabled = live.AbrEnabled,
            DefaultRemoteCapKbps = live.DefaultRemoteCapKbps > 0 ? live.DefaultRemoteCapKbps : baseOpts.DefaultRemoteCapKbps,
            Throttle = baseOpts.Throttle,
            MaxAheadSegments = baseOpts.MaxAheadSegments,
            IdleTimeoutSec = baseOpts.IdleTimeoutSec,
            HdrToneMapMethod = method,
            Ladder = live.Ladder.Count > 0
                ? live.Ladder.Select(r => new LadderRung { Id = r.Id, Height = r.Height, VideoBitrateKbps = r.VideoBitrateKbps }).ToList()
                : baseOpts.Ladder,
        };
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

        logger.LogInformation("ffmpeg stop {SessionId}", sessionId);
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

    public void NotifyPlaybackActive(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var running))
            return;
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
                // Do not pause until the player has pulled at least one media segment.
                // Otherwise a cold start (playlist/init only) or a starved client leaves
                // ffmpeg SIGSTOP'd forever while the UI spins on buffering.
                if (requested < 0)
                {
                    TryResume(running);
                    continue;
                }

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
            var code = running.Process.ExitCode;
            if (code == 0)
            {
                logger.LogInformation("ffmpeg exit {SessionId} code={Code}", sessionId, code);
            }
            else
            {
                var stderr = running.StderrTail.ToString().Trim();
                if (stderr.Length > 0)
                {
                    logger.LogWarning(
                        "ffmpeg exit {SessionId} code={Code} stderr={Stderr}",
                        sessionId,
                        code,
                        TruncateForLog(stderr, 1500));
                }
                else
                {
                    logger.LogWarning("ffmpeg exit {SessionId} code={Code}", sessionId, code);
                }
            }
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

    private async Task DrainAsync(
        StreamReader reader,
        string sessionId,
        string stream,
        RunningSession running,
        CancellationToken ct)
    {
        var buffer = stream == "stderr" ? running.StderrTail : new StringBuilder();
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

        if (stream != "stderr" && buffer.Length > 0)
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
            "1080p-high" => 20_000_000,
            "1440p" => 16_000_000,
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

    private static string TruncateForLog(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "…";

    private sealed class RunningSession(Process process, string outputDir, PlaybackOptions opts)
    {
        public Process Process { get; } = process;
        public string OutputDir { get; } = outputDir;
        public CancellationTokenSource Cts { get; } = new();
        public int MaxAheadSegments { get; } = Math.Max(3, opts.MaxAheadSegments);
        /// <summary>-1 until the player requests <c>segmentN.m4s</c> (see throttle loop).</summary>
        public int LastRequestedSegment = -1;
        public bool Paused;
        public StringBuilder StderrTail { get; } = new();
    }
}

/// <summary>Pure ffmpeg argv builder (array form) — unit-tested, no process I/O.</summary>
public static class FfmpegArgumentBuilder
{
    private static readonly HashSet<string> ToneMapMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "hable", "mobius", "reinhard", "bt2390",
    };

    public static IReadOnlyList<string> Build(TranscodeRequest request, string outputDir, PlaybackOptions opts)
    {
        var method = request.Session.Method;
        var qualityId = request.QualityId;
        var rung = opts.EffectiveLadder.FirstOrDefault(r => r.Id == qualityId);
        var isOpenEndedQuality = qualityId.Equals("auto", StringComparison.OrdinalIgnoreCase)
                                 || qualityId.Equals("original", StringComparison.OrdinalIgnoreCase);
        var toneMap = request.ToneMap
                      || ContainsReason(request.Reason, "HdrNotSupported")
                      || ContainsReason(request.Reason, "ForceHdrToSdr");

        // Audio-only remux (-c:v copy) follows source keyframes. BluRay-style ~10s GOPs
        // produce ~20–30 MB HLS segments that stall players (SegmentWait is short, ABR
        // cannot recover). Always re-encode video so segments stay near SegmentDurationSec.
        var encodeVideo = method == PlaybackMethod.Transcode && (
            rung is not null
            || isOpenEndedQuality
            || toneMap
            || ContainsReason(request.Reason, "VideoCodecNotSupported")
            || ContainsReason(request.Reason, "HdrNotSupported")
            || ContainsReason(request.Reason, "ForceHdrToSdr")
            || ContainsReason(request.Reason, "ResolutionTooHigh")
            || ContainsReason(request.Reason, "BitrateTooHigh")
            || ContainsReason(request.Reason, "NoVideoStream")
            || ContainsReason(request.Reason, "SubtitleBurnIn")
            || ContainsReason(request.Reason, "ManualQuality")
            || ContainsReason(request.Reason, "AudioCodecNotSupported")
            || ContainsReason(request.Reason, "AudioDownmix"));

        // Browser MSE is unreliable with AC3/EAC3 in fMP4; always AAC on Transcode.
        // DirectStream keeps audio copy (codecs already accepted by the profile).
        var encodeAudio = method != PlaybackMethod.DirectStream;
        var profileMax = request.MaxOutputHeight
                         ?? ResolutionLimits.ParseMaxHeight(request.Session.Profile.MaxResolution);
        var scaleHeight = ResolveScaleHeight(rung, request.SourceHeight, profileMax);
        // Ladder rungs always get an explicit scale (even when clamped to source height).
        // auto/original only scale when the profile max is below the source.
        var needsScale = scaleHeight is int sh && sh > 0 && (
            rung is not null
            || request.SourceHeight is null
            || sh < request.SourceHeight.Value);
        var videoBitrateKbps = ResolveVideoBitrateKbps(rung, scaleHeight, opts);
        var keyint = Math.Max(24, opts.SegmentDurationSec * 30);

        // VAAPI path: decode + scale/tonemap_vaapi + optional overlay_vaapi (bitmap burn-in) + encode.
        // Software fallback when HardwareAccel is not vaapi (zscale/tonemap + overlay).
        int? burnInIndex = request.SubtitleBurnInIndex is int idx && idx >= 0 ? idx : null;
        var burnIn = burnInIndex is not null;
        if (burnIn || toneMap)
            encodeVideo = true;

        // Session/request method wins over admin default. Software algorithms force the
        // CPU path even when HardwareAccel=vaapi so the player menu can A/B methods live.
        var requestedToneMethod = toneMap
            ? HdrToneMapMethods.Resolve(
                request.HdrToneMapMethod,
                opts.HardwareAccel,
                opts.HdrToneMapMethod)
            : request.HdrToneMapMethod;
        var toneMapOnVaapi = !toneMap
                            || HdrToneMapMethods.UsesVaapi(requestedToneMethod, opts.HardwareAccel);
        var useVaapi = encodeVideo
                       && opts.HardwareAccel.Equals("vaapi", StringComparison.OrdinalIgnoreCase)
                       && toneMapOnVaapi;

        var layoutId = string.IsNullOrWhiteSpace(request.AudioLayout)
            ? AudioLayouts.DefaultEncode
            : request.AudioLayout;
        if (!AudioLayouts.IsKnown(layoutId))
            layoutId = AudioLayouts.DefaultEncode;

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
            // Named device shared by hwaccel decode and scale_vaapi / h264_vaapi.
            args.Add("-init_hw_device");
            args.Add($"vaapi=va:{device}");
            args.Add("-filter_hw_device");
            args.Add("va");
            // Decode on the GPU — without this, ffmpeg does software decode then hwupload,
            // which saturates CPU on 4K HEVC Main10 / HDR and falls below realtime.
            args.Add("-hwaccel");
            args.Add("vaapi");
            args.Add("-hwaccel_device");
            args.Add("va");
            args.Add("-hwaccel_output_format");
            args.Add("vaapi");
        }

        if (request.StartPositionMs > 0)
        {
            args.Add("-ss");
            args.Add((request.StartPositionMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(request.Session.SourcePath);

        var toneMapMethod = toneMap && HdrToneMapMethods.IsSoftware(requestedToneMethod)
            ? HdrToneMapMethods.NormalizeSoftwareAlgorithm(requestedToneMethod)
            : NormalizeToneMapMethod(opts.HdrToneMapMethod);
        var softwareFilter = BuildSoftwareVideoFilter(toneMap, toneMapMethod, needsScale ? scaleHeight : null);
        var vaapiFilter = BuildVaapiVideoFilter(toneMap, needsScale ? scaleHeight : null);

        if (burnInIndex is int burnIdx)
        {
            args.Add("-filter_complex");
            args.Add(useVaapi
                ? BuildVaapiBurnInFilterComplex(vaapiFilter, burnIdx)
                : BuildSoftwareBurnInFilterComplex(softwareFilter, burnIdx));
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
            // SelectVideoEncoder returns h264_vaapi for vaapi accel even when we fell back —
            // that encoder needs an initialized hw device.
            if (!useVaapi && encoder == "h264_vaapi")
                encoder = "libx264";
            args.Add("-c:v");
            args.Add(encoder);

            // Short GOPs so HLS segments stay near SegmentDurationSec (copy path cannot do this).
            args.Add("-g");
            args.Add(keyint.ToString(CultureInfo.InvariantCulture));
            args.Add("-keyint_min");
            args.Add(keyint.ToString(CultureInfo.InvariantCulture));

            if (useVaapi)
            {
                // Burn-in already applied filters via filter_complex; otherwise stay on-GPU with -vf.
                if (!burnIn)
                {
                    args.Add("-vf");
                    args.Add(vaapiFilter);
                }

                args.Add("-b:v");
                args.Add($"{videoBitrateKbps}k");
                args.Add("-maxrate");
                args.Add($"{videoBitrateKbps}k");
                args.Add("-bufsize");
                args.Add($"{videoBitrateKbps * 2}k");
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
                // Burn-in already applied filters via filter_complex.
                if (!burnIn && !string.IsNullOrEmpty(softwareFilter))
                {
                    args.Add("-vf");
                    args.Add(softwareFilter);
                }
                else if (!burnIn && needsScale)
                {
                    args.Add("-vf");
                    args.Add($"scale=-2:{scaleHeight}");
                }

                args.Add("-b:v");
                args.Add($"{videoBitrateKbps}k");
                args.Add("-maxrate");
                args.Add($"{videoBitrateKbps}k");
                args.Add("-bufsize");
                args.Add($"{videoBitrateKbps * 2}k");
            }
        }
        else
        {
            args.Add("-c:v");
            args.Add("copy");
        }

        if (encodeAudio)
        {
            var channels = AudioLayouts.ChannelCount(layoutId);
            if (channels <= 0)
                channels = 2;
            var bitrate = AudioLayouts.AacBitrateKbps(layoutId);
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-aac_coder");
            args.Add("fast");
            args.Add("-ac");
            args.Add(channels.ToString(CultureInfo.InvariantCulture));
            if (!layoutId.Equals(AudioLayouts.Stereo, StringComparison.OrdinalIgnoreCase)
                && !layoutId.Equals(AudioLayouts.Mono, StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-channel_layout");
                args.Add(layoutId);
            }

            args.Add("-b:a");
            args.Add($"{bitrate}k");
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

        // Normalize timestamps after -ss so MSE/hls.js do not stall on negative/gap CTS.
        args.Add("-avoid_negative_ts");
        args.Add("make_zero");
        args.Add("-start_at_zero");

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

    internal static string NormalizeToneMapMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method) || !ToneMapMethods.Contains(method))
            return "hable";
        return method.ToLowerInvariant();
    }

    /// <summary>
    /// On-GPU VAAPI filter: optional downscale, optional HDR→SDR via <c>tonemap_vaapi</c>, nv12 out.
    /// </summary>
    internal static string BuildVaapiVideoFilter(bool toneMap, int? scaleHeight)
    {
        var parts = new List<string>();
        // Scale before tonemap so VPP works at the output resolution.
        if (scaleHeight is int h and > 0)
        {
            parts.Add(toneMap
                ? $"scale_vaapi=w=-2:h={h.ToString(CultureInfo.InvariantCulture)}"
                : $"scale_vaapi=-2:{h.ToString(CultureInfo.InvariantCulture)}:format=nv12");
        }
        else if (!toneMap)
        {
            parts.Add("scale_vaapi=format=nv12");
        }

        if (toneMap)
            parts.Add("tonemap_vaapi=format=nv12:p=bt709:t=bt709:m=bt709");

        return string.Join(',', parts);
    }

    /// <summary>
    /// Bitmap subtitle burn-in on VAAPI: base chain → upload PGS/VobSub as BGRA → <c>overlay_vaapi</c>.
    /// Overlay is scaled to the main frame so 4K subs on a 1080p base do not fail VPP.
    /// </summary>
    internal static string BuildVaapiBurnInFilterComplex(string vaapiBaseFilter, int burnInStreamIndex)
    {
        var basePart = string.IsNullOrEmpty(vaapiBaseFilter) ? "" : vaapiBaseFilter;
        return $"[0:v:0]{basePart}[base];[0:{burnInStreamIndex.ToString(CultureInfo.InvariantCulture)}]format=bgra,hwupload[sub];[base][sub]overlay_vaapi=w=main_w:h=main_h[v]";
    }

    /// <summary>Software burn-in: optional scale/tonemap then <c>overlay</c>.</summary>
    internal static string BuildSoftwareBurnInFilterComplex(string softwareFilter, int burnInStreamIndex)
    {
        var idx = burnInStreamIndex.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(softwareFilter))
            return $"[0:v:0][0:{idx}]overlay[v]";
        return $"[0:v:0]{softwareFilter}[tm];[tm][0:{idx}]overlay[v]";
    }

    /// <summary>
    /// Software HDR→SDR (+ optional scale) filter chain. Empty when neither is needed.
    /// Used when VAAPI is unavailable. Scales inside the first zscale so float tonemap
    /// does not run at full 4K.
    /// </summary>
    internal static string BuildSoftwareVideoFilter(bool toneMap, string toneMapMethod, int? scaleHeight)
    {
        var parts = new List<string>();
        if (toneMap)
        {
            // zscale linear → tonemap → bt709; jellyfin-ffmpeg includes zimg.
            parts.Add(scaleHeight is int h and > 0
                ? $"zscale=w=-2:h={h.ToString(CultureInfo.InvariantCulture)}:t=linear:npl=100"
                : "zscale=t=linear:npl=100");
            parts.Add("format=gbrpf32le");
            parts.Add("zscale=p=bt709");
            parts.Add($"tonemap={toneMapMethod}:desat=0");
            parts.Add("zscale=t=bt709:m=bt709:r=tv");
            parts.Add("format=yuv420p");
        }
        else if (scaleHeight is int h and > 0)
        {
            parts.Add($"scale=-2:{h.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(',', parts);
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

    /// <summary>
    /// Ladder rung height, else profile max, else source — never upscale.
    /// </summary>
    private static int? ResolveScaleHeight(LadderRung? rung, int? sourceHeight, int? profileMaxHeight)
    {
        int? target = rung?.Height;
        if (target is null && profileMaxHeight is int ph && ph > 0)
            target = ph;
        if (target is null)
            return sourceHeight;
        if (sourceHeight is int sh && sh > 0)
            return Math.Min(target.Value, sh);
        return target;
    }

    private static int ResolveVideoBitrateKbps(LadderRung? rung, int? scaleHeight, PlaybackOptions opts)
    {
        if (rung is { VideoBitrateKbps: > 0 })
            return rung.VideoBitrateKbps;

        if (scaleHeight is int h && h > 0)
        {
            // Prefer the cheapest rung at the target height (1080p over 1080p-high for auto/original).
            var match = opts.EffectiveLadder
                .Where(r => r.Height <= h)
                .OrderByDescending(r => r.Height)
                .ThenBy(r => r.VideoBitrateKbps)
                .FirstOrDefault();
            if (match is not null)
                return match.VideoBitrateKbps;
        }

        return 4000;
    }
}
