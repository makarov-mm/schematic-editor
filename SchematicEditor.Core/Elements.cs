namespace SchematicEditor.Core;

/// <summary>Base class for everything placed on a sheet.</summary>
public abstract class SchematicElement
{
    public int Id { get; internal set; }
    public abstract Rect2 Bounds { get; }
}

/// <summary>A placed instance of a library symbol.</summary>
public sealed class SymbolInstance : SchematicElement
{
    public SymbolDefinition Definition { get; }
    public Vec2 Position { get; set; }
    public Rotation Rotation { get; set; }
    public bool Mirror { get; set; }
    public string RefDes { get; set; }
    public string Value { get; set; }

    /// <summary>Interactive state (closed for switches). Persisted; toggled at run time.</summary>
    public bool StateOn { get; set; }

    public SymbolInstance(SymbolDefinition definition, Vec2 position)
    {
        Definition = definition;
        Position = position;
        RefDes = definition.RefPrefix + "?";
        Value = definition.DefaultValue;
    }

    public Vec2 ToWorld(Vec2 local) => Transform2.Apply(local, Rotation, Mirror, Position);

    /// <summary>Primitives to draw for the current state.</summary>
    public IReadOnlyList<DrawPrimitive> ActivePrimitives =>
        StateOn && Definition.OnPrimitives is { } on ? on : Definition.Primitives;

    /// <summary>World-space connection points of all pins, in definition order.</summary>
    public IEnumerable<(PinDefinition Pin, Vec2 World)> WorldPins()
    {
        foreach (var pin in Definition.Pins)
            yield return (pin, ToWorld(pin.Position));
    }

    public override Rect2 Bounds
    {
        get
        {
            var lb = Definition.LocalBounds;
            var r = Rect2.Empty;
            r = r.Include(ToWorld(new Vec2(lb.MinX, lb.MinY)));
            r = r.Include(ToWorld(new Vec2(lb.MaxX, lb.MinY)));
            r = r.Include(ToWorld(new Vec2(lb.MaxX, lb.MaxY)));
            r = r.Include(ToWorld(new Vec2(lb.MinX, lb.MaxY)));
            return r;
        }
    }

    /// <summary>Anchor points for the refdes (above) and value (below) labels, world space.</summary>
    public (Vec2 RefDes, Vec2 Value) LabelAnchors()
    {
        var b = Bounds;
        var c = b.Center;
        return (new Vec2(c.X, b.MinY - 4), new Vec2(c.X, b.MaxY + 4));
    }
}

/// <summary>A wire: an open polyline of grid-snapped vertices (orthogonal by construction).</summary>
public sealed class Wire : SchematicElement
{
    public List<Vec2> Points { get; }

    public Wire(IEnumerable<Vec2> points) => Points = new List<Vec2>(points);

    public IEnumerable<(Vec2 A, Vec2 B)> Segments()
    {
        for (int i = 0; i + 1 < Points.Count; i++)
            yield return (Points[i], Points[i + 1]);
    }

    public override Rect2 Bounds
    {
        get
        {
            var r = Rect2.Empty;

            foreach (Vec2 p in Points)
            {
                r = r.Include(p);
            }

            return r;
        }
    }

    public double DistanceTo(Vec2 p)
    {
        double best = double.MaxValue;

        foreach (var (a, b) in Segments())
        {
            best = Math.Min(best, p.DistanceToSegment(a, b));
        }

        return best;
    }
}
