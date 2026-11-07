using System.Diagnostics.CodeAnalysis;

namespace SchematicEditor.Core;

/// <summary>
/// The built-in symbols, IEC 60617 style.
/// Symbol coordinates: origin at the symbol centre, Y down, grid pitch 5.
/// </summary>
public static class SymbolLibrary
{
    public static IReadOnlyList<SymbolDefinition> All { get; } = Build();

    private static readonly Dictionary<string, SymbolDefinition> ByName = All.ToDictionary(d => d.Name);

    public static SymbolDefinition Get(string name) =>
        TryGet(name, out SymbolDefinition? definition) ? definition : throw new KeyNotFoundException($"Unknown symbol '{name}'.");

    public static bool TryGet(string name, [NotNullWhen(true)] out SymbolDefinition? definition) =>
        ByName.TryGetValue(name, out definition);

    private static Vec2 V(double x, double y) => new(x, y);
    private static LinePrim L(double x1, double y1, double x2, double y2) => new(V(x1, y1), V(x2, y2));
    private static PinDefinition Pin(string name, double x, double y) => new(name, V(x, y));

    // Horizontal two-pin symbols have their pins at (-20, 0) and (20, 0), vertical ones at (0, -20) and (0, 20).
    private static PinDefinition[] Horizontal(string a = "1", string b = "2") => [Pin(a, -20, 0), Pin(b, 20, 0)];
    private static PinDefinition[] Vertical() => [Pin("+", 0, -20), Pin("-", 0, 20)];

    private static SymbolDefinition[] Build()
    {
        double d = 8.0 / Math.Sqrt(2.0);   // the lamp's cross ends on its circle

        return
        [
            new("Resistor", SymbolKind.Resistor, "R", "10k", Horizontal(),
            [
                L(-20, 0, -10, 0), L(10, 0, 20, 0),
                new PolyPrim([V(-10, -4), V(10, -4), V(10, 4), V(-10, 4)], Closed: true, Filled: false),
            ]),

            new("Capacitor", SymbolKind.Capacitor, "C", "100n", Horizontal(),
            [
                L(-20, 0, -2, 0), L(2, 0, 20, 0),
                L(-2, -7, -2, 7), L(2, -7, 2, 7),
            ]),

            // Four semicircles.
            new("Inductor", SymbolKind.Inductor, "L", "10m", Horizontal(),
            [
                new ArcPrim(V(-15, 0), 5, 180, 360),
                new ArcPrim(V(-5, 0), 5, 180, 360),
                new ArcPrim(V(5, 0), 5, 180, 360),
                new ArcPrim(V(15, 0), 5, 180, 360),
            ]),

            // Anode on the left.
            new("Diode", SymbolKind.Diode, "D", "1N4148", Horizontal("A", "K"),
            [
                L(-20, 0, -6, 0), L(6, 0, 20, 0),
                new PolyPrim([V(-6, -6), V(-6, 6), V(6, 0)], Closed: true, Filled: true),
                L(6, -6, 6, 6),
            ]),

            // Earth; the single pin on top.
            new("Ground", SymbolKind.Ground, "GND", "", [Pin("1", 0, 0)],
            [
                L(0, 0, 0, 6),
                L(-8, 6, 8, 6), L(-5, 9, 5, 9), L(-2, 12, 2, 12),
            ]),

            // DC voltage source: a circle with polarity marks.
            new("VSource", SymbolKind.DcSource, "V", "5V", Vertical(),
            [
                L(0, -20, 0, -10), L(0, 10, 0, 20),
                new CirclePrim(V(0, 0), 10, Filled: false),
                new TextPrim(V(0, -4), "+", 6),
                new TextPrim(V(0, 5), "−", 6),
            ]),

            // Sine source.
            new("ACSource", SymbolKind.AcSource, "V", "5V 50Hz", Vertical(),
            [
                L(0, -20, 0, -10), L(0, 10, 0, 20),
                new CirclePrim(V(0, 0), 10, Filled: false),
                new ArcPrim(V(-2.5, 0), 2.5, 180, 360),
                new ArcPrim(V(2.5, 0), 2.5, 0, 180),
            ]),

            // The long plate, on top, is the plus terminal.
            new("Battery", SymbolKind.DcSource, "BT", "9V", Vertical(),
            [
                L(0, -20, 0, -2), L(0, 2, 0, 20),
                L(-8, -2, 8, -2), L(-4, 2, 4, 2),
                new TextPrim(V(7, -7), "+", 5),
            ]),

            // A circle with a cross.
            new("Lamp", SymbolKind.Lamp, "E", "12V 5W", Horizontal(),
            [
                L(-20, 0, -8, 0), L(8, 0, 20, 0),
                new CirclePrim(V(0, 0), 8, Filled: false),
                L(-d, -d, d, d), L(-d, d, d, -d),
            ]),

            // Normally open; click it while the simulation runs.
            new("Switch", SymbolKind.Switch, "S", "", Horizontal(),
            [
                L(-20, 0, -10, 0), L(10, 0, 20, 0),
                new CirclePrim(V(-10, 0), 1.5, Filled: false),
                new CirclePrim(V(10, 0), 1.5, Filled: false),
                L(-8.7, -0.8, 8, -8),
            ],
            onPrimitives:
            [
                L(-20, 0, -10, 0), L(10, 0, 20, 0),
                new CirclePrim(V(-10, 0), 1.5, Filled: false),
                new CirclePrim(V(10, 0), 1.5, Filled: false),
                L(-8.6, -0.7, 8.6, -0.7),
            ]),

            // A rectangle with the line running through it.
            new("Fuse", SymbolKind.Fuse, "F", "1A", Horizontal(),
            [
                L(-20, 0, 20, 0),
                new PolyPrim([V(-10, -4), V(10, -4), V(10, 4), V(-10, 4)], Closed: true, Filled: false),
            ]),

            // Base on the left, collector top right, emitter bottom right.
            new("NPN", SymbolKind.NpnTransistor, "Q", "BC547", [Pin("B", -20, 0), Pin("C", 10, -20), Pin("E", 10, 20)],
            [
                L(-20, 0, -4, 0),
                L(-4, -10, -4, 10),
                L(-4, -4, 10, -14), L(10, -14, 10, -20),
                L(-4, 4, 10, 14), L(10, 14, 10, 20),
                new PolyPrim([V(10, 14), V(4.8, 12.7), V(7.1, 9.4)], Closed: true, Filled: true),
            ]),
        ];
    }
}
