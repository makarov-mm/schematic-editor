using System.Globalization;
using System.Text;

namespace SchematicEditor.Core;

/// <summary>
/// A hand-written DXF R12 (AC1009) exporter: R12 is the version every CAD package imports. The
/// editor's Y axis points down and DXF's up, so Y is negated. Numbers use '.' as the decimal
/// separator, and characters outside ASCII are written as \U+XXXX, as R12 has no Unicode.
/// </summary>
public static class DxfExporter
{
    private const string SymbolLayer = "SYMBOLS";
    private const string WireLayer = "WIRES";
    private const string TextLayer = "TEXT";
    private const double LabelHeight = 5;

    public static string Export(SchematicDocument doc)
    {
        var dxf = new DxfWriter();

        dxf.BeginSection("HEADER");
        dxf.Pair(9, "$ACADVER");
        dxf.Pair(1, "AC1009");
        dxf.EndSection();

        dxf.BeginSection("TABLES");
        dxf.Pair(0, "TABLE");
        dxf.Pair(2, "LAYER");
        dxf.Pair(70, 3);
        dxf.Layer(SymbolLayer, color: 7);
        dxf.Layer(WireLayer, color: 5);
        dxf.Layer(TextLayer, color: 3);
        dxf.Pair(0, "ENDTAB");
        dxf.EndSection();

        dxf.BeginSection("ENTITIES");

        foreach (Wire wire in doc.Wires)
        {
            foreach ((Vec2 a, Vec2 b) in wire.Segments())
                dxf.Line(WireLayer, a, b);
        }

        foreach (Vec2 junction in NetlistExtractor.Extract(doc).Junctions)
            dxf.Circle(WireLayer, junction, 1.2);

        foreach (SymbolInstance symbol in doc.Symbols)
        {
            foreach (DrawPrimitive primitive in symbol.WorldPrimitives())
                dxf.Primitive(SymbolLayer, primitive);

            (Vec2 refDesAnchor, Vec2 valueAnchor) = symbol.LabelAnchors();
            if (symbol.RefDes.Length > 0 && symbol.Definition.Kind.ShowsRefDes)
                dxf.Text(TextLayer, refDesAnchor, symbol.RefDes, LabelHeight, VerticalAlignment.Bottom);
            if (symbol.Value.Length > 0)
                dxf.Text(TextLayer, valueAnchor, symbol.Value, LabelHeight, VerticalAlignment.Top);
        }

        dxf.EndSection();
        dxf.Pair(0, "EOF");
        return dxf.ToString();
    }

    public static void ExportToFile(SchematicDocument doc, string path) => File.WriteAllText(path, Export(doc), Encoding.ASCII);

    private enum VerticalAlignment { Baseline = 0, Bottom = 1, Middle = 2, Top = 3 }

    /// <summary>Writes group code / value pairs.</summary>
    private sealed class DxfWriter
    {
        private readonly StringBuilder _text = new();

        public override string ToString() => _text.ToString();

        public void Pair(int code, string value) => _text.Append(code).Append('\n').Append(value).Append('\n');

        public void Pair(int code, int value) => Pair(code, value.ToString(CultureInfo.InvariantCulture));

        public void Pair(int code, double value)
        {
            string text = value.ToString("0.####", CultureInfo.InvariantCulture);
            Pair(code, text == "-0" ? "0" : text);
        }

        /// <summary>A point: X, Y and Z under the codes <paramref name="code"/>, +10 and +20.</summary>
        public void Point(int code, Vec2 p)
        {
            Pair(code, p.X);
            Pair(code + 10, -p.Y);
            Pair(code + 20, "0");
        }

        public void BeginSection(string name)
        {
            Pair(0, "SECTION");
            Pair(2, name);
        }

        public void EndSection() => Pair(0, "ENDSEC");

        public void Layer(string name, int color)
        {
            Pair(0, "LAYER");
            Pair(2, name);
            Pair(70, 0);
            Pair(62, color);
            Pair(6, "CONTINUOUS");
        }

        public void Line(string layer, Vec2 a, Vec2 b)
        {
            Entity("LINE", layer);
            Point(10, a);
            Point(11, b);
        }

        public void Circle(string layer, Vec2 center, double radius)
        {
            Entity("CIRCLE", layer);
            Point(10, center);
            Pair(40, radius);
        }

        /// <summary>A world-space primitive; filled triangles become SOLIDs, other polygons outlines.</summary>
        public void Primitive(string layer, DrawPrimitive primitive)
        {
            switch (primitive)
            {
                case LinePrim l:
                    Line(layer, l.A, l.B);
                    break;

                case PolyPrim { Filled: true, Points: [Vec2 a, Vec2 b, Vec2 c] }:
                    Entity("SOLID", layer);
                    Point(10, a);
                    Point(11, b);
                    Point(12, c);
                    Point(13, c);   // a triangle repeats its third corner
                    break;

                case PolyPrim poly:
                    int count = poly.Closed ? poly.Points.Length : poly.Points.Length - 1;
                    for (int i = 0; i < count; i++)
                        Line(layer, poly.Points[i], poly.Points[(i + 1) % poly.Points.Length]);
                    break;

                case CirclePrim c:
                    Circle(layer, c.Center, c.Radius);
                    break;

                case TextPrim t:
                    Text(layer, t.Anchor, t.Text, t.Height, VerticalAlignment.Middle);
                    break;
            }
        }

        /// <summary>
        /// Text centred horizontally on <paramref name="anchor"/>. The alignment point is exact; the
        /// insertion point, which readers ignoring alignment use, estimates the left end of the baseline.
        /// </summary>
        public void Text(string layer, Vec2 anchor, string text, double height, VerticalAlignment alignment)
        {
            double baselineBelowAnchor = alignment switch
            {
                VerticalAlignment.Top => height,
                VerticalAlignment.Middle => height * 0.5,
                _ => 0,
            };

            Entity("TEXT", layer);
            Point(10, new Vec2(anchor.X - text.Length * height * 0.4, anchor.Y + baselineBelowAnchor));
            Pair(40, height);
            Pair(1, Escape(text));
            Pair(72, 1);   // centred
            Point(11, anchor);
            Pair(73, (int)alignment);
        }

        private void Entity(string type, string layer)
        {
            Pair(0, type);
            Pair(8, layer);
        }

        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c is >= ' ' and <= '~')
                    escaped.Append(c);
                else
                    escaped.Append(CultureInfo.InvariantCulture, $"\\U+{(int)c:X4}");
            }
            return escaped.ToString();
        }
    }
}
