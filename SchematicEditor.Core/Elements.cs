namespace SchematicEditor.Core;

/// <summary>Anything placed on a sheet.</summary>
public abstract class SchematicElement
{
    /// <summary>Unique within a document; 0 until the element is added to one.</summary>
    public int Id { get; internal set; }

    public abstract Rect2 Bounds { get; }
}

/// <summary>A placed library symbol.</summary>
public sealed class SymbolInstance(SymbolDefinition definition, Vec2 position) : SchematicElement
{
    public SymbolDefinition Definition { get; } = definition;
    public Vec2 Position { get; set; } = position;
    public Rotation Rotation { get; set; }

    /// <summary>Mirrored left to right before the rotation.</summary>
    public bool Mirror { get; set; }

    /// <summary>The reference designator; one ending in '?' gets numbered when the symbol is added.</summary>
    public string RefDes { get; set; } = definition.RefPrefix + "?";

    public string Value { get; set; } = definition.DefaultValue;

    /// <summary>The reference designator, or the symbol's name while it has none.</summary>
    public string DisplayName => RefDes.Length > 0 ? RefDes : Definition.Name;

    /// <summary>Interactive state: a closed switch. Saved with the document, toggled while simulating.</summary>
    public bool StateOn { get; set; }

    public IReadOnlyList<DrawPrimitive> ActivePrimitives =>
        StateOn && Definition.OnPrimitives is { } on ? on : Definition.Primitives;

    public Vec2 ToWorld(Vec2 local)
    {
        double x = Mirror ? -local.X : local.X;
        double y = local.Y;
        (x, y) = Rotation switch
        {
            Rotation.R90 => (-y, x),
            Rotation.R180 => (-x, -y),
            Rotation.R270 => (y, -x),
            _ => (x, y),
        };
        return new Vec2(x + Position.X, y + Position.Y);
    }

    /// <summary>The primitives of the current state in world coordinates, arcs flattened.</summary>
    public IEnumerable<DrawPrimitive> WorldPrimitives() => ActivePrimitives.Select(p => p.Map(ToWorld));

    /// <summary>The connection points of all pins in world coordinates, in definition order.</summary>
    public IEnumerable<(PinDefinition Pin, Vec2 World)> WorldPins() => Definition.Pins.Select(pin => (pin, ToWorld(pin.Position)));

    public override Rect2 Bounds
    {
        get
        {
            Rect2 local = Definition.LocalBounds;
            return Rect2.Empty
                .Include(ToWorld(new Vec2(local.MinX, local.MinY)))
                .Include(ToWorld(new Vec2(local.MaxX, local.MinY)))
                .Include(ToWorld(new Vec2(local.MaxX, local.MaxY)))
                .Include(ToWorld(new Vec2(local.MinX, local.MaxY)));
        }
    }

    /// <summary>Where the reference designator (its bottom centre) and the value (its top centre) go.</summary>
    public (Vec2 RefDes, Vec2 Value) LabelAnchors()
    {
        Rect2 b = Bounds;
        return (new Vec2(b.Center.X, b.MinY - 4), new Vec2(b.Center.X, b.MaxY + 4));
    }
}

/// <summary>A wire: an open polyline through grid points, orthogonal by construction.</summary>
public sealed class Wire(IEnumerable<Vec2> points) : SchematicElement
{
    public List<Vec2> Points { get; } = [.. points];

    public IEnumerable<(Vec2 A, Vec2 B)> Segments()
    {
        for (int i = 0; i + 1 < Points.Count; i++)
            yield return (Points[i], Points[i + 1]);
    }

    public override Rect2 Bounds => Points.Aggregate(Rect2.Empty, static (r, p) => r.Include(p));

    public double DistanceTo(Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < Points.Count; i++)
            best = Math.Min(best, p.DistanceToSegment(Points[i], Points[i + 1]));
        return best;
    }
}
