namespace HuorongAce.Native.Win32;

/// <summary>
/// Shapes a window into something other than a plain rectangle.
/// </summary>
/// <remarks>
/// <para>
/// This is the public face of the Win32 region calls. The application layer
/// needs the effect but should not have to import GDI handles itself, so the
/// details stay here next to the other native declarations.
/// </para>
/// <para>
/// Once a region is handed to <c>SetWindowRgn</c> the system owns it and
/// disposes of the previous one, which is why nothing here needs to be freed
/// by the caller.
/// </para>
/// </remarks>
public static class WindowShape
{
    /// <summary>
    /// Clips the window to a rounded rectangle.
    /// </summary>
    /// <param name="hwnd">Window to shape.</param>
    /// <param name="width">Outer window width in pixels.</param>
    /// <param name="height">Outer window height in pixels.</param>
    /// <param name="radius">Corner radius in pixels.</param>
    /// <returns>False when the region could not be created or applied.</returns>
    public static bool ApplyRoundedCorners(nint hwnd, int width, int height, int radius)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        // +1 so the bottom and right edges are inside the region; otherwise the
        // shape loses its last row and column of pixels.
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region == 0)
        {
            return false;
        }

        if (NativeMethods.SetWindowRgn(hwnd, region, true) != 0)
        {
            return true;
        }

        NativeMethods.DeleteObject(region);
        return false;
    }
}
