using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using HuorongAce.Core.Monitoring;
using HuorongAce.Native.Win32;

namespace HuorongAce.Native;

/// <summary>
/// Full-screen ACE-style termination overlay, drawn with GDI+ on a native
/// topmost window.
/// </summary>
/// <remarks>
/// <para>
/// Kept as a Win32 window rather than a WinUI window on purpose: the overlay
/// has to cover the whole virtual desktop (every monitor, above the taskbar),
/// which WinUI's windowing model cannot express. The Go build made the same
/// choice for the same reason.
/// </para>
/// <para>
/// Drawing uses GDI+ instead of raw GDI. That is the one place where the port
/// deliberately improves on the original: GDI+ gives anti-aliased text, true
/// rounded rectangles and easy off-screen buffering, which replaced roughly 200
/// lines of hand-rolled GDI helpers.
/// </para>
/// </remarks>
public sealed class RedScreenOverlay : IDisposable
{
    private const string ClassName = "HuorongAceRedScreen";

    // Palette sampled from the real ACE termination screen.
    private static readonly Color Background = Color.FromArgb(0xD3, 0x37, 0x3A);
    private static readonly Color Separator = Color.FromArgb(0xE4, 0x74, 0x78);
    private static readonly Color ButtonFill = Color.FromArgb(0xD8, 0x60, 0x66);
    private static readonly Color ButtonHover = Color.FromArgb(0xE4, 0x74, 0x78);
    private static readonly Color ButtonEdge = Color.FromArgb(0xE9, 0x9A, 0x9E);
    private static readonly Color InfoText = Color.FromArgb(0xF0, 0xC4, 0xC6);

    private const string TitleText = "检测到安全威胁";
    private const string SubText = "程序已终止";
    private const string DescriptionText = "威胁程序已被隔离，相关进程已终止，请及时进行全盘扫描。";
    private const string HintText = "点击「继续」或按 Esc 退出";
    private const string BackText = "‹ 返回";
    private const string ButtonText = "继续";

    private readonly object _sync = new();
    private NativeWindowHost? _host;
    private ThreatInfo? _current;
    private Rectangle _buttonBounds;
    private bool _buttonHovered;
    private bool _disposed;

    /// <summary>True while the overlay is on screen.</summary>
    public bool IsVisible
    {
        get
        {
            lock (_sync)
            {
                return _host is not null && _host.Handle != 0;
            }
        }
    }

    /// <summary>Shows the overlay, or refreshes it with new content if already shown.</summary>
    public void Show(ThreatInfo threat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _current = threat;

            if (_host is not null && _host.Handle != 0)
            {
                _host.Post(NativeMethods.WmOverlayUpdate);
                return;
            }

            // The previous host either never existed or already exited its pump
            // (the user dismissed the overlay), so retire it before making a new
            // one. Otherwise every detection would leak a finished thread.
            _host?.Dispose();
            _host = new NativeWindowHost(ClassName, CreateOverlayWindow, HandleMessage);
        }

        _host.WaitUntilReady();
        _host.Post(NativeMethods.WmOverlayUpdate);
    }

    /// <summary>Dismisses the overlay.</summary>
    /// <remarks>
    /// The host is dropped as well as stopped: a stopped host keeps its thread
    /// alive with a dead handle, and <see cref="Show"/> would otherwise keep
    /// posting to a window that no longer exists.
    /// </remarks>
    public void Hide()
    {
        lock (_sync)
        {
            if (_host is null)
            {
                return;
            }

            _host.Dispose();
            _host = null;
        }
    }

    private nint CreateOverlayWindow(nint instance)
    {
        // Cover the entire virtual desktop, including the taskbar.
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);

        var hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WsExTopMost | NativeMethods.WsExToolWindow | NativeMethods.WsExComposited,
            ClassName,
            null,
            NativeMethods.WsPopup | NativeMethods.WsVisible,
            x, y, width, height,
            0, 0, instance, 0);

        if (hwnd == 0)
        {
            return 0;
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SwShow);
        NativeMethods.UpdateWindow(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        return hwnd;
    }

    private nint? HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmEraseBackground:
                return 1; // Painted in WM_PAINT; suppress the background erase.

            case NativeMethods.WmPaint:
                Paint(hwnd);
                return 0;

            case NativeMethods.WmKeyDown:
            case NativeMethods.WmSystemKeyDown:
                // Only Esc dismisses; every other key is swallowed so a stray
                // keystroke cannot get rid of the overlay by accident.
                if (wParam == NativeMethods.VkEscape)
                {
                    Dismiss(hwnd);
                }

                return 0;

            case NativeMethods.WmChar:
            case NativeMethods.WmKeyUp:
                return 0;

            case NativeMethods.WmMouseMove:
                UpdateHover(hwnd, NativeMethods.DecodePoint(lParam));
                return 0;

            case NativeMethods.WmSetCursor:
                if (_buttonHovered)
                {
                    NativeMethods.SetCursor(NativeMethods.LoadCursorW(0, NativeMethods.IdcHand));
                    return 1;
                }

                return null;

            case NativeMethods.WmLeftButtonDown:
                var (clickX, clickY) = NativeMethods.DecodePoint(lParam);
                if (_buttonBounds.Contains(clickX, clickY))
                {
                    Dismiss(hwnd);
                }

                return 0;

            case NativeMethods.WmRightButtonDown:
            case NativeMethods.WmMiddleButtonDown:
                return 0;

            case NativeMethods.WmOverlayUpdate:
                NativeMethods.InvalidateRect(hwnd, 0, false);
                return 0;

            case NativeMethods.WmOverlayClose:
                NativeMethods.DestroyWindow(hwnd);
                return 0;

            case NativeMethods.WmClose:
                NativeMethods.DestroyWindow(hwnd);
                return 0;

            case NativeMethods.WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return 0;
        }

        return null;
    }

    private void UpdateHover(nint hwnd, (int X, int Y) point)
    {
        var hovered = _buttonBounds.Contains(point.X, point.Y);
        if (hovered == _buttonHovered)
        {
            return;
        }

        _buttonHovered = hovered;
        NativeMethods.InvalidateRect(hwnd, 0, false);
    }

    private void Dismiss(nint hwnd) => NativeMethods.PostMessageW(hwnd, NativeMethods.WmOverlayClose, 0, 0);

    private void Paint(nint hwnd)
    {
        NativeMethods.GetClientRect(hwnd, out var rect);
        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Off-screen composition: GDI+ draws into a bitmap that is blitted in
        // one go, which is what keeps the surface from flickering.
        using var buffer = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(buffer))
        {
            Draw(graphics, width, height);
        }

        var hdc = NativeMethods.BeginPaint(hwnd, out var paint);
        try
        {
            using var target = Graphics.FromHdc(hdc);
            target.DrawImage(buffer, 0, 0);
        }
        finally
        {
            NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private void Draw(Graphics graphics, int width, int height)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Background);

        ThreatInfo? threat;
        lock (_sync)
        {
            threat = _current;
        }

        // Hairlines near the top and bottom edges.
        var left = (int)(width * 0.040f);
        var right = (int)(width * 0.960f);
        using var separatorPen = new Pen(Separator, Math.Max(1, height / 720f));
        graphics.DrawLine(separatorPen, left, height * 0.045f, right, height * 0.045f);
        graphics.DrawLine(separatorPen, left, height * 0.951f, right, height * 0.951f);

        DrawTopRightBack(graphics, width, height);
        DrawWordmark(graphics, width, height);

        using var strongFont = new Font("Microsoft YaHei", height * 0.085f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Microsoft YaHei", height * 0.050f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Microsoft YaHei", height * 0.028f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var infoFont = new Font("Microsoft YaHei", height * 0.024f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var hintFont = new Font("Microsoft YaHei", height * 0.022f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var whiteBrush = new SolidBrush(Color.White);
        using var infoBrush = new SolidBrush(InfoText);
        var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var content = new RectangleF(width * 0.120f, 0, width * 0.760f, 0);

        DrawCentered(graphics, TitleText, strongFont, whiteBrush, content.WithVertical(height * 0.425f, height * 0.075f), centre);
        DrawCentered(graphics, SubText, subFont, whiteBrush, content.WithVertical(height * 0.522f, height * 0.056f), centre);
        DrawCentered(graphics, DescriptionText, bodyFont, whiteBrush, content.WithVertical(height * 0.570f, height * 0.030f), centre);

        if (threat is not null)
        {
            var line = string.IsNullOrWhiteSpace(threat.Name)
                ? threat.Detail
                : "威胁名称: " + threat.Name;
            DrawCentered(graphics, line, infoFont, infoBrush, content.WithVertical(height * 0.605f, height * 0.035f), centre);
        }

        DrawContinueButton(graphics, width, height);

        // Tell the user how to get out: the overlay no longer closes on any input.
        DrawCentered(graphics, HintText, hintFont, infoBrush, content.WithVertical(height * 0.902f, height * 0.030f), centre);
    }

    private static void DrawCentered(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds, StringFormat format)
    {
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawTopRightBack(Graphics graphics, int width, int height)
    {
        using var font = new Font("Microsoft YaHei", height * 0.032f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        graphics.DrawString(BackText, font, brush,
            new RectangleF(width * 0.860f, height * 0.058f, width * 0.098f, height * 0.040f), format);
    }

    /// <summary>
    /// Draws the outlined ACE wordmark. The Go version painted the text twice
    /// (white, then a smaller pass in the background colour); GDI+ can stroke a
    /// text path directly, which is sharper and independent of glyph metrics.
    /// </summary>
    private static void DrawWordmark(Graphics graphics, int width, int height)
    {
        var size = height * 0.140f;
        var centreY = height * 0.333f;

        using var family = new FontFamily("Arial Black");
        using var path = new GraphicsPath();
        path.AddString("ACE", family, (int)FontStyle.Regular, size,
            new PointF(width / 2f, centreY - size / 2f),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near });

        using var fillBrush = new SolidBrush(Color.White);
        using var outlinePen = new Pen(Background, Math.Max(2, size * 0.06f)) { LineJoin = LineJoin.Round };
        graphics.FillPath(fillBrush, path);
        graphics.DrawPath(outlinePen, path);
    }

    private void DrawContinueButton(Graphics graphics, int width, int height)
    {
        var buttonWidth = (int)(width * 0.125f);
        var buttonHeight = (int)(height * 0.061f);
        var bounds = new Rectangle(
            width / 2 - buttonWidth / 2,
            (int)(height * 0.849f) - buttonHeight / 2,
            buttonWidth,
            buttonHeight);

        // Single source of truth for the hit target and the painted shape.
        _buttonBounds = bounds;

        var radius = Math.Max(2, height * 0.009f);
        using var path = RoundedRectangle(bounds, radius);
        using var fill = new SolidBrush(_buttonHovered ? ButtonHover : ButtonFill);
        using var edge = new Pen(_buttonHovered ? Color.White : ButtonEdge, 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(edge, path);

        using var font = new Font("Microsoft YaHei", height * 0.035f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(ButtonText, font, brush, bounds, format);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            _host?.Dispose();
            _host = null;
        }
    }
}

internal static class RectangleExtensions
{
    public static RectangleF WithVertical(this RectangleF source, float top, float height) =>
        new(source.X, top, source.Width, height);
}
