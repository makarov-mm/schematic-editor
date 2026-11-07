using System.Globalization;
using System.Text;

namespace SchematicEditor.Core;

/// <summary>A symbol pin in a net.</summary>
public sealed record NetPin(SymbolInstance Symbol, PinDefinition Pin, Vec2 World)
{
    public override string ToString() => $"{Symbol.DisplayName}.{Pin.Name}";
}

/// <summary>An electrical net: pins connected by wires or by touching each other.</summary>
public sealed class Net
{
    private readonly List<NetPin> _pins = [];
    private readonly List<Wire> _wires = [];

    /// <summary>"GND" for ground nets, otherwise N001, N002, ...</summary>
    public string Name { get; internal set; } = "";

    /// <summary>A ground symbol is attached: the net is the 0 V reference.</summary>
    public bool IsGround { get; internal set; }

    public IReadOnlyList<NetPin> Pins => _pins;
    public IReadOnlyList<Wire> Wires => _wires;

    /// <summary>The net connects something: it has a wire or more than one pin.</summary>
    public bool IsConnecting => _wires.Count > 0 || _pins.Count > 1;

    internal void Add(NetPin pin) => _pins.Add(pin);
    internal void Add(Wire wire) => _wires.Add(wire);
}

/// <summary>The connectivity of a document, see <see cref="NetlistExtractor"/>.</summary>
public sealed class NetlistResult
{
    private readonly Dictionary<Wire, Net> _netOfWire;
    private readonly Dictionary<(SymbolInstance, PinDefinition), Net> _netOfPin;

    internal NetlistResult(
        IReadOnlyList<Net> nets, IReadOnlyList<Vec2> junctions, IReadOnlyList<Vec2> danglingWireEnds,
        Dictionary<Wire, Net> netOfWire, Dictionary<(SymbolInstance, PinDefinition), Net> netOfPin)
    {
        Nets = nets;
        Junctions = junctions;
        DanglingWireEnds = danglingWireEnds;
        _netOfWire = netOfWire;
        _netOfPin = netOfPin;
    }

    /// <summary>The nets, sorted by name.</summary>
    public IReadOnlyList<Net> Nets { get; }

    /// <summary>Points where three or more connections meet, drawn as junction dots.</summary>
    public IReadOnlyList<Vec2> Junctions { get; }

    /// <summary>Wire ends that connect to nothing.</summary>
    public IReadOnlyList<Vec2> DanglingWireEnds { get; }

    public Net? NetOf(Wire wire) => _netOfWire.GetValueOrDefault(wire);

    public Net? NetOf(SymbolInstance symbol, PinDefinition pin) => _netOfPin.GetValueOrDefault((symbol, pin));

    /// <summary>The net at a point: a pin, a wire vertex or a point on a wire segment.</summary>
    public Net? FindNetAt(Vec2 point)
    {
        PointKey key = point.Key;
        return Nets.FirstOrDefault(net =>
            net.Pins.Any(p => p.World.Key == key) ||
            net.Wires.Any(w => w.Points.Any(v => v.Key == key) || w.Segments().Any(s => point.IsOnSegment(s.A, s.B))));
    }

    public string ToText()
    {
        if (Nets.Count == 0)
            return "(no nets)" + Environment.NewLine;

        var text = new StringBuilder();
        foreach (Net net in Nets)
            text.Append(net.Name).Append(": ").AppendJoin(", ", net.Pins).AppendLine();
        return text.ToString();
    }
}

/// <summary>
/// Extracts the nets of a document. Connection points (pins and wire vertices) that coincide are
/// connected, consecutive vertices of a wire are connected, and a connection point on the interior
/// of a wire segment connects to it (a T-connection).
/// </summary>
public static class NetlistExtractor
{
    public static NetlistResult Extract(SchematicDocument doc)
    {
        // Nodes: the connection points, coincident ones merged.
        var nodeAt = new Dictionary<PointKey, int>();
        var nodePoints = new List<Vec2>();
        int Node(Vec2 p)
        {
            if (!nodeAt.TryGetValue(p.Key, out int node))
            {
                node = nodePoints.Count;
                nodeAt.Add(p.Key, node);
                nodePoints.Add(p);
            }
            return node;
        }

        Wire[] wires = [.. doc.Wires];
        int[][] wireNodes = [.. wires.Select(w => w.Points.Select(Node).ToArray())];

        var pins = new List<(NetPin Pin, int Node)>();
        foreach (SymbolInstance symbol in doc.Symbols)
        {
            foreach ((PinDefinition pin, Vec2 world) in symbol.WorldPins())
                pins.Add((new NetPin(symbol, pin, world), Node(world)));
        }

        int nodeCount = nodePoints.Count;
        var pinsAt = new List<NetPin>?[nodeCount];
        foreach ((NetPin pin, int node) in pins)
            (pinsAt[node] ??= []).Add(pin);

        // Connectivity, and the number of connections at each node: a pin or a wire end counts
        // one, a wire passing through two.
        var sets = new DisjointSets(nodeCount);
        var degree = new int[nodeCount];

        foreach (int[] nodes in wireNodes)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                if (i + 1 < nodes.Length)
                    sets.Union(nodes[i], nodes[i + 1]);
                degree[nodes[i]] += i == 0 || i == nodes.Length - 1 ? 1 : 2;
            }
        }

        for (int w = 0; w < wires.Length; w++)
        {
            List<Vec2> points = wires[w].Points;
            for (int s = 0; s + 1 < points.Count; s++)
            {
                Vec2 a = points[s], b = points[s + 1];
                Rect2 box = Rect2.FromPoints(a, b).Inflate(0.1);
                for (int n = 0; n < nodeCount; n++)
                {
                    Vec2 p = nodePoints[n];
                    if (box.Contains(p) && p.Key != a.Key && p.Key != b.Key && p.IsOnSegment(a, b))
                    {
                        sets.Union(n, wireNodes[w][s]);
                        degree[n] += 2;
                    }
                }
            }
        }

        for (int n = 0; n < nodeCount; n++)
            degree[n] += pinsAt[n]?.Count ?? 0;

        // Nets, in the order of their first node with a pin, then of their first wire.
        var nets = new List<Net>();
        var netOfRoot = new Dictionary<int, Net>();
        Net NetAt(int node)
        {
            int root = sets.Find(node);
            if (!netOfRoot.TryGetValue(root, out Net? net))
            {
                net = new Net();
                netOfRoot.Add(root, net);
                nets.Add(net);
            }
            return net;
        }

        var netOfPin = new Dictionary<(SymbolInstance, PinDefinition), Net>();
        for (int n = 0; n < nodeCount; n++)
        {
            foreach (NetPin pin in pinsAt[n] ?? [])
            {
                Net net = NetAt(n);
                net.Add(pin);
                netOfPin[(pin.Symbol, pin.Pin)] = net;
            }
        }

        var netOfWire = new Dictionary<Wire, Net>();
        var dangling = new List<Vec2>();
        for (int w = 0; w < wires.Length; w++)
        {
            int[] nodes = wireNodes[w];
            if (nodes.Length == 0)
                continue;

            Net net = NetAt(nodes[0]);
            net.Add(wires[w]);
            netOfWire.Add(wires[w], net);

            if (degree[nodes[0]] <= 1)
                dangling.Add(nodePoints[nodes[0]]);
            if (degree[nodes[^1]] <= 1)
                dangling.Add(nodePoints[nodes[^1]]);
        }

        List<Vec2> junctions = [.. Enumerable.Range(0, nodeCount).Where(n => degree[n] >= 3).Select(n => nodePoints[n])];

        int counter = 1;
        foreach (Net net in nets)
        {
            net.IsGround = net.Pins.Any(p => p.Symbol.Definition.Kind == SymbolKind.Ground);
            net.Name = net.IsGround ? "GND" : "N" + counter++.ToString("000", CultureInfo.InvariantCulture);
        }

        return new NetlistResult([.. nets.OrderBy(n => n.Name, StringComparer.Ordinal)], junctions, dangling, netOfWire, netOfPin);
    }

    /// <summary>Union-find with path halving.</summary>
    private sealed class DisjointSets
    {
        private readonly int[] _parent;

        public DisjointSets(int count)
        {
            _parent = new int[count];
            for (int i = 0; i < count; i++)
                _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                _parent[ra] = rb;
        }
    }
}
