using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SchematicEditor.App;

/// <summary>Frozen brushes and pens: shareable, and cheaper to draw with than mutable ones.</summary>
internal static class Paint
{
    public static SolidColorBrush Brush(Color color) => Freeze(new SolidColorBrush(color));

    public static SolidColorBrush Brush(byte r, byte g, byte b, byte alpha = 0xFF) => Brush(Color.FromArgb(alpha, r, g, b));

    /// <summary>A pen; a round one has round caps and joins, as schematic lines do.</summary>
    public static Pen Pen(Brush brush, double thickness, bool round = false)
    {
        var pen = new Pen(brush, thickness);
        if (round)
        {
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
        }
        return Freeze(pen);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>The dark look of the oscilloscope and the Bode plot.</summary>
internal static class PlotStyle
{
    public static readonly Brush Background = Paint.Brush(0x12, 0x16, 0x1C);
    public static readonly Pen GridPen = Paint.Pen(Paint.Brush(0x26, 0x2E, 0x38), 1);
    public static readonly Pen AxisPen = Paint.Pen(Paint.Brush(0x4A, 0x57, 0x66), 1);
    public static readonly Brush TextBrush = Paint.Brush(0x9A, 0xA5, 0xB4);

    private static readonly Typeface Mono = new("Consolas");

    public static FormattedText Text(Visual visual, string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
}
