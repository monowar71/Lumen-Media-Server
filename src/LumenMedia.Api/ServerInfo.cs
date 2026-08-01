using System.Diagnostics;

namespace LumenMedia.Api;

/// <summary>Static server identity/uptime helpers used by health and server-info endpoints.</summary>
public static class ServerInfo
{
    public const string Name = "LumenMedia";
    public const string Version = "0.1.10";

    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public static long UptimeSeconds => (long)Stopwatch.GetElapsedTime(StartTimestamp).TotalSeconds;

    public static bool FfmpegAvailable => FindOnPath("ffmpeg") is not null;

    private static string? FindOnPath(string binary)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var candidates = OperatingSystem.IsWindows() ? new[] { binary + ".exe", binary } : [binary];
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }
}
