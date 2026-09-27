using System.Runtime.InteropServices;
using System.Diagnostics;
using HuorongAce.Native.Win32;

namespace HuorongAce.Native;

/// <summary>
/// System tray icon with the 打开 / 退出 context menu.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 ships no tray icon API, so this is plain <c>Shell_NotifyIcon</c> —
/// the same mechanism Fyne used in the Go build. It lives on its own thread
/// because the notify icon needs a window to receive its callback messages, and
/// that window must not share a message pump with the WinUI window.
/// </para>
/// <para>
/// Events are raised on the tray thread. The host marshals them onto the UI
/// thread before touching any WinUI object.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "HuorongAceTrayHost";
    private const uint IconId = 1;

    private const int MenuOpen = 1001;
    private const int MenuExit = 1002;

    // WM_LBUTTONUP / WM_RBUTTONUP
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;

    private readonly string _tooltip;
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    private NativeWindowHost? _host;
    private nint _iconHandle;
    private bool _disposed;
    private volatile bool _added;
    private volatile int _lastShellError;

    /// <summary>Raised when 打开 is chosen.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when 退出 is chosen.</summary>
    public event EventHandler? ExitRequested;

    public TrayIcon(string tooltip = "火绒ACE") => _tooltip = tooltip;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_host is not null)
        {
            return;
        }

        _iconHandle = ShieldIcon.Create(TrayIconSize());
        _host = new NativeWindowHost(ClassName, CreateHostWindow, HandleMessage);
        _host.WaitUntilReady();
    }

    /// <summary>
    /// The tray renders at the system small-icon size, which follows DPI
    /// (16 at 100%, 32 at 200%). Rendering at that exact size avoids the blur
    /// that comes from the shell downscaling a larger bitmap.
    /// </summary>
    private static int TrayIconSize()
    {
        const int SmCxSmIcon = 49;
        var size = NativeMethods.GetSystemMetrics(SmCxSmIcon);
        return size < 16 || size > 64 ? 32 : size;
    }

    /// <summary>
    /// Whether <c>Shell_NotifyIcon</c> accepted the icon.
    /// </summary>
    /// <remarks>
    /// The tray icon is the only way to reach 打开 / 退出, and a failed
    /// <c>Shell_NotifyIcon</c> call returns FALSE without any other signal —
    /// the app would keep running with no way to interact with it. This flag
    /// makes that failure observable (and testable).
    /// </remarks>
    internal bool IsAdded => _added;

    internal nint HostHandle => _host?.Handle ?? 0;

    internal nint IconHandle => _iconHandle;

    internal int LastShellError => _lastShellError;

    /// <summary>Whether the shell currently owns a visible tray entry.</summary>
    public bool IsVisible => _added;

    private nint CreateHostWindow(nint instance)
    {
        // A zero-sized popup is enough: it never paints, it only receives the
        // notify-icon callback messages.
        var hwnd = NativeMethods.CreateWindowExW(
            0, ClassName, ClassName, NativeMethods.WsPopup,
            0, 0, 0, 0, 0, 0, instance, 0);

        if (hwnd == 0)
        {
            return 0;
        }

        _added = TryAddIcon(hwnd);
        return hwnd;
    }

    private bool TryAddIcon(nint hwnd)
    {
        foreach (var size in new[] { (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(), 296u, 168u })
        {
            var data = NotifyData(hwnd, size);
            if (NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref data))
            {
                return true;
            }

            _lastShellError = Marshal.GetLastWin32Error();
            Thread.Sleep(75);
        }

        Debug.WriteLine("[tray] Shell_NotifyIcon(NIM_ADD) failed after three attempts.");
        return false;
    }

    private void ReAddIcon(nint hwnd)
    {
        if (_disposed || hwnd == 0)
        {
            return;
        }

        var delete = NotifyData(hwnd, (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>());
        NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref delete);
        _added = TryAddIcon(hwnd);
    }

    private NativeMethods.NotifyIconData NotifyData(nint hwnd, uint size) => new()
    {
        CbSize = size,
        Hwnd = hwnd,
        Id = IconId,
        Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
        CallbackMessage = NativeMethods.WmTrayIcon,
        Icon = _iconHandle,
        Tip = _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint? HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            ReAddIcon(hwnd);
            return 0;
        }

        if (message != NativeMethods.WmTrayIcon)
        {
            return null;
        }

        // lParam carries the mouse event id. Both buttons open the menu, which
        // is what users expect from a tray icon.
        var eventId = (uint)lParam;
        if (eventId is WmLButtonUp or WmRButtonUp)
        {
            ShowContextMenu(hwnd);
        }

        return 0;
    }

    private void ShowContextMenu(nint hwnd)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MfString, (nint)MenuOpen, "打开");
            NativeMethods.AppendMenuW(menu, NativeMethods.MfString, (nint)MenuExit, "退出");

            NativeMethods.GetCursorPos(out var cursor);

            // TrackPopupMenu needs the owning window foregrounded, otherwise the
            // menu may fail to dismiss when the user clicks elsewhere.
            NativeMethods.SetForegroundWindow(hwnd);

            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCommand | NativeMethods.TpmNoNotify,
                cursor.X, cursor.Y, 0, hwnd, 0);

            switch (command)
            {
                case MenuOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_host is not null)
        {
            var hwnd = _host.Handle;
            if (hwnd != 0)
            {
                var data = NotifyData(hwnd, (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>());
                NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref data);
            }

            _host.Dispose();
            _host = null;
        }

        if (_iconHandle != 0)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }
}
