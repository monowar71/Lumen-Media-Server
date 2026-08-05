using System.Diagnostics;
using LumenMedia.Application.Abstractions;
using LumenMedia.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumenMedia.Infrastructure.Torrents;

/// <summary>
/// Starts TorrServer only while torrent playback leases are held; stops after idle grace.
/// </summary>
public sealed class TorrServerProcessManager : ITorrServerProcess, IAsyncDisposable
{
    private readonly TorrServerOptions _opts;
    private readonly PathsOptions _paths;
    private readonly ILogger<TorrServerProcessManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _leaseLock = new();
    private Process? _process;
    private int _leases;
    private CancellationTokenSource? _idleCts;

    public TorrServerProcessManager(
        IOptions<TorrServerOptions> opts,
        IOptions<PathsOptions> paths,
        ILogger<TorrServerProcessManager> logger)
    {
        _opts = opts.Value;
        _paths = paths.Value;
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    public void AcquireLease()
    {
        lock (_leaseLock)
        {
            _leases++;
            _idleCts?.Cancel();
            _idleCts = null;
        }
    }

    public void ReleaseLease()
    {
        lock (_leaseLock)
        {
            if (_leases > 0)
                _leases--;
            if (_leases == 0)
                ScheduleIdleShutdown();
        }
    }

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
            throw new InvalidOperationException("TorrServer integration is disabled.");

        if (!_opts.ManageProcess)
            return; // External BaseUrl assumed ready.

        await _gate.WaitAsync(ct);
        try
        {
            if (IsRunning)
                return;

            var dataDir = ResolveDataDir();
            Directory.CreateDirectory(dataDir);

            if (!File.Exists(_opts.BinaryPath))
                throw new FileNotFoundException($"TorrServer binary not found at '{_opts.BinaryPath}'.");

            var start = new ProcessStartInfo
            {
                FileName = _opts.BinaryPath,
                Arguments = $"-p {_opts.Port} -i 127.0.0.1 -d \"{dataDir}\" -k",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _logger.LogInformation("Starting TorrServer on 127.0.0.1:{Port} data={DataDir}", _opts.Port, dataDir);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("TorrServer: {Line}", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("TorrServer: {Line}", e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start TorrServer process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            await WaitUntilReadyAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await KillProcessAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _idleCts?.Cancel();
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }

    private void ScheduleIdleShutdown()
    {
        _idleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _idleCts = cts;
        var delay = TimeSpan.FromSeconds(Math.Max(5, _opts.IdleShutdownSeconds));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                lock (_leaseLock)
                {
                    if (_leases > 0)
                        return;
                }

                _logger.LogInformation("TorrServer idle for {Seconds}s — stopping", _opts.IdleShutdownSeconds);
                await StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // New lease or dispose cancelled idle timer.
            }
        }, CancellationToken.None);
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _opts.StartTimeoutSeconds)));
        var url = $"{_opts.ResolveBaseUrl()}/echo";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        while (!timeout.IsCancellationRequested)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"TorrServer exited early with code {_process.ExitCode}.");

            try
            {
                var resp = await http.GetAsync(url, timeout.Token);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // retry
            }

            await Task.Delay(200, timeout.Token);
        }

        await KillProcessAsync();
        throw new TimeoutException("TorrServer did not become ready in time.");
    }

    private async Task KillProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping TorrServer");
        }
        finally
        {
            process.Dispose();
        }
    }

    private string ResolveDataDir()
    {
        if (Path.IsPathRooted(_opts.DataPath))
            return _opts.DataPath;
        return Path.Combine(_paths.Config, _opts.DataPath);
    }
}
