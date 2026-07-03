using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace SchematicEditor.App;

/// <summary>
/// AC analysis result window: log-log magnitude on top, phase below, one curve per voltage
/// probe. Everything is drawn by hand in one FrameworkElement - same approach as the scope.
/// </summary>
public sealed class BodeWindow : Window
{
    public BodeWindow(List<(string Label, Color Color, double[] Freq, Complex[] Response)> traces)
    {
        Title = "AC Analysis";
        Width = 940;
        Height = 640;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1C));
        Content = new BodePlot(traces);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

internal sealed class BodePlot : FrameworkElement
{
    private readonly List<(string Label, Color Color, double[] Freq, Complex[] Response)> _traces;

    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x26, 0x2E, 0x38), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0x4A, 0x57, 0x66), 1);
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4)));
    private static readonly Typeface Mono = new("Consolas");

    private const double MarginLeft = 56;
    private const double MarginRight = 16;
    private const double MarginTop = 28;
    private const double MarginBottom = 30;
    private const double PanelGap = 26;

    public BodePlot(List<(string, Color, double[], Complex[])> traces) => _traces = traces;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 100 || h < 100 || _traces.Count == 0 || _traces[0].Freq.Length < 2) { return; }

        double[] freq = _traces[0].Freq;
        double fMin = freq[0], fMax = freq[^1];
        double plotW = w - MarginLeft - MarginRight;
        double magH = (h - MarginTop - MarginBottom - PanelGap) * 0.62;
        double phH = (h - MarginTop - MarginBottom - PanelGap) * 0.38;
        double magTop = MarginTop;
        double phTop = MarginTop + magH + PanelGap;

        // Magnitude range across all traces, clamped to sane decades.
        double magMin = double.MaxValue, magMax = double.MinValue;
        foreach (var t in _traces)
        {
            foreach (Complex v in t.Response)
            {
                double m = Math.Max(v.Magnitude, 1e-9);
                magMin = Math.Min(magMin, m);
                magMax = Math.Max(magMax, m);
            }
        }
        double logLo = Math.Floor(Math.Log10(magMin));
        double logHi = Math.Ceiling(Math.Log10(magMax));
        if (logHi - logLo < 2) { logLo = logHi - 2; }
        if (logHi - logLo > 8) { logLo = logHi - 8; }

        double X(double f) => MarginLeft + (Math.Log10(f) - Math.Log10(fMin)) / (Math.Log10(fMax) - Math.Log10(fMin)) * plotW;
        double YMag(double m) => magTop + magH - (Math.Log10(Math.Clamp(m, Math.Pow(10, logLo), Math.Pow(10, logHi))) - logLo) / (logHi - logLo) * magH;
        double YPh(double deg) => phTop + phH - (deg + 180.0) / 360.0 * phH;

        // Frequency decades: vertical grid shared by both panels.
        for (double d = Math.Ceiling(Math.Log10(fMin)); d <= Math.Floor(Math.Log10(fMax)); d++)
        {
            double f = Math.Pow(10, d);
            double x = X(f);
            dc.DrawLine(GridPen, new Point(x, magTop), new Point(x, magTop + magH));
            dc.DrawLine(GridPen, new Point(x, phTop), new Point(x, phTop + phH));
            var label = Text(FormatFreq(f), 11, TextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, h - MarginBottom + 6));
        }

        // Magnitude decades.
        for (double d = logLo; d <= logHi; d++)
        {
            double y = YMag(Math.Pow(10, d));
            dc.DrawLine(GridPen, new Point(MarginLeft, y), new Point(MarginLeft + plotW, y));
            var label = Text(FormatMag(Math.Pow(10, d)), 11, TextBrush);
            dc.DrawText(label, new Point(MarginLeft - label.Width - 6, y - label.Height / 2));
        }

        // Phase grid: every 90 degrees, zero line brighter.
        for (int deg = -180; deg <= 180; deg += 90)
        {
            double y = YPh(deg);
            dc.DrawLine(deg == 0 ? AxisPen : GridPen, new Point(MarginLeft, y), new Point(MarginLeft + plotW, y));
            var label = Text($"{deg}\u00b0", 11, TextBrush);
            dc.DrawText(label, new Point(MarginLeft - label.Width - 6, y - label.Height / 2));
        }

        foreach (var t in _traces)
        {
            var pen = new Pen(new SolidColorBrush(t.Color), 1.7) { LineJoin = PenLineJoin.Round };
            pen.Freeze();

            var mag = new StreamGeometry();
            using (var ctx = mag.Open())
            {
                ctx.BeginFigure(new Point(X(t.Freq[0]), YMag(t.Response[0].Magnitude)), false, false);
                for (int i = 1; i < t.Freq.Length; i++) { ctx.LineTo(new Point(X(t.Freq[i]), YMag(t.Response[i].Magnitude)), true, false); }
            }
            mag.Freeze();
            dc.DrawGeometry(null, pen, mag);

            var ph = new StreamGeometry();
            using (var ctx = ph.Open())
            {
                ctx.BeginFigure(new Point(X(t.Freq[0]), YPh(t.Response[0].Phase * 180.0 / Math.PI)), false, false);
                for (int i = 1; i < t.Freq.Length; i++) { ctx.LineTo(new Point(X(t.Freq[i]), YPh(t.Response[i].Phase * 180.0 / Math.PI)), true, false); }
            }
            ph.Freeze();
            dc.DrawGeometry(null, pen, ph);
        }

        // Titles and legend.
        dc.DrawText(Text("MAGNITUDE", 10, TextBrush), new Point(MarginLeft, magTop - 16));
        dc.DrawText(Text("PHASE", 10, TextBrush), new Point(MarginLeft, phTop - 16));
        double ly = magTop + 4;
        foreach (var t in _traces)
        {
            var brush = new SolidColorBrush(t.Color);
            brush.Freeze();
            var ft = Text(t.Label, 12, brush);
            dc.DrawText(ft, new Point(MarginLeft + plotW - ft.Width - 8, ly));
            ly += ft.Height + 2;
        }
    }

    private static string FormatFreq(double f) => f >= 1e6 ? $"{f / 1e6:0.#} MHz" : f >= 1e3 ? $"{f / 1e3:0.#} kHz" : $"{f:0.#} Hz";

    private static string FormatMag(double m) => m >= 1 ? $"{m:0.###} V" : m >= 1e-3 ? $"{m * 1e3:0.###} mV" : $"{m * 1e6:0.###} \u00b5V";

    private FormattedText Text(string s, double size, Brush brush) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
