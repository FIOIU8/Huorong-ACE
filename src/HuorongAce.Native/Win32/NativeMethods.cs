using System.Runtime.InteropServices;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Win32 imports used by the overlay and the tray icon.
/// </summary>
/// <remarks>
/// The Go version had to hand-declare every symbol because
/// <c>golang.org/x/sys/windows</c> does not expose GDI/USER32. C# has the same
/// job but solves it declaratively with <see cref="DllImportAttribute"/>:
/// marshalling of strings, structs and blittable types is handled by the
/// runtime, which removes roughly 400 lines of hand-written binding code.
///
/// Only the symbols actually needed are declared, and everything lives in one
/// place so the surface stays auditable.
/// </remarks>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------------
    // Window messages
    // ---------------------------------------------------------------------
    public const uint WmPaint = 0x000F;
    public const uint WmEraseBackground = 0x0014;
    public const uint WmDestroy = 0x0002;
    public const uint WmClose = 0x0010;
    public const uint WmKeyDown = 0x0100;
    public const uint WmSystemKeyDown = 0x0104;
    public const uint WmKeyUp = 0x0101;
    public const uint WmChar = 0x0102;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLeftButtonDown = 0x0201;
    public const uint WmLeftButtonUp = 0x0202;
    public const uint WmRightButtonDown = 0x0204;
    public const uint WmMiddleButtonDown = 0x0207;
    public const uint WmSetCursor = 0x0020;
    public const uint WmTimer = 0x0113;
    public const uint WmUser = 0x0400;

    public const uint WmTrayIcon = WmUser + 1;
    public const uint WmOverlayClose = WmUser + 2;
    public const uint WmOverlayUpdate = WmUser + 3;

    // ---------------------------------------------------------------------
    // Virtual key codes
    // ---------------------------------------------------------------------
    public const nint VkEscape = 0x1B;

    // ---------------------------------------------------------------------
    // Window styles
    // ---------------------------------------------------------------------
    public const uint WsPopup = 0x80000000;
    public const uint WsVisible = 0x10000000;

    public const uint WsExTopMost = 0x00000008;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExComposited = 0x02000000;
    public const uint WsExNoActivate = 0x08000000;

    // ---------------------------------------------------------------------
    // System metrics
    // ---------------------------------------------------------------------
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    // ---------------------------------------------------------------------
    // Cursors
    // ---------------------------------------------------------------------
    public const int IdcArrow = 32512;
    public const int IdcHand = 32649;

    public const int SwShow = 5;
    public const int SwHide = 0;

    // ---------------------------------------------------------------------
    // Notify icon
    // ---------------------------------------------------------------------
    public const uint NimAdd = 0x00000000;
    public const uint NimDelete = 0x00000002;
    public const uint NimModify = 0x00000001;
    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const uint NifInfo = 0x00000010;

    // ---------------------------------------------------------------------
    // Structs
    // ---------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect RcPaint;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] RgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint IconSmall;
    }

    /// <summary><c>NOTIFYICONDATAW</c> layout accepted by the modern shell.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint CbSize;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    public delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    // ---------------------------------------------------------------------
    // kernel32.dll
    // ---------------------------------------------------------------------
    // GetModuleHandle lives in kernel32; declaring it against user32 throws
    // EntryPointNotFoundException the first time a window is created.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? moduleName);

    // ---------------------------------------------------------------------
    // user32.dll
    // ---------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern nint LoadCursorW(nint instance, nint cursorId);

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    // ---------------------------------------------------------------------
    // shell32.dll
    // ---------------------------------------------------------------------
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint command, ref NotifyIconData data);

    // ---------------------------------------------------------------------
    // Popup menus (tray context menu)
    // ---------------------------------------------------------------------
    public const uint MfString = 0x00000000;
    public const uint MfSeparator = 0x00000800;
    public const uint TpmRightButton = 0x00000002;
    public const uint TpmReturnCommand = 0x00000100;
    public const uint TpmNoNotify = 0x00000080;
    public const uint TpmLeftAlign = 0x00000000;

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint menu, uint flags, nint idNewItem, string? newItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint hwnd,
        nint rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    /// <summary>
    /// Clips a window to a region, which is how a non-rectangular window is
    /// made. The system owns the region after a successful call — do not delete
    /// the handle passed in.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    // ---------------------------------------------------------------------
    // gdi32.dll
    // ---------------------------------------------------------------------
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        nint destination,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    public const uint SrcCopy = 0x00CC0020;

    [DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(
        int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint handle);

    /// <summary>Decomposes an LPARAM into signed client coordinates.</summary>
    public static (int X, int Y) DecodePoint(nint lParam)
    {
        var value = (long)lParam;
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return (x, y);
    }
}
