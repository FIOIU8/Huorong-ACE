using HuorongAce.Core.Configuration;
using HuorongAce.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HuorongAce.Core.Monitoring;

/// <summary>
/// Polls the Huorong quarantine database and raises an event per new threat.
/// </summary>
/// <remarks>
/// C# counterpart of the Go <c>Monitor</c>. The Go version relied on a
/// goroutine plus a <c>stop chan struct{}</c>; here the same shape is expressed
/// with a long-running <see cref="Task"/> driven by a
/// <see cref="CancellationToken"/>, which also removes the need for the manual
/// "running" flag. Callbacks become a C# <c>event</c>, so multiple listeners
/// (red screen overlay, notification, status bar) can subscribe independently.
/// </remarks>
public sealed class ThreatMonitor : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly IAppLog _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private HuorongQuarantineReader? _reader;
    private CancellationTokenSource? _stopSource;
    private Task? _worker;
    private bool _enabled;
    private bool _huorongAvailable;
    private ThreatInfo? _lastDetection;
    private DateTimeOffset? _lastDetectionTime;

    public ThreatMonitor(AppConfig config, IAppLog? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? new NullAppLog();
        _enabled = config.MonitorEnabled;
    }

    /// <summary>Raised when a new threat is detected (or simulated).</summary>
    public event EventHandler<ThreatInfo>? ThreatDetected;

    /// <summary>Raised when availability or enablement changes, so the UI can refresh.</summary>
    public event EventHandler? StateChanged;

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _enabled;
            }
        }
    }

    /// <summary>True when the Huorong database was opened successfully.</summary>
    public bool IsHuorongAvailable
    {
        get
        {
            lock (_sync)
            {
                return _huorongAvailable;
            }
        }
    }

    public (ThreatInfo? Info, DateTimeOffset? Time) LastDetection
    {
        get
        {
            lock (_sync)
            {
                return (_lastDetection, _lastDetectionTime);
            }
        }
    }

    /// <summary>Opens the database and starts polling.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_worker is not null)
            {
                return;
            }

            _stopSource = new CancellationTokenSource();
            _worker = Task.Run(() => PollLoopAsync(_stopSource.Token));
        }

        // Opening outside the lock keeps Start() from blocking on disk I/O.
        _ = Task.Run(() =>
        {
            TryOpen(_config.LogPath);
            RaiseStateChanged();
        });
    }

    private void TryOpen(string path)
    {
        var reader = new HuorongQuarantineReader(path, _config, _log);
        try
        {
            reader.Open();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or SqliteException)
        {
            _log.Error($"无法打开火绒日志，仅启用测试模式: {ex.Message}", ex);
            lock (_sync)
            {
                _huorongAvailable = false;
            }

            return;
        }

        lock (_sync)
        {
            _reader = reader;
            _huorongAvailable = true;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_config.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task PollOnceAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        HuorongQuarantineReader? reader;
        lock (_sync)
        {
            reader = _reader;
        }

        if (reader is null)
        {
            return;
        }

        // One poll at a time: ReloadAsync swaps the reader underneath us.
        await _pollGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var detected = reader.Poll();
            if (detected.Count == 0)
            {
                return;
            }

            var latest = detected[^1];
            lock (_sync)
            {
                _lastDetection = latest;
                _lastDetectionTime = DateTimeOffset.Now;
            }

            RaiseThreatDetected(latest);
        }
        catch (Exception ex)
        {
            _log.Error($"读取日志失败: {ex.Message}", ex);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Switches to a different database, re-opening it and parking the cursors
    /// at its current end.
    /// </summary>
    /// <remarks>
    /// Used after the path changes in the settings window so the change takes
    /// effect without restarting the app.
    /// </remarks>
    public async Task<bool> ReloadAsync(string path)
    {
        var reader = new HuorongQuarantineReader(path, _config, _log);
        try
        {
            reader.Open();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or SqliteException)
        {
            _log.Error($"无法打开数据库 {path}: {ex.Message}", ex);
            return false;
        }

        await _pollGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _reader = reader;
                _huorongAvailable = true;
            }

            _log.Info($"已切换到日志库: {path}");
            return true;
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
        }

        RaiseStateChanged();
    }

    /// <summary>Fires a synthetic detection, used by the "测试红屏" button.</summary>
    public void Simulate(string name)
    {
        RaiseThreatDetected(ThreatInfo.Simulated(name));
    }

    private void RaiseThreatDetected(ThreatInfo info) =>
        Volatile.Read(ref ThreatDetected)?.Invoke(this, info);

    private void RaiseStateChanged() =>
        Volatile.Read(ref StateChanged)?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stop;
        Task? worker;
        lock (_sync)
        {
            stop = _stopSource;
            worker = _worker;
            _stopSource = null;
            _worker = null;
        }

        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            stop.Dispose();
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _pollGate.Dispose();
    }
}
