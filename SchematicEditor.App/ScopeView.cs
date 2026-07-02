using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SchematicEditor.App;

/// <summary>
/// Rolling oscilloscope for canvas probes. Autoscaled vertically (1-2-5 ladder),
/// fixed time window, decimated to roughly one point per pixel while drawing.
/// </summary>
public sealed class ScopeView : FrameworkElement
{
    public double WindowSeconds { get; set; } = 0.2;

    private SchematicCanvas? _canvas;

    private static readonly Brush Bg = Frozen(Color.FromRgb(0x12, 0x16, 0x1C));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x26, 0x2E, 0x38), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0x3A, 0x45, 0x52), 1);
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x9A, 0xA5, 0xB4));
    private static readonly Typeface Mono = new("Consolas");

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

        // Graticule: 10 x 6 divisions.
        for (int i = 1; i < 10; i++)
            dc.DrawLine(GridPen, new Point(w * i / 10, 0), new Point(w * i / 10, h));
        for (int i = 1; i < 6; i++)
            dc.DrawLine(GridPen, new Point(0, h * i / 6), new Point(w, h * i / 6));
        dc.DrawLine(AxisPen, new Point(0, h / 2), new Point(w, h / 2));

        var probes = _canvas?.Probes;
        if (_canvas is null || probes is null || probes.Count == 0)
        {
            var hint = Text("Arm the probe tool and click a wire (voltage) or a component (current).", 12, TextBrush);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        double tNow = _canvas.SimTime;
        double t0 = tNow - WindowSeconds;

        // Vertical autoscale over the visible window, 1-2-5 ladder.
        double peak = 1e-6;
        foreach (var p in probes)
        {
            var (times, values) = (p.Times, p.Values);
            for (int i = values.Count - 1; i >= 0; i--)
            {
                if (times[i] < t0) break;
                peak = Math.Max(peak, Math.Abs(values[i]));
            }
        }
        double scale = NiceCeil(peak * 1.05);

        double X(double t) => (t - t0) / WindowSeconds * w;
        double Y(double v) => h / 2 - v / scale * (h / 2 - 6);

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

        // Scale captions.
        var top = Text($"+{FormatScale(scale)}", 11, TextBrush);
        dc.DrawText(top, new Point(w - top.Width - 6, 4));
        var bottom = Text($"\u2212{FormatScale(scale)}", 11, TextBrush);
        dc.DrawText(bottom, new Point(w - bottom.Width - 6, h - bottom.Height - 4));
        var win = Text(WindowSeconds >= 1 ? $"{WindowSeconds:0.#} s" : $"{WindowSeconds * 1000:0.#} ms", 11, TextBrush);
        dc.DrawText(win, new Point(w - win.Width - 6, h / 2 - win.Height - 2));
    }

    private static string FormatScale(double s) =>
        s >= 1 ? $"{s:0.##}" : s >= 1e-3 ? $"{s * 1e3:0.##}m" : $"{s * 1e6:0.##}\u00b5";

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
