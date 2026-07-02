namespace SchematicEditor.Core;

/// <summary>A pin reference inside a net.</summary>
public sealed record NetPin(SymbolInstance Symbol, PinDefinition Pin, Vec2 World)
{
    public override string ToString() => $"{Symbol.RefDes}.{Pin.Name}";
}

/// <summary>An electrical net: a set of pins connected by wires or direct contact.</summary>
public sealed class Net
{
    public string Name { get; internal set; } = "";
    public List<NetPin> Pins { get; } = [];
    public List<Wire> Wires { get; } = [];
}

/// <summary>Result of connectivity extraction.</summary>
public sealed class NetlistResult
{
    public List<Net> Nets { get; } = [];

    /// <summary>Wire endpoints that connect to nothing (for ERC and junction rendering).</summary>
    public List<Vec2> DanglingWireEnds { get; } = [];

    /// <summary>Points where three or more connections meet (rendered as junction dots).</summary>
    public List<Vec2> Junctions { get; } = [];

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var net in Nets)
        {
            sb.Append(net.Name).Append(": ");
            sb.AppendLine(string.Join(", ", net.Pins.Select(p => p.ToString())));
        }
        if (Nets.Count == 0) sb.AppendLine("(no nets)");
        return sb.ToString();
    }
}

/// <summary>
/// Extracts nets from the document. Connectivity rules:
///  - coincident connection points (pins, wire vertices) are connected;
///  - consecutive vertices of a wire are connected;
///  - a connection point lying on the interior of a wire segment forms a T-connection.
/// Union-find keeps the whole pass close to O(n log n) in practice.
/// </summary>
public static class NetlistExtractor
{
    private sealed class UnionFind
    {
        private readonly int[] _parent;

        public UnionFind(int n)
        {
            _parent = new int[n];
            for (int i = 0; i < n; i++) _parent[i] = i;
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
            if (ra != rb) _parent[ra] = rb;
        }
    }

    public static NetlistResult Extract(SchematicDocument doc)
    {
        var result = new NetlistResult();

        // 1. Collect all connection points, merging coincident ones by quantized key.
        var nodeIndex = new Dictionary<(long, long), int>();
        List<Vec2> nodePoints = [];

        int NodeOf(Vec2 p)
        {
            var key = p.Key();
            if (!nodeIndex.TryGetValue(key, out int idx))
            {
                idx = nodePoints.Count;
                nodeIndex[key] = idx;
                nodePoints.Add(p);
            }
            return idx;
        }

        List<List<NetPin>> pinAtNode = [];
        void EnsurePinLists() { while (pinAtNode.Count < nodePoints.Count) pinAtNode.Add([]); }

        var wires = doc.Wires.ToList();
        List<int[]> wireNodeLists = [];

        foreach (var wire in wires)
            wireNodeLists.Add(wire.Points.Select(NodeOf).ToArray());

        foreach (var sym in doc.Symbols)
        {
            foreach (var (pin, world) in sym.WorldPins())
            {
                int n = NodeOf(world);
                EnsurePinLists();
                pinAtNode[n].Add(new NetPin(sym, pin, world));
            }
        }
        EnsurePinLists();

        var uf = new UnionFind(nodePoints.Count);

        // 2. Wire segments connect their endpoints.
        foreach (var nodes in wireNodeLists)
            for (int i = 0; i + 1 < nodes.Length; i++)
                uf.Union(nodes[i], nodes[i + 1]);

        // 3. T-connections: node lying on the interior of another wire's segment.
        List<int> tConnections = [];
        for (int w = 0; w < wires.Count; w++)
        {
            var wire = wires[w];
            var nodes = wireNodeLists[w];
            for (int s = 0; s + 1 < wire.Points.Count; s++)
            {
                Vec2 a = wire.Points[s], b = wire.Points[s + 1];
                var segBounds = Rect2.FromPoints(a, b).Inflate(0.1);
                for (int n = 0; n < nodePoints.Count; n++)
                {
                    Vec2 p = nodePoints[n];
                    if (!segBounds.Contains(p)) continue;
                    if (p.Key() == a.Key() || p.Key() == b.Key()) continue;
                    if (p.IsOnSegment(a, b))
                    {
                        uf.Union(n, nodes[s]);
                        tConnections.Add(n); // the segment passes through this node
                    }
                }
            }
        }

        // 4. Group nodes into nets; count connection degree per node.
        var degree = new int[nodePoints.Count];
        foreach (int n in tConnections) degree[n] += 2;
        foreach (var nodes in wireNodeLists)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                bool isEndpoint = i == 0 || i == nodes.Length - 1;
                degree[nodes[i]] += isEndpoint ? 1 : 2;
            }
        }
        for (int n = 0; n < nodePoints.Count; n++)
            degree[n] += pinAtNode[n].Count;

        var netByRoot = new Dictionary<int, Net>();
        Net NetOf(int node)
        {
            int root = uf.Find(node);
            if (!netByRoot.TryGetValue(root, out var net))
            {
                net = new Net();
                netByRoot[root] = net;
            }
            return net;
        }

        for (int n = 0; n < nodePoints.Count; n++)
        {
            if (pinAtNode[n].Count == 0) continue;
            NetOf(n).Pins.AddRange(pinAtNode[n]);
        }

        for (int w = 0; w < wires.Count; w++)
        {
            if (wireNodeLists[w].Length == 0) continue;
            var net = NetOf(wireNodeLists[w][0]);
            net.Wires.Add(wires[w]);
        }

        // 5. Junction dots and dangling wire ends.
        for (int w = 0; w < wires.Count; w++)
        {
            var nodes = wireNodeLists[w];
            if (nodes.Length == 0) continue;
            foreach (int end in new[] { nodes[0], nodes[^1] })
                if (degree[end] <= 1)
                    result.DanglingWireEnds.Add(nodePoints[end]);
        }
        for (int n = 0; n < nodePoints.Count; n++)
            if (degree[n] >= 3)
                result.Junctions.Add(nodePoints[n]);

        // 6. Name nets: prefer GND if a ground symbol is attached, otherwise N001, N002...
        int counter = 1;
        foreach (var net in netByRoot.Values.Where(x => x.Pins.Count > 0 || x.Wires.Count > 0))
        {
            net.Name = net.Pins.Any(p => p.Symbol.Definition.Name == "Ground")
                ? "GND"
                : $"N{counter++:000}";
            result.Nets.Add(net);
        }

        result.Nets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }
}
