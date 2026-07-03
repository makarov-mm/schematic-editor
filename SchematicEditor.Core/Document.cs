namespace SchematicEditor.Core;

/// <summary>
/// The schematic document: owns all elements and assigns ids / reference designators.
/// Mutations go through commands (see Commands.cs) so that undo/redo stays consistent.
/// </summary>
public sealed class SchematicDocument
{
    public const double Grid = 5.0;

    private readonly List<SchematicElement> _elements = [];
    private int _nextId = 1;

    public IReadOnlyList<SchematicElement> Elements => _elements;
    public IEnumerable<SymbolInstance> Symbols => _elements.OfType<SymbolInstance>();
    public IEnumerable<Wire> Wires => _elements.OfType<Wire>();

    /// <summary>Raised after any structural or geometric change.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    internal void AddElement(SchematicElement e)
    {
        if (e.Id == 0) e.Id = _nextId++;
        else _nextId = Math.Max(_nextId, e.Id + 1);
        _elements.Add(e);
    }

    internal void RemoveElement(SchematicElement e) => _elements.Remove(e);

    public SchematicElement? FindById(int id) => _elements.FirstOrDefault(e => e.Id == id);

    /// <summary>Next free reference designator for a prefix (R1, R2, ...).</summary>
    public string NextRefDes(string prefix)
    {
        int max = 0;
        foreach (var s in Symbols)
        {
            if (!s.RefDes.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(s.RefDes.AsSpan(prefix.Length), out int n))
                max = Math.Max(max, n);
        }
        return prefix + (max + 1);
    }

    public Rect2 ContentBounds()
    {
        var r = Rect2.Empty;
        foreach (var e in _elements) r = r.Union(e.Bounds);
        return r;
    }

    // --------------------------------------------------- connectivity queries

    /// <summary>All pin connection points in world space.</summary>
    public IEnumerable<Vec2> AllPinPoints()
    {
        foreach (var s in Symbols)
            foreach (var (_, world) in s.WorldPins())
                yield return world;
    }

    /// <summary>Nearest pin within <paramref name="radius"/>, or null. Used for magnet snapping.</summary>
    public Vec2? FindPinNear(Vec2 p, double radius)
    {
        Vec2? best = null;
        double bestDist = radius;
        foreach (var pin in AllPinPoints())
        {
            double d = pin.DistanceTo(p);
            if (d <= bestDist) { bestDist = d; best = pin; }
        }
        return best;
    }

    /// <summary>
    /// True if a wire may legally start or end at <paramref name="p"/>:
    /// the point coincides with a symbol pin or lies on an existing wire.
    /// </summary>
    public bool IsConnectionPoint(Vec2 p, double tol = 0.05)
    {
        var key = p.Key();
        foreach (var pin in AllPinPoints())
            if (pin.Key() == key)
                return true;

        foreach (var w in Wires)
        {
            foreach (var v in w.Points)
                if (v.Key() == key)
                    return true;
            foreach (var (a, b) in w.Segments())
                if (p.IsOnSegment(a, b, tol))
                    return true;
        }
        return false;
    }

    public void Clear()
    {
        _elements.Clear();
        _nextId = 1;
        NotifyChanged();
    }
}
