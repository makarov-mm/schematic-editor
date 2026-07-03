namespace SchematicEditor.Core;

/// <summary>
/// Time-domain circuit simulator built on Modified Nodal Analysis.
///
/// Reactive elements use backward-Euler companion models (L-stable, so stiff
/// circuits like an ideal source across a small resistance stay well-behaved).
/// Diodes are piecewise-linear (off / on with Von and Ron) and resolved by
/// state iteration within each time step. Switches read the live
/// <see cref="SymbolInstance.StateOn"/> flag, so the user can toggle them
/// while the simulation runs. Fuses accumulate no history: they blow the
/// moment their current rating is exceeded and stay blown until Reset.
///
/// The matrix is assembled and solved from scratch every step with dense
/// Gaussian elimination — at schematic scale (tens of nodes) this is
/// microseconds and keeps the code obvious.
/// </summary>
public sealed class CircuitSimulator
{
    private enum Kind { Resistor, Lamp, Fuse, Switch, Capacitor, Inductor, Diode, SourceDc, SourceAc }

    private sealed class Element
    {
        public required Kind Kind;
        public required SymbolInstance Symbol;
        public required int NodeA;          // pin 1 (or anode / plus)
        public required int NodeB;          // pin 2 (or cathode / minus)
        public double Value;                // R [Ohm], C [F], L [H], V [V] (DC amplitude)
        public double Frequency;            // AC only
        public double RatedPower;           // lamp
        public double RatedCurrent;         // fuse
        public int Branch = -1;             // extra MNA unknown (sources, inductors)

        // Per-step state.
        public double PrevVoltage;          // capacitor
        public double PrevCurrent;          // inductor
        public bool DiodeOn;
        public bool FuseBlown;
        public double Current;              // last solved current, A→B positive
    }

    private const double Gmin = 1e-9;
    private const double SwitchOnR = 1e-3;
    private const double FuseR = 1e-2;
    private const double DiodeVon = 0.7;
    private const double DiodeRon = 0.05;

    private readonly List<Element> _elements = [];
    private readonly Dictionary<Net, int> _netNode = [];
    private readonly Dictionary<int, Element> _bySymbolId = [];
    private readonly NetlistResult _netlist;
    private readonly int _nodeCount;        // non-ground nodes
    private readonly int _branchCount;
    private readonly double[,] _a;
    private readonly double[] _rhs;
    private readonly double[] _x;
    private readonly double[] _nodeVoltage; // index 0 = ground

    public double Time { get; private set; }
    public IReadOnlyList<string> Warnings { get; }

    private CircuitSimulator(NetlistResult netlist, List<Element> elements, Dictionary<Net, int> netNode, int nodeCount, List<string> warnings)
    {
        _netlist = netlist;
        _elements = elements;
        _netNode = netNode;
        _nodeCount = nodeCount;
        Warnings = warnings;

        int branch = 0;

        foreach (Element e in elements)
        {
            if (e.Kind is Kind.SourceDc or Kind.SourceAc or Kind.Inductor)
            {
                e.Branch = branch++;
            }
        }

        _branchCount = branch;

        int n = _nodeCount + _branchCount;
        _a = new double[n, n];
        _rhs = new double[n];
        _x = new double[n];
        _nodeVoltage = new double[_nodeCount + 1];

        foreach (var e in elements)
        {
            _bySymbolId[e.Symbol.Id] = e;
        }
    }

    /// <summary>
    /// Build a simulator from the document. Returns null when the circuit cannot
    /// be simulated at all; recoverable oddities are reported as warnings.
    /// </summary>
    public static CircuitSimulator? Build(SchematicDocument doc, NetlistResult netlist,
        out List<string> problems)
    {
        problems = [];
        List<string> warnings = [];

        // Node numbering: all nets that contain a Ground pin collapse to node 0.
        Dictionary<Net, int> netNode = [];
        int next = 1;
        foreach (var net in netlist.Nets)
        {
            bool grounded = net.Pins.Any(p => p.Symbol.Definition.Name == "Ground");
            netNode[net] = grounded ? 0 : next++;
        }

        if (netlist.Nets.All(n => netNode[n] != 0))
        {
            problems.Add("No ground: place a Ground symbol to define 0 V.");
            return null;
        }

        // Pin → net lookup.
        Dictionary<(int, string), Net> pinNet = [];
        foreach (var net in netlist.Nets)
            foreach (var pin in net.Pins)
                pinNet[(pin.Symbol.Id, pin.Pin.Name)] = net;

        List<Element> elements = [];
        bool anySource = false;

        foreach (var sym in doc.Symbols)
        {
            string name = sym.Definition.Name;
            if (name == "Ground") continue;

            if (name == "NPN")
            {
                warnings.Add($"{sym.RefDes}: transistors are not simulated (treated as open).");
                continue;
            }

            var pins = sym.Definition.Pins;
            if (pins.Count != 2) continue;

            if (!pinNet.TryGetValue((sym.Id, pins[0].Name), out var netA) ||
                !pinNet.TryGetValue((sym.Id, pins[1].Name), out var netB))
            {
                warnings.Add($"{sym.RefDes}: not fully connected, skipped.");
                continue;
            }

            var e = new Element
            {
                Kind = Kind.Resistor,
                Symbol = sym,
                NodeA = netNode[netA],
                NodeB = netNode[netB],
            };

            switch (name)
            {
                case "Resistor":
                    e.Kind = Kind.Resistor;
                    if (!Units.TryParse(sym.Value, out e.Value) || e.Value <= 0)
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 1k.");
                        e.Value = 1e3;
                    }
                    break;

                case "Lamp":
                    e.Kind = Kind.Lamp;
                    if (!Units.TryParseLampRating(sym.Value, out e.Value, out e.RatedPower))
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 12V 5W.");
                        e.Value = 12 * 12 / 5.0;
                        e.RatedPower = 5;
                    }
                    break;

                case "Fuse":
                    e.Kind = Kind.Fuse;
                    e.Value = FuseR;
                    if (!Units.TryParse(sym.Value, out e.RatedCurrent) || e.RatedCurrent <= 0)
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 1A.");
                        e.RatedCurrent = 1;
                    }
                    break;

                case "Switch":
                    e.Kind = Kind.Switch;
                    break;

                case "Capacitor":
                    e.Kind = Kind.Capacitor;
                    if (!Units.TryParse(sym.Value, out e.Value) || e.Value <= 0)
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 100n.");
                        e.Value = 100e-9;
                    }
                    break;

                case "Inductor":
                    e.Kind = Kind.Inductor;
                    if (!Units.TryParse(sym.Value, out e.Value) || e.Value <= 0)
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 10m.");
                        e.Value = 10e-3;
                    }
                    break;

                case "Diode":
                    e.Kind = Kind.Diode;
                    break;

                case "VSource" or "Battery":
                    e.Kind = Kind.SourceDc;
                    anySource = true;
                    if (!Units.TryParse(sym.Value, out e.Value))
                    {
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 5V.");
                        e.Value = 5;
                    }
                    break;

                case "ACSource":
                    e.Kind = Kind.SourceAc;
                    anySource = true;
                    if (!Units.TryParseAcSpec(sym.Value, out e.Value, out e.Frequency))
                        warnings.Add($"{sym.RefDes}: cannot parse '{sym.Value}', using 5V 50Hz.");
                    break;

                default:
                    warnings.Add($"{sym.RefDes}: '{name}' is not simulated.");
                    continue;
            }

            elements.Add(e);
        }

        if (!anySource)
        {
            problems.Add("No voltage source: add a Battery, VSource or ACSource.");
            return null;
        }

        return new CircuitSimulator(netlist, elements, netNode,
            netNode.Values.DefaultIfEmpty(0).Max(), warnings);
    }

    /// <summary>Reset time, reactive state, blown fuses and diode states.</summary>
    public void Reset()
    {
        Time = 0;
        foreach (var e in _elements)
        {
            e.PrevVoltage = 0;
            e.PrevCurrent = 0;
            e.DiodeOn = false;
            e.FuseBlown = false;
            e.Current = 0;
        }
        Array.Clear(_nodeVoltage);
    }

    /// <summary>Advance the simulation by one time step.</summary>
    public void Step(double dt)
    {
        Time += dt;

        // Diode PWL state iteration: assume states, solve, verify, flip, repeat.
        for (int iter = 0; ; iter++)
        {
            Assemble(dt);
            Solve();

            bool consistent = true;
            foreach (var e in _elements)
            {
                if (e.Kind != Kind.Diode) continue;
                double vd = V(e.NodeA) - V(e.NodeB);
                if (e.DiodeOn)
                {
                    double i = (vd - DiodeVon) / DiodeRon;
                    if (i < 0) { e.DiodeOn = false; consistent = false; }
                }
                else if (vd > DiodeVon)
                {
                    e.DiodeOn = true;
                    consistent = false;
                }
            }

            if (consistent || iter >= 12) break;
        }

        // Copy node voltages (index 0 stays 0 V).
        for (int i = 1; i <= _nodeCount; i++) _nodeVoltage[i] = _x[i - 1];

        // Element currents and state updates.
        foreach (var e in _elements)
        {
            double va = V(e.NodeA), vb = V(e.NodeB), vd = va - vb;
            switch (e.Kind)
            {
                case Kind.Resistor or Kind.Lamp:
                    e.Current = vd / e.Value;
                    break;

                case Kind.Fuse:
                    e.Current = e.FuseBlown ? vd * Gmin : vd / e.Value;
                    if (!e.FuseBlown && Math.Abs(e.Current) > e.RatedCurrent)
                    {
                        e.FuseBlown = true;
                        e.Current = 0;
                    }
                    break;

                case Kind.Switch:
                    e.Current = e.Symbol.StateOn ? vd / SwitchOnR : vd * Gmin;
                    break;

                case Kind.Capacitor:
                {
                    double g = e.Value / dt;
                    e.Current = g * (vd - e.PrevVoltage);
                    e.PrevVoltage = vd;
                    break;
                }

                case Kind.Inductor:
                    e.Current = _x[_nodeCount + e.Branch];
                    e.PrevCurrent = e.Current;
                    break;

                case Kind.Diode:
                    e.Current = e.DiodeOn ? (vd - DiodeVon) / DiodeRon : vd * Gmin;
                    break;

                case Kind.SourceDc or Kind.SourceAc:
                    // Branch current flows out of the + terminal through the circuit.
                    e.Current = -_x[_nodeCount + e.Branch];
                    break;
            }
        }
    }

    private double V(int node) => node == 0 ? 0 : _x[node - 1];

    private void Assemble(double dt)
    {
        Array.Clear(_a);
        Array.Clear(_rhs);

        // Gmin from every node to ground keeps floating subcircuits solvable.
        for (int i = 0; i < _nodeCount; i++) _a[i, i] += Gmin;

        foreach (var e in _elements)
        {
            switch (e.Kind)
            {
                case Kind.Resistor or Kind.Lamp:
                    StampConductance(e.NodeA, e.NodeB, 1.0 / e.Value);
                    break;

                case Kind.Fuse:
                    StampConductance(e.NodeA, e.NodeB, e.FuseBlown ? Gmin : 1.0 / e.Value);
                    break;

                case Kind.Switch:
                    StampConductance(e.NodeA, e.NodeB, e.Symbol.StateOn ? 1.0 / SwitchOnR : Gmin);
                    break;

                case Kind.Capacitor:
                {
                    double g = e.Value / dt;
                    StampConductance(e.NodeA, e.NodeB, g);
                    StampCurrent(e.NodeA, e.NodeB, g * e.PrevVoltage);
                    break;
                }

                case Kind.Diode:
                    if (e.DiodeOn)
                    {
                        StampConductance(e.NodeA, e.NodeB, 1.0 / DiodeRon);
                        StampCurrent(e.NodeA, e.NodeB, DiodeVon / DiodeRon);
                    }
                    else
                    {
                        StampConductance(e.NodeA, e.NodeB, Gmin);
                    }
                    break;

                case Kind.Inductor:
                {
                    int k = _nodeCount + e.Branch;
                    StampBranch(e.NodeA, e.NodeB, k);
                    _a[k, k] -= e.Value / dt;
                    _rhs[k] -= e.Value / dt * e.PrevCurrent;
                    break;
                }

                case Kind.SourceDc or Kind.SourceAc:
                {
                    int k = _nodeCount + e.Branch;
                    StampBranch(e.NodeA, e.NodeB, k);
                    _rhs[k] = e.Kind == Kind.SourceDc
                        ? e.Value
                        : e.Value * Math.Sin(2.0 * Math.PI * e.Frequency * Time);
                    break;
                }
            }
        }
    }

    private void StampConductance(int a, int b, double g)
    {
        if (a != 0) _a[a - 1, a - 1] += g;
        if (b != 0) _a[b - 1, b - 1] += g;
        if (a != 0 && b != 0)
        {
            _a[a - 1, b - 1] -= g;
            _a[b - 1, a - 1] -= g;
        }
    }

    /// <summary>Current source injecting into node A and out of node B.</summary>
    private void StampCurrent(int a, int b, double i)
    {
        if (a != 0) _rhs[a - 1] += i;
        if (b != 0) _rhs[b - 1] -= i;
    }

    /// <summary>KCL and KVL rows shared by voltage sources and inductors.</summary>
    private void StampBranch(int a, int b, int k)
    {
        if (a != 0) { _a[a - 1, k] += 1; _a[k, a - 1] += 1; }
        if (b != 0) { _a[b - 1, k] -= 1; _a[k, b - 1] -= 1; }
    }

    /// <summary>Dense Gaussian elimination with partial pivoting.</summary>
    private void Solve()
    {
        int n = _nodeCount + _branchCount;
        // Work on copies so Assemble can rebuild cleanly next iteration.
        var m = (double[,])_a.Clone();
        var b = (double[])_rhs.Clone();

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > best) { best = v; pivot = r; }
            }
            if (best < 1e-14)
                throw new InvalidOperationException("Singular matrix: circuit has no unique solution.");

            if (pivot != col)
            {
                for (int c = col; c < n; c++)
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
                b[r] -= f * b[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < n; c++) sum -= m[r, c] * _x[c];
            _x[r] = sum / m[r, r];
        }
    }

    /// <summary>Resolve the MNA node index that owns the given point (0 = ground), or null.</summary>
    public int? ResolveNode(Vec2 point) => _netlist.FindNetAt(point) is { } net && _netNode.TryGetValue(net, out int node) ? node : null;

    public double GetNodeVoltage(int node) => node == 0 ? 0.0 : _nodeVoltage[node];

    /// <summary>Voltage of the net that owns the given point, or null.</summary>
    public double? GetVoltageAt(Vec2 point) => ResolveNode(point) is { } node ? GetNodeVoltage(node) : null;

    /// <summary>Current through a two-terminal symbol (pin 1 → pin 2 positive), or null.</summary>
    public double? GetCurrent(SymbolInstance sym) => _bySymbolId.TryGetValue(sym.Id, out var e) ? e.Current : null;

    /// <summary>Lamp brightness 0..1 (rated power → 1).</summary>
    public double GetLampBrightness(SymbolInstance lamp)
    {
        if (!_bySymbolId.TryGetValue(lamp.Id, out var e) || e.Kind != Kind.Lamp) return 0;
        double v = V(e.NodeA) - V(e.NodeB);
        double power = v * v / e.Value;
        return Math.Clamp(power / e.RatedPower, 0, 1);
    }

    public bool IsFuseBlown(SymbolInstance fuse) => _bySymbolId.TryGetValue(fuse.Id, out var e) && e.FuseBlown;
}
