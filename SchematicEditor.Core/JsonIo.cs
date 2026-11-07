using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchematicEditor.Core;

/// <summary>A saved scope probe: Type "V" with the point it measures, or "I" with the id of the symbol.</summary>
public sealed record ProbeInfo(string Type, double X = 0, double Y = 0, int SymbolId = 0);

/// <summary>The native file format, .schem.json: plain JSON through source-generated System.Text.Json.</summary>
public static partial class JsonIo
{
    private const string FormatName = "schematic-editor";
    private const int FormatVersion = 1;

    private sealed record SymbolDto(
        int Id, string? Symbol, double X, double Y, int Rotation, bool Mirror, string? RefDes, string? Value, bool On = false);

    private sealed record WireDto(int Id, double[]? Points);

    private sealed record DocumentDto(
        string? Format, int Version, List<SymbolDto>? Symbols, List<WireDto>? Wires, List<ProbeInfo>? Probes = null);

    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonSerializable(typeof(DocumentDto))]
    private sealed partial class Context : JsonSerializerContext
    {
    }

    public static string Save(SchematicDocument doc, IReadOnlyList<ProbeInfo>? probes = null)
    {
        var dto = new DocumentDto(
            FormatName,
            FormatVersion,
            [.. doc.Symbols.Select(s => new SymbolDto(
                s.Id, s.Definition.Name, s.Position.X, s.Position.Y, (int)s.Rotation, s.Mirror, s.RefDes, s.Value, s.StateOn))],
            [.. doc.Wires.Select(w => new WireDto(w.Id, [.. w.Points.SelectMany(p => new[] { p.X, p.Y })]))],
            probes is { Count: > 0 } ? [.. probes] : null);

        return JsonSerializer.Serialize(dto, Context.Default.DocumentDto);
    }

    public static SchematicDocument Load(string json) => Load(json, out _);

    /// <summary>Reads a document and its probes. Throws InvalidDataException or JsonException for a bad file.</summary>
    public static SchematicDocument Load(string json, out List<ProbeInfo> probes)
    {
        DocumentDto dto = JsonSerializer.Deserialize(json, Context.Default.DocumentDto)
            ?? throw new InvalidDataException("The file is empty.");
        if (dto.Format != FormatName)
            throw new InvalidDataException($"Unknown format '{dto.Format}'.");
        if (dto.Version > FormatVersion)
            throw new InvalidDataException($"The file was saved by a newer version of the editor (format version {dto.Version}).");

        var doc = new SchematicDocument();
        foreach (SymbolDto s in dto.Symbols ?? [])
        {
            if (s.Symbol is null || !SymbolLibrary.TryGet(s.Symbol, out SymbolDefinition? definition))
                throw new InvalidDataException($"Unknown symbol '{s.Symbol}'.");

            doc.Add(new SymbolInstance(definition, new Vec2(s.X, s.Y))
            {
                Id = s.Id,
                Rotation = (Rotation)(s.Rotation & 3),
                Mirror = s.Mirror,
                RefDes = s.RefDes ?? "",
                Value = s.Value ?? "",
                StateOn = s.On,
            });
        }

        // A wire needs two points or more, as x, y pairs.
        foreach (WireDto w in dto.Wires ?? [])
        {
            if (w.Points is { Length: >= 4 } p && p.Length % 2 == 0)
                doc.Add(new Wire(Enumerable.Range(0, p.Length / 2).Select(i => new Vec2(p[2 * i], p[2 * i + 1]))) { Id = w.Id });
        }

        probes = dto.Probes ?? [];
        return doc;
    }

    public static SchematicDocument LoadFromFile(string path, out List<ProbeInfo> probes) => Load(File.ReadAllText(path), out probes);
}
