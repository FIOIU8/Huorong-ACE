using System.Runtime.InteropServices;
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
    private NativeWindowHost? _host;
    private nint _iconHandle;
    private bool _disposed;
    private volatile bool _added;

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

        _iconHandle = ShieldIcon.Create(32);
        _host = new NativeWindowHost(ClassName, CreateHostWindow, HandleMessage);
        _host.WaitUntilReady();
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

        var data = NotifyData(hwnd);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref data);
        return hwnd;
    }

    private NativeMethods.NotifyIconData NotifyData(nint hwnd) => new()
    {
        CbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Hwnd = hwnd,
        Id = IconId,
        Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
        CallbackMessage = NativeMethods.WmTrayIcon,
        Icon = _iconHandle,
        Tip = _tooltip,
    };

    private nint? HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
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
                var data = NotifyData(hwnd);
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
