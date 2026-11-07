using System.Globalization;

namespace SchematicEditor.Core;

/// <summary>
/// The schematic: owns the elements and gives them ids. Edits go through commands
/// (see <see cref="UndoStack"/>), so undo and redo stay consistent.
/// </summary>
public sealed class SchematicDocument
{
    /// <summary>The grid pitch in world units.</summary>
    public const double Grid = 5.0;

    private readonly List<SchematicElement> _elements = [];
    private readonly Dictionary<int, SchematicElement> _byId = [];
    private int _nextId = 1;

    /// <summary>All elements, bottom to top.</summary>
    public IReadOnlyList<SchematicElement> Elements => _elements;
    public IEnumerable<SymbolInstance> Symbols => _elements.OfType<SymbolInstance>();
    public IEnumerable<Wire> Wires => _elements.OfType<Wire>();

    /// <summary>Raised after every edit.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public SchematicElement? FindById(int id) => _byId.GetValueOrDefault(id);

    /// <summary>Adds an element on top, or at <paramref name="index"/>. It keeps its id unless it has none or the id is taken.</summary>
    internal void Add(SchematicElement element, int index = -1)
    {
        if (element.Id <= 0 || _byId.ContainsKey(element.Id))
            element.Id = _nextId;
        _nextId = Math.Max(_nextId, element.Id + 1);
        _byId.Add(element.Id, element);
        _elements.Insert(index < 0 ? _elements.Count : index, element);
    }

    /// <summary>Removes an element and returns where it was, -1 when it wasn't there.</summary>
    internal int Remove(SchematicElement element)
    {
        int index = _elements.IndexOf(element);
        if (index >= 0)
        {
            _elements.RemoveAt(index);
            _byId.Remove(element.Id);
        }
        return index;
    }

    /// <summary>The next free reference designator for a prefix: R1, R2, ...</summary>
    public string NextRefDes(string prefix)
    {
        int max = 0;
        foreach (SymbolInstance s in Symbols)
        {
            if (s.RefDes.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(s.RefDes.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                max = Math.Max(max, n);
        }
        return prefix + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    public Rect2 ContentBounds() => _elements.Aggregate(Rect2.Empty, static (r, e) => r.Union(e.Bounds));

    // ------------------------------------------------------------------ connection points

    public IEnumerable<Vec2> AllPinPoints() => Symbols.SelectMany(s => s.WorldPins(), static (_, pin) => pin.World);

    /// <summary>The pin nearest to <paramref name="p"/> within <paramref name="radius"/>; magnet snapping.</summary>
    public Vec2? FindPinNear(Vec2 p, double radius)
    {
        Vec2? best = null;
        double bestDistance = radius;
        foreach (Vec2 pin in AllPinPoints())
        {
            double d = pin.DistanceTo(p);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = pin;
            }
        }
        return best;
    }

    /// <summary>Whether a wire may start or end at <paramref name="p"/>: on a pin or on an existing wire.</summary>
    public bool IsConnectionPoint(Vec2 p, double tolerance = 0.05)
    {
        PointKey key = p.Key;
        return AllPinPoints().Any(pin => pin.Key == key)
            || Wires.Any(w => w.Points.Any(v => v.Key == key) || w.Segments().Any(s => p.IsOnSegment(s.A, s.B, tolerance)));
    }
}
