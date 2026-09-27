using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HuorongAce.Native.Win32;

/// <summary>
/// The application's shield icon, rendered from a vector path.
/// </summary>
/// <remarks>
/// <para>
/// The previous icon was a hand-written polygon — straight shoulders and a
/// triangular point, which looks crude next to any real icon, especially at
/// 16 pixels in the tray. It is now the shield from
/// <b>Fluent UI System Icons</b> (Microsoft, MIT licence), stored as SVG path
/// data and rasterised on demand by <see cref="SvgPath"/>.
/// </para>
/// <para>
/// Keeping the path instead of a bitmap matters: the shell asks for whatever
/// size suits the current DPI, and a vector re-renders cleanly at every one of
/// them.
/// </para>
/// </remarks>
public static class ShieldIcon
{
    /// <summary>
    /// Fluent UI System Icons "shield" (filled), 24×24 grid.
    /// https://github.com/microsoft/fluentui-system-icons — MIT licence.
    /// </summary>
    private const string ShieldPathData =
        "M3 5.75A.75.75 0 0 1 3.75 5c2.663 0 5.258-.943 7.8-2.85a.75.75 0 0 1 .9 0" +
        "C14.992 4.057 17.587 5 20.25 5a.75.75 0 0 1 .75.75V11c0 5.001-2.958 8.676-8.725 10.948" +
        "a.75.75 0 0 1-.55 0C5.958 19.676 3 16 3 11z";

    /// <summary>
    /// The same red as the termination screen (#D3373A), so the icon, the
    /// window and the overlay read as one product.
    /// </summary>
    private static readonly Color PlateColour = Color.FromArgb(0xD3, 0x37, 0x3A);

    /// <summary>
    /// Sizes bundled into the .ico file. Windows picks the closest one, so the
    /// title bar and Alt-Tab stay sharp instead of being scaled from 32 px.
    /// </summary>
    private static readonly int[] IconSizes = [16, 20, 24, 32, 48, 64, 256];

    /// <summary>Renders the icon at <paramref name="size"/> and returns an HICON.</summary>
    public static nint Create(int size = 32)
    {
        using var bitmap = Render(size);
        return bitmap.GetHicon();
    }

    /// <summary>Renders the icon into a transparent bitmap.</summary>
    /// <remarks>
    /// The shield is drawn the same way at every size — a single red
    /// silhouette — rather than as a white shield on a red plate at large sizes
    /// and something simpler at small ones. A plate reads well at 48 pixels but
    /// collapses into a red dot at 16, and switching between the two looks like
    /// two different applications. One shape, drawn once, scales all the way
    /// down and still says "shield" in the tray.
    /// </remarks>
    public static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            // The Fluent glyph carries its own padding inside the 24×24 box,
            // so it is scaled up to fill the square and stay legible small.
            var glyphSize = size * 0.98f;
            using var path = SvgPath.Parse24(ShieldPathData, glyphSize);
            using var brush = new SolidBrush(PlateColour);
            graphics.TranslateTransform((size - glyphSize) / 2f, (size - glyphSize) / 2f);
            graphics.FillPath(brush, path);
        }

        return bitmap;
    }

    /// <summary>The shield silhouette alone, centred in a square of <paramref name="size"/>.</summary>
    public static GraphicsPath ShieldPath(int size) =>
        SvgPath.Parse24(ShieldPathData, size);

    /// <summary>
    /// Writes a multi-size .ico file. Vista and later accept PNG-compressed
    /// icon entries, which is far simpler than emitting BMP masks by hand.
    /// </summary>
    public static bool TryWriteIcoFile(string target)
    {
        try
        {
            var images = new List<byte[]>();
            foreach (var size in IconSizes)
            {
                using var bitmap = Render(size);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                images.Add(stream.ToArray());
            }

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            // ICONDIR: reserved, type 1 (icon), image count.
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)images.Count);

            // Offsets: header (6) + one 16-byte directory entry per image.
            var offset = 6 + 16 * images.Count;
            for (var i = 0; i < images.Count; i++)
            {
                var size = IconSizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0); // palette colours
                writer.Write((byte)0); // reserved
                writer.Write((ushort)1); // colour planes
                writer.Write((ushort)32); // bits per pixel
                writer.Write((uint)images[i].Length);
                writer.Write((uint)offset);
                offset += images[i].Length;
            }

            foreach (var image in images)
            {
                writer.Write(image);
            }

            writer.Flush();
            File.WriteAllBytes(target, output.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

}
