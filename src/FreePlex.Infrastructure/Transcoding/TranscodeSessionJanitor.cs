using FreePlex.Application.Abstractions;
using FreePlex.Application.Playback;
using FreePlex.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FreePlex.Infrastructure.Transcoding;

/// <summary>
/// Periodically stops idle/expired playback sessions and deletes orphaned transcode dirs.
/// Also enqueues a <c>CleanupTranscodes</c> job for observability when work is done.
/// </summary>
public sealed class TranscodeSessionJanitor(
    IPlaybackSessionStore sessions,
    ITranscoder transcoder,
    IOptions<PlaybackOptions> playbackOptions,
    IOptions<PathsOptions> pathsOptions,
    TimeProvider clock,
    ILogger<TranscodeSessionJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Transcode janitor sweep failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var idle = TimeSpan.FromSeconds(Math.Max(30, playbackOptions.Value.IdleTimeoutSec));
        var stopped = 0;

        foreach (var session in sessions.ActiveSessions.ToArray())
        {
            var expired = session.ExpiresAt <= now;
            var idleTooLong = now - session.LastAccess > idle;
            if (!expired && !idleTooLong)
                continue;

            logger.LogInformation(
                "Cleaning idle/expired playback session {SessionId} (expired={Expired}, idle={Idle})",
                session.SessionId,
                expired,
                idleTooLong);
            await transcoder.StopAsync(session.SessionId, ct);
            sessions.Remove(session.SessionId);
            stopped++;
        }

        // Orphan dirs left after crash / process exit without StopAsync.
        var root = pathsOptions.Value.Transcodes;
        if (Directory.Exists(root))
        {
            var active = sessions.ActiveSessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name is "bench" || active.Contains(name))
                    continue;
                // Only remove sess-* dirs that look abandoned.
                if (!name.StartsWith("sess-", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    stopped++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to delete orphan transcode dir {Dir}", dir);
                }
            }
        }

        if (stopped > 0)
            logger.LogInformation("Transcode janitor cleaned {Count} session(s)/dir(s)", stopped);
    }
}
