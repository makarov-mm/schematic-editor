using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>
/// Renders a <see cref="SymbolDefinition"/> into a small vector icon.
/// The palette therefore gets a correct icon for every symbol automatically —
/// including any symbols added to the library later.
/// </summary>
public static class SymbolIconFactory
{
    public static ImageSource Create(SymbolDefinition def, double size = 24)
    {
        var visual = new DrawingVisual();

        var b = def.LocalBounds.Inflate(2);
        double scale = (size - 2) / Math.Max(b.Width, b.Height);
        var center = b.Center;

        // Stroke width chosen so every icon has the same optical weight
        // regardless of the symbol's world-space size.
        var stroke = new SolidColorBrush(Color.FromRgb(0x2b, 0x2b, 0x2b));
        stroke.Freeze();
        var pen = new Pen(stroke, 1.25 / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(size / 2, size / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-center.X, -center.Y));

            foreach (var prim in def.Primitives)
                DrawPrimitive(dc, prim, pen, stroke, scale);

            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        var drawing = visual.Drawing;
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static void DrawPrimitive(DrawingContext dc, DrawPrimitive prim, Pen pen, Brush fill, double scale)
    {
        switch (prim)
        {
            case LinePrim l:
                dc.DrawLine(pen, P(l.A), P(l.B));
                break;

            case PolyPrim poly:
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(P(poly.Points[0]), poly.Filled, poly.Closed);
                    for (int i = 1; i < poly.Points.Length; i++)
                        ctx.LineTo(P(poly.Points[i]), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(poly.Filled ? fill : null, pen, geo);
                break;
            }

            case CirclePrim c:
                dc.DrawEllipse(c.Filled ? fill : null, pen, P(c.Center), c.Radius, c.Radius);
                break;

            case ArcPrim arc:
            {
                var pts = arc.Flatten();
                for (int i = 0; i + 1 < pts.Length; i++)
                    dc.DrawLine(pen, P(pts[i]), P(pts[i + 1]));
                break;
            }

            case TextPrim t:
            {
                var ft = new FormattedText(t.Text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                    t.Height, fill, 1.0);
                dc.DrawText(ft, new Point(t.Anchor.X - ft.Width / 2, t.Anchor.Y - ft.Height / 2));
                break;
            }
        }

        static Point P(Vec2 v) => new(v.X, v.Y);
    }
}
