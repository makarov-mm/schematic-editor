using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>The frequency response of one voltage probe.</summary>
public sealed record AcTrace(string Label, Color Color, Complex[] Response);

/// <summary>A frequency sweep: the frequencies and one response per probe.</summary>
public sealed record AcSweep(double[] Frequencies, IReadOnlyList<AcTrace> Traces);

/// <summary>The result of an AC analysis: magnitude on log-log axes above, phase below, a curve per voltage probe.</summary>
public sealed class BodeWindow : Window
{
    public BodeWindow(AcSweep sweep)
    {
        Title = "AC Analysis";
        Width = 940;
        Height = 640;
        Background = PlotStyle.Background;
        Content = new BodePlot(sweep);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }
}

internal sealed class BodePlot(AcSweep sweep) : FrameworkElement
{
    private const double MarginLeft = 56;
    private const double MarginRight = 16;
    private const double MarginTop = 28;
    private const double MarginBottom = 30;
    private const double PanelGap = 26;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double[] f = sweep.Frequencies;
        if (w < 100 || h < 100 || sweep.Traces.Count == 0 || f.Length < 2)
            return;

        double plotWidth = w - MarginLeft - MarginRight;
        double plotsHeight = h - MarginTop - MarginBottom - PanelGap;
        double magnitudeTop = MarginTop, magnitudeHeight = plotsHeight * 0.62;
        double phaseTop = MarginTop + magnitudeHeight + PanelGap, phaseHeight = plotsHeight * 0.38;

        // The magnitude axis spans whole decades: at least two, at most eight.
        double magnitudeMin = sweep.Traces.SelectMany(t => t.Response).Min(v => Math.Max(v.Magnitude, 1e-9));
        double magnitudeMax = sweep.Traces.SelectMany(t => t.Response).Max(v => Math.Max(v.Magnitude, 1e-9));
        double logHigh = Math.Ceiling(Math.Log10(magnitudeMax));
        double logLow = Math.Clamp(Math.Floor(Math.Log10(magnitudeMin)), logHigh - 8, logHigh - 2);

        double logFMin = Math.Log10(f[0]), logFMax = Math.Log10(f[^1]);
        double X(double frequency) => MarginLeft + (Math.Log10(frequency) - logFMin) / (logFMax - logFMin) * plotWidth;
        double YMagnitude(double m) => magnitudeTop + magnitudeHeight - (Math.Clamp(Math.Log10(m), logLow, logHigh) - logLow) / (logHigh - logLow) * magnitudeHeight;
        double YPhase(double degrees) => phaseTop + phaseHeight - (degrees + 180.0) / 360.0 * phaseHeight;

        // Frequency decades, shared by both panels.
        for (double d = Math.Ceiling(logFMin); d <= Math.Floor(logFMax); d++)
        {
            double frequency = Math.Pow(10, d);
            double x = X(frequency);
            dc.DrawLine(PlotStyle.GridPen, new Point(x, magnitudeTop), new Point(x, magnitudeTop + magnitudeHeight));
            dc.DrawLine(PlotStyle.GridPen, new Point(x, phaseTop), new Point(x, phaseTop + phaseHeight));
            FormattedText label = Text(FormatFrequency(frequency), 11, PlotStyle.TextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, h - MarginBottom + 6));
        }

        // Magnitude decades.
        for (double d = logLow; d <= logHigh; d++)
        {
            double y = YMagnitude(Math.Pow(10, d));
            dc.DrawLine(PlotStyle.GridPen, new Point(MarginLeft, y), new Point(MarginLeft + plotWidth, y));
            FormattedText label = Text(FormatMagnitude(Math.Pow(10, d)), 11, PlotStyle.TextBrush);
            dc.DrawText(label, new Point(MarginLeft - label.Width - 6, y - label.Height / 2));
        }

        // Phase every 90 degrees, the zero line brighter.
        for (int degrees = -180; degrees <= 180; degrees += 90)
        {
            double y = YPhase(degrees);
            dc.DrawLine(degrees == 0 ? PlotStyle.AxisPen : PlotStyle.GridPen, new Point(MarginLeft, y), new Point(MarginLeft + plotWidth, y));
            FormattedText label = Text(degrees.ToString(CultureInfo.InvariantCulture) + "°", 11, PlotStyle.TextBrush);
            dc.DrawText(label, new Point(MarginLeft - label.Width - 6, y - label.Height / 2));
        }

        foreach (AcTrace trace in sweep.Traces)
        {
            Pen pen = Paint.Pen(Paint.Brush(trace.Color), 1.7, round: true);
            double[] phase = [.. trace.Response.Select(v => v.Phase * 180.0 / Math.PI)];
            dc.DrawGeometry(null, pen, Curve(f.Length, i => new Point(X(f[i]), YMagnitude(trace.Response[i].Magnitude)), _ => false));
            // The phase wraps around at ±180°: no line across the plot there.
            dc.DrawGeometry(null, pen, Curve(f.Length, i => new Point(X(f[i]), YPhase(phase[i])), i => Math.Abs(phase[i] - phase[i - 1]) > 180));
        }

        dc.DrawText(Text("MAGNITUDE", 10, PlotStyle.TextBrush), new Point(MarginLeft, magnitudeTop - 16));
        dc.DrawText(Text("PHASE", 10, PlotStyle.TextBrush), new Point(MarginLeft, phaseTop - 16));

        double legendY = magnitudeTop + 4;
        foreach (AcTrace trace in sweep.Traces)
        {
            FormattedText legend = Text(trace.Label, 12, Paint.Brush(trace.Color));
            dc.DrawText(legend, new Point(MarginLeft + plotWidth - legend.Width - 8, legendY));
            legendY += legend.Height + 2;
        }
    }

    /// <summary>A polyline through points 0 to count - 1 that starts afresh where <paramref name="breakBefore"/> says so.</summary>
    private static StreamGeometry Curve(int count, Func<int, Point> point, Func<int, bool> breakBefore)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(point(0), isFilled: false, isClosed: false);
            for (int i = 1; i < count; i++)
            {
                if (breakBefore(i))
                    context.BeginFigure(point(i), isFilled: false, isClosed: false);
                else
                    context.LineTo(point(i), isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static string FormatFrequency(double f) => f switch
    {
        >= 1e6 => string.Create(CultureInfo.InvariantCulture, $"{f / 1e6:0.#} MHz"),
        >= 1e3 => string.Create(CultureInfo.InvariantCulture, $"{f / 1e3:0.#} kHz"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{f:0.#} Hz"),
    };

    private static string FormatMagnitude(double m) => Units.Format(m, "V");

    private FormattedText Text(string text, double size, Brush brush) => PlotStyle.Text(this, text, size, brush);
}
