namespace SchematicEditor.Core;

/// <summary>
/// Backend-agnostic drawing primitives in symbol coordinates (origin at the symbol centre,
/// Y down). Renderers and exporters interpret them; the core never touches a GUI toolkit.
/// </summary>
public abstract record DrawPrimitive
{
    /// <summary>
    /// The primitive moved by a rigid motion, e.g. from symbol into world coordinates. Arcs come
    /// back flattened to polylines, so consumers of mapped primitives never see an arc; text stays
    /// upright and only its anchor moves.
    /// </summary>
    public abstract DrawPrimitive Map(Func<Vec2, Vec2> map);
}

public sealed record LinePrim(Vec2 A, Vec2 B) : DrawPrimitive
{
    public override DrawPrimitive Map(Func<Vec2, Vec2> map) => new LinePrim(map(A), map(B));
}

/// <summary>A polyline, or a polygon when Closed. Filled polygons are solid (e.g. the diode triangle).</summary>
public sealed record PolyPrim(Vec2[] Points, bool Closed, bool Filled) : DrawPrimitive
{
    public override DrawPrimitive Map(Func<Vec2, Vec2> map) => this with { Points = [.. Points.Select(map)] };
}

public sealed record CirclePrim(Vec2 Center, double Radius, bool Filled) : DrawPrimitive
{
    public override DrawPrimitive Map(Func<Vec2, Vec2> map) => this with { Center = map(Center) };
}

/// <summary>A circular arc from StartDeg to EndDeg, angles measured from +X towards +Y.</summary>
public sealed record ArcPrim(Vec2 Center, double Radius, double StartDeg, double EndDeg) : DrawPrimitive
{
    private const int Segments = 12;

    public Vec2[] Flatten()
    {
        double a0 = StartDeg * Math.PI / 180.0;
        double a1 = EndDeg * Math.PI / 180.0;
        var points = new Vec2[Segments + 1];
        for (int i = 0; i <= Segments; i++)
        {
            double a = a0 + (a1 - a0) * i / Segments;
            points[i] = new Vec2(Center.X + Radius * Math.Cos(a), Center.Y + Radius * Math.Sin(a));
        }
        return points;
    }

    public override DrawPrimitive Map(Func<Vec2, Vec2> map) =>
        new PolyPrim([.. Flatten().Select(map)], Closed: false, Filled: false);
}

/// <summary>A short text inside a symbol body (e.g. a polarity mark), centred on Anchor.</summary>
public sealed record TextPrim(Vec2 Anchor, string Text, double Height) : DrawPrimitive
{
    public override DrawPrimitive Map(Func<Vec2, Vec2> map) => this with { Anchor = map(Anchor) };
}
