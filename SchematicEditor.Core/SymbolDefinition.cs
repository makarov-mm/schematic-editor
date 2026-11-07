namespace SchematicEditor.Core;

/// <summary>What a symbol is electrically: drives net naming, ERC and simulation.</summary>
public enum SymbolKind
{
    Resistor,
    Capacitor,
    Inductor,
    Diode,
    Ground,
    DcSource,
    AcSource,
    Lamp,
    Switch,
    Fuse,
    NpnTransistor,
}

public static class SymbolKindExtensions
{
    extension(SymbolKind kind)
    {
        public bool IsVoltageSource => kind is SymbolKind.DcSource or SymbolKind.AcSource;

        /// <summary>Ground symbols show no reference designator.</summary>
        public bool ShowsRefDes => kind != SymbolKind.Ground;
    }
}

/// <summary>A pin in symbol coordinates. Position is the connection point.</summary>
public sealed record PinDefinition(string Name, Vec2 Position);

/// <summary>
/// An immutable library symbol: geometry and pins. Instances refer to it, files by its name.
/// Symbol coordinates: origin at the symbol centre, Y down, grid pitch 5; pin connection points
/// lie on the grid.
/// </summary>
public sealed class SymbolDefinition(
    string name,
    SymbolKind kind,
    string refPrefix,
    string defaultValue,
    IReadOnlyList<PinDefinition> pins,
    IReadOnlyList<DrawPrimitive> primitives,
    IReadOnlyList<DrawPrimitive>? onPrimitives = null)
{
    public string Name { get; } = name;
    public SymbolKind Kind { get; } = kind;
    public string RefPrefix { get; } = refPrefix;
    public string DefaultValue { get; } = defaultValue;
    public IReadOnlyList<PinDefinition> Pins { get; } = pins;
    public IReadOnlyList<DrawPrimitive> Primitives { get; } = primitives;

    /// <summary>The look in the "on" state (a closed switch); null when it looks the same.</summary>
    public IReadOnlyList<DrawPrimitive>? OnPrimitives { get; } = onPrimitives;

    public Rect2 LocalBounds { get; } = ComputeBounds(pins, primitives);

    private static Rect2 ComputeBounds(IReadOnlyList<PinDefinition> pins, IReadOnlyList<DrawPrimitive> primitives)
    {
        Rect2 r = Rect2.Empty;
        foreach (PinDefinition pin in pins)
            r = r.Include(pin.Position);

        foreach (DrawPrimitive primitive in primitives)
        {
            r = primitive.Map(static p => p) switch
            {
                LinePrim l => r.Include(l.A).Include(l.B),
                PolyPrim poly => poly.Points.Aggregate(r, static (bounds, p) => bounds.Include(p)),
                CirclePrim c => r.Include(c.Center - new Vec2(c.Radius, c.Radius)).Include(c.Center + new Vec2(c.Radius, c.Radius)),
                TextPrim t => r.Include(t.Anchor),
                _ => r,
            };
        }

        return r.IsEmpty ? new Rect2(-5, -5, 5, 5) : r;
    }
}
