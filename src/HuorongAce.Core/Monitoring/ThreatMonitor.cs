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
    private TaskCompletionSource _reloadsDrained = CompletedSignal();
    private int _activeReloads;

    private HuorongQuarantineReader? _reader;
    private CancellationTokenSource? _stopSource;
    private Task? _worker;
    private Task? _openTask;
    private long _readerGeneration;
    private bool _enabled;
    private bool _huorongAvailable;
    private bool _disposed;
    private ThreatInfo? _lastDetection;
    private DateTimeOffset? _lastDetectionTime;

    public ThreatMonitor(AppConfig config, IAppLog? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Normalize();
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
        CancellationToken token;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return;
            }

            _stopSource = new CancellationTokenSource();
            token = _stopSource.Token;
            _worker = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
            var generation = ++_readerGeneration;
            _openTask = Task.Run(() => OpenInitialAsync(_config.LogPath, generation, token), CancellationToken.None);
        }
    }

    private async Task OpenInitialAsync(string path, long generation, CancellationToken cancellationToken)
    {
        var reader = new HuorongQuarantineReader(path, _config, _log);
        try
        {
            reader.Open();
            cancellationToken.ThrowIfCancellationRequested();

            await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_disposed || generation != _readerGeneration)
                    {
                        return;
                    }

                    _reader = reader;
                    _huorongAvailable = true;
                }
            }
            finally
            {
                _pollGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.Error($"无法打开火绒日志，仅启用测试模式: {ex.Message}", ex);
            lock (_sync)
            {
                if (generation == _readerGeneration)
                {
                    _huorongAvailable = false;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"打开火绒日志时发生未处理错误: {ex.Message}", ex);
            lock (_sync)
            {
                if (generation == _readerGeneration)
                {
                    _huorongAvailable = false;
                }
            }
        }

        if (!IsDisposed && IsCurrentGeneration(generation))
        {
            RaiseStateChanged();
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        CancellationToken cancellationToken;
        long generation;
        lock (_sync)
        {
            if (_disposed || _stopSource is null)
            {
                return false;
            }

            if (_activeReloads++ == 0)
            {
                _reloadsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            generation = ++_readerGeneration;
            cancellationToken = _stopSource.Token;
        }

        try
        {
            var reader = new HuorongQuarantineReader(path, _config, _log);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                reader.Open();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or SqliteException or IOException or UnauthorizedAccessException)
            {
                _log.Error($"无法打开数据库 {path}: {ex.Message}", ex);
                return false;
            }

            try
            {
                await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            try
            {
                lock (_sync)
                {
                    if (_disposed || generation != _readerGeneration)
                    {
                        return false;
                    }

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
        finally
        {
            lock (_sync)
            {
                if (--_activeReloads == 0)
                {
                    _reloadsDrained.TrySetResult();
                }
            }
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
        Task? openTask;
        Task reloadsDrained;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stop = _stopSource;
            worker = _worker;
            openTask = _openTask;
            reloadsDrained = _reloadsDrained.Task;
            _stopSource = null;
            _worker = null;
            _openTask = null;
        }

        if (stop is not null)
        {
            stop.Cancel();
        }

        if (openTask is not null)
        {
            try
            {
                await openTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
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

        await reloadsDrained.ConfigureAwait(false);

        stop?.Dispose();
        _pollGate.Dispose();
    }

    private bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_sync)
        {
            return !_disposed && generation == _readerGeneration;
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
