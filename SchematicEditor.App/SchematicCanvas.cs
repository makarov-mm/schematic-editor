using System.Windows;
using SchematicEditor.Core;

namespace SchematicEditor.App;

public enum EditorTool { Select, Wire, Place }

/// <summary>
/// The editing surface. The document is retained and drawn immediately in OnRender, in world
/// units under a single zoom/pan transform, so lines and text stay crisp at any zoom.
/// The other parts: input and editing (SchematicCanvas.Input.cs), simulation and probes
/// (SchematicCanvas.Simulation.cs), drawing (SchematicCanvas.Rendering.cs).
/// </summary>
public sealed partial class SchematicCanvas : FrameworkElement
{
    private const double Grid = SchematicDocument.Grid;

    private readonly HashSet<SchematicElement> _selection = [];
    private NetlistResult _netlist;

    // The view: screen = world * _zoom + _pan.
    private double _zoom = 3.0;
    private Vector _pan = new(80, 200);

    public SchematicCanvas()
    {
        Document = new SchematicDocument();
        Undo = new UndoStack(Document);
        Document.Changed += OnDocumentChanged;
        _netlist = NetlistExtractor.Extract(Document);
        Focusable = true;
        ClipToBounds = true;
    }

    public SchematicDocument Document { get; private set; }
    public UndoStack Undo { get; private set; }
    public NetlistResult CurrentNetlist => _netlist;

    /// <summary>Messages for the status bar: results of actions, warnings.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The continuous readout: cursor position and zoom, or live values while simulating.</summary>
    public event Action<string>? CursorStatusChanged;

    public event Action? SelectionOrToolChanged;

    /// <summary>Raised after every edit of the document, once the netlist is up to date.</summary>
    public event Action? DocumentChanged;

    public void SetDocument(SchematicDocument doc)
    {
        StopSimulation();
        EndGesture();
        _wirePoints.Clear();
        Document.Changed -= OnDocumentChanged;
        Document = doc;
        Undo = new UndoStack(doc);
        Document.Changed += OnDocumentChanged;
        _selection.Clear();
        _probes.Clear();   // they belong to the old document
        OnDocumentChanged();
        ZoomToFit();
    }

    private void OnDocumentChanged()
    {
        StopSimulation();   // the circuit has changed
        _netlist = NetlistExtractor.Extract(Document);
        _selection.RemoveWhere(e => Document.FindById(e.Id) != e);
        RevalidateProbes();
        DocumentChanged?.Invoke();
        SelectionOrToolChanged?.Invoke();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ view

    private Vec2 ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    /// <summary>How close the cursor has to be to hit something: 6 pixels, in world units.</summary>
    private double HitTolerance => 6.0 / _zoom;

    public void ZoomToFit()
    {
        Rect2 bounds = Document.ContentBounds();
        if (bounds.IsEmpty || ActualWidth < 10 || ActualHeight < 10)
        {
            _zoom = 3.0;
            _pan = new Vector(ActualWidth * 0.3, ActualHeight * 0.5);
        }
        else
        {
            bounds = bounds.Inflate(30);
            _zoom = Math.Clamp(Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height), 0.05, 100);
            CenterOn(bounds.Center);
        }
        InvalidateVisual();
    }

    public void CenterOn(Vec2 world)
    {
        _pan = new Vector(ActualWidth * 0.5 - world.X * _zoom, ActualHeight * 0.5 - world.Y * _zoom);
        InvalidateVisual();
    }

    private void Status(string message) => StatusChanged?.Invoke(message);
}
