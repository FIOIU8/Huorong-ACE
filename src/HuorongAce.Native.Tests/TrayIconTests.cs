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
    public void NotifyIconData_UsesTheV1Size()
    {
        var size = Marshal.SizeOf<NativeMethods.NotifyIconData>();
        _output.WriteLine($"sizeof(NOTIFYICONDATAW) V1 = {size}");

        // 296 == FIELD_OFFSET(szTip) + sizeof(szTip) on 64-bit, i.e.
        // NOTIFYICONDATA_V1_SIZE. Shell_NotifyIcon only accepts the exact
        // V1/V2/V3/V4 sizes, so any drift here means a silently missing icon.
        Assert.Equal(296, size);
    }

    [Fact]
    public void TrayIcon_IsAcceptedByTheShell()
    {
        using var tray = new TrayIcon("火绒ACE 测试");
        tray.Show();

        _output.WriteLine($"Shell_NotifyIcon accepted: {tray.IsAdded}");
        Assert.True(tray.IsAdded);
    }
}
