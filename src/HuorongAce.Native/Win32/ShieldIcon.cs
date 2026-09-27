using System.Drawing;
using System.Drawing.Drawing2D;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Builds the application's shield icon.
/// </summary>
/// <remarks>
/// The Go version generated a PNG by inspecting each pixel against a shield
/// function and encoded it with <c>image/png</c>. GDI+ can fill a
/// <see cref="GraphicsPath"/> directly, which is shorter and scales cleanly.
/// The result is handed out as an HICON for the tray and the window title bar.
/// </remarks>
internal static class ShieldIcon
{
    public static nint Create(int size = 32, Color? background = null, Color? foreground = null)
    {
        var back = background ?? Color.FromArgb(0xC0, 0x00, 0x00);
        var fore = foreground ?? Color.White;

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var backgroundBrush = new SolidBrush(back);
            graphics.FillRoundedRectangle(backgroundBrush, new Rectangle(0, 0, size, size), size * 0.22f);

            using var path = ShieldPath(size);
            using var brush = new SolidBrush(fore);
            graphics.FillPath(brush, path);
        }

        return bitmap.GetHicon();
    }

    /// <summary>Shield silhouette: rounded top tapering to a point at the bottom.</summary>
    public static GraphicsPath ShieldPath(int size)
    {
        var half = size / 2f;
        var top = size * 0.10f;
        var bottom = size * 0.90f;
        var shoulder = size * 0.55f;
        var corner = size * 0.18f;

        var path = new GraphicsPath();
        path.StartFigure();
        path.AddArc(size / 2f - half, top, half * 2, corner * 2, 180, 90);
        path.AddLine(size / 2f + half, top + corner, size / 2f + half, shoulder);
        path.AddLine(size / 2f + half, shoulder, size / 2f, bottom);
        path.AddLine(size / 2f, bottom, size / 2f - half, shoulder);
        path.AddLine(size / 2f - half, shoulder, size / 2f - half, top + corner);
        path.AddArc(size / 2f - half, top, half * 2, corner * 2, 270, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
