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
    private int _disposeStarted;

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
        nint hwnd = 0;
        nint instance = 0;
        ushort classAtom = 0;
        try
        {
            instance = NativeMethods.GetModuleHandleW(null);

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
            classAtom = NativeMethods.RegisterClassExW(ref windowClass);
            if (classAtom == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassExW failed for {_className}: {Marshal.GetLastWin32Error()}");
            }

            hwnd = _createWindow(instance);
            _handle = hwnd;
            _ready.Set();

            if (hwnd == 0)
            {
                return;
            }

            // Dispose may race with window creation. Destroy the window on its
            // owning thread instead of leaving a hidden native window behind.
            if (_stopped)
            {
                NativeMethods.DestroyWindow(hwnd);
                return;
            }

            while (!_stopped && NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[native] message loop failure: {ex}");
        }
        finally
        {
            if (hwnd != 0 && NativeMethods.IsWindow(hwnd))
            {
                NativeMethods.DestroyWindow(hwnd);
            }

            if (classAtom != 0)
            {
                if (!NativeMethods.UnregisterClassW(_className, instance))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[native] UnregisterClassW failed for {_className}: {Marshal.GetLastWin32Error()}");
                }
            }

            _handle = 0;
            // Signal both successful creation and failures. Dispose waits for
            // the thread before releasing this event, so Set cannot race with
            // disposal.
            _ready.Set();
        }
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

            if (message == NativeMethods.WmClose)
            {
                NativeMethods.DestroyWindow(hwnd);
                return 0;
            }

            if (message == NativeMethods.WmDestroy)
            {
                NativeMethods.PostQuitMessage(0);
                return 0;
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
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _stopped = true;
        Stop();

        if (_thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        if (!_thread.IsAlive)
        {
            _ready.Dispose();
        }
    }
}
