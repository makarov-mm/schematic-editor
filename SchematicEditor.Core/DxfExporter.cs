using System.Globalization;
using System.Text;

namespace SchematicEditor.Core;

/// <summary>
/// Minimal hand-written DXF R12 (AC1009) ASCII exporter — no external libraries.
/// R12 was chosen because virtually every CAD package can import it.
/// Editor Y axis points down; DXF Y points up, so Y is negated on export.
/// All numbers use InvariantCulture (DXF requires '.' as decimal separator).
/// </summary>
public static class DxfExporter
{
    private const string LayerSymbols = "SYMBOLS";
    private const string LayerWires = "WIRES";
    private const string LayerText = "TEXT";

    public static string Export(SchematicDocument doc)
    {
        var sb = new StringBuilder();

        // --- HEADER ---
        Pair(sb, 0, "SECTION"); Pair(sb, 2, "HEADER");
        Pair(sb, 9, "$ACADVER"); Pair(sb, 1, "AC1009");
        Pair(sb, 0, "ENDSEC");

        // --- TABLES (layers) ---
        Pair(sb, 0, "SECTION"); Pair(sb, 2, "TABLES");
        Pair(sb, 0, "TABLE"); Pair(sb, 2, "LAYER"); Pair(sb, 70, "3");
        Layer(sb, LayerSymbols, 7);
        Layer(sb, LayerWires, 5);
        Layer(sb, LayerText, 3);
        Pair(sb, 0, "ENDTAB");
        Pair(sb, 0, "ENDSEC");

        // --- ENTITIES ---
        Pair(sb, 0, "SECTION"); Pair(sb, 2, "ENTITIES");

        foreach (var wire in doc.Wires)
            foreach (var (a, b) in wire.Segments())
                Line(sb, LayerWires, a, b);

        var netlist = NetlistExtractor.Extract(doc);
        foreach (var j in netlist.Junctions)
            Circle(sb, LayerWires, j, 1.2);

        foreach (var sym in doc.Symbols)
        {
            ExportSymbol(sb, sym);

            var (refPos, valPos) = sym.LabelAnchors();
            if (!string.IsNullOrEmpty(sym.RefDes) && sym.Definition.Name != "Ground")
                Text(sb, LayerText, refPos, sym.RefDes, 5);
            if (!string.IsNullOrEmpty(sym.Value))
                Text(sb, LayerText, valPos, sym.Value, 5);
        }

        Pair(sb, 0, "ENDSEC");
        Pair(sb, 0, "EOF");
        return sb.ToString();
    }

    public static void ExportToFile(SchematicDocument doc, string path) =>
        File.WriteAllText(path, Export(doc), new UTF8Encoding(false));

    private static void ExportSymbol(StringBuilder sb, SymbolInstance sym)
    {
        foreach (var prim in sym.ActivePrimitives)
        {
            switch (prim)
            {
                case LinePrim l:
                    Line(sb, LayerSymbols, sym.ToWorld(l.A), sym.ToWorld(l.B));
                    break;

                case PolyPrim poly:
                {
                    var pts = poly.Points.Select(sym.ToWorld).ToArray();
                    if (poly.Filled && pts.Length == 3)
                    {
                        Solid(sb, LayerSymbols, pts[0], pts[1], pts[2]);
                    }
                    else
                    {
                        int count = poly.Closed ? pts.Length : pts.Length - 1;
                        for (int i = 0; i < count; i++)
                            Line(sb, LayerSymbols, pts[i], pts[(i + 1) % pts.Length]);
                    }
                    break;
                }

                case CirclePrim c:
                    Circle(sb, LayerSymbols, sym.ToWorld(c.Center), c.Radius);
                    break;

                case ArcPrim arc:
                {
                    // Flattened: rotation/mirror of true ARC entities is error-prone,
                    // short polylines import identically everywhere.
                    var pts = arc.Flatten().Select(sym.ToWorld).ToArray();
                    for (int i = 0; i + 1 < pts.Length; i++)
                        Line(sb, LayerSymbols, pts[i], pts[i + 1]);
                    break;
                }

                case TextPrim t:
                    Text(sb, LayerSymbols, sym.ToWorld(t.Anchor), t.Text, t.Height);
                    break;
            }
        }
    }

    // --- entity helpers -------------------------------------------------

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static void Pair(StringBuilder sb, int code, string value)
    {
        sb.Append(code).Append('\n').Append(value).Append('\n');
    }

    private static void Layer(StringBuilder sb, string name, int color)
    {
        Pair(sb, 0, "LAYER");
        Pair(sb, 2, name);
        Pair(sb, 70, "0");
        Pair(sb, 62, color.ToString(CultureInfo.InvariantCulture));
        Pair(sb, 6, "CONTINUOUS");
    }

    private static void Line(StringBuilder sb, string layer, Vec2 a, Vec2 b)
    {
        Pair(sb, 0, "LINE"); Pair(sb, 8, layer);
        Pair(sb, 10, F(a.X)); Pair(sb, 20, F(-a.Y)); Pair(sb, 30, "0");
        Pair(sb, 11, F(b.X)); Pair(sb, 21, F(-b.Y)); Pair(sb, 31, "0");
    }

    private static void Circle(StringBuilder sb, string layer, Vec2 c, double r)
    {
        Pair(sb, 0, "CIRCLE"); Pair(sb, 8, layer);
        Pair(sb, 10, F(c.X)); Pair(sb, 20, F(-c.Y)); Pair(sb, 30, "0");
        Pair(sb, 40, F(r));
    }

    private static void Solid(StringBuilder sb, string layer, Vec2 a, Vec2 b, Vec2 c)
    {
        Pair(sb, 0, "SOLID"); Pair(sb, 8, layer);
        Pair(sb, 10, F(a.X)); Pair(sb, 20, F(-a.Y)); Pair(sb, 30, "0");
        Pair(sb, 11, F(b.X)); Pair(sb, 21, F(-b.Y)); Pair(sb, 31, "0");
        Pair(sb, 12, F(c.X)); Pair(sb, 22, F(-c.Y)); Pair(sb, 32, "0");
        Pair(sb, 13, F(c.X)); Pair(sb, 23, F(-c.Y)); Pair(sb, 33, "0");
    }

    private static void Text(StringBuilder sb, string layer, Vec2 center, string text, double height)
    {
        // DXF TEXT default anchor is left baseline; approximate centering.
        double x = center.X - text.Length * height * 0.4;
        double y = -center.Y - height * 0.5;
        Pair(sb, 0, "TEXT"); Pair(sb, 8, layer);
        Pair(sb, 10, F(x)); Pair(sb, 20, F(y)); Pair(sb, 30, "0");
        Pair(sb, 40, F(height));
        Pair(sb, 1, text);
    }
}
