using System.Globalization;
using System.Text;

namespace SchematicEditor.Core;

/// <summary>A hand-written SVG exporter. Editor coordinates map directly: both have Y down.</summary>
public static class SvgExporter
{
    private const string SymbolStroke = "#1a1a1a";
    private const string WireStroke = "#0050c8";
    private const string TextFill = "#606060";

    public static string Export(SchematicDocument doc)
    {
        Rect2 bounds = doc.ContentBounds();
        bounds = (bounds.IsEmpty ? new Rect2(0, 0, 100, 100) : bounds).Inflate(20);

        var svg = new StringBuilder();
        svg.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        svg.AppendLine(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="{F(bounds.MinX)} {F(bounds.MinY)} {F(bounds.Width)} {F(bounds.Height)}" width="{F(bounds.Width * 4)}" height="{F(bounds.Height * 4)}">""");
        svg.AppendLine(CultureInfo.InvariantCulture, $"""<rect x="{F(bounds.MinX)}" y="{F(bounds.MinY)}" width="{F(bounds.Width)}" height="{F(bounds.Height)}" fill="white"/>""");

        svg.AppendLine(CultureInfo.InvariantCulture, $"""<g stroke="{WireStroke}" stroke-width="1" fill="none" stroke-linecap="round">""");
        foreach (Wire wire in doc.Wires)
            svg.AppendLine(CultureInfo.InvariantCulture, $"""<polyline points="{Points(wire.Points)}"/>""");
        svg.AppendLine("</g>");

        svg.AppendLine(CultureInfo.InvariantCulture, $"""<g fill="{WireStroke}">""");
        foreach (Vec2 j in NetlistExtractor.Extract(doc).Junctions)
            svg.AppendLine(CultureInfo.InvariantCulture, $"""<circle cx="{F(j.X)}" cy="{F(j.Y)}" r="1.5"/>""");
        svg.AppendLine("</g>");

        svg.AppendLine(CultureInfo.InvariantCulture, $"""<g stroke="{SymbolStroke}" stroke-width="1" fill="none" stroke-linecap="round" stroke-linejoin="round">""");
        foreach (SymbolInstance symbol in doc.Symbols)
        {
            foreach (DrawPrimitive primitive in symbol.WorldPrimitives())
                svg.AppendLine(Element(primitive));
        }
        svg.AppendLine("</g>");

        svg.AppendLine(CultureInfo.InvariantCulture, $"""<g fill="{TextFill}" font-family="sans-serif" font-size="5" text-anchor="middle">""");
        foreach (SymbolInstance symbol in doc.Symbols)
        {
            (Vec2 refDes, Vec2 value) = symbol.LabelAnchors();
            if (symbol.RefDes.Length > 0 && symbol.Definition.Kind.ShowsRefDes)
                svg.AppendLine(CultureInfo.InvariantCulture, $"""<text x="{F(refDes.X)}" y="{F(refDes.Y)}">{Escape(symbol.RefDes)}</text>""");
            if (symbol.Value.Length > 0)
                svg.AppendLine(CultureInfo.InvariantCulture, $"""<text x="{F(value.X)}" y="{F(value.Y + 4)}">{Escape(symbol.Value)}</text>""");
        }
        svg.AppendLine("</g>");

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    public static void ExportToFile(SchematicDocument doc, string path) => File.WriteAllText(path, Export(doc), new UTF8Encoding(false));

    /// <summary>The SVG element of a world-space primitive.</summary>
    private static string Element(DrawPrimitive primitive) => primitive switch
    {
        LinePrim l => $"""<line x1="{F(l.A.X)}" y1="{F(l.A.Y)}" x2="{F(l.B.X)}" y2="{F(l.B.Y)}"/>""",
        PolyPrim p => $"""<{(p.Closed ? "polygon" : "polyline")} points="{Points(p.Points)}" fill="{(p.Filled ? SymbolStroke : "none")}"/>""",
        CirclePrim c => $"""<circle cx="{F(c.Center.X)}" cy="{F(c.Center.Y)}" r="{F(c.Radius)}" fill="{(c.Filled ? SymbolStroke : "none")}"/>""",
        TextPrim t => $"""<text x="{F(t.Anchor.X)}" y="{F(t.Anchor.Y + t.Height * 0.35)}" fill="{SymbolStroke}" stroke="none" font-family="sans-serif" font-size="{F(t.Height)}" text-anchor="middle">{Escape(t.Text)}</text>""",
        _ => "",
    };

    private static string Points(IEnumerable<Vec2> points) => string.Join(" ", points.Select(p => F(p.X) + "," + F(p.Y)));

    private static string F(double v)
    {
        string text = v.ToString("0.###", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
