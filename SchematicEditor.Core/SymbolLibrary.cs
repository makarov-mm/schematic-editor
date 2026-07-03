namespace SchematicEditor.Core;

/// <summary>
/// Built-in symbol library, IEC 60617 style.
/// Local coordinates: origin at symbol center, Y down, grid pitch 5.
/// Pin connection points always lie on the grid.
/// </summary>
public static class SymbolLibrary
{
    public static IReadOnlyList<SymbolDefinition> All { get; } = Build();

    private static readonly Dictionary<string, SymbolDefinition> ByName =
        All.ToDictionary(d => d.Name);

    public static SymbolDefinition Get(string name) =>
        ByName.TryGetValue(name, out var d)
            ? d
            : throw new KeyNotFoundException($"Unknown symbol '{name}'.");

    private static Vec2 V(double x, double y) => new(x, y);
    private static LinePrim L(double x1, double y1, double x2, double y2) => new(V(x1, y1), V(x2, y2));

    private static List<SymbolDefinition> Build()
    {
        // Lamp: circle with cross.
        double d = 8.0 / Math.Sqrt(2.0);

        var list = new List<SymbolDefinition>
        {
            // Resistor (IEC rectangle), horizontal, pins at (-20,0) / (20,0).
            new("Resistor", "R", "10k",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    L(-20, 0, -10, 0), L(10, 0, 20, 0),
                    new PolyPrim([V(-10, -4), V(10, -4), V(10, 4), V(-10, 4)], Closed: true, Filled: false)
                ]),
            // Capacitor, horizontal.
            new("Capacitor", "C", "100n",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    L(-20, 0, -2, 0), L(2, 0, 20, 0),
                    L(-2, -7, -2, 7), L(2, -7, 2, 7)
                ]),
            // Inductor: four upper semicircles.
            new("Inductor", "L", "10m",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    new ArcPrim(V(-15, 0), 5, 180, 360),
                    new ArcPrim(V(-5, 0), 5, 180, 360),
                    new ArcPrim(V(5, 0), 5, 180, 360),
                    new ArcPrim(V(15, 0), 5, 180, 360)
                ]),
            // Diode, anode left.
            new("Diode", "D", "1N4148",
                [new PinDefinition("A", V(-20, 0)), new PinDefinition("K", V(20, 0))],
                [
                    L(-20, 0, -6, 0), L(6, 0, 20, 0),
                    new PolyPrim([V(-6, -6), V(-6, 6), V(6, 0)], Closed: true, Filled: true),
                    L(6, -6, 6, 6)
                ]),
            // Ground (earth), single pin on top.
            new("Ground", "GND", "",
                [new PinDefinition("1", V(0, 0))],
                [
                    L(0, 0, 0, 6),
                    L(-8, 6, 8, 6), L(-5, 9, 5, 9), L(-2, 12, 2, 12)
                ]),
            // DC voltage source: circle with polarity marks, vertical.
            new("VSource", "V", "5V",
                [new PinDefinition("+", V(0, -20)), new PinDefinition("-", V(0, 20))],
                [
                    L(0, -20, 0, -10), L(0, 10, 0, 20),
                    new CirclePrim(V(0, 0), 10, Filled: false),
                    new TextPrim(V(0, -4), "+", 6),
                    new TextPrim(V(0, 5), "\u2212", 6)
                ]),
            // AC sine source, vertical.
            new("ACSource", "V", "5V 50Hz",
                [new PinDefinition("+", V(0, -20)), new PinDefinition("-", V(0, 20))],
                [
                    L(0, -20, 0, -10), L(0, 10, 0, 20),
                    new CirclePrim(V(0, 0), 10, Filled: false),
                    new ArcPrim(V(-2.5, 0), 2.5, 180, 360),
                    new ArcPrim(V(2.5, 0), 2.5, 0, 180)
                ]),
            // Battery, vertical, long plate = plus on top.
            new("Battery", "BT", "9V",
                [new PinDefinition("+", V(0, -20)), new PinDefinition("-", V(0, 20))],
                [
                    L(0, -20, 0, -2), L(0, 2, 0, 20),
                    L(-8, -2, 8, -2), L(-4, 2, 4, 2),
                    new TextPrim(V(7, -7), "+", 5)
                ]),
            new("Lamp", "E", "12V 5W",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    L(-20, 0, -8, 0), L(8, 0, 20, 0),
                    new CirclePrim(V(0, 0), 8, Filled: false),
                    L(-d, -d, d, d), L(-d, d, d, -d)
                ]),
            // Switch (SPST, normally open). Click it while the simulation runs.
            new("Switch", "S", "",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    L(-20, 0, -10, 0), L(10, 0, 20, 0),
                    new CirclePrim(V(-10, 0), 1.5, Filled: false),
                    new CirclePrim(V(10, 0), 1.5, Filled: false),
                    L(-8.7, -0.8, 8, -8)
                ],
                onPrimitives:
                [
                    L(-20, 0, -10, 0), L(10, 0, 20, 0),
                    new CirclePrim(V(-10, 0), 1.5, Filled: false),
                    new CirclePrim(V(10, 0), 1.5, Filled: false),
                    L(-8.6, -0.7, 8.6, -0.7)
                ]),
            // Fuse (IEC): rectangle with line through it.
            new("Fuse", "F", "1A",
                [new PinDefinition("1", V(-20, 0)), new PinDefinition("2", V(20, 0))],
                [
                    L(-20, 0, 20, 0),
                    new PolyPrim([V(-10, -4), V(10, -4), V(10, 4), V(-10, 4)], Closed: true, Filled: false)
                ]),
            // NPN transistor: base left, collector top-right, emitter bottom-right.
            new("NPN", "Q", "BC547",
                [
                    new PinDefinition("B", V(-20, 0)),
                    new PinDefinition("C", V(10, -20)),
                    new PinDefinition("E", V(10, 20))
                ],
                [
                    L(-20, 0, -4, 0),
                    L(-4, -10, -4, 10),
                    L(-4, -4, 10, -14), L(10, -14, 10, -20),
                    L(-4, 4, 10, 14), L(10, 14, 10, 20),
                    new PolyPrim([V(10, 14), V(4.8, 12.7), V(7.1, 9.4)], Closed: true, Filled: true)
                ])
        };


        return list;
    }
}
