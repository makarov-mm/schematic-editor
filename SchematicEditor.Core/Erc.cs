namespace SchematicEditor.Core;

public enum ErcSeverity { Warning, Error }

public sealed record ErcIssue(ErcSeverity Severity, string Message, Vec2 Location)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// Electrical rule check. MVP rules:
///  E1: unconnected symbol pin;
///  E2: dangling wire end;
///  E3: net with a single pin;
///  E4: both terminals of a source/battery on the same net (short circuit);
///  W1: symbol with default refdes still containing '?'.
/// </summary>
public static class ErcChecker
{
    public static List<ErcIssue> Check(SchematicDocument doc, NetlistResult netlist)
    {
        List<ErcIssue> issues = [];

        // E1: pins not present in any net with >= 2 connections.
        var connectedPins = new HashSet<(int, string)>();
        foreach (var net in netlist.Nets)
        {
            bool hasWires = net.Wires.Count > 0;
            foreach (var pin in net.Pins)
                if (hasWires || net.Pins.Count > 1)
                    connectedPins.Add((pin.Symbol.Id, pin.Pin.Name));
        }

        foreach (var sym in doc.Symbols)
        {
            foreach (var (pin, world) in sym.WorldPins())
            {
                if (!connectedPins.Contains((sym.Id, pin.Name)))
                    issues.Add(new ErcIssue(ErcSeverity.Error,
                        $"Unconnected pin {sym.RefDes}.{pin.Name}", world));
            }

            if (sym.RefDes.EndsWith('?'))
                issues.Add(new ErcIssue(ErcSeverity.Warning,
                    $"Symbol has no reference designator: {sym.RefDes}", sym.Position));
        }

        // E2: dangling wire ends.
        foreach (var p in netlist.DanglingWireEnds)
            issues.Add(new ErcIssue(ErcSeverity.Error,
                $"Dangling wire end at ({p.X:0.#}, {p.Y:0.#})", p));

        // E3: single-pin nets (a wire that reaches exactly one pin).
        foreach (var net in netlist.Nets)
            if (net.Pins.Count == 1 && net.Wires.Count > 0)
                issues.Add(new ErcIssue(ErcSeverity.Warning,
                    $"Net {net.Name} connects only one pin ({net.Pins[0]})", net.Pins[0].World));

        // E4: shorted two-terminal sources.
        foreach (var net in netlist.Nets)
        {
            var bySymbol = net.Pins
                .Where(p => p.Symbol.Definition.Name is "VSource" or "Battery")
                .GroupBy(p => p.Symbol.Id);
            foreach (var g in bySymbol)
            {
                if (g.Count() >= 2)
                    issues.Add(new ErcIssue(ErcSeverity.Error,
                        $"Source {g.First().Symbol.RefDes} is short-circuited (both terminals on net {net.Name})",
                        g.First().Symbol.Position));
            }
        }

        return issues;
    }
}
