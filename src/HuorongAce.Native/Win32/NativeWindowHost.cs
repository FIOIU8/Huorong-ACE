using System.Runtime.InteropServices;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Hosts a plain Win32 window on a dedicated thread with its own message loop.
/// </summary>
/// <remarks>
/// The Go version created its overlay and tray windows from goroutines pinned
/// with <c>runtime.LockOSThread()</c>, each running its own
/// <c>GetMessage</c> loop. C# does the same with an STA thread and an explicit
/// pump. Keeping the window off the UI thread matters here: the overlay is a
/// modal, topmost surface and must not share a pump with the WinUI window.
/// </remarks>
internal sealed class NativeWindowHost : IDisposable
{
    /// <summary>Handles a message. Return null to defer to DefWindowProc.</summary>
    public delegate nint? MessageHandler(nint hwnd, uint message, nint wParam, nint lParam);

    private readonly MessageHandler _handler;
    private readonly Func<nint, nint> _createWindow;
    private readonly string _className;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private NativeMethods.WindowProc? _windowProc; // kept alive: it is marshalled to a function pointer
    private volatile nint _handle;
    private volatile bool _stopped;

    /// <summary>Window handle, valid once <see cref="WaitUntilReady"/> returns.</summary>
    public nint Handle => _handle;

    public NativeWindowHost(string className, Func<nint, nint> createWindow, MessageHandler handler)
    {
        _className = className;
        _createWindow = createWindow;
        _handler = handler;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = className,
        };
        // Win32 windows expect a single-threaded apartment for COM and IME.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Blocks until the window has been created (or creation failed).</summary>
    public nint WaitUntilReady(TimeSpan? timeout = null)
    {
        _ready.Wait(timeout ?? TimeSpan.FromSeconds(5));
        return _handle;
    }

    private void MessageLoop()
    {
        var instance = NativeMethods.GetModuleHandleW(null);

        // Registering the class here (not on the caller's thread) keeps the
        // window procedure and the message loop on the same thread.
        var windowClass = new NativeMethods.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_windowProc = Dispatch),
            Instance = instance,
            Cursor = NativeMethods.LoadCursorW(0, NativeMethods.IdcArrow),
            ClassName = _className,
        };
        NativeMethods.RegisterClassExW(ref windowClass);

        var hwnd = _createWindow(instance);
        _handle = hwnd;
        _ready.Set();

        if (hwnd == 0)
        {
            return;
        }

        while (!_stopped && NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        _handle = 0;
    }

    private nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // Never let an exception escape into native code.
        try
        {
            var handled = _handler(hwnd, message, wParam, lParam);
            if (handled.HasValue)
            {
                return handled.Value;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[native] window proc failure: {ex}");
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Posts a message to the window thread.</summary>
    public void Post(uint message, nint wParam = 0, nint lParam = 0)
    {
        var hwnd = _handle;
        if (hwnd != 0)
        {
            NativeMethods.PostMessageW(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>Destroys the window and ends the message loop.</summary>
    public void Stop()
    {
        var hwnd = _handle;
        if (hwnd != 0)
        {
            NativeMethods.PostMessageW(hwnd, NativeMethods.WmClose, 0, 0);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        Stop();
        _ready.Dispose();
    }
}
