using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  .ccell file format — rev 1 (alpha, no back-compat per policy).
//  Mirrors SymbolPersistence.cs conventions:
//    - System.Text.Json, enum-as-string (JsonStringEnumConverter)
//    - WhenWritingNull on optional fields
//    - format_version: reject on mismatch (alpha policy)
//    - Id never persisted
// ──────────────────────────────────────────────────────────────────────────────

// ── JSON file model ───────────────────────────────────────────────────────────

/// <summary>
/// One parameter in the cell's declared interface.  Mirrors EditableParameter's
/// persisted shape but holds the <em>default</em> expression — it is the cell's
/// interface declaration, not an instance override value.
/// </summary>
public sealed class CcellParameter
{
    public string        Name              { get; set; } = "";
    public string        DefaultExpression { get; set; } = "";
    public string        Unit              { get; set; } = "";
    public UnitDimension Dimension         { get; set; } = UnitDimension.None;
    public bool          ShowOnSchematic   { get; set; } = true;

    public CcellParameter Clone() => new()
    {
        Name              = Name,
        DefaultExpression = DefaultExpression,
        Unit              = Unit,
        Dimension         = Dimension,
        ShowOnSchematic   = ShowOnSchematic,
    };
}

/// <summary>
/// On-disk model for a .ccell file.  Records the cell's parameter interface and
/// which view file (relative filename) is primary for each view type.
/// Id is never persisted; the cell folder name is the identity.
/// </summary>
public sealed class CcellFile
{
    public int FormatVersion { get; set; } = 1;

    public List<CcellParameter> Parameters { get; set; } = [];

    /// <summary>
    /// Filename relative to the schematic/ sub-folder that is the primary schematic
    /// (e.g. "amp.csch").  Null = none chosen yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimarySchematic { get; set; }

    /// <summary>
    /// Filename relative to the symbol/ sub-folder that is the primary symbol
    /// (e.g. "amp.csym").  Null = none chosen yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimarySymbol { get; set; }

    /// <summary>
    /// Filename relative to the layout/ sub-folder that is the primary layout
    /// (e.g. "amp.clay").  Null = none chosen yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryLayout { get; set; }

    /// <summary>True when this cell's schematic carries analyses and measurements.</summary>
    public bool IsTestBench { get; set; }

    /// <summary>
    /// Number of electrical ports this cell exposes to instantiating parents.
    /// Default 0 so existing alpha .ccell files (which omit this field) load cleanly.
    /// The primary symbol's ExternalPortCount is fed from this value, not the other way around.
    /// </summary>
    public int NumPorts { get; set; }
}

// ── Serializer ────────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes .ccell files.  Framework-free (no Avalonia / Skia).
/// Mirrors SymbolPersistence: enum-as-string, WhenWritingNull,
/// format_version reject-on-mismatch.
/// </summary>
public static class CellPersistence
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(CcellFile cell)
        => JsonSerializer.Serialize(cell, _jsonOpts);

    public static void SaveToFile(string path, CcellFile cell)
        => File.WriteAllText(path, Serialize(cell));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static CcellFile Deserialize(string json)
    {
        var cell = JsonSerializer.Deserialize<CcellFile>(json, _jsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .ccell file.");

        if (cell.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $".ccell format_version {cell.FormatVersion} does not match " +
                $"expected {CurrentFormatVersion}. Regenerate the file.");

        return cell;
    }

    public static CcellFile LoadFromFile(string path)
        => Deserialize(File.ReadAllText(path));
}
