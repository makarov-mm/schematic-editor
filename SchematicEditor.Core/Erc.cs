using System.Globalization;

namespace SchematicEditor.Core;

public enum ErcSeverity { Warning, Error }

public sealed record ErcIssue(ErcSeverity Severity, string Message, Vec2 Location)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// Electrical rule check: unconnected pins, dangling wire ends, nets that reach a single pin,
/// short-circuited sources, and missing or duplicate reference designators.
/// </summary>
public static class ErcChecker
{
    public static List<ErcIssue> Check(SchematicDocument doc, NetlistResult netlist)
    {
        List<ErcIssue> issues = [];

        foreach (SymbolInstance symbol in doc.Symbols)
        {
            foreach ((PinDefinition pin, Vec2 world) in symbol.WorldPins())
            {
                if (netlist.NetOf(symbol, pin) is not { IsConnecting: true })
                    issues.Add(new(ErcSeverity.Error, $"Unconnected pin {symbol.DisplayName}.{pin.Name}", world));
            }

            if (symbol.Definition.Kind.ShowsRefDes && (symbol.RefDes.Length == 0 || symbol.RefDes.EndsWith('?')))
                issues.Add(new(ErcSeverity.Warning, $"Symbol has no reference designator: {symbol.DisplayName}", symbol.Position));
        }

        IEnumerable<SymbolInstance> duplicates = doc.Symbols
            .Where(s => s.Definition.Kind.ShowsRefDes && s.RefDes.Length > 0 && !s.RefDes.EndsWith('?'))
            .GroupBy(s => s.RefDes, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g);
        foreach (SymbolInstance symbol in duplicates)
            issues.Add(new(ErcSeverity.Warning, $"Duplicate reference designator {symbol.RefDes}", symbol.Position));

        foreach (Vec2 p in netlist.DanglingWireEnds)
            issues.Add(new(ErcSeverity.Error, string.Create(CultureInfo.InvariantCulture, $"Dangling wire end at ({p.X:0.#}, {p.Y:0.#})"), p));

        foreach (Net net in netlist.Nets)
        {
            if (net.Pins.Count == 1 && net.Wires.Count > 0)
                issues.Add(new(ErcSeverity.Warning, $"Net {net.Name} connects only one pin ({net.Pins[0]})", net.Pins[0].World));
        }

        // Both terminals on one net, or on two ground nets, which the simulation joins.
        foreach (SymbolInstance source in doc.Symbols.Where(s => s.Definition.Kind.IsVoltageSource))
        {
            Net? plus = netlist.NetOf(source, source.Definition.Pins[0]);
            Net? minus = netlist.NetOf(source, source.Definition.Pins[1]);
            if (plus is not null && minus is not null && (plus == minus || plus.IsGround && minus.IsGround))
                issues.Add(new(ErcSeverity.Error, $"Source {source.DisplayName} is short-circuited (both terminals on net {plus.Name})", source.Position));
        }

        return issues;
    }
}
