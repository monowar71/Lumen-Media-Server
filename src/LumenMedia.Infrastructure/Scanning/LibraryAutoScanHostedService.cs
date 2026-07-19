using System.Collections.Concurrent;
using LumenMedia.Application.Libraries;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Scanning;

/// <summary>
/// Watches library roots (when <c>AutoScan</c> is on) and enqueues <see cref="LibraryService.ScanAsync"/>
/// after a quiet debounce. Also runs a periodic reconcile — Docker/Synology mounts often drop inotify.
/// </summary>
public sealed class LibraryAutoScanHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<AutoScanOptions> options,
    TimeProvider clock,
    ILogger<LibraryAutoScanHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _dirtyUntil = new();
    private readonly object _watchersGate = new();
    private List<FileSystemWatcher> _watchers = [];
    private IReadOnlyList<(Guid LibraryId, IReadOnlyList<string> Roots)> _watchedLibraries = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Library auto-scan is disabled (LumenMedia:AutoScan:Enabled=false).");
            return;
        }

        logger.LogInformation(
            "Library auto-scan enabled (debounce={Debounce}s, reconcile={Reconcile}m, startupDelay={Startup}s)",
            Math.Max(5, opts.DebounceSeconds),
            Math.Max(0, opts.ReconcileMinutes),
            Math.Max(0, opts.StartupDelaySeconds));

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, opts.StartupDelaySeconds));
        if (startupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(startupDelay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        await RefreshWatchersAsync(stoppingToken);
        await ScanAllAutoScanLibrariesAsync(reason: "startup", stoppingToken);

        var debounce = TimeSpan.FromSeconds(Math.Max(5, opts.DebounceSeconds));
        var reconcile = opts.ReconcileMinutes > 0
            ? TimeSpan.FromMinutes(opts.ReconcileMinutes)
            : Timeout.InfiniteTimeSpan;
        var nextReconcile = clock.GetUtcNow() + (reconcile == Timeout.InfiniteTimeSpan
            ? TimeSpan.FromDays(3650)
            : reconcile);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await FlushDirtyAsync(debounce, stoppingToken);

            var now = clock.GetUtcNow();
            if (reconcile != Timeout.InfiniteTimeSpan && now >= nextReconcile)
            {
                await RefreshWatchersAsync(stoppingToken);
                await ScanAllAutoScanLibrariesAsync(reason: "reconcile", stoppingToken);
                nextReconcile = now + reconcile;
            }
        }
    }

    public override void Dispose()
    {
        DisposeWatchers();
        base.Dispose();
    }

    private async Task FlushDirtyAsync(TimeSpan debounce, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var (libraryId, markedAt) in _dirtyUntil.ToArray())
        {
            if (now - markedAt < debounce)
                continue;
            if (!_dirtyUntil.TryRemove(libraryId, out _))
                continue;

            await EnqueueScanAsync(libraryId, reason: "fs-event", ct);
        }
    }

    private async Task ScanAllAutoScanLibrariesAsync(string reason, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<Application.Abstractions.IUnitOfWork>();
        var libraries = await uow.Libraries.ListAsync(ct);
        foreach (var lib in libraries.Where(l => l.AutoScan))
            await EnqueueScanAsync(lib.Id, reason, ct);
    }

    private async Task EnqueueScanAsync(Guid libraryId, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var libraries = scope.ServiceProvider.GetRequiredService<LibraryService>();
            var job = await libraries.ScanAsync(libraryId, ct);
            logger.LogInformation(
                "Auto-scan enqueued for library {LibraryId} ({Reason}), job {JobId} state={State}",
                libraryId, reason, job.Id, job.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to enqueue auto-scan for library {LibraryId} ({Reason})", libraryId, reason);
        }
    }

    private async Task RefreshWatchersAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<Application.Abstractions.IUnitOfWork>();
        var libraries = (await uow.Libraries.ListAsync(ct))
            .Where(l => l.AutoScan)
            .Select(l => (l.Id, (IReadOnlyList<string>)l.Paths.Select(p => p.Path).ToList()))
            .ToList();

        _watchedLibraries = libraries;

        var uniqueRoots = libraries
            .SelectMany(l => l.Item2)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        lock (_watchersGate)
        {
            DisposeWatchersUnlocked();
            foreach (var root in uniqueRoots)
            {
                if (!Directory.Exists(root))
                {
                    logger.LogWarning("Auto-scan root missing, skip watcher: {Root}", root);
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName
                                       | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite
                                       | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += OnFsEvent;
                    watcher.Changed += OnFsEvent;
                    watcher.Renamed += OnFsRenamed;
                    watcher.Deleted += OnFsEvent;
                    watcher.Error += OnFsError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                    logger.LogInformation("Watching library root {Root}", root);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to watch library root {Root}", root);
                }
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) =>
        MarkDirtyForPath(e.FullPath);

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        MarkDirtyForPath(e.OldFullPath);
        MarkDirtyForPath(e.FullPath);
    }

    private void OnFsError(object sender, ErrorEventArgs e) =>
        logger.LogWarning(e.GetException(), "Library FileSystemWatcher error — reconcile will catch up");

    private void MarkDirtyForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (LibraryAutoScanRules.IsIncompleteName(name))
            return;

        var isVideo = LibraryAutoScanRules.IsVideoFile(path);
        var looksLikeDirectory = !Path.HasExtension(trimmed)
                                 || path.EndsWith(Path.DirectorySeparatorChar)
                                 || path.EndsWith(Path.AltDirectorySeparatorChar);
        if (!isVideo && !looksLikeDirectory)
            return;

        var libs = LibraryAutoScanRules.LibrariesForPath(path, _watchedLibraries);
        if (libs.Count == 0)
            return;

        var now = clock.GetUtcNow();
        foreach (var id in libs)
            _dirtyUntil[id] = now;
    }

    private void DisposeWatchers()
    {
        lock (_watchersGate)
            DisposeWatchersUnlocked();
    }

    private void DisposeWatchersUnlocked()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Created -= OnFsEvent;
                w.Changed -= OnFsEvent;
                w.Renamed -= OnFsRenamed;
                w.Deleted -= OnFsEvent;
                w.Error -= OnFsError;
                w.Dispose();
            }
            catch
            {
                // ignore dispose races on shutdown
            }
        }

        _watchers = [];
    }
}
