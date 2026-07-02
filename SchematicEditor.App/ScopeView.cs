using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SchematicEditor.App;

/// <summary>
/// Rolling oscilloscope for canvas probes. The vertical scale follows the
/// actual [min..max] of the visible window (not a symmetric ±peak), so
/// DC-offset signals like a rectifier rail fill the screen and their ripple
/// is visible. Gridlines land on a 1-2-5 ladder; the zero axis is drawn
/// whenever it falls inside the window. Traces decimate to roughly one
/// point per pixel.
/// </summary>
public sealed class ScopeView : FrameworkElement
{
    public double WindowSeconds { get; set; } = 0.2;

    private SchematicCanvas? _canvas;

    private static readonly Brush Bg = Frozen(Color.FromRgb(0x12, 0x16, 0x1C));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x26, 0x2E, 0x38), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0x4A, 0x57, 0x66), 1);
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x9A, 0xA5, 0xB4));
    private static readonly Typeface Mono = new("Consolas");

    private const double MarginY = 8;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }

    public void Attach(SchematicCanvas canvas)
    {
        _canvas = canvas;
        canvas.SimulationFrame += InvalidateVisual;
        canvas.SimulationStateChanged += InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        // Time graticule: 10 divisions.
        for (int i = 1; i < 10; i++)
            dc.DrawLine(GridPen, new Point(w * i / 10, 0), new Point(w * i / 10, h));

        var probes = _canvas?.Probes;
        if (_canvas is null || probes is null || probes.Count == 0)
        {
            for (int i = 1; i < 6; i++)
                dc.DrawLine(GridPen, new Point(0, h * i / 6), new Point(w, h * i / 6));
            var hint = Text("Arm the probe tool and click a wire (voltage) or a component (current).", 12, TextBrush);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        double tNow = _canvas.SimTime;
        if (!_canvas.IsRunning)                                  // frozen after stop
            foreach (var p in probes)
                if (p.Times.Count > 0)
                    tNow = Math.Max(tNow, p.Times[^1]);
        double t0 = tNow - WindowSeconds;

        // Range of the visible window across all traces.
        double vmin = double.MaxValue, vmax = double.MinValue;
        foreach (var p in probes)
        {
            var (times, values) = (p.Times, p.Values);
            for (int i = values.Count - 1; i >= 0; i--)
            {
                if (times[i] < t0) break;
                double v = values[i];
                if (v < vmin) vmin = v;
                if (v > vmax) vmax = v;
            }
        }
        if (vmin > vmax) { vmin = -1; vmax = 1; }               // no samples yet

        // Pad, and never let the span collapse (flat DC trace).
        double span = vmax - vmin;
        double minSpan = Math.Max(1e-6, Math.Max(Math.Abs(vmax), Math.Abs(vmin)) * 0.02);
        if (span < minSpan)
        {
            double mid = (vmin + vmax) / 2;
            vmin = mid - minSpan / 2;
            vmax = mid + minSpan / 2;
            span = minSpan;
        }
        double lo = vmin - span * 0.07;
        double hi = vmax + span * 0.07;

        double Y(double v) => h - MarginY - (v - lo) / (hi - lo) * (h - 2 * MarginY);
        double X(double t) => (t - t0) / WindowSeconds * w;

        // Value gridlines on a 1-2-5 ladder, labelled at the right edge.
        double step = NiceCeil((hi - lo) / 6);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + step * 1e-9; v += step)
        {
            double y = Y(v);
            bool isZero = Math.Abs(v) < step * 1e-6;
            dc.DrawLine(isZero ? AxisPen : GridPen, new Point(0, y), new Point(w, y));
            var label = Text(FormatValue(v), 10, TextBrush);
            dc.DrawText(label, new Point(w - label.Width - 4, y - label.Height - 1));
        }

        // Traces.
        foreach (var p in probes)
        {
            var (times, values) = (p.Times, p.Values);
            int count = values.Count;
            if (count < 2) continue;

            int first = count - 1;
            while (first > 0 && times[first - 1] >= t0) first--;
            int visible = count - first;
            if (visible < 2) continue;

            int stride = Math.Max(1, visible / Math.Max(64, (int)w));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(X(times[first]), Y(values[first])), false, false);
                for (int i = first + stride; i < count; i += stride)
                    ctx.LineTo(new Point(X(times[i]), Y(values[i])), true, false);
                ctx.LineTo(new Point(X(times[count - 1]), Y(values[count - 1])), true, false);
            }
            geo.Freeze();

            var pen = new Pen(new SolidColorBrush(p.Color), 1.6)
            {
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        // Legend with live values.
        double ly = 6;
        foreach (var p in probes)
        {
            var brush = new SolidColorBrush(p.Color);
            brush.Freeze();
            string unit = p.IsCurrent ? "A" : "V";
            var ft = Text($"{p.Label}  {SchematicCanvas.FormatSi(p.LastValue, unit)}", 12, brush);
            dc.DrawText(ft, new Point(8, ly));
            ly += ft.Height + 2;
        }

        // Time window caption.
        var win = Text(WindowSeconds >= 1 ? $"{WindowSeconds:0.#} s" : $"{WindowSeconds * 1000:0.#} ms", 11, TextBrush);
        dc.DrawText(win, new Point(w - win.Width - 4, h - win.Height - 2));
    }

    private static string FormatValue(double v)
    {
        double a = Math.Abs(v);
        if (a < 1e-12) return "0";
        (double m, string s) = a switch
        {
            >= 1e6 => (1e-6, "M"),
            >= 1e3 => (1e-3, "k"),
            >= 1 => (1.0, ""),
            >= 1e-3 => (1e3, "m"),
            >= 1e-6 => (1e6, "\u00b5"),
            _ => (1e9, "n"),
        };
        return (v * m).ToString("0.###", CultureInfo.InvariantCulture) + s;
    }

    private static double NiceCeil(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Mono, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
