using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchematicEditor.Core;

/// <summary>Native document format (.schem.json). Plain DTOs, System.Text.Json, no dependencies.</summary>
public static class JsonIo
{
    private sealed record SymbolDto(
        int Id, string Symbol, double X, double Y, int Rotation, bool Mirror,
        string RefDes, string Value, bool On = false);

    private sealed record WireDto(int Id, double[] Points);

    private sealed record DocumentDto(
        string Format, int Version, List<SymbolDto> Symbols, List<WireDto> Wires);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string Save(SchematicDocument doc)
    {
        var dto = new DocumentDto(
            "schematic-editor", 1,
            doc.Symbols.Select(s => new SymbolDto(
                s.Id, s.Definition.Name, s.Position.X, s.Position.Y,
                (int)s.Rotation, s.Mirror, s.RefDes, s.Value, s.StateOn)).ToList(),
            doc.Wires.Select(w => new WireDto(
                w.Id, w.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray())).ToList());

        return JsonSerializer.Serialize(dto, Options);
    }

    public static void SaveToFile(SchematicDocument doc, string path) =>
        File.WriteAllText(path, Save(doc));

    public static SchematicDocument Load(string json)
    {
        var dto = JsonSerializer.Deserialize<DocumentDto>(json, Options)
            ?? throw new InvalidDataException("Empty or invalid document.");
        if (dto.Format != "schematic-editor")
            throw new InvalidDataException($"Unknown format '{dto.Format}'.");

        var doc = new SchematicDocument();

        foreach (var s in dto.Symbols ?? new List<SymbolDto>())
        {
            var inst = new SymbolInstance(SymbolLibrary.Get(s.Symbol), new Vec2(s.X, s.Y))
            {
                Rotation = (Rotation)(s.Rotation & 3),
                Mirror = s.Mirror,
                RefDes = s.RefDes ?? "?",
                Value = s.Value ?? "",
                StateOn = s.On,
                Id = s.Id,
            };
            doc.AddElement(inst);
        }

        foreach (var w in dto.Wires ?? new List<WireDto>())
        {
            if (w.Points.Length < 4 || w.Points.Length % 2 != 0) continue;
            var pts = new List<Vec2>(w.Points.Length / 2);
            for (int i = 0; i + 1 < w.Points.Length; i += 2)
                pts.Add(new Vec2(w.Points[i], w.Points[i + 1]));
            doc.AddElement(new Wire(pts) { Id = w.Id });
        }

        return doc;
    }

    public static SchematicDocument LoadFromFile(string path) =>
        Load(File.ReadAllText(path));
}
