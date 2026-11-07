using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>
/// Draws <see cref="DrawPrimitive"/>s with WPF: the canvas draws symbols with it, the palette its
/// icons. The primitives must be mapped (see <see cref="SymbolInstance.WorldPrimitives"/>), so
/// that arcs are already polylines.
/// </summary>
internal static class PrimitiveRenderer
{
    private static readonly Typeface Typeface = new("Segoe UI");

    /// <summary>Strokes with <paramref name="pen"/>; filled shapes and text use <paramref name="fill"/>.</summary>
    public static void Draw(DrawingContext dc, IEnumerable<DrawPrimitive> primitives, Pen pen, Brush fill, double pixelsPerDip)
    {
        foreach (DrawPrimitive primitive in primitives)
        {
            switch (primitive)
            {
                case LinePrim l:
                    dc.DrawLine(pen, l.A.ToPoint(), l.B.ToPoint());
                    break;

                case PolyPrim p:
                    dc.DrawGeometry(p.Filled ? fill : null, pen, Polyline(p));
                    break;

                case CirclePrim c:
                    dc.DrawEllipse(c.Filled ? fill : null, pen, c.Center.ToPoint(), c.Radius, c.Radius);
                    break;

                case TextPrim t:
                    var text = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, t.Height, fill, pixelsPerDip);
                    dc.DrawText(text, new Point(t.Anchor.X - text.Width / 2, t.Anchor.Y - text.Height / 2));
                    break;
            }
        }
    }

    private static StreamGeometry Polyline(PolyPrim p)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(p.Points[0].ToPoint(), p.Filled, p.Closed);
            foreach (Vec2 point in p.Points.AsSpan(1))
                context.LineTo(point.ToPoint(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }
}

internal static class Vec2Extensions
{
    extension(Vec2 v)
    {
        public Point ToPoint() => new(v.X, v.Y);
    }
}
