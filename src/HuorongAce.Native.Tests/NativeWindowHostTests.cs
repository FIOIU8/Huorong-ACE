using HuorongAce.Native.Win32;
using Xunit;

namespace HuorongAce.Native.Tests;

public sealed class NativeWindowHostTests
{
    [Fact]
    public void Dispose_AllowsWindowClassToBeRegisteredAgain()
    {
        const string className = "HuorongAceNativeWindowHostTest";

        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var host = new NativeWindowHost(
                className,
                instance => NativeMethods.CreateWindowExW(
                    0,
                    className,
                    className,
                    NativeMethods.WsPopup,
                    0, 0, 0, 0,
                    0, 0,
                    instance,
                    0),
                (_, _, _, _) => null);

            Assert.NotEqual(0, host.WaitUntilReady());
            host.Dispose();
            Assert.Equal(0, host.Handle);
        }
    }
}
