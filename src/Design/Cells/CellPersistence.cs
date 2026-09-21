using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Cells;

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
/// One row of the cell's TERMINAL MAP: which layout pin is which schematic port
/// (<c>docs/design/lvs.md</c> §4.2, R-lvs-9).
///
/// <para><b>Why a cell has to say this at all.</b> The schematic orders a cell's ports by the
/// <c>Num</c> parameter on its <c>Port</c> components and the layout appends its pins in whatever
/// order they were drawn; nothing relates the two, and <see cref="LayoutPin"/> is explicitly allowed
/// to be empty. Position is not a correspondence either — a symbol's pin positions and a land
/// pattern's pad positions have no reason to agree. Where the cell does not say, <c>TerminalMap</c>
/// derives an answer and states which rule produced it.</para>
/// </summary>
public sealed class CcellTerminal
{
    /// <summary>1-based, and it is the number BOTH sides already use — <c>SymbolPin.PortIndex</c> and
    /// the <c>Port Num=</c> parameter. It is not re-derived here and not renumbered (R-lvs1-1b).</summary>
    public int Port { get; set; }

    /// <summary>The terminal's own name, for reports. It may differ from <see cref="LayoutPin"/> and
    /// usually does not. Empty is legal (R-lvs1-1d).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Entries in the PRIMARY <c>.clay</c>'s <c>Pins</c>, by name. Usually one; SEVERAL where several
    /// pins are one terminal — a bonded ground, a FET's two source pads. That is not new semantics:
    /// it is the <c>GND@1</c>/<c>GND@2</c> case <c>ComponentTerminals</c> already understands.
    ///
    /// <para>Spelled in JSON as a bare string when there is one and as an array when there are
    /// several, which is what <see cref="CcellLayoutPinConverter"/> is for.</para>
    /// </summary>
    [JsonConverter(typeof(CcellLayoutPinConverter))]
    public List<string> LayoutPin { get; set; } = [];

    public CcellTerminal Clone() => new()
    {
        Port      = Port,
        Name      = Name,
        LayoutPin = [.. LayoutPin],
    };
}

/// <summary>
/// Reads <c>"G"</c> and <c>["S1", "S2"]</c> alike into <see cref="CcellTerminal.LayoutPin"/>, and
/// writes the one-element case back as a bare string so the ordinary row round-trips byte for byte.
///
/// <para><b>It never throws.</b> R-lvs1-1f: an unreadable terminal block makes the cell's map
/// ABSENT, which is a defined state that derives — it must not take the rest of the <c>.ccell</c>
/// (the parameter interface, the primary views) down with it.</para>
/// </summary>
internal sealed class CcellLayoutPinConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return [reader.GetString() ?? ""];

            case JsonTokenType.StartArray:
                var names = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (reader.TokenType == JsonTokenType.String) names.Add(reader.GetString() ?? "");
                    else reader.Skip();
                return names;

            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }

        writer.WriteStartArray();
        foreach (var name in value) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
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
    /// Where this cell came from, when an importer built it rather than a person drawing it.
    /// <c>WhenWritingNull</c>, so every hand-drawn cell's <c>.ccell</c> stays byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CcellImportProvenance? ImportedFrom { get; set; }

    /// <summary>
    /// Which layout pin is which schematic port (<c>brief-lvs-1-terminal-map.md</c> R-lvs1-1). Null —
    /// the state every cell written before this field existed is in — means the cell does not say, and
    /// <c>TerminalMap.Resolve</c> derives an answer with its provenance stated. <c>WhenWritingNull</c>,
    /// so every existing <c>.ccell</c> re-serializes byte for byte.
    ///
    /// <para>An EMPTY list is deliberately distinct from null: it is what
    /// <see cref="CellFolder.CreateCellFolder"/> writes, and it means <i>this cell was created by
    /// circuitRF and has no terminals yet</i> — a brand-new cell with no views legitimately is that.
    /// It still derives, because a cell that has since been drawn has terminals the empty list does
    /// not know about (R-lvs1-5c).</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CcellTerminal>? Terminals { get; set; }

    /// <summary>
    /// Number of electrical ports this cell exposes to instantiating parents.
    /// Default 0 so existing alpha .ccell files (which omit this field) load cleanly.
    /// The primary symbol's ExternalPortCount is fed from this value, not the other way around.
    /// </summary>
    public int NumPorts { get; set; }
}

/// <summary>
/// What an import wrote a cell FROM — recorded so a second import of the same file can tell "this
/// is the cell I already wrote" from "this is a different cell that happens to share a name".
///
/// <para><b>Without it that question has no answer.</b> A library file routinely holds two variants
/// of a part over one shared core, so importing the second one hits a folder the first one wrote and
/// there is nothing on disk to say whether the two agree. Never overwriting is the right rule, so
/// the only way for the second import to succeed is to PROVE the existing cell is the same thing.
/// </para>
/// </summary>
public sealed class CcellImportProvenance
{
    /// <summary>
    /// The file this was read from, by NAME only — not a path.
    ///
    /// <para>A <c>.ccell</c> travels: into an archive, onto a colleague's machine, into a repository.
    /// The sender's absolute path means nothing at any of those destinations and is exactly the kind
    /// of thing that should not leave the machine it was typed on. The name is what a reader needs in
    /// order to recognise the file, which is all this field is for — nothing resolves through it.</para>
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>The definition inside that file — a <c>.subckt</c> name, verbatim.</summary>
    public string Definition { get; set; } = "";

    /// <summary>
    /// Hex SHA-256 over exactly what the import wrote — the schematic, the symbol, and the declared
    /// parameter list. Compared two ways when a folder already exists: against what a fresh import
    /// WOULD write (same definition?) and against what is on disk now (edited since?).
    /// </summary>
    public string ContentHash { get; set; } = "";
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
