using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>
/// A rolling oscilloscope of the canvas's probes. The vertical scale follows the actual minimum and
/// maximum of the visible window rather than a symmetric ± peak, so that signals with a DC offset,
/// such as a rectifier's output, fill the screen and their ripple shows. Gridlines fall on a 1-2-5
/// ladder, with the zero line brighter whenever it is in view. Each trace is reduced to the minimum
/// and the maximum of every pixel column, so that no peak gets lost.
/// </summary>
public sealed class ScopeView : FrameworkElement
{
    private const double MarginY = 8;

    private SchematicCanvas? _canvas;
    private readonly List<Point> _points = [];

    /// <summary>The time span shown; at most <see cref="Probe.TraceSeconds"/>.</summary>
    public double WindowSeconds
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = 0.2;

    public void Attach(SchematicCanvas canvas)
    {
        _canvas = canvas;
        canvas.SimulationFrame += InvalidateVisual;
        canvas.SimulationStateChanged += InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10)
            return;

        dc.DrawRectangle(PlotStyle.Background, null, new Rect(0, 0, w, h));
        for (int i = 1; i < 10; i++)   // ten time divisions
            dc.DrawLine(PlotStyle.GridPen, new Point(w * i / 10, 0), new Point(w * i / 10, h));

        if (_canvas is not { Probes: { Count: > 0 } probes } canvas)
        {
            for (int i = 1; i < 6; i++)
                dc.DrawLine(PlotStyle.GridPen, new Point(0, h * i / 6), new Point(w, h * i / 6));
            FormattedText hint = Text("Arm the probe tool and click a wire (voltage) or a component (current).", 12, PlotStyle.TextBrush);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        // Up to the simulation time, or to the last sample once the simulation has stopped.
        double end = canvas.IsRunning ? canvas.SimTime : probes.Max(p => p.Trace.Count > 0 ? p.Trace.EndTime : 0);
        double start = end - WindowSeconds;

        (double lo, double hi) = VisibleRange(probes, start);
        double Y(double v) => h - MarginY - (v - lo) / (hi - lo) * (h - 2 * MarginY);
        double X(double t) => (t - start) / WindowSeconds * w;

        // Value gridlines on a 1-2-5 ladder, labelled at the right edge.
        double step = NiceCeiling((hi - lo) / 6);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + step * 1e-9; v += step)
        {
            double y = Y(v);
            bool zero = Math.Abs(v) < step * 1e-6;
            dc.DrawLine(zero ? PlotStyle.AxisPen : PlotStyle.GridPen, new Point(0, y), new Point(w, y));
            FormattedText label = Text(Units.Format(zero ? 0 : v), 10, PlotStyle.TextBrush);
            dc.DrawText(label, new Point(w - label.Width - 4, y - label.Height - 1));
        }

        foreach (Probe probe in probes)
        {
            ProbeTrace trace = probe.Trace;
            int first = trace.FirstIndexAt(start);
            if (trace.Count - first < 2)
                continue;

            ReduceToColumns(trace, first, X, Y);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(_points[0], isFilled: false, isClosed: false);
                context.PolyLineTo(_points.GetRange(1, _points.Count - 1), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, probe.Pen, geometry);
        }

        // Legend with the latest values.
        double legendY = 6;
        foreach (Probe probe in probes)
        {
            FormattedText legend = Text($"{probe.Label}  {Units.Format(probe.Trace.Last, probe.Unit)}", 12, probe.Brush);
            dc.DrawText(legend, new Point(8, legendY));
            legendY += legend.Height + 2;
        }

        string window = WindowSeconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{WindowSeconds:0.#} s")
            : string.Create(CultureInfo.InvariantCulture, $"{WindowSeconds * 1000:0.#} ms");
        FormattedText caption = Text(window, 11, PlotStyle.TextBrush);
        dc.DrawText(caption, new Point(w - caption.Width - 4, h - caption.Height - 2));
    }

    /// <summary>The value range of the samples from <paramref name="start"/> on, padded, and never empty.</summary>
    private static (double Low, double High) VisibleRange(IReadOnlyList<Probe> probes, double start)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (Probe probe in probes)
        {
            ProbeTrace trace = probe.Trace;
            for (int i = trace.FirstIndexAt(start); i < trace.Count; i++)
            {
                min = Math.Min(min, trace[i]);
                max = Math.Max(max, trace[i]);
            }
        }
        if (min > max)   // no samples yet
            (min, max) = (-1, 1);

        // A flat trace still gets some height.
        double span = max - min;
        double minSpan = Math.Max(1e-6, Math.Max(Math.Abs(max), Math.Abs(min)) * 0.02);
        if (span < minSpan)
        {
            double mid = (min + max) / 2;
            (min, max, span) = (mid - minSpan / 2, mid + minSpan / 2, minSpan);
        }
        return (min - span * 0.07, max + span * 0.07);
    }

    /// <summary>
    /// Fills _points with the trace from <paramref name="first"/> on: for every pixel column the
    /// samples with the lowest and the highest value, in the order they were taken.
    /// </summary>
    private void ReduceToColumns(ProbeTrace trace, int first, Func<double, double> x, Func<double, double> y)
    {
        _points.Clear();
        int column = int.MinValue, low = first, high = first;
        for (int i = first; i < trace.Count; i++)
        {
            int c = (int)Math.Floor(x(trace.TimeAt(i)));
            if (c != column)
            {
                AddColumn();
                (column, low, high) = (c, i, i);
            }
            else if (trace[i] < trace[low])
            {
                low = i;
            }
            else if (trace[i] > trace[high])
            {
                high = i;
            }
        }
        AddColumn();

        void AddColumn()
        {
            if (column == int.MinValue)
                return;
            AddPoint(Math.Min(low, high));
            if (low != high)
                AddPoint(Math.Max(low, high));
        }

        void AddPoint(int i) => _points.Add(new Point(x(trace.TimeAt(i)), y(trace[i])));
    }

    // The 1-2-5 step at or above v.
    private static double NiceCeiling(double v)
    {
        double decade = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / decade;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * decade;
    }

    private FormattedText Text(string text, double size, Brush brush) => PlotStyle.Text(this, text, size, brush);
}
