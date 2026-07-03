namespace SchematicEditor.Core;

/// <summary>A pin in symbol-local coordinates. Position is the connection point.</summary>
public sealed record PinDefinition(string Name, Vec2 Position);

/// <summary>
/// Immutable library symbol: geometry + pins. Instances reference definitions by name.
/// Local coordinate convention: origin at symbol center, grid pitch 5, Y down.
/// Pin connection points must lie on the grid.
/// </summary>
public sealed class SymbolDefinition(
    string name,
    string refPrefix,
    string defaultValue,
    IReadOnlyList<PinDefinition> pins,
    IReadOnlyList<DrawPrimitive> primitives,
    IReadOnlyList<DrawPrimitive>? onPrimitives = null)
{
    public string Name { get; } = name;
    public string RefPrefix { get; } = refPrefix;
    public string DefaultValue { get; } = defaultValue;
    public IReadOnlyList<PinDefinition> Pins { get; } = pins;
    public IReadOnlyList<DrawPrimitive> Primitives { get; } = primitives;

    /// <summary>Alternate primitives when the instance is in the "on" state (e.g. closed switch).</summary>
    public IReadOnlyList<DrawPrimitive>? OnPrimitives { get; } = onPrimitives;

    public Rect2 LocalBounds { get; } = ComputeBounds(pins, primitives);

    private static Rect2 ComputeBounds(IReadOnlyList<PinDefinition> pins, IReadOnlyList<DrawPrimitive> prims)
    {
        var r = Rect2.Empty;

        foreach (PinDefinition p in pins)
        {
            r = r.Include(p.Position);
        }

        foreach (var prim in prims)
        {
            switch (prim)
            {
                case LinePrim l:
                    r = r.Include(l.A).Include(l.B);
                    break;

                case PolyPrim poly:
                    foreach (Vec2 pt in poly.Points)
                    {
                        r = r.Include(pt);
                    }
                    break;

                case CirclePrim c:
                    r = r.Include(new Vec2(c.Center.X - c.Radius, c.Center.Y - c.Radius));
                    r = r.Include(new Vec2(c.Center.X + c.Radius, c.Center.Y + c.Radius));
                    break;

                case ArcPrim a:
                    foreach (Vec2 pt in a.Flatten(8))
                    {
                        r = r.Include(pt);
                    }
                    break;

                case TextPrim t:
                    r = r.Include(t.Anchor);
                    break;
            }
        }

        return r.IsEmpty ? new Rect2(-5, -5, 5, 5) : r;
    }
}
