using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SchematicEditor.Core;

namespace SchematicEditor.App;

// Tools, editing commands, mouse and keyboard.
public sealed partial class SchematicCanvas
{
    private sealed record ClipSymbol(SymbolDefinition Definition, Vec2 Position, Rotation Rotation, bool Mirror, string Value);

    private SymbolInstance? _ghost;              // the symbol about to be placed
    private readonly List<Vec2> _wirePoints = [];
    private Vec2 _cursorWorld;
    private Vec2 _wireTarget;                    // where a click of the wire tool goes: a pin nearby, else the grid
    private bool _wireTargetValid;               // a wire may start or end there

    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;
    private bool _dragging;
    private Vec2 _dragStart;
    private Vec2 _dragDelta;
    private bool _rubberBand;
    private Vec2 _rubberStart;

    private readonly List<ClipSymbol> _clipSymbols = [];
    private readonly List<Vec2[]> _clipWires = [];
    private int _pasteCount;                     // pastes since the copy; each lands a bit further away

    public EditorTool Tool { get; private set; } = EditorTool.Select;

    /// <summary>Clicks place and remove probes; works while editing and while simulating.</summary>
    public bool ProbeArmed
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            SelectionOrToolChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Whether Rotate and Mirror have something to act on: the symbol being placed or selected symbols.</summary>
    public bool CanRotateMirror => !IsRunning && (Tool == EditorTool.Place && _ghost is not null || _selection.OfType<SymbolInstance>().Any());

    /// <summary>The net of a single selected wire, highlighted to show what it connects.</summary>
    private Net? SelectedNet => _selection.Count == 1 && _selection.First() is Wire wire ? _netlist.NetOf(wire) : null;

    // ------------------------------------------------------------------ tools

    public void SetSelectTool()
    {
        SetSelectToolQuietly();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    private void SetSelectToolQuietly()
    {
        Tool = EditorTool.Select;
        _ghost = null;
        _wirePoints.Clear();
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

    // ------------------------------------------------------------------ editing

    public void DeleteSelection()
    {
        if (_selection.Count > 0)
            Undo.Push(new DeleteElementsCommand(_selection));
    }

    /// <summary>Rotates the symbol being placed, or else the selected symbols: the R key.</summary>
    public void RotateSelectionOrGhost()
    {
        if (Tool == EditorTool.Place && _ghost is not null)
        {
            _ghost.Rotation = _ghost.Rotation.Next;
            InvalidateVisual();
        }
        else if (SelectedSymbols() is { Count: > 0 } symbols)
        {
            Undo.Push(new RotateSymbolsCommand(symbols));
        }
    }

    /// <summary>Mirrors the symbol being placed, or else the selected symbols: the M key.</summary>
    public void MirrorSelectionOrGhost()
    {
        if (Tool == EditorTool.Place && _ghost is not null)
        {
            _ghost.Mirror = !_ghost.Mirror;
            InvalidateVisual();
        }
        else if (SelectedSymbols() is { Count: > 0 } symbols)
        {
            Undo.Push(new MirrorSymbolsCommand(symbols));
        }
    }

    private List<SymbolInstance> SelectedSymbols() => [.. _selection.OfType<SymbolInstance>()];

    private void SelectAll()
    {
        _selection.UnionWith(Document.Elements);
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    public void CopySelection()
    {
        _clipSymbols.Clear();
        _clipWires.Clear();
        _pasteCount = 0;
        foreach (SchematicElement element in _selection)
        {
            switch (element)
            {
                case SymbolInstance s:
                    _clipSymbols.Add(new ClipSymbol(s.Definition, s.Position, s.Rotation, s.Mirror, s.Value));
                    break;
                case Wire w:
                    _clipWires.Add([.. w.Points]);
                    break;
            }
        }
        Status($"Copied {_clipSymbols.Count + _clipWires.Count} element(s).");
    }

    /// <summary>Adds copies of the copied elements, two grid steps further for every paste, and selects them.</summary>
    public void Paste()
    {
        if (_clipSymbols.Count == 0 && _clipWires.Count == 0)
            return;

        Vec2 offset = new Vec2(2 * Grid, 2 * Grid) * ++_pasteCount;
        List<SchematicElement> pasted =
        [
            .. _clipSymbols.Select(c => new SymbolInstance(c.Definition, c.Position + offset) { Rotation = c.Rotation, Mirror = c.Mirror, Value = c.Value }),
            .. _clipWires.Select(points => new Wire(points.Select(p => p + offset))),
        ];
        Undo.Push(new CompositeCommand("Paste", pasted.Select(e => new AddElementCommand(e))));

        _selection.Clear();
        _selection.UnionWith(pasted);
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    private void EditProperties(SymbolInstance symbol)
    {
        var dialog = new PropertiesDialog(symbol.RefDes, symbol.Value) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && (dialog.RefDes != symbol.RefDes || dialog.Value != symbol.Value))
            Undo.Push(new EditSymbolCommand(symbol, dialog.RefDes, dialog.Value));
    }

    /// <summary>The topmost symbol at a point, or else the nearest wire within reach.</summary>
    private SchematicElement? HitTest(Vec2 p)
    {
        for (int i = Document.Elements.Count - 1; i >= 0; i--)
        {
            if (Document.Elements[i] is SymbolInstance s && s.Bounds.Inflate(1).Contains(p))
                return s;
        }

        Wire? nearest = null;
        double best = HitTolerance;
        foreach (Wire w in Document.Wires)
        {
            double d = w.DistanceTo(p);
            if (d <= best)
            {
                best = d;
                nearest = w;
            }
        }
        return nearest;
    }

    // ------------------------------------------------------------------ wires

    /// <summary>
    /// A click of the wire tool. Wires start and end on a connection point, a pin or an existing
    /// wire; the corners in between are free. A click on a connection point finishes the wire.
    /// </summary>
    private void WireClick()
    {
        UpdateWireTarget();
        Vec2 p = _wireTarget;

        if (_wirePoints.Count == 0)
        {
            if (_wireTargetValid)
                _wirePoints.Add(p);
            else
                Status("A wire must start on a pin or an existing wire.");
            InvalidateVisual();
            return;
        }

        if (p.Key == _wirePoints[^1].Key)
            return;

        foreach (Vec2 corner in OrthogonalRoute(_wirePoints[^1], p))
        {
            if (corner.Key != _wirePoints[^1].Key)
                _wirePoints.Add(corner);
        }

        if (_wireTargetValid)
            CommitWire();
        InvalidateVisual();
    }

    /// <summary>Where the wire tool points: magnetically at a pin nearby, else at the grid.</summary>
    private void UpdateWireTarget()
    {
        _wireTarget = Document.FindPinNear(_cursorWorld, Math.Max(HitTolerance * 1.5, Grid * 0.6)) ?? _cursorWorld.Snap(Grid);
        _wireTargetValid = Document.IsConnectionPoint(_wireTarget);
    }

    /// <summary>The corner and the end of an L-shaped route from a to b, along the longer axis first.</summary>
    private static IEnumerable<Vec2> OrthogonalRoute(Vec2 a, Vec2 b)
    {
        if (Math.Abs(a.X - b.X) >= 0.01 && Math.Abs(a.Y - b.Y) >= 0.01)
            yield return Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y) ? new Vec2(b.X, a.Y) : new Vec2(a.X, b.Y);
        yield return b;
    }

    private void CommitWire()
    {
        if (_wirePoints.Count >= 2 && Document.IsConnectionPoint(_wirePoints[0]) && Document.IsConnectionPoint(_wirePoints[^1]))
        {
            Undo.Push(new AddElementCommand(new Wire(_wirePoints)));
            Status("Wire connected.");
        }
        _wirePoints.Clear();
    }

    private void CancelWire()
    {
        _wirePoints.Clear();
        Status("Wire cancelled.");
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point screen = e.GetPosition(this);
        _cursorWorld = ToWorld(screen);

        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
        {
            _panning = true;
            _panStart = screen;
            _panOrigin = _pan;
            CaptureMouse();
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            if (ProbeArmed)
                ProbeArmed = false;
            else if (IsRunning)
                return;
            else if (Tool == EditorTool.Wire && _wirePoints.Count > 0)
                CancelWire();
            else
                SetSelectTool();
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
            return;

        if (ProbeArmed)
            ProbeClick(_cursorWorld);
        else if (IsRunning)
            ToggleSwitchAt(_cursorWorld);
        else if (Tool == EditorTool.Place && _ghost is not null)   // and stay in place mode for the next one
            Undo.Push(new AddElementCommand(new SymbolInstance(_ghost.Definition, _cursorWorld.Snap(Grid)) { Rotation = _ghost.Rotation, Mirror = _ghost.Mirror }));
        else if (Tool == EditorTool.Wire)
            WireClick();
        else if (Tool == EditorTool.Select)
            SelectClick(e.ClickCount);
    }

    private void SelectClick(int clickCount)
    {
        SchematicElement? hit = HitTest(_cursorWorld);

        if (clickCount == 2)
        {
            if (hit is SymbolInstance symbol)
                EditProperties(symbol);
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (hit is null)
        {
            if (!shift)
                _selection.Clear();
            _rubberBand = true;
            _rubberStart = _cursorWorld;
        }
        else
        {
            if (shift)
            {
                if (!_selection.Add(hit))
                    _selection.Remove(hit);
            }
            else if (!_selection.Contains(hit))
            {
                _selection.Clear();
                _selection.Add(hit);
            }
            _dragging = true;
            _dragStart = _cursorWorld.Snap(Grid);
            _dragDelta = Vec2.Zero;
        }
        CaptureMouse();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point screen = e.GetPosition(this);
        _cursorWorld = ToWorld(screen);
        Vec2 snapped = _cursorWorld.Snap(Grid);
        CursorStatusChanged?.Invoke(_sim is null
            ? string.Create(CultureInfo.InvariantCulture, $"x {snapped.X:0}   y {snapped.Y:0}   zoom {_zoom:0.0}×")
            : LiveReadout(_sim, snapped));

        if (_panning)
            _pan = _panOrigin + (screen - _panStart);
        else if (_dragging)
            _dragDelta = snapped - _dragStart;
        else if (Tool == EditorTool.Wire)
            UpdateWireTarget();
        else if (Tool == EditorTool.Place && _ghost is not null)
            _ghost.Position = snapped;
        else if (!_rubberBand)
            return;   // nothing follows the cursor

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            EndGesture();
        }
        else if (_dragging && e.ChangedButton == MouseButton.Left)
        {
            Vec2 delta = _dragDelta;
            EndGesture();
            if (delta.Length > 0.01 && _selection.Count > 0)
                Undo.Push(new MoveElementsCommand(_selection, delta));
        }
        else if (_rubberBand && e.ChangedButton == MouseButton.Left)
        {
            Rect2 band = Rect2.FromPoints(_rubberStart, _cursorWorld);
            EndGesture();
            _selection.UnionWith(Document.Elements.Where(el => band.Intersects(el.Bounds)));
            SelectionOrToolChanged?.Invoke();
        }
    }

    // When another window takes the mouse away in the middle of a drag, the drag is dropped.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panning = _dragging = _rubberBand = false;
        _dragDelta = Vec2.Zero;
        InvalidateVisual();
    }

    private void EndGesture()
    {
        _panning = _dragging = _rubberBand = false;
        _dragDelta = Vec2.Zero;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        if (IsRunning && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            HitTest(_cursorWorld) is SymbolInstance { Definition.Kind: SymbolKind.Resistor or SymbolKind.Capacitor or SymbolKind.Inductor } component)
        {
            TweakValue(component, up: e.Delta > 0);
            return;
        }

        // Zoom about the cursor.
        Point screen = e.GetPosition(this);
        Vec2 anchor = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.05, 100.0);
        _pan = new Vector(screen.X - anchor.X * _zoom, screen.Y - anchor.Y * _zoom);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = IsRunning ? RunModeKey(e.Key) : EditKey(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private bool RunModeKey(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                StopSimulation();
                return true;
            case Key.F:
                ZoomToFit();
                return true;
            default:
                return false;
        }
    }

    private bool EditKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Escape when ProbeArmed:
                ProbeArmed = false;
                break;
            case Key.Escape when Tool == EditorTool.Wire && _wirePoints.Count > 0:
                CancelWire();
                break;
            case Key.Escape:
                SetSelectTool();
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
            case Key.W when !ctrl:
                SetWireTool();
                break;
            case Key.F:
                ZoomToFit();
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
                SelectAll();
                break;
            default:
                return false;
        }
        return true;
    }
}
