using System.Globalization;
using HuorongAce.Core.Configuration;
using HuorongAce.Core.Diagnostics;
using HuorongAce.Core.Monitoring;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace HuorongAce.App;

/// <summary>
/// Settings window: a WinUI 3 NavigationView hosting the 监控 / 测试与保存 /
/// 使用说明 pages.
/// </summary>
/// <remarks>
/// <para>
/// This is the biggest structural change from the Go build. There, the whole
/// window — background, sidebar, cards, switches, buttons, text — was painted
/// by hand with GDI because Fyne could not produce the wanted look, and the
/// settings window ended up around 1200 lines of drawing code. WinUI provides
/// those controls natively, so the same UI is now declarative XAML plus a small
/// amount of wiring, and it picks up Fluent theming, high-contrast support,
/// keyboard navigation and screen-reader support for free.
/// </para>
/// <para>
/// Behaviour is kept identical: same three pages, same fields, same status
/// strip, same "save writes config.json and re-opens the database" semantics.
/// </para>
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth = 900;
    private const int WindowHeight = 520;

    private readonly AppConfig _config;
    private readonly ThreatMonitor _monitor;
    private readonly IAppLog _log;
    private readonly DispatcherTimer _statusTimer;

    private string _lastStatusMessage = string.Empty;

    public SettingsWindow(AppConfig config, ThreatMonitor monitor, IAppLog log)
    {
        _config = config;
        _monitor = monitor;
        _log = log;

        InitializeComponent();

        Title = "火绒ACE";
        ConfigureWindow();

        Navigation.SelectedItem = NavMonitor;

        EnabledSwitch.IsOn = monitor.IsEnabled;
        PollIntervalBox.Text = config.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        KeywordsBox.Text = string.Join(", ", config.Keywords);
        LogPathText.Text = config.LogPath;

        monitor.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshStatus);
        RefreshStatus();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Closed += OnClosed;
    }

    private void ConfigureWindow()
    {
        var appWindow = GetAppWindow();
        appWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

        // Centre on the display that contains the window.
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var x = area.WorkArea.X + (area.WorkArea.Width - WindowWidth) / 2;
        var y = area.WorkArea.Y + (area.WorkArea.Height - WindowHeight) / 2;
        appWindow.Move(new PointInt32(x, y));
    }

    private AppWindow GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    // ---------------------------------------------------------------------
    // Status strip
    // ---------------------------------------------------------------------

    private void RefreshStatus()
    {
        string message;
        InfoBarSeverity severity;

        if (!_monitor.IsHuorongAvailable)
        {
            message = "未检测到火绒日志，当前为测试模式";
            severity = InfoBarSeverity.Warning;
        }
        else if (_monitor.IsEnabled)
        {
            message = "监控运行中，正在监听火绒检测事件";
            severity = InfoBarSeverity.Success;
        }
        else
        {
            message = "监控已停止";
            severity = InfoBarSeverity.Informational;
        }

        var (last, when) = _monitor.LastDetection;
        if (last is not null && when is not null)
        {
            message += $"　·　上次检测 {last.Name}（{when.Value.LocalDateTime:HH:mm:ss}）";
        }

        // Skip a pointless layout pass when nothing changed.
        if (message == _lastStatusMessage)
        {
            return;
        }

        _lastStatusMessage = message;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        EnabledSwitch.IsOn = _monitor.IsEnabled;
    }

    // ---------------------------------------------------------------------
    // Navigation
    // ---------------------------------------------------------------------

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag as string;
        MonitorPage.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        ActionsPage.Visibility = tag == "Actions" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Filters the sidebar entries; the Go build drew its own filtered list,
    /// WinUI just toggles item visibility.
    /// </summary>
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var filter = sender.Text?.Trim() ?? string.Empty;

        foreach (var item in new[] { NavMonitor, NavActions, NavAbout })
        {
            var label = item.Content as string ?? string.Empty;
            item.Visibility = filter.Length == 0 ||
                              label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        _monitor.SetEnabled(EnabledSwitch.IsOn);
        RefreshStatus();
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var path = await PickDatabaseFileAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _config.LogPath = path;
        LogPathText.Text = path;

        // Re-open at once so the new path takes effect without a restart.
        if (!await _monitor.ReloadAsync(path))
        {
            await ShowDialogAsync("无法打开该数据库", path);
            return;
        }

        RefreshStatus();
    }

    private void OnTestClicked(object sender, RoutedEventArgs e)
    {
        _monitor.Simulate("EICAR-Test-Signature (模拟)");
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PollIntervalBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval)
            && interval >= 1)
        {
            _config.PollIntervalSeconds = interval;
        }

        _config.Keywords = KeywordsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        try
        {
            _config.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("保存失败", ex);
            await ShowDialogAsync("保存失败", ex.Message);
            return;
        }

        // Apply the possibly changed path without needing a restart.
        await _monitor.ReloadAsync(_config.LogPath);
        RefreshStatus();
        await ShowDialogAsync("已保存", "设置已写入 config.json");
    }

    private async Task<string?> PickDatabaseFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();

        // A picker needs an owner window handle on WinUI 3 desktop apps.
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".db");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Stop();
        Closed -= OnClosed;
    }
}
