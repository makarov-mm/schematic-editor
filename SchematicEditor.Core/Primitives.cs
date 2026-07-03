namespace SchematicEditor.Core;

/// <summary>
/// Backend-agnostic drawing primitives in symbol-local coordinates.
/// Renderers and exporters interpret these; the core never touches a GUI toolkit.
/// </summary>
public abstract record DrawPrimitive;

public sealed record LinePrim(Vec2 A, Vec2 B) : DrawPrimitive;

/// <summary>Closed polygon. Filled = solid body (e.g. diode triangle).</summary>
public sealed record PolyPrim(Vec2[] Points, bool Closed, bool Filled) : DrawPrimitive;

public sealed record CirclePrim(Vec2 Center, double Radius, bool Filled) : DrawPrimitive;

/// <summary>
/// Circular arc, angles in degrees, counter-clockwise from StartDeg to EndDeg
/// in symbol-local coordinates (Y down). Flattened to a polyline for rendering.
/// </summary>
public sealed record ArcPrim(Vec2 Center, double Radius, double StartDeg, double EndDeg) : DrawPrimitive
{
    /// <summary>Flatten to points in local coordinates.</summary>
    public Vec2[] Flatten(int segments = 12)
    {
        double a0 = StartDeg * Math.PI / 180.0;
        double a1 = EndDeg * Math.PI / 180.0;
        var pts = new Vec2[segments + 1];

        for (int i = 0; i <= segments; i++)
        {
            double a = a0 + (a1 - a0) * i / segments;
            pts[i] = new Vec2(Center.X + Radius * Math.Cos(a), Center.Y + Radius * Math.Sin(a));
        }

        return pts;
    }
}

/// <summary>Small text inside a symbol body (e.g. polarity mark). Anchor is the text center.</summary>
public sealed record TextPrim(Vec2 Anchor, string Text, double Height) : DrawPrimitive;
