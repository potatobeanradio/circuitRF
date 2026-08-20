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

    /// <summary>
    /// A closed set of values this parameter accepts, or null for an ordinary free-text one. Present
    /// when the parameter selects WHICH model the cell is built from rather than supplying a value
    /// to one — the Parameter Editor then offers a picker instead of a text box.
    ///
    /// <para>Null (not an empty list) is the ordinary case, so every existing <c>.ccell</c> stays
    /// byte-identical.</para>
    /// </summary>
    public List<string>? Choices { get; set; }

    /// <summary>
    /// Choices the cell declares but circuitRF cannot build. Deliberately still offered by the
    /// picker: a user who picks one is told it is not implemented at Run, which is information —
    /// leaving it out of the list would only look like the kit was missing something.
    /// </summary>
    public List<string>? UnsupportedChoices { get; set; }

    /// <summary>
    /// True when this parameter names a FILE — a model library, a data table. The Parameter Editor
    /// then offers a Browse… picker rather than expecting a path to be typed, and lists it first,
    /// because which file a part is modelled from is the thing a user settles before anything else
    /// about it. Null (not false) for an ordinary parameter, so every existing <c>.ccell</c> stays
    /// byte-identical.
    /// </summary>
    public bool? IsFilePath { get; set; }

    /// <summary>
    /// The kit's own one-line description of this parameter, shown as the field's tooltip. Worth
    /// carrying: it is the sentence the kit's documentation uses, so a user can search for it.
    /// </summary>
    public string? Description { get; set; }

    public CcellParameter Clone() => new()
    {
        Name               = Name,
        DefaultExpression  = DefaultExpression,
        Unit               = Unit,
        Dimension          = Dimension,
        ShowOnSchematic    = ShowOnSchematic,
        Choices            = Choices is null ? null : [.. Choices],
        UnsupportedChoices = UnsupportedChoices is null ? null : [.. UnsupportedChoices],
        IsFilePath         = IsFilePath,
        Description        = Description,
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
    /// Names the registered external device provider that supplies this cell's behaviour, when the
    /// cell is a LEAF backed by a provider rather than a hierarchy of its own.
    ///
    /// <para>Such a cell has a symbol but deliberately no schematic: extraction emits it as a single
    /// external-device instance instead of descending into it. Null — the overwhelmingly common
    /// case — means an ordinary hierarchical cell, and both fields are omitted from the file, so
    /// every existing <c>.ccell</c> is byte-identical.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalProvider { get; set; }

    /// <summary>Device type within <see cref="ExternalProvider"/>. Meaningless without it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalType { get; set; }

    /// <summary>
    /// Absolute path to the kit's own palette icon for this part, when it shipped one. Recorded so
    /// reopening a workspace can restore the tile without re-importing the kit.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalIconPath { get; set; }

    /// <summary>
    /// Parameters emitted with every instance but never offered for editing — the kit's own
    /// infrastructure, such as where its model data lives. They are not design quantities: changing
    /// one per-instance would point that instance at data the kit does not have. Emitted verbatim so
    /// the provider still receives them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ExternalFixedParameters { get; set; }

    /// <summary>
    /// A netlist holding this cell's definition, when the cell is a CIRCUIT rather than a single
    /// device — a package, a matching network, an assembly. Absolute, because the file stays with the
    /// kit it came from while the cell is installed into the workspace.
    ///
    /// <para>Takes precedence over <see cref="ExternalProvider"/>: a part with a circuit definition
    /// is not a leaf, whatever else the kit says about it.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalNetlistPath { get; set; }

    /// <summary>
    /// Which subcircuit in <see cref="ExternalNetlistPath"/> defines this cell. May name a parameter
    /// in braces — <c>Part_{ModelAs}</c> — which is replaced by the instance's own value, so one
    /// placed part can resolve to one of several formulations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalNetlistCell { get; set; }

    /// <summary>
    /// The <c>Match</c> design this cell was flattened from — base64 of its JSON, exactly the blob a
    /// <c>Match</c> component carries (match.md §11.1). Present only on a cell that <b>Flatten to
    /// Cell</b> wrote; omitted from every other <c>.ccell</c>, which therefore stays byte-identical.
    ///
    /// <para><b>Deliberately NOT a <see cref="CcellParameter"/>.</b> A declared parameter is seeded
    /// onto every placed instance as an override, and an instance override is <i>eagerly evaluated
    /// as an expression</i> at elaboration — a base64 blob is not one, so every placement of a
    /// flattened cell would refuse to elaborate. This is cell metadata, like
    /// <see cref="ExternalNetlistPath"/> beside it, and nothing in the cell's own netlist reads
    /// it.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchDesign { get; set; }

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
        => AtomicFile.WriteAllText(path, Serialize(cell));

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
