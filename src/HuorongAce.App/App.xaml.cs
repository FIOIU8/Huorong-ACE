using HuorongAce.Core.Configuration;
using HuorongAce.Core.Diagnostics;
using HuorongAce.Core.Monitoring;
using HuorongAce.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HuorongAce.App;

/// <summary>
/// Application host. Owns the monitor, the overlay and the tray icon.
/// </summary>
/// <remarks>
/// Behaviour matches the Go <c>main</c>: start with no visible window, sit in
/// the tray, pop the red overlay on detection, and offer 打开 / 退出 from the
/// tray menu. The difference is that here the pieces are injected as objects
/// with events instead of wired together through package-level functions.
/// </remarks>
public partial class App : Application
{
    private readonly IAppLog _log = new DebugAppLog();
    private readonly RedScreenOverlay _overlay = new();

    /// <summary>
    /// Captured on the UI thread in the constructor so background callbacks can
    /// hop back onto it later. WinUI's <c>Application</c> does not expose a
    /// <c>DispatcherQueue</c> property of its own.
    /// </summary>
    private readonly DispatcherQueue _uiQueue = DispatcherQueue.GetForCurrentThread();

    private AppConfig _config = null!;
    private ThreatMonitor _monitor = null!;
    private TrayIcon _tray = null!;
    private SettingsWindow? _settingsWindow;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _config = AppConfig.Load();

        _monitor = new ThreatMonitor(_config, _log);
        _monitor.ThreatDetected += OnThreatDetected;

        _tray = new TrayIcon("火绒ACE");
        _tray.OpenRequested += OnTrayOpenRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        _tray.Show();

        // Keep the process usable even when Explorer rejects the tray entry.
        if (!_tray.IsVisible)
        {
            ShowSettingsWindow();
        }

        _monitor.Start();

        AnnounceStartup();

        if (Environment.GetCommandLineArgs().Contains("--open-settings", StringComparer.OrdinalIgnoreCase))
        {
            ShowSettingsWindow();
        }
    }

    /// <summary>
    /// Raises the start-up notification, waiting a moment on the very first run.
    /// </summary>
    /// <remarks>
    /// The first run also creates the Start-menu shortcut that gives the app its
    /// notification identity. The shell resolves an AppUserModelID through an
    /// index it refreshes asynchronously, so a toast sent in that same instant
    /// is silently dropped. Two seconds is enough for the index to catch up,
    /// and it only ever costs that once.
    /// </remarks>
    private async void AnnounceStartup()
    {
        const string title = "火绒ACE 已启动";
        const string content = "正在监控火绒日志，右键托盘图标可打开管理界面或退出。";

        if (SystemToast.WarmUp())
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Notify(title, content);
    }

    private void OnThreatDetected(object? sender, ThreatInfo threat)
    {
        // The event arrives on the polling thread; the overlay is a Win32 window
        // with its own thread, so no marshalling is needed for it.
        _overlay.Show(threat);
    }

    private void OnTrayOpenRequested(object? sender, EventArgs e)
    {
        // Tray callbacks arrive on the tray thread, so hop to the UI thread
        // before touching any WinUI object.
        _uiQueue.TryEnqueue(ShowSettingsWindow);
    }

    /// <summary>Shows the settings window, creating it on first use.</summary>
    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            var window = new SettingsWindow(_config, _monitor, _log);

            // A WinUI window cannot be re-shown after it is closed, so drop the
            // reference and build a fresh one next time.
            window.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow = window;
        }

        _settingsWindow.Activate();
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _uiQueue.TryEnqueue(() => _ = ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _monitor.SetEnabled(false);

        // Synchronous on purpose: the notification must be raised before the
        // process goes away. PowerShell is a separate process, so the toast
        // outlives us.
        Notify("火绒ACE 已退出", "已停止监控火绒日志，程序即将退出。");

        await _monitor.DisposeAsync();
        _tray.Dispose();
        _overlay.Dispose();

        Exit();
    }

    private void Notify(string title, string content)
    {
        if (!SystemToast.TryShow(title, content))
        {
            _log.Info($"系统通知不可用，已跳过: {title}");
        }
    }
}
