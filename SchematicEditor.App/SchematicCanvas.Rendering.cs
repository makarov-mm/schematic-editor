using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

// Drawing.
public sealed partial class SchematicCanvas
{
    private static readonly Brush SymbolBrush = Paint.Brush(0x1A, 0x1A, 0x1A);
    private static readonly Brush WireBrush = Paint.Brush(0x00, 0x50, 0xC8);
    private static readonly Brush SelectionBrush = Paint.Brush(0xE8, 0x7A, 0x00);
    private static readonly Brush GhostBrush = Paint.Brush(0x30, 0x90, 0x30, alpha: 0x90);
    private static readonly Brush ErrorBrush = Paint.Brush(0xD0, 0x20, 0x20);
    private static readonly Brush LabelBrush = Paint.Brush(0x60, 0x60, 0x60);
    private static readonly Brush ValidBrush = Paint.Brush(0x18, 0xA0, 0x46);

    private static readonly Pen SymbolPen = Paint.Pen(SymbolBrush, 1.0, round: true);
    private static readonly Pen WirePen = Paint.Pen(WireBrush, 0.9, round: true);
    private static readonly Pen NetHighlightPen = Paint.Pen(Paint.Brush(0xFF, 0x9E, 0x1B, alpha: 0x90), 3.2, round: true);
    private static readonly Pen SelectionPen = Paint.Pen(SelectionBrush, 1.4, round: true);
    private static readonly Pen GhostPen = Paint.Pen(GhostBrush, 1.0, round: true);
    private static readonly Pen UnconnectedPinPen = Paint.Pen(ErrorBrush, 0.8, round: true);
    private static readonly Pen ValidTargetPen = Paint.Pen(ValidBrush, 1.2, round: true);
    private static readonly Pen InvalidTargetPen = Paint.Pen(ErrorBrush, 1.0, round: true);
    private static readonly Pen BlownFusePen = Paint.Pen(ErrorBrush, 1.6, round: true);
    private static readonly Pen ProbeOutlinePen = Paint.Pen(Brushes.White, 0.7, round: true);
    private static readonly Pen MinorGridPen = Paint.Pen(Paint.Brush(0xEA, 0xEA, 0xEA), 1.0);
    private static readonly Pen MajorGridPen = Paint.Pen(Paint.Brush(0xD6, 0xD6, 0xD6), 1.0);

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    // Laid-out labels, kept across frames: building a FormattedText is the costly part of drawing text.
    private readonly Dictionary<(string Text, double Size, Brush Brush), FormattedText> _texts = [];

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));   // also makes the whole surface hit-testable
        DrawGrid(dc);

        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _pan.X, _pan.Y));

        if (SelectedNet is { } net)
        {
            foreach (Wire wire in net.Wires)
                DrawWire(dc, wire, NetHighlightPen);
            foreach (NetPin pin in net.Pins)
                dc.DrawEllipse(null, NetHighlightPen, pin.World.ToPoint(), 2.6, 2.6);
        }

        foreach (Wire wire in Document.Wires)
        {
            bool selected = _selection.Contains(wire);
            PushDragOffset(dc, selected);
            DrawWire(dc, wire, selected ? SelectionPen : WirePen);
            PopDragOffset(dc, selected);
        }

        foreach (Vec2 junction in _netlist.Junctions)
            dc.DrawEllipse(WireBrush, null, junction.ToPoint(), 1.4, 1.4);

        foreach (SymbolInstance symbol in Document.Symbols)
        {
            bool selected = _selection.Contains(symbol);
            PushDragOffset(dc, selected);
            if (_sim is not null && symbol.Definition.Kind == SymbolKind.Lamp)
                DrawLampGlow(dc, symbol, _sim.GetLampBrightness(symbol));
            DrawSymbol(dc, symbol, selected ? SelectionPen : SymbolPen, selected ? SelectionBrush : SymbolBrush);
            DrawLabels(dc, symbol);
            DrawUnconnectedPins(dc, symbol);
            if (_sim is not null && symbol.Definition.Kind == SymbolKind.Fuse && _sim.IsFuseBlown(symbol))
                DrawBlownFuse(dc, symbol);
            PopDragOffset(dc, selected);
        }

        DrawProbes(dc);

        if (Tool == EditorTool.Place && _ghost is not null)
            DrawSymbol(dc, _ghost, GhostPen, GhostBrush);

        if (Tool == EditorTool.Wire)
            DrawWirePreview(dc);

        if (_rubberBand)
        {
            Rect2 band = Rect2.FromPoints(_rubberStart, _cursorWorld);
            var pen = new Pen(SelectionBrush, 1.0 / _zoom) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, new Rect(band.MinX, band.MinY, band.Width, band.Height));
        }

        dc.Pop();
    }

    // Selected elements follow the cursor while they are dragged.
    private void PushDragOffset(DrawingContext dc, bool selected)
    {
        if (selected && _dragDelta != Vec2.Zero)
            dc.PushTransform(new TranslateTransform(_dragDelta.X, _dragDelta.Y));
    }

    private void PopDragOffset(DrawingContext dc, bool selected)
    {
        if (selected && _dragDelta != Vec2.Zero)
            dc.Pop();
    }

    /// <summary>The grid, in screen space so that its lines are one pixel wide at any zoom; minor lines only when they are far enough apart.</summary>
    private void DrawGrid(DrawingContext dc)
    {
        double spacing = Grid * _zoom;
        if (spacing < 5)
            return;

        Vec2 topLeft = ToWorld(new Point(0, 0));
        Vec2 bottomRight = ToWorld(new Point(ActualWidth, ActualHeight));
        bool minorLines = spacing >= 12;

        for (double x = Math.Floor(topLeft.X / Grid) * Grid; x <= bottomRight.X; x += Grid)
        {
            bool major = IsMajorGridLine(x);
            if (major || minorLines)
            {
                double sx = x * _zoom + _pan.X;
                dc.DrawLine(major ? MajorGridPen : MinorGridPen, new Point(sx, 0), new Point(sx, ActualHeight));
            }
        }
        for (double y = Math.Floor(topLeft.Y / Grid) * Grid; y <= bottomRight.Y; y += Grid)
        {
            bool major = IsMajorGridLine(y);
            if (major || minorLines)
            {
                double sy = y * _zoom + _pan.Y;
                dc.DrawLine(major ? MajorGridPen : MinorGridPen, new Point(0, sy), new Point(ActualWidth, sy));
            }
        }

        static bool IsMajorGridLine(double v) => Math.Abs(Math.IEEERemainder(v, Grid * 5)) < 0.01;
    }

    private static void DrawWire(DrawingContext dc, Wire wire, Pen pen)
    {
        foreach ((Vec2 a, Vec2 b) in wire.Segments())
            dc.DrawLine(pen, a.ToPoint(), b.ToPoint());
    }

    private void DrawSymbol(DrawingContext dc, SymbolInstance symbol, Pen pen, Brush fill) =>
        PrimitiveRenderer.Draw(dc, symbol.WorldPrimitives(), pen, fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabels(DrawingContext dc, SymbolInstance symbol)
    {
        (Vec2 refDes, Vec2 value) = symbol.LabelAnchors();
        if (symbol.RefDes.Length > 0 && symbol.Definition.Kind.ShowsRefDes)
        {
            FormattedText text = Text(symbol.RefDes, 5, LabelBrush);
            dc.DrawText(text, new Point(refDes.X - text.Width / 2, refDes.Y - text.Height));
        }
        if (symbol.Value.Length > 0)
        {
            FormattedText text = Text(symbol.Value, 5, LabelBrush);
            dc.DrawText(text, new Point(value.X - text.Width / 2, value.Y));
        }
    }

    // A red ring on every pin that connects to nothing.
    private void DrawUnconnectedPins(DrawingContext dc, SymbolInstance symbol)
    {
        foreach ((PinDefinition pin, Vec2 world) in symbol.WorldPins())
        {
            if (_netlist.NetOf(symbol, pin) is not { IsConnecting: true })
                dc.DrawEllipse(null, UnconnectedPinPen, world.ToPoint(), 1.6, 1.6);
        }
    }

    private static void DrawLampGlow(DrawingContext dc, SymbolInstance lamp, double brightness)
    {
        if (brightness < 0.02)
            return;

        Point center = lamp.Position.ToPoint();
        var glow = new RadialGradientBrush(Color.FromArgb((byte)(150 * brightness), 0xFF, 0xD9, 0x40), Color.FromArgb(0, 0xFF, 0xD9, 0x40));
        glow.Freeze();
        double radius = 10 + 14 * brightness;
        dc.DrawEllipse(glow, null, center, radius, radius);

        dc.DrawEllipse(Paint.Brush(0xFF, 0xE8, 0x80, alpha: (byte)(120 * brightness)), null, center, 8, 8);
    }

    private static void DrawBlownFuse(DrawingContext dc, SymbolInstance fuse)
    {
        Vec2 c = fuse.Position;
        dc.DrawLine(BlownFusePen, new Point(c.X - 5, c.Y - 5), new Point(c.X + 5, c.Y + 5));
        dc.DrawLine(BlownFusePen, new Point(c.X - 5, c.Y + 5), new Point(c.X + 5, c.Y - 5));
    }

    private void DrawProbes(DrawingContext dc)
    {
        foreach (Probe probe in _probes)
        {
            Point position = probe.Position.ToPoint();
            dc.DrawEllipse(probe.Brush, ProbeOutlinePen, position, 2.4, 2.4);
            FormattedText label = Text(probe.Label, 4, probe.Brush);
            dc.DrawText(label, new Point(position.X + 3.5, position.Y - label.Height));
        }
    }

    // The ring on the wire tool's target, green on a legal end point, and the route the next click would add.
    private void DrawWirePreview(DrawingContext dc)
    {
        dc.DrawEllipse(null, _wireTargetValid ? ValidTargetPen : InvalidTargetPen, _wireTarget.ToPoint(), 2.2, 2.2);
        if (_wirePoints.Count == 0)
            return;

        Pen pen = _wireTargetValid ? ValidTargetPen : GhostPen;
        Vec2 previous = _wirePoints[0];
        foreach (Vec2 point in _wirePoints.Skip(1).Concat(OrthogonalRoute(_wirePoints[^1], _wireTarget)))
        {
            dc.DrawLine(pen, previous.ToPoint(), point.ToPoint());
            previous = point;
        }
    }

    private FormattedText Text(string text, double size, Brush brush)
    {
        if (!_texts.TryGetValue((text, size, brush), out FormattedText? formatted))
        {
            if (_texts.Count >= 1000)
                _texts.Clear();   // values change while simulating; don't hoard the old ones
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _texts.Add((text, size, brush), formatted);
        }
        return formatted;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _texts.Clear();
        base.OnDpiChanged(oldDpi, newDpi);
    }
}
