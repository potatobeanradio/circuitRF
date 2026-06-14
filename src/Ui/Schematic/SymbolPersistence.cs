using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  .csym file format — rev 1 (alpha, no back-compat per policy).
//  Mirrors SchematicPersistence.cs conventions:
//    - System.Text.Json, enum-as-string
//    - References not payloads (Bitmap stores file path)
//    - Nullable / defaulted fields for graceful within-version loads
//    - format_version: reject on mismatch (alpha policy)
//    - Id never persisted
// ──────────────────────────────────────────────────────────────────────────────

// ── JSON file model ───────────────────────────────────────────────────────────

public sealed class CsymFile
{
    public int    FormatVersion { get; set; } = 1;
    /// <summary>
    /// Grid pitch P_src (100 units per grid square by default) — the authoring grid
    /// this symbol was drawn on.  Written for the future cross-grid paste check;
    /// not acted on in this version.
    /// </summary>
    public double GridSize     { get; set; } = 100.0;

    /// <summary>
    /// Number of ports this symbol can map pins to. Defaults to the pin count
    /// when 0 (backward-compat with files written before 4c).
    /// </summary>
    public int PortCount { get; set; }

    /// <summary>Ordered primitive list; each entry carries a "$type" discriminator.</summary>
    public List<SymbolPrimitive> Primitives { get; set; } = [];

    /// <summary>Pin definitions: local coord + port mapping + optional name.</summary>
    public List<CsymPin> Pins { get; set; } = [];
}

public sealed class CsymPin
{
    // Id is NOT persisted — runtime identity only (no persistent id on pins).
    public double  LocalX    { get; set; }
    public double  LocalY    { get; set; }
    public int     PortIndex { get; set; }
    public string? Name      { get; set; }
}

// ── Serializer ────────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes .csym files. Framework-free (no Avalonia / Skia).
/// Mirrors SchematicPersistence: references not payloads, enum-as-string,
/// format_version reject-on-mismatch.
/// </summary>
public static class SymbolPersistence
{
    public const int CurrentFormatVersion = 6;   // was 5: TextPrimitive VAlign/Rotation/ForceReadable

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(Symbol symbol, double gridSize = 100.0)
    {
        var file = ToFileModel(symbol, gridSize);
        return JsonSerializer.Serialize(file, _jsonOpts);
    }

    public static void SaveToFile(string path, Symbol symbol, double gridSize = 100.0)
        => AtomicFile.WriteAllText(path, Serialize(symbol, gridSize));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static Symbol Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<CsymFile>(json, _jsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .csym file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".csym format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");
        // Versions < CurrentFormatVersion are accepted; fields added in later versions
        // default gracefully (e.g. PortCount defaults to 0 → falls back to pin count).

        return FromFileModel(file);
    }

    public static Symbol LoadFromFile(string path)
        => Deserialize(File.ReadAllText(path));

    // ── Convert Symbol ↔ CsymFile ─────────────────────────────────────────────

    private static CsymFile ToFileModel(Symbol sym, double gridSize)
    {
        var file = new CsymFile
        {
            FormatVersion = CurrentFormatVersion,
            GridSize      = gridSize,
            PortCount     = sym.PortCount,
        };

        // Primitives: stored directly (polymorphic via [JsonPolymorphic] on SymbolPrimitive).
        file.Primitives.AddRange(sym.Primitives);

        // Pins
        foreach (var pin in sym.Pins)
            file.Pins.Add(new CsymPin
            {
                LocalX    = pin.LocalX,
                LocalY    = pin.LocalY,
                PortIndex = pin.PortIndex,
                Name      = pin.Name,
            });

        return file;
    }

    private static Symbol FromFileModel(CsymFile file)
    {
        var pins = file.Pins
            .Select(p => new SymbolPin(p.LocalX, p.LocalY, p.PortIndex, p.Name))
            .ToList();
        return new Symbol(file.Primitives, pins, file.PortCount);
    }
}
