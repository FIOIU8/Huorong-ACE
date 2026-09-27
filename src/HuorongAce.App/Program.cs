using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace HuorongAce.App;

/// <summary>
/// Entry point for the unpackaged WinUI 3 application.
/// </summary>
/// <remarks>
/// A packaged (MSIX) WinUI app gets its entry point generated. Because this app
/// is deployed as a plain folder — like the Go build — the entry point has to be
/// written by hand: initialise COM interop, start XAML, and install a
/// <see cref="DispatcherQueueSynchronizationContext"/> so that
/// <c>async/await</c> continuations run back on the UI thread.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Keeps the application object rooted. Without this the JIT can collect the
    /// instance after <see cref="Application.Start"/> returns the callback,
    /// because XAML only holds a weak reference to it.
    /// </summary>
    private static App? s_app;

    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            s_app = new App();
        });
    }
}
