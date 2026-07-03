using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SchematicEditor.Core;

namespace SchematicEditor.App;

public enum EditorTool { Select, Wire, Place }

/// <summary>
/// Retained-document, immediate-render schematic canvas.
/// All drawing happens in world units under a single zoom/pan transform;
/// WPF tessellates on the fly, so lines and text stay crisp at any zoom.
/// </summary>
public sealed class SchematicCanvas : FrameworkElement
{
    private const double Grid = SchematicDocument.Grid;

    public SchematicDocument Document { get; private set; }
    public UndoStack Undo { get; private set; }

    public EditorTool Tool { get; private set; } = EditorTool.Select;

    /// <summary>Transient messages (commit results, warnings).</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Continuous cursor/zoom readout.</summary>
    public event Action<string>? CursorStatusChanged;
    public event Action? SelectionOrToolChanged;

    private readonly HashSet<SchematicElement> _selection = [];
    public IReadOnlyCollection<SchematicElement> Selection => _selection;

    // View transform: screen = world * _zoom + _pan.
    private double _zoom = 3.0;
    private Vector _pan = new(80, 200);

    // Tool state.
    private SymbolInstance? _ghost;
    private readonly List<Vec2> _wirePoints = [];
    private Vec2 _cursorWorld;
    private Vec2 _wireTarget;          // current snapped wire endpoint candidate
    private bool _wireTargetValid;     // candidate is a legal connection point
    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;
    private bool _dragging;
    private Vec2 _dragStartWorld;
    private Vec2 _dragDelta;
    private bool _rubberBand;
    private Vec2 _rubberStart;

    // Clipboard (in-process).
    private sealed record ClipSymbol(string Name, Vec2 Pos, Rotation Rot, bool Mirror, string Value);
    private readonly List<ClipSymbol> _clipSymbols = [];
    private readonly List<List<Vec2>> _clipWires = [];

    // ------------------------------------------------------------ simulation

    /// <summary>A scope probe: voltage at a point, or current through a symbol.</summary>
    public sealed class Probe
    {
        public required string Label { get; set; }
        public required Color Color { get; init; }
        public Vec2 Anchor { get; init; }
        public SymbolInstance? Symbol { get; init; }   // set = current probe
        public bool IsCurrent => Symbol is not null;

        internal int Node = -1;                        // resolved at run start
        public List<float> Times { get; } = [];
        public List<float> Values { get; } = [];
        public double LastValue { get; internal set; }

        internal void Append(double t, double v)
        {
            Times.Add((float)t);
            Values.Add((float)v);
            LastValue = v;
            if (Times.Count > 130_000)
            {
                Times.RemoveRange(0, 30_000);
                Values.RemoveRange(0, 30_000);
            }
        }

        internal void Clear()
        {
            Times.Clear();
            Values.Clear();
        }
    }

    private static readonly Color[] ProbePalette =
    [
        Color.FromRgb(0xE6, 0x19, 0x4B), Color.FromRgb(0x3C, 0xB4, 0x4B),
        Color.FromRgb(0x43, 0x63, 0xD8), Color.FromRgb(0xF5, 0x82, 0x31),
        Color.FromRgb(0x91, 0x1E, 0xB4), Color.FromRgb(0x00, 0xA8, 0xB5),
        Color.FromRgb(0xF0, 0x32, 0xE6), Color.FromRgb(0x9A, 0x63, 0x24),
    ];

    public const double SimDt = 5e-5;

    private CircuitSimulator? _sim;
    private readonly DispatcherTimer _simTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _simClock = new();

    public bool IsRunning => _sim is not null;
    public double SimTime => _sim?.Time ?? 0;
    public List<Probe> Probes { get; } = [];

    /// <summary>When true, clicks place probes (works while editing and while running).</summary>
    public bool ProbeArmed
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            SelectionOrToolChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Raised after every simulation frame (batch of steps).</summary>
    public event Action? SimulationFrame;
    /// <summary>Raised when the simulation starts or stops.</summary>
    public event Action? SimulationStateChanged;

    // Cached derived data.
    private NetlistResult _netlist = new();
    private HashSet<(long, long)> _connectedPinKeys = [];

    // Pens / brushes (frozen once).
    private static readonly Brush SymbolBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)));
    private static readonly Brush WireBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x50, 0xc8)));
    private static readonly Brush SelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xe8, 0x7a, 0x00)));
    private static readonly Brush GhostBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x30, 0x90, 0x30)));
    private static readonly Brush DanglingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xd0, 0x20, 0x20)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));
    private static readonly Brush TargetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0xa0, 0x46)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static Pen MakePen(Brush b, double w)
    {
        var p = new Pen(b, w)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }

    private static readonly Pen SymbolPen = MakePen(SymbolBrush, 1.0);
    private static readonly Pen WirePen = MakePen(WireBrush, 0.9);
    private static readonly Pen SelPen = MakePen(SelBrush, 1.4);
    private static readonly Pen GhostPen = MakePen(GhostBrush, 1.0);
    private static readonly Pen DanglingPen = MakePen(DanglingBrush, 0.8);
    private static readonly Pen TargetPen = MakePen(TargetBrush, 1.2);
    private static readonly Pen InvalidPen = MakePen(DanglingBrush, 1.0);

    public SchematicCanvas()
    {
        Document = new SchematicDocument();
        Undo = new UndoStack(Document);
        Document.Changed += OnDocumentChanged;
        Focusable = true;
        ClipToBounds = true;
        _simTimer.Tick += OnSimTick;
    }

    // ------------------------------------------------------------- document

    public void SetDocument(SchematicDocument doc)
    {
        Document.Changed -= OnDocumentChanged;
        Document = doc;
        Undo = new UndoStack(doc);
        Document.Changed += OnDocumentChanged;
        _selection.Clear();
        Probes.Clear();                 // anchored to the old document's geometry
        SimulationFrame?.Invoke();      // repaint the (now empty) scope
        OnDocumentChanged();
        ZoomToFit();
    }

    private void OnDocumentChanged()
    {
        if (IsRunning) StopSimulation();
        _netlist = NetlistExtractor.Extract(Document);
        _connectedPinKeys = [];
        foreach (var net in _netlist.Nets)
        {
            bool multi = net.Wires.Count > 0 || net.Pins.Count > 1;
            if (!multi) continue;
            foreach (var pin in net.Pins) _connectedPinKeys.Add(pin.World.Key());
        }
        _selection.RemoveWhere(e => Document.FindById(e.Id) == null);
        RevalidateProbes();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>After any edit: drop probes whose net or symbol is gone, refresh net names.</summary>
    private void RevalidateProbes()
    {
        bool changed = false;
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var p = Probes[i];
            if (p.IsCurrent)
            {
                if (!Document.Symbols.Contains(p.Symbol!))
                {
                    Probes.RemoveAt(i);
                    changed = true;
                }
            }
            else if (_netlist.FindNetAt(p.Anchor) is { } net)
            {
                p.Label = $"V({net.Name})";
            }
            else
            {
                Probes.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) SimulationFrame?.Invoke();
    }

    public NetlistResult CurrentNetlist => _netlist;
    private Vec2 ToWorld(Point s) => new((s.X - _pan.X) / _zoom, (s.Y - _pan.Y) / _zoom);
    private double HitTolerance => 6.0 / _zoom;

    public void ZoomToFit()
    {
        var b = Document.ContentBounds();
        if (b.IsEmpty || ActualWidth < 10 || ActualHeight < 10)
        {
            _zoom = 3.0;
            _pan = new Vector(ActualWidth * 0.3, ActualHeight * 0.5);
        }
        else
        {
            b = b.Inflate(30);
            _zoom = Math.Clamp(Math.Min(ActualWidth / b.Width, ActualHeight / b.Height), 0.05, 100);
            var c = b.Center;
            _pan = new Vector(ActualWidth * 0.5 - c.X * _zoom, ActualHeight * 0.5 - c.Y * _zoom);
        }
        InvalidateVisual();
    }

    public void CenterOn(Vec2 world)
    {
        _pan = new Vector(ActualWidth * 0.5 - world.X * _zoom, ActualHeight * 0.5 - world.Y * _zoom);
        InvalidateVisual();
    }

    /// <summary>Build the circuit and start real-time simulation. False if it cannot run.</summary>
    public bool StartSimulation()
    {
        if (IsRunning) return true;

        var sim = CircuitSimulator.Build(Document, _netlist, out var problems);
        if (sim is null)
        {
            Status(string.Join(" ", problems));
            return false;
        }
        foreach (var w in sim.Warnings) Status(w);

        _sim = sim;
        _sim.Reset();
        ResolveProbes();
        foreach (var p in Probes) p.Clear();

        _selection.Clear();
        SetSelectToolQuiet();
        _simClock.Restart();
        _simTimer.Start();
        SimulationStateChanged?.Invoke();
        SelectionOrToolChanged?.Invoke();
        Status("Simulation running.");
        InvalidateVisual();
        return true;
    }

    public void StopSimulation()
    {
        if (!IsRunning) return;
        _simTimer.Stop();
        _sim = null;
        SimulationStateChanged?.Invoke();
        SelectionOrToolChanged?.Invoke();
        Status("Simulation stopped.");
        InvalidateVisual();
    }

    /// <summary>Zero time and reactive state, un-blow fuses, clear probe traces.</summary>
    public void ResetSimulation()
    {
        _sim?.Reset();
        foreach (var p in Probes) p.Clear();
        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    public void ClearProbes()
    {
        Probes.Clear();
        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    private void SetSelectToolQuiet()
    {
        Tool = EditorTool.Select;
        _ghost = null;
        _wirePoints.Clear();
    }

    private void ResolveProbes()
    {
        if (_sim is null) return;
        int before = Probes.Count;
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var p = Probes[i];
            if (p.IsCurrent)
            {
                // Reference check: an Id lookup could silently hit a different
                // symbol after a file load re-uses the same ids.
                if (!Document.Symbols.Contains(p.Symbol!)) Probes.RemoveAt(i);
            }
            else
            {
                p.Node = _sim.ResolveNode(p.Anchor) ?? -1;
                if (p.Node < 0) Probes.RemoveAt(i);
            }
        }
        if (Probes.Count < before)
            Status($"Removed {before - Probes.Count} probe(s) that no longer match the circuit.");
    }

    private void OnSimTick(object? sender, EventArgs e)
    {
        if (_sim is null) return;

        double elapsed = Math.Min(_simClock.Elapsed.TotalSeconds, 0.08);
        _simClock.Restart();
        int steps = (int)Math.Round(elapsed / SimDt);

        try
        {
            for (int i = 0; i < steps; i++)
            {
                _sim.Step(SimDt);
                foreach (var p in Probes)
                {
                    double v = p.IsCurrent
                        ? _sim.GetCurrent(p.Symbol!) ?? 0
                        : p.Node >= 0 ? _sim.GetNodeVoltage(p.Node) : 0;
                    p.Append(_sim.Time, v);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            Status("Simulation stopped: " + ex.Message);
            StopSimulation();
            return;
        }

        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    /// <summary>Left click while the simulation runs: toggle switches.</summary>
    private void RunModeClick()
    {
        if (_sim is null) return;

        if (HitTest(_cursorWorld) is SymbolInstance { Definition.Name: "Switch" } sw)
        {
            sw.StateOn = !sw.StateOn;   // live toggle: no undo entry, no rebuild
            InvalidateVisual();
        }
    }

    /// <summary>Place or remove a probe at the cursor. Works in edit mode and in run mode.</summary>
    private void ProbeClick()
    {
        // Clicking an existing probe removes it.
        var near = Probes.FirstOrDefault(p =>
            !p.IsCurrent && p.Anchor.DistanceTo(_cursorWorld) <= HitTolerance * 2 ||
            p.IsCurrent && p.Symbol!.Position.DistanceTo(_cursorWorld) <= HitTolerance * 2);
        if (near is not null)
        {
            Probes.Remove(near);
            Status($"Removed probe {near.Label}.");
            InvalidateVisual();
            SimulationFrame?.Invoke();
            return;
        }

        var color = ProbePalette[Probes.Count % ProbePalette.Length];
        if (HitTest(_cursorWorld) is SymbolInstance sym &&
            sym.Definition.Pins.Count == 2 && sym.Definition.Name != "Ground")
        {
            Probes.Add(new Probe { Label = $"I({sym.RefDes})", Color = color, Symbol = sym });
            Status($"Current probe on {sym.RefDes}.");
        }
        else
        {
            var anchor = Document.FindPinNear(_cursorWorld, HitTolerance * 2)
                         ?? _cursorWorld.Snap(SchematicDocument.Grid);
            var net = _netlist.FindNetAt(anchor);
            if (net is null)
            {
                Status("No net here — click a wire or a pin.");
                return;
            }
            Probes.Add(new Probe
            {
                Label = $"V({net.Name})",
                Color = color,
                Anchor = anchor,
                Node = _sim?.ResolveNode(anchor) ?? -1,
            });
            Status($"Voltage probe on {net.Name}.");
        }
        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    /// <summary>Restore probes saved in a schematic file (colors are reassigned from the palette).</summary>
    public void LoadProbes(IEnumerable<ProbeInfo>? saved)
    {
        Probes.Clear();
        foreach (var info in saved ?? [])
        {
            var color = ProbePalette[Probes.Count % ProbePalette.Length];
            if (info.Type == "I")
            {
                if (Document.FindById(info.SymbolId) is SymbolInstance s &&
                    s.Definition.Pins.Count == 2)
                    Probes.Add(new Probe { Label = $"I({s.RefDes})", Color = color, Symbol = s });
            }
            else
            {
                var anchor = new Vec2(info.X, info.Y);
                if (_netlist.FindNetAt(anchor) is { } net)
                    Probes.Add(new Probe { Label = $"V({net.Name})", Color = color, Anchor = anchor });
            }
        }
        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    /// <summary>Probes in their persistable form.</summary>
    public List<ProbeInfo> ExportProbes() =>
        [.. Probes.Select(p => p.IsCurrent
            ? new ProbeInfo("I", SymbolId: p.Symbol!.Id)
            : new ProbeInfo("V", p.Anchor.X, p.Anchor.Y))];

    // ----------------------------------------------------------------- tools

    public void SetSelectTool()
    {
        Tool = EditorTool.Select;
        _ghost = null;
        _wirePoints.Clear();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetWireTool()
    {
        Tool = EditorTool.Wire;
        _ghost = null;
        _selection.Clear();
        _wirePoints.Clear();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetPlaceTool(string symbolName)
    {
        Tool = EditorTool.Place;
        _selection.Clear();
        _wirePoints.Clear();
        _ghost = new SymbolInstance(SymbolLibrary.Get(symbolName), _cursorWorld.Snap(Grid));
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    // ----------------------------------------------------------- edit actions

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        Undo.Push(new DeleteElementsCommand(_selection));
    }

    public void RotateSelection()
    {
        var symbols = _selection.OfType<SymbolInstance>().ToList();
        if (symbols.Count == 0) return;
        Undo.Push(new CompositeCommand("Rotate",
            symbols.Select(s => (IEditCommand)new RotateSymbolCommand(s))));
    }

    public void MirrorSelection()
    {
        var symbols = _selection.OfType<SymbolInstance>().ToList();
        if (symbols.Count == 0) return;
        Undo.Push(new CompositeCommand("Mirror",
            symbols.Select(s => (IEditCommand)new MirrorSymbolCommand(s))));
    }

    /// <summary>True when Rotate/Mirror have something to act on (ghost or selected symbols).</summary>
    public bool CanRotateMirror =>
        !IsRunning &&
        (Tool == EditorTool.Place && _ghost is not null ||
         _selection.OfType<SymbolInstance>().Any());

    /// <summary>Rotate the placement ghost if active, otherwise the selection. Same as the R key.</summary>
    public void RotateSelectionOrGhost()
    {
        if (Tool == EditorTool.Place && _ghost is not null)
        {
            _ghost.Rotation = (Rotation)(((int)_ghost.Rotation + 1) & 3);
            InvalidateVisual();
        }
        else RotateSelection();
    }

    /// <summary>Mirror the placement ghost if active, otherwise the selection. Same as the M key.</summary>
    public void MirrorSelectionOrGhost()
    {
        if (Tool == EditorTool.Place && _ghost is not null)
        {
            _ghost.Mirror = !_ghost.Mirror;
            InvalidateVisual();
        }
        else MirrorSelection();
    }

    public void CopySelection()
    {
        _clipSymbols.Clear();
        _clipWires.Clear();
        foreach (var e in _selection)
        {
            switch (e)
            {
                case SymbolInstance s:
                    _clipSymbols.Add(new ClipSymbol(s.Definition.Name, s.Position, s.Rotation, s.Mirror, s.Value));
                    break;
                case Wire w:
                    _clipWires.Add([.. w.Points]);
                    break;
            }
        }
        Status($"Copied {_clipSymbols.Count + _clipWires.Count} element(s)");
    }

    public void Paste()
    {
        if (_clipSymbols.Count == 0 && _clipWires.Count == 0) return;
        var offset = new Vec2(2 * Grid, 2 * Grid);
        List<IEditCommand> commands = [];
        List<SchematicElement> pasted = [];

        foreach (var c in _clipSymbols)
        {
            var inst = new SymbolInstance(SymbolLibrary.Get(c.Name), c.Pos + offset)
            {
                Rotation = c.Rot,
                Mirror = c.Mirror,
                Value = c.Value,
            };
            commands.Add(new AddElementCommand(inst));
            pasted.Add(inst);
        }
        foreach (var pts in _clipWires)
        {
            var w = new Wire(pts.Select(p => p + offset));
            commands.Add(new AddElementCommand(w));
            pasted.Add(w);
        }

        Undo.Push(new CompositeCommand("Paste", commands));
        _selection.Clear();
        foreach (var e in pasted) _selection.Add(e);
        InvalidateVisual();
    }

    // ------------------------------------------------------------- hit tests

    private SchematicElement? HitTest(Vec2 p)
    {
        // Symbols first (topmost feel), then wires.
        foreach (var s in Document.Symbols.Reverse())
            if (s.Bounds.Inflate(1).Contains(p))
                return s;
        Wire? bestWire = null;
        double best = HitTolerance;
        foreach (var w in Document.Wires)
        {
            double d = w.DistanceTo(p);
            if (d <= best) { best = d; bestWire = w; }
        }
        return bestWire;
    }

    // ------------------------------------------------------------ mouse input

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var screen = e.GetPosition(this);
        _cursorWorld = ToWorld(screen);

        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _panning = true;
            _panStart = screen;
            _panOrigin = _pan;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            if (IsRunning) return;
            if (Tool == EditorTool.Wire && _wirePoints.Count > 0) CancelWire();
            else SetSelectTool();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        if (ProbeArmed)
        {
            ProbeClick();
            return;
        }

        if (IsRunning)
        {
            RunModeClick();
            return;
        }

        switch (Tool)
        {
            case EditorTool.Place when _ghost != null:
                Undo.Push(new AddElementCommand(new SymbolInstance(_ghost.Definition, _cursorWorld.Snap(Grid))
                {
                    Rotation = _ghost.Rotation,
                    Mirror = _ghost.Mirror,
                }));
                // Stay in place mode for repeated placement.
                break;

            case EditorTool.Wire:
                WireClick();
                break;

            case EditorTool.Select:
                SelectClick(e);
                break;
        }
    }

    /// <summary>
    /// Wire tool click. Wires may only start and end on a connection point
    /// (a symbol pin or an existing wire); intermediate corners are free.
    /// Clicking a valid endpoint finishes the wire immediately.
    /// </summary>
    private void WireClick()
    {
        UpdateWireTarget();
        var p = _wireTarget;

        if (_wirePoints.Count == 0)
        {
            if (!_wireTargetValid)
            {
                Status("A wire must start on a pin or an existing wire.");
                return;
            }
            _wirePoints.Add(p);
            InvalidateVisual();
            return;
        }

        if (p.Key() == _wirePoints[^1].Key()) return;

        foreach (var pt in OrthoRoute(_wirePoints[^1], p))
            if (pt.Key() != _wirePoints[^1].Key())
                _wirePoints.Add(pt);

        if (_wireTargetValid && _wirePoints.Count >= 2)
            CommitWire();
        else
            InvalidateVisual();
    }

    private void SelectClick(MouseButtonEventArgs e)
    {
        var hit = HitTest(_cursorWorld);

        if (e.ClickCount == 2)
        {
            if (hit is SymbolInstance sym) EditProperties(sym);
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (hit == null)
        {
            if (!shift) _selection.Clear();
            _rubberBand = true;
            _rubberStart = _cursorWorld;
            CaptureMouse();
        }
        else
        {
            if (shift)
            {
                if (!_selection.Add(hit)) _selection.Remove(hit);
            }
            else if (!_selection.Contains(hit))
            {
                _selection.Clear();
                _selection.Add(hit);
            }
            _dragging = true;
            _dragStartWorld = _cursorWorld;
            _dragDelta = Vec2.Zero;
            CaptureMouse();
        }
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        _cursorWorld = ToWorld(screen);
        var snapped = _cursorWorld.Snap(Grid);
        if (_sim is not null)
        {
            string extra = "";
            if (HitTest(_cursorWorld) is SymbolInstance hoverSym &&
                _sim.GetCurrent(hoverSym) is { } cur)
                extra = $"   {hoverSym.RefDes}: {FormatSi(cur, "A")}";
            else if (_sim.GetVoltageAt(Document.FindPinNear(_cursorWorld, HitTolerance * 2) ?? snapped) is { } volts)
                extra = $"   {FormatSi(volts, "V")}";
            CursorStatus($"t {_sim.Time:0.000} s{extra}");
        }
        else
        {
            CursorStatus($"x {snapped.X:0}   y {snapped.Y:0}   zoom {_zoom:0.0}\u00d7");
        }

        if (_panning)
        {
            _pan = _panOrigin + (screen - _panStart);
            InvalidateVisual();
            return;
        }

        if (_dragging)
        {
            _dragDelta = snapped - _dragStartWorld.Snap(Grid);
            InvalidateVisual();
            return;
        }

        if (_rubberBand)
        {
            InvalidateVisual();
            return;
        }

        if (Tool == EditorTool.Wire)
        {
            UpdateWireTarget();
            InvalidateVisual();
            return;
        }

        if (Tool == EditorTool.Place && _ghost != null)
        {
            _ghost.Position = snapped;
            InvalidateVisual();
        }
    }

    /// <summary>Snap the wire cursor: magnet to the nearest pin, else to the grid.</summary>
    private void UpdateWireTarget()
    {
        var pin = Document.FindPinNear(_cursorWorld, Math.Max(HitTolerance * 1.5, Grid * 0.6));
        _wireTarget = pin ?? _cursorWorld.Snap(Grid);
        _wireTargetValid = Document.IsConnectionPoint(_wireTarget);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = false;
            ReleaseMouseCapture();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            if (_dragDelta.Length > 0.01 && _selection.Count > 0)
                Undo.Push(new MoveElementsCommand(_selection, _dragDelta));
            _dragDelta = Vec2.Zero;
            InvalidateVisual();
        }

        if (_rubberBand)
        {
            _rubberBand = false;
            ReleaseMouseCapture();
            var rect = Rect2.FromPoints(_rubberStart, _cursorWorld);
            foreach (var el in Document.Elements)
                if (rect.Intersects(el.Bounds))
                    _selection.Add(el);
            SelectionOrToolChanged?.Invoke();
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var screen = e.GetPosition(this);
        var anchor = ToWorld(screen);
        double factor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 0.05, 100.0);
        _pan = new Vector(screen.X - anchor.X * _zoom, screen.Y - anchor.Y * _zoom);
        InvalidateVisual();
    }

    // --------------------------------------------------------------- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsRunning)
        {
            if (e.Key == Key.Escape) { StopSimulation(); e.Handled = true; }
            else if (e.Key == Key.F) { ZoomToFit(); e.Handled = true; }
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape when ProbeArmed:
                ProbeArmed = false;
                break;
            case Key.Escape:
                if (Tool == EditorTool.Wire && _wirePoints.Count > 0) CancelWire();
                else SetSelectTool();
                break;
            case Key.Delete or Key.Back:
                DeleteSelection();
                break;
            case Key.R when !ctrl:
                RotateSelectionOrGhost();
                break;
            case Key.M when !ctrl:
                MirrorSelectionOrGhost();
                break;
            case Key.Z when ctrl:
                Undo.Undo();
                break;
            case Key.Y when ctrl:
                Undo.Redo();
                break;
            case Key.C when ctrl:
                CopySelection();
                break;
            case Key.V when ctrl:
                Paste();
                break;
            case Key.A when ctrl:
                foreach (var el in Document.Elements) _selection.Add(el);
                SelectionOrToolChanged?.Invoke();
                InvalidateVisual();
                break;
            case Key.F:
                ZoomToFit();
                break;
            case Key.W when !ctrl:
                SetWireTool();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------ wire helper

    /// <summary>L-shaped orthogonal route from a to b (dominant axis first).</summary>
    private static IEnumerable<Vec2> OrthoRoute(Vec2 a, Vec2 b)
    {
        if (Math.Abs(a.X - b.X) < 0.01 || Math.Abs(a.Y - b.Y) < 0.01)
        {
            yield return b;
            yield break;
        }
        if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
            yield return new Vec2(b.X, a.Y); // horizontal first
        else
            yield return new Vec2(a.X, b.Y); // vertical first
        yield return b;
    }

    private void CommitWire()
    {
        if (_wirePoints.Count >= 2 &&
            Document.IsConnectionPoint(_wirePoints[0]) &&
            Document.IsConnectionPoint(_wirePoints[^1]))
        {
            Undo.Push(new AddElementCommand(new Wire(_wirePoints)));
            Status("Wire connected.");
        }
        _wirePoints.Clear();
        InvalidateVisual();
    }

    private void CancelWire()
    {
        _wirePoints.Clear();
        Status("Wire cancelled.");
        InvalidateVisual();
    }

    private void Status(string s) => StatusChanged?.Invoke(s);
    private void CursorStatus(string s) => CursorStatusChanged?.Invoke(s);

    private void EditProperties(SymbolInstance sym)
    {
        var dlg = new PropertiesDialog(sym.RefDes, sym.Value) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            List<IEditCommand> cmds = [];
            if (dlg.RefDes != sym.RefDes) cmds.Add(new SetPropertyCommand(sym, false, dlg.RefDes));
            if (dlg.Value != sym.Value) cmds.Add(new SetPropertyCommand(sym, true, dlg.Value));
            if (cmds.Count > 0) Undo.Push(new CompositeCommand("Edit properties", cmds));
        }
    }

    // -------------------------------------------------------------- rendering

    protected override void OnRender(DrawingContext dc)
    {
        // Background (also makes the whole surface hit-testable).
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var m = new Matrix(_zoom, 0, 0, _zoom, _pan.X, _pan.Y);
        dc.PushTransform(new MatrixTransform(m));

        DrawGrid(dc);

        foreach (var wire in Document.Wires)
        {
            bool selected = _selection.Contains(wire);
            DrawWire(dc, wire, selected ? SelPen : WirePen, selected ? _dragDelta : Vec2.Zero);
        }

        foreach (var j in _netlist.Junctions)
            dc.DrawEllipse(WireBrush, null, new Point(j.X, j.Y), 1.4, 1.4);

        foreach (var sym in Document.Symbols)
        {
            bool selected = _selection.Contains(sym);
            var offset = selected ? _dragDelta : Vec2.Zero;

            if (_sim is not null && sym.Definition.Name == "Lamp")
                DrawLampGlow(dc, sym);

            DrawSymbol(dc, sym, selected ? SelPen : SymbolPen,
                selected ? SelBrush : SymbolBrush, offset);
            DrawLabels(dc, sym, offset);
            DrawPinMarkers(dc, sym, offset);

            if (_sim is not null && sym.Definition.Name == "Fuse" && _sim.IsFuseBlown(sym))
                DrawFuseBlown(dc, sym);
        }

        if (Probes.Count > 0)
            DrawProbeMarkers(dc);

        if (Tool == EditorTool.Place && _ghost != null)
            DrawSymbol(dc, _ghost, GhostPen, GhostBrush, Vec2.Zero);

        if (Tool == EditorTool.Wire)
            DrawWirePreview(dc);

        if (_rubberBand)
        {
            var r = Rect2.FromPoints(_rubberStart, _cursorWorld);
            var pen = new Pen(SelBrush, 1.0 / _zoom) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, new Rect(r.MinX, r.MinY, r.Width, r.Height));
        }

        dc.Pop();
    }

    private void DrawWirePreview(DrawingContext dc)
    {
        // Endpoint candidate marker: green ring when it is a legal connection point.
        var targetPen = _wireTargetValid ? TargetPen : InvalidPen;
        dc.DrawEllipse(null, targetPen, new Point(_wireTarget.X, _wireTarget.Y), 2.2, 2.2);

        if (_wirePoints.Count == 0) return;

        List<Vec2> preview = [.. _wirePoints];
        foreach (var pt in OrthoRoute(preview[^1], _wireTarget))
            preview.Add(pt);

        var pen = _wireTargetValid ? TargetPen : GhostPen;
        for (int i = 0; i + 1 < preview.Count; i++)
            dc.DrawLine(pen,
                new Point(preview[i].X, preview[i].Y),
                new Point(preview[i + 1].X, preview[i + 1].Y));
    }

    private void DrawLampGlow(DrawingContext dc, SymbolInstance lamp)
    {
        double b = _sim!.GetLampBrightness(lamp);
        if (b < 0.02) return;

        var c = lamp.Position;
        double r = 10 + 14 * b;
        var brush = new RadialGradientBrush(
            Color.FromArgb((byte)(150 * b), 0xFF, 0xD9, 0x40),
            Color.FromArgb(0, 0xFF, 0xD9, 0x40));
        brush.Freeze();
        dc.DrawEllipse(brush, null, new Point(c.X, c.Y), r, r);

        var core = new SolidColorBrush(Color.FromArgb((byte)(120 * b), 0xFF, 0xE8, 0x80));
        core.Freeze();
        dc.DrawEllipse(core, null, new Point(c.X, c.Y), 8, 8);
    }

    private static void DrawFuseBlown(DrawingContext dc, SymbolInstance fuse)
    {
        var c = fuse.Position;
        var pen = MakePen(DanglingBrush, 1.6);
        dc.DrawLine(pen, new Point(c.X - 5, c.Y - 5), new Point(c.X + 5, c.Y + 5));
        dc.DrawLine(pen, new Point(c.X - 5, c.Y + 5), new Point(c.X + 5, c.Y - 5));
    }

    private void DrawProbeMarkers(DrawingContext dc)
    {
        foreach (var p in Probes)
        {
            var pos = p.IsCurrent ? p.Symbol!.Position : p.Anchor;
            var fill = new SolidColorBrush(p.Color);
            fill.Freeze();
            dc.DrawEllipse(fill, MakePen(Brushes.White, 0.7), new Point(pos.X, pos.Y), 2.4, 2.4);

            var ft = MakeText(p.Label, 4, fill);
            dc.DrawText(ft, new Point(pos.X + 3.5, pos.Y - ft.Height));
        }
    }

    /// <summary>Engineering notation: 0.0032 → "3.2 mA".</summary>
    public static string FormatSi(double v, string unit)
    {
        double a = Math.Abs(v);
        (double m, string p) = a switch
        {
            >= 1e6 => (1e-6, "M"),
            >= 1e3 => (1e-3, "k"),
            >= 1 => (1.0, ""),
            >= 1e-3 => (1e3, "m"),
            >= 1e-6 => (1e6, "\u00b5"),
            >= 1e-9 => (1e9, "n"),
            _ => (1.0, ""),
        };
        return $"{v * m:0.###} {p}{unit}";
    }

    private void DrawGrid(DrawingContext dc)
    {
        double spacingPx = Grid * _zoom;
        if (spacingPx < 5) return;

        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(ActualWidth, ActualHeight));

        var minor = new Pen(new SolidColorBrush(Color.FromRgb(0xea, 0xea, 0xea)), 1.0 / _zoom);
        var major = new Pen(new SolidColorBrush(Color.FromRgb(0xd6, 0xd6, 0xd6)), 1.0 / _zoom);
        minor.Freeze();
        major.Freeze();

        double x0 = Math.Floor(topLeft.X / Grid) * Grid;
        double y0 = Math.Floor(topLeft.Y / Grid) * Grid;

        for (double x = x0; x <= bottomRight.X; x += Grid)
        {
            bool isMajor = Math.Abs(Math.IEEERemainder(x, Grid * 5)) < 0.01;
            if (!isMajor && spacingPx < 12) continue;
            dc.DrawLine(isMajor ? major : minor,
                new Point(x, topLeft.Y), new Point(x, bottomRight.Y));
        }
        for (double y = y0; y <= bottomRight.Y; y += Grid)
        {
            bool isMajor = Math.Abs(Math.IEEERemainder(y, Grid * 5)) < 0.01;
            if (!isMajor && spacingPx < 12) continue;
            dc.DrawLine(isMajor ? major : minor,
                new Point(topLeft.X, y), new Point(bottomRight.X, y));
        }
    }

    private static void DrawWire(DrawingContext dc, Wire wire, Pen pen, Vec2 offset)
    {
        foreach (var (a, b) in wire.Segments())
            dc.DrawLine(pen,
                new Point(a.X + offset.X, a.Y + offset.Y),
                new Point(b.X + offset.X, b.Y + offset.Y));
    }

    private void DrawSymbol(DrawingContext dc, SymbolInstance sym, Pen pen, Brush fill, Vec2 offset)
    {
        Point W(Vec2 local)
        {
            var w = sym.ToWorld(local);
            return new Point(w.X + offset.X, w.Y + offset.Y);
        }

        foreach (var prim in sym.ActivePrimitives)
        {
            switch (prim)
            {
                case LinePrim l:
                    dc.DrawLine(pen, W(l.A), W(l.B));
                    break;

                case PolyPrim poly:
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(W(poly.Points[0]), poly.Filled, poly.Closed);
                        for (int i = 1; i < poly.Points.Length; i++)
                            ctx.LineTo(W(poly.Points[i]), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(poly.Filled ? fill : null, pen, geo);
                    break;
                }

                case CirclePrim c:
                    dc.DrawEllipse(c.Filled ? fill : null, pen, W(c.Center), c.Radius, c.Radius);
                    break;

                case ArcPrim arc:
                {
                    var pts = arc.Flatten();
                    for (int i = 0; i + 1 < pts.Length; i++)
                        dc.DrawLine(pen, W(pts[i]), W(pts[i + 1]));
                    break;
                }

                case TextPrim t:
                {
                    var ft = MakeText(t.Text, t.Height, fill);
                    var p = W(t.Anchor);
                    dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
                    break;
                }
            }
        }
    }

    private void DrawLabels(DrawingContext dc, SymbolInstance sym, Vec2 offset)
    {
        var (refPos, valPos) = sym.LabelAnchors();
        if (!string.IsNullOrEmpty(sym.RefDes) && sym.Definition.Name != "Ground")
        {
            var ft = MakeText(sym.RefDes, 5, LabelBrush);
            dc.DrawText(ft, new Point(refPos.X + offset.X - ft.Width / 2, refPos.Y + offset.Y - ft.Height));
        }
        if (!string.IsNullOrEmpty(sym.Value))
        {
            var ft = MakeText(sym.Value, 5, LabelBrush);
            dc.DrawText(ft, new Point(valPos.X + offset.X - ft.Width / 2, valPos.Y + offset.Y));
        }
    }

    private void DrawPinMarkers(DrawingContext dc, SymbolInstance sym, Vec2 offset)
    {
        foreach (var (_, world) in sym.WorldPins())
        {
            if (_connectedPinKeys.Contains(world.Key())) continue;
            dc.DrawEllipse(null, DanglingPen,
                new Point(world.X + offset.X, world.Y + offset.Y), 1.6, 1.6);
        }
    }

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
