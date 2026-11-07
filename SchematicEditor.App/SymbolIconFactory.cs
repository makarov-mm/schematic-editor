using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>
/// Draws a <see cref="SymbolDefinition"/> as a small vector icon, so the palette has the right icon
/// for every symbol, including any added to the library later.
/// </summary>
public static class SymbolIconFactory
{
    private static readonly Brush Ink = Paint.Brush(0x2B, 0x2B, 0x2B);

    public static ImageSource Create(SymbolDefinition definition, double size = 24)
    {
        Rect2 bounds = definition.LocalBounds.Inflate(2);
        double scale = (size - 2) / Math.Max(bounds.Width, bounds.Height);
        Vec2 center = bounds.Center;

        // The same stroke width on screen whatever the size of the symbol, so all icons look alike.
        Pen pen = Paint.Pen(Ink, 1.25 / scale, round: true);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, size / 2 - center.X * scale, size / 2 - center.Y * scale));
            PrimitiveRenderer.Draw(dc, definition.Primitives.Select(p => p.Map(static v => v)), pen, Ink, pixelsPerDip: 1.0);
            dc.Pop();
        }

        var image = new DrawingImage(visual.Drawing);
        image.Freeze();
        return image;
    }
}
