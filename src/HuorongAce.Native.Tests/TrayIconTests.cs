using System.Runtime.InteropServices;
using HuorongAce.Native.Win32;
using Xunit;
using Xunit.Abstractions;

namespace HuorongAce.Native.Tests;

/// <summary>
/// Guards the two Win32 details that fail silently when wrong: the size of
/// <c>NOTIFYICONDATA</c> and the result of <c>Shell_NotifyIcon</c>.
/// </summary>
/// <remarks>
/// Neither has an equivalent in the Go build — there the struct was laid out
/// field by field in Go, so the size question never came up. C# marshals the
/// struct, so the layout is worth pinning down with a test.
/// </remarks>
public sealed class TrayIconTests
{
    private readonly ITestOutputHelper _output;

    public TrayIconTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void NotifyIconData_UsesTheModernSize()
    {
        var size = Marshal.SizeOf<NativeMethods.NotifyIconData>();
        _output.WriteLine($"sizeof(NOTIFYICONDATAW) modern = {size}");

        // Windows 10/11 require the complete Vista+ layout for NIM_ADD.
        Assert.Equal(976, size);
    }

    [Fact(Skip = "Requires an interactive Explorer notification area; the CI desktop may reject Shell_NotifyIcon.")]
    public void TrayIcon_IsAcceptedByTheShell()
    {
        using var tray = new TrayIcon("火绒ACE 测试");
        tray.Show();

        _output.WriteLine($"Tray host handle: 0x{tray.HostHandle:X}");
        _output.WriteLine($"Tray icon handle: 0x{tray.IconHandle:X}");
        _output.WriteLine($"Shell error: {tray.LastShellError}");
        _output.WriteLine($"Shell_NotifyIcon accepted: {tray.IsAdded}");
        Assert.True(tray.IsAdded);
    }
}
