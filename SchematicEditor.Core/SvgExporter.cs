using System.Globalization;
using System.Text;

namespace SchematicEditor.Core;

/// <summary>Hand-written SVG exporter. Editor coordinates map directly (both Y down).</summary>
public static class SvgExporter
{
    private const string SymbolStroke = "#1a1a1a";
    private const string WireStroke = "#0050c8";
    private const string TextFill = "#606060";

    public static string Export(SchematicDocument doc)
    {
        var bounds = doc.ContentBounds();
        if (bounds.IsEmpty) bounds = new Rect2(0, 0, 100, 100);
        bounds = bounds.Inflate(20);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
            $"viewBox=\"{F(bounds.MinX)} {F(bounds.MinY)} {F(bounds.Width)} {F(bounds.Height)}\" " +
            $"width=\"{F(bounds.Width * 4)}\" height=\"{F(bounds.Height * 4)}\">");
        sb.AppendLine("<rect x=\"" + F(bounds.MinX) + "\" y=\"" + F(bounds.MinY) +
            "\" width=\"" + F(bounds.Width) + "\" height=\"" + F(bounds.Height) + "\" fill=\"white\"/>");

        // Wires.
        sb.AppendLine($"<g stroke=\"{WireStroke}\" stroke-width=\"1\" fill=\"none\" stroke-linecap=\"round\">");
        foreach (var wire in doc.Wires)
        {
            sb.Append("<polyline points=\"");
            sb.Append(string.Join(" ", wire.Points.Select(p => F(p.X) + "," + F(p.Y))));
            sb.AppendLine("\"/>");
        }
        sb.AppendLine("</g>");

        // Junction dots.
        var netlist = NetlistExtractor.Extract(doc);
        sb.AppendLine($"<g fill=\"{WireStroke}\">");
        foreach (var j in netlist.Junctions)
            sb.AppendLine($"<circle cx=\"{F(j.X)}\" cy=\"{F(j.Y)}\" r=\"1.5\"/>");
        sb.AppendLine("</g>");

        // Symbols.
        sb.AppendLine($"<g stroke=\"{SymbolStroke}\" stroke-width=\"1\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        foreach (var sym in doc.Symbols)
            ExportSymbol(sb, sym);
        sb.AppendLine("</g>");

        // Labels.
        sb.AppendLine($"<g fill=\"{TextFill}\" font-family=\"sans-serif\" font-size=\"5\" text-anchor=\"middle\">");
        foreach (var sym in doc.Symbols)
        {
            var (refPos, valPos) = sym.LabelAnchors();
            if (!string.IsNullOrEmpty(sym.RefDes) && sym.Definition.Name != "Ground")
                sb.AppendLine($"<text x=\"{F(refPos.X)}\" y=\"{F(refPos.Y)}\">{Esc(sym.RefDes)}</text>");
            if (!string.IsNullOrEmpty(sym.Value))
                sb.AppendLine($"<text x=\"{F(valPos.X)}\" y=\"{F(valPos.Y + 4)}\">{Esc(sym.Value)}</text>");
        }
        sb.AppendLine("</g>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void ExportSymbol(StringBuilder sb, SymbolInstance sym)
    {
        foreach (DrawPrimitive prim in sym.ActivePrimitives)
        {
            switch (prim)
            {
                case LinePrim l:
                {
                    Vec2 a = sym.ToWorld(l.A), b = sym.ToWorld(l.B);
                    sb.AppendLine($"<line x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>");
                    break;
                }
                case PolyPrim poly:
                {
                    var pts = string.Join(" ", poly.Points.Select(p =>
                    {
                        var w = sym.ToWorld(p);
                        return F(w.X) + "," + F(w.Y);
                    }));
                    string tag = poly.Closed ? "polygon" : "polyline";
                    string fill = poly.Filled ? SymbolStroke : "none";
                    sb.AppendLine($"<{tag} points=\"{pts}\" fill=\"{fill}\"/>");
                    break;
                }
                case CirclePrim c:
                {
                    var w = sym.ToWorld(c.Center);
                    string fill = c.Filled ? SymbolStroke : "none";
                    sb.AppendLine($"<circle cx=\"{F(w.X)}\" cy=\"{F(w.Y)}\" r=\"{F(c.Radius)}\" fill=\"{fill}\"/>");
                    break;
                }
                case ArcPrim arc:
                {
                    var pts = string.Join(" ", arc.Flatten().Select(p =>
                    {
                        var w = sym.ToWorld(p);
                        return F(w.X) + "," + F(w.Y);
                    }));
                    sb.AppendLine($"<polyline points=\"{pts}\"/>");
                    break;
                }
                case TextPrim t:
                {
                    var w = sym.ToWorld(t.Anchor);
                    sb.AppendLine(
                        $"<text x=\"{F(w.X)}\" y=\"{F(w.Y + t.Height * 0.35)}\" fill=\"{SymbolStroke}\" " +
                        $"stroke=\"none\" font-family=\"sans-serif\" font-size=\"{F(t.Height)}\" " +
                        $"text-anchor=\"middle\">{Esc(t.Text)}</text>");
                    break;
                }
            }
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    public static void ExportToFile(SchematicDocument doc, string path) => File.WriteAllText(path, Export(doc), new UTF8Encoding(false));
}
