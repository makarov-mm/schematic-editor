using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

/// <summary>A scope probe: the voltage of the net at a point, or the current through a symbol.</summary>
public sealed class Probe
{
    /// <summary>How much of the signal a trace keeps; the scope's longest window has to fit.</summary>
    public const double TraceSeconds = 2.5;

    private Probe(Color color, Vec2 anchor, SymbolInstance? symbol)
    {
        Color = color;
        Brush = Paint.Brush(color);
        Pen = Paint.Pen(Brush, 1.6, round: true);
        Anchor = anchor;
        Symbol = symbol;
        Trace = new ProbeTrace((int)Math.Ceiling(TraceSeconds / SchematicCanvas.SimDt), SchematicCanvas.SimDt);
    }

    public static Probe Voltage(Vec2 anchor, Color color) => new(color, anchor, null);

    public static Probe Current(SymbolInstance symbol, Color color) => new(color, symbol.Position, symbol);

    /// <summary>V(net) or I(symbol).</summary>
    public string Label { get; internal set; } = "";

    public Color Color { get; }
    public Brush Brush { get; }
    public Pen Pen { get; }

    /// <summary>The point a voltage probe measures.</summary>
    public Vec2 Anchor { get; }

    /// <summary>The symbol a current probe measures; null for voltage probes.</summary>
    public SymbolInstance? Symbol { get; }

    public bool IsCurrent => Symbol is not null;
    public string Unit => IsCurrent ? "A" : "V";

    /// <summary>Where the probe's marker is drawn.</summary>
    public Vec2 Position => Symbol?.Position ?? Anchor;

    /// <summary>The MNA node of a voltage probe while the simulation runs, -1 when unknown.</summary>
    internal int Node { get; set; } = -1;

    public ProbeTrace Trace { get; }
}

/// <summary>
/// The latest samples of a probe, taken every <see cref="Interval"/> seconds, in a ring buffer that
/// drops the oldest when full. Times aren't stored: sample i was taken at
/// <see cref="EndTime"/> - (Count - 1 - i) * Interval.
/// </summary>
public sealed class ProbeTrace(int capacity, double interval)
{
    private readonly float[] _values = new float[capacity];
    private int _oldest;

    public double Interval { get; } = interval;
    public int Count { get; private set; }

    /// <summary>The time of the newest sample.</summary>
    public double EndTime { get; private set; }

    /// <summary>Sample <paramref name="index"/>, the oldest being 0.</summary>
    public double this[int index] => _values[(_oldest + index) % _values.Length];

    /// <summary>The newest sample, 0 when there is none.</summary>
    public double Last => Count > 0 ? this[Count - 1] : 0;

    public double TimeAt(int index) => EndTime - (Count - 1 - index) * Interval;

    /// <summary>The index of the first sample taken at or after <paramref name="time"/>.</summary>
    public int FirstIndexAt(double time) =>
        Math.Clamp((int)Math.Ceiling(Count - 1 - (EndTime - time) / Interval - 1e-6), 0, Count);

    public void Add(double time, double value)
    {
        if (Count < _values.Length)
        {
            _values[(_oldest + Count) % _values.Length] = (float)value;
            Count++;
        }
        else
        {
            _values[_oldest] = (float)value;
            _oldest = (_oldest + 1) % _values.Length;
        }
        EndTime = time;
    }

    public void Clear()
    {
        _oldest = 0;
        Count = 0;
        EndTime = 0;
    }
}
