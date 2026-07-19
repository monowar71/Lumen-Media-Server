namespace LumenMedia.Infrastructure.Configuration;

/// <summary>
/// Automatic library rescan when files appear under library paths.
/// Synology/Docker bind mounts often miss inotify events — reconcile is the safety net.
/// </summary>
public sealed class AutoScanOptions
{
    public const string SectionName = "LumenMedia:AutoScan";

    /// <summary>Master switch. Per-library <c>AutoScan</c> still must be true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Quiet period after the last filesystem event before enqueueing a scan.</summary>
    public int DebounceSeconds { get; set; } = 45;

    /// <summary>Periodic full scan of AutoScan libraries (0 disables).</summary>
    public int ReconcileMinutes { get; set; } = 15;

    /// <summary>Delay after process start before the first AutoScan sweep.</summary>
    public int StartupDelaySeconds { get; set; } = 20;
}
