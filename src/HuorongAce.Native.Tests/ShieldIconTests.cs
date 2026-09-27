using System.Drawing;
using System.Drawing.Imaging;
using HuorongAce.Native.Win32;
using Xunit;
using Xunit.Abstractions;

namespace HuorongAce.Native.Tests;

/// <summary>
/// Pins down that the vector shield path parses and renders into the shape we
/// expect.
/// </summary>
/// <remarks>
/// A broken SVG parser does not throw — it silently draws a wrong shape, which
/// is exactly the kind of bug that produces an "ugly icon". These checks catch
/// the common failure modes: an empty path, a shape that fills the whole
/// square, or a shape that is off-centre.
/// </remarks>
public sealed class ShieldIconTests
{
    private readonly ITestOutputHelper _output;

    public ShieldIconTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Icon_HasTransparentCornersAndAnOpaqueMiddle()
    {
        const int size = 64;
        using var bitmap = ShieldIcon.Render(size);

        // Corners must be transparent: the plate is rounded, not a full square.
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(size - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, size - 1).A);
        Assert.Equal(0, bitmap.GetPixel(size - 1, size - 1).A);

        // The middle must be painted.
        Assert.Equal(255, bitmap.GetPixel(size / 2, size / 2).A);

        _output.WriteLine($"centre colour: {bitmap.GetPixel(size / 2, size / 2)}");
    }

    [Fact]
    public void Icon_CoverageIsShieldShaped()
    {
        const int size = 64;
        using var bitmap = ShieldIcon.Render(size);

        var opaque = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (bitmap.GetPixel(x, y).A > 128)
                {
                    opaque++;
                }
            }
        }

        var fraction = (double)opaque / (size * size);
        _output.WriteLine($"opaque fraction: {fraction:F3}");

        // A shield covers roughly 45% of its square: it is widest across the
        // shoulders and tapers to a point, so it can never approach the ~99%
        // of a filled rectangle. The band is wide enough to allow antialiasing
        // differences but tight enough to catch a path that failed to parse and
        // silently produced an empty or full shape.
        Assert.InRange(fraction, 0.30, 0.70);
    }

    [Fact]
    public void Icon_WritesAMultiSizeIcoFile()
    {
        var target = Path.Combine(Path.GetTempPath(), "huorong-ace-icon-test.ico");

        Assert.True(ShieldIcon.TryWriteIcoFile(target));

        var info = new FileInfo(target);
        _output.WriteLine($"ico size: {info.Length} bytes");

        // Seven images plus header and directory: anything tiny means the PNG
        // entries failed to encode.
        Assert.True(info.Length > 5000, "ico file looks too small to hold seven images");
    }
}
