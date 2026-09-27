using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Minimal SVG path parser that produces a GDI+ <see cref="GraphicsPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// Icons fetched from an icon library arrive as SVG path data. Embedding the
/// path rather than a bitmap keeps the icon sharp at every size the shell asks
/// for — the tray can request anything from 16 to 64 pixels depending on DPI,
/// and a single 32-pixel bitmap scaled down turns muddy.
/// </para>
/// <para>
/// Supports the commands icon libraries actually emit: M/m, L/l, H/h, V/v,
/// C/c, S/s, Q/q, T/t, A/a and Z/z. Elliptical arcs are converted from the SVG
/// endpoint parameterisation to centre parameterisation and then approximated
/// with cubic Bézier segments of at most 90° each, which is the standard
/// approach and is accurate to well under a pixel at icon sizes.
/// </para>
/// </remarks>
internal static class SvgPath
{
    /// <summary>
    /// Parses <paramref name="data"/> (coordinates in a 24×24 icon grid) into a
    /// path scaled to a square of <paramref name="size"/> pixels.
    /// </summary>
    public static GraphicsPath Parse24(string data, float size)
    {
        return Parse(data, size / 24f, size / 24f, 0f, 0f);
    }

    public static GraphicsPath Parse(string data, float scaleX, float scaleY, float offsetX, float offsetY)
    {
        var path = new GraphicsPath();
        var scanner = new Scanner(data);

        var currentX = 0f;
        var currentY = 0f;
        var startX = 0f;
        var startY = 0f;

        // Last control point of the previous curve, for the S/T shorthands.
        var lastControlX = 0f;
        var lastControlY = 0f;
        var lastCurveWasCubic = false;
        var lastCurveWasQuadratic = false;

        var command = ' ';
        while (scanner.MoveNext())
        {
            if (scanner.CurrentIsCommand)
            {
                command = scanner.Current;
                scanner.Advance();
            }
            else if (command == 'M')
            {
                command = 'L';
            }
            else if (command == 'm')
            {
                command = 'l';
            }

            var relative = char.IsLower(command);
            var pointX = relative ? currentX : 0f;
            var pointY = relative ? currentY : 0f;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    currentX = pointX + scanner.NextFloat();
                    currentY = pointY + scanner.NextFloat();
                    startX = currentX;
                    startY = currentY;
                    path.StartFigure();
                    break;
                }

                case 'L':
                {
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();
                    path.AddLine(Transform(currentX, currentY), Transform(x, y));
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'H':
                {
                    var x = pointX + scanner.NextFloat();
                    path.AddLine(Transform(currentX, currentY), Transform(x, currentY));
                    currentX = x;
                    break;
                }

                case 'V':
                {
                    var y = pointY + scanner.NextFloat();
                    path.AddLine(Transform(currentX, currentY), Transform(currentX, y));
                    currentY = y;
                    break;
                }

                case 'C':
                {
                    var c1x = pointX + scanner.NextFloat();
                    var c1y = pointY + scanner.NextFloat();
                    var c2x = pointX + scanner.NextFloat();
                    var c2y = pointY + scanner.NextFloat();
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();
                    path.AddBezier(
                        Transform(currentX, currentY),
                        Transform(c1x, c1y),
                        Transform(c2x, c2y),
                        Transform(x, y));
                    lastControlX = c2x;
                    lastControlY = c2y;
                    lastCurveWasCubic = true;
                    lastCurveWasQuadratic = false;
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'S':
                {
                    var c2x = pointX + scanner.NextFloat();
                    var c2y = pointY + scanner.NextFloat();
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();

                    // First control point is the reflection of the previous one.
                    var c1x = lastCurveWasCubic ? 2 * currentX - lastControlX : currentX;
                    var c1y = lastCurveWasCubic ? 2 * currentY - lastControlY : currentY;

                    path.AddBezier(
                        Transform(currentX, currentY),
                        Transform(c1x, c1y),
                        Transform(c2x, c2y),
                        Transform(x, y));
                    lastControlX = c2x;
                    lastControlY = c2y;
                    lastCurveWasCubic = true;
                    lastCurveWasQuadratic = false;
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'Q':
                {
                    var cx = pointX + scanner.NextFloat();
                    var cy = pointY + scanner.NextFloat();
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();
                    ApproximateQuadratic(path, currentX, currentY, cx, cy, x, y);
                    lastControlX = cx;
                    lastControlY = cy;
                    lastCurveWasQuadratic = true;
                    lastCurveWasCubic = false;
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'T':
                {
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();
                    var cx = lastCurveWasQuadratic ? 2 * currentX - lastControlX : currentX;
                    var cy = lastCurveWasQuadratic ? 2 * currentY - lastControlY : currentY;
                    ApproximateQuadratic(path, currentX, currentY, cx, cy, x, y);
                    lastControlX = cx;
                    lastControlY = cy;
                    lastCurveWasQuadratic = true;
                    lastCurveWasCubic = false;
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'A':
                {
                    var rx = scanner.NextFloat();
                    var ry = scanner.NextFloat();
                    var rotation = scanner.NextFloat();
                    var largeArc = scanner.NextFlag();
                    var sweep = scanner.NextFlag();
                    var x = pointX + scanner.NextFloat();
                    var y = pointY + scanner.NextFloat();
                    AddArc(path, currentX, currentY, rx, ry, rotation, largeArc, sweep, x, y);
                    currentX = x;
                    currentY = y;
                    break;
                }

                case 'Z':
                {
                    path.CloseFigure();
                    currentX = startX;
                    currentY = startY;
                    break;
                }

                default:
                    // Unknown command: stop rather than emit a wrong shape.
                    return path;
            }
        }

        return path;

        PointF Transform(float x, float y) => new(offsetX + x * scaleX, offsetY + y * scaleY);
    }

    private static void ApproximateQuadratic(
        GraphicsPath path, float x0, float y0, float cx, float cy, float x1, float y1)
    {
        // A quadratic is exactly representable as a cubic.
        var c1x = x0 + 2f / 3f * (cx - x0);
        var c1y = y0 + 2f / 3f * (cy - y0);
        var c2x = x1 + 2f / 3f * (cx - x1);
        var c2y = y1 + 2f / 3f * (cy - y1);
        path.AddBezier(new PointF(x0, y0), new PointF(c1x, c1y), new PointF(c2x, c2y), new PointF(x1, y1));
    }

    private static void AddArc(
        GraphicsPath path,
        float x1, float y1,
        float rx, float ry,
        float rotationDegrees,
        bool largeArc, bool sweep,
        float x2, float y2)
    {
        if (rx == 0 || ry == 0 || (x1 == x2 && y1 == y2))
        {
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        var phi = rotationDegrees * (float)(Math.PI / 180.0);
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        // Step 1: endpoint to centre parameterisation.
        var dx2 = (x1 - x2) / 2.0;
        var dy2 = (y1 - y2) / 2.0;
        var x1p = cosPhi * dx2 + sinPhi * dy2;
        var y1p = -sinPhi * dx2 + cosPhi * dy2;

        // Correct radii that are too small for the chord.
        var lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= (float)scale;
            ry *= (float)scale;
        }

        var rxSq = rx * rx;
        var rySq = ry * ry;
        var x1pSq = x1p * x1p;
        var y1pSq = y1p * y1p;

        var numerator = rxSq * rySq - rxSq * y1pSq - rySq * x1pSq;
        var denominator = rxSq * y1pSq + rySq * x1pSq;
        var factor = Math.Sqrt(Math.Max(0, numerator / denominator));
        if (largeArc == sweep)
        {
            factor = -factor;
        }

        var cxp = factor * (rx * y1p) / ry;
        var cyp = -factor * (ry * x1p) / rx;

        var midX = (x1 + x2) / 2.0;
        var midY = (y1 + y2) / 2.0;
        var cx = cosPhi * cxp - sinPhi * cyp + midX;
        var cy = sinPhi * cxp + cosPhi * cyp + midY;

        var theta1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
        var theta2 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);

        var delta = theta2 - theta1;
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        // Step 2: approximate with Bézier segments of at most 90°.
        var segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
        var segmentAngle = delta / segments;
        var alpha = 4.0 / 3.0 * Math.Tan(segmentAngle / 4.0);

        var start = theta1;
        for (var i = 0; i < segments; i++)
        {
            var end = start + segmentAngle;
            AddArcSegment(path, cx, cy, rx, ry, phi, start, end, alpha);
            start = end;
        }
    }

    private static void AddArcSegment(
        GraphicsPath path,
        double cx, double cy,
        float rx, float ry,
        double phi,
        double thetaStart, double thetaEnd,
        double alpha)
    {
        var cosStart = Math.Cos(thetaStart);
        var sinStart = Math.Sin(thetaStart);
        var cosEnd = Math.Cos(thetaEnd);
        var sinEnd = Math.Sin(thetaEnd);

        static (double X, double Y) Point(double cx, double cy, float rx, float ry, double phi, double cos, double sin) =>
            (cx + rx * cos * Math.Cos(phi) - ry * sin * Math.Sin(phi),
             cy + rx * cos * Math.Sin(phi) + ry * sin * Math.Cos(phi));

        var (p0x, p0y) = Point(cx, cy, rx, ry, phi, cosStart, sinStart);
        var (p3x, p3y) = Point(cx, cy, rx, ry, phi, cosEnd, sinEnd);

        static (double X, double Y) Derivative(float rx, float ry, double phi, double cos, double sin) =>
            (-rx * sin * Math.Cos(phi) - ry * cos * Math.Sin(phi),
             -rx * sin * Math.Sin(phi) + ry * cos * Math.Cos(phi));

        var (d0x, d0y) = Derivative(rx, ry, phi, cosStart, sinStart);
        var (d3x, d3y) = Derivative(rx, ry, phi, cosEnd, sinEnd);

        path.AddBezier(
            new PointF((float)p0x, (float)p0y),
            new PointF((float)(p0x + alpha * d0x), (float)(p0y + alpha * d0y)),
            new PointF((float)(p3x - alpha * d3x), (float)(p3y - alpha * d3y)),
            new PointF((float)p3x, (float)p3y));
    }

    /// <summary>
    /// Tokenises path data: numbers (with optional signs, decimals and implied
    /// separators) and command letters.
    /// </summary>
    private sealed class Scanner
    {
        private readonly string _data;
        private int _index;
        private char _current;
        private bool _hasCurrent;

        public Scanner(string data)
        {
            _data = data;
            _index = 0;
        }

        public char Current => _current;

        public bool CurrentIsCommand => _hasCurrent && char.IsLetter(_current);

        /// <summary>Positions the scanner on the next meaningful token.</summary>
        public bool MoveNext()
        {
            SkipSeparators();
            if (_index >= _data.Length)
            {
                _hasCurrent = false;
                return false;
            }

            _current = _data[_index];
            _hasCurrent = true;
            return true;
        }

        public void Advance()
        {
            if (_hasCurrent)
            {
                _index++;
                _hasCurrent = false;
            }
        }

        public float NextFloat()
        {
            SkipSeparators();

            var start = _index;
            if (_index < _data.Length && (_data[_index] == '+' || _data[_index] == '-'))
            {
                _index++;
            }

            // SVG allows a bare fractional part (".75") and separates adjacent
            // numbers with nothing but the sign or the next decimal point, as
            // in "A.75.75 0 0 1". So a second '.' ends the current number.
            var seenDecimalPoint = false;
            while (_index < _data.Length)
            {
                var c = _data[_index];
                if (char.IsDigit(c))
                {
                    _index++;
                }
                else if (c == '.' && !seenDecimalPoint)
                {
                    seenDecimalPoint = true;
                    _index++;
                }
                else
                {
                    break;
                }
            }

            // Exponent, e.g. 1e-3.
            if (_index < _data.Length && (_data[_index] == 'e' || _data[_index] == 'E'))
            {
                _index++;
                if (_index < _data.Length && (_data[_index] == '+' || _data[_index] == '-'))
                {
                    _index++;
                }

                while (_index < _data.Length && char.IsDigit(_data[_index]))
                {
                    _index++;
                }
            }

            _hasCurrent = false;
            return float.Parse(_data.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public bool NextFlag()
        {
            SkipSeparators();
            var flag = _data[_index] != '0';
            _index++;
            _hasCurrent = false;
            return flag;
        }

        private void SkipSeparators()
        {
            while (_index < _data.Length && (char.IsWhiteSpace(_data[_index]) || _data[_index] == ','))
            {
                _index++;
            }
        }
    }
}
