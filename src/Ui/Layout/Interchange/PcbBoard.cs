// The neutral, still-format-shaped result of reading a board file, before any of it touches editor
// state (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md R-L4d-0). PcbReader fills this in; PcbImport
// is the only thing that turns it into cell folders, layer reconciliation and Messages.
//
// Shapes here already carry real LayoutShape geometry in DBU — the InterchangeStructure contract
// (§8 R15: "one neutral in-memory model", the existing LayoutShape/LayoutInstance types, never a
// parallel object model) — but their LayerKey is not yet resolved, because which key a source layer
// NAME lands on is a reconciliation decision, not a reading one. That is why the layer travels
// alongside each shape as a string, exactly as DxfImportedShape carries a DXF layer name.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One row of the file's own <c>(layers …)</c> table: <c>(0 "F.Cu" signal "top_layer")</c>.</summary>
/// <param name="Ordinal">The file's own layer number. <b>Not stable across format epochs</b> — B.Cu is
/// 31 in files up to the 20221018 epoch and 2 at 20260206, and F.Mask moves 39 → 1. Never hard-code
/// one, and never infer "is this copper" from a range (R-L4d-1).</param>
/// <param name="CanonicalName">The name every entity references. Usually the well-known
/// <c>F.Cu</c>/<c>B.SilkS</c> spelling — but NOT always: in the 20171130 epoch a renamed layer's USER
/// name occupies this slot (a measured file has <c>(0 top_layer signal)</c> and contains no "F.Cu"
/// anywhere), which is the second reason a reader must resolve names through this table.</param>
/// <param name="Type">The table's own type word — <c>signal</c>, <c>power</c>, <c>mixed</c>,
/// <c>jumper</c> for copper; <c>user</c> for everything else. <b>This, not the name and not the
/// ordinal, is what makes a layer copper</b>, and it is what <c>*.Cu</c> expands against.</param>
/// <param name="UserName">The optional user-facing name in the fourth slot, or null.</param>
public sealed record PcbLayerTableEntry(int Ordinal, string CanonicalName, string Type, string? UserName)
{
    /// <summary>Copper, by the table's own type word (see <see cref="Type"/>).</summary>
    public bool IsCopper => Type is "signal" or "power" or "mixed" or "jumper";
}

/// <summary>A shape plus the source layer NAME it was drawn on — <see cref="LayoutShape.Layer"/> is
/// filled in later, by reconciliation.</summary>
/// <param name="LandingLayerName">Vias only (R-L4d-10): the source name of the PAD's own copper layer,
/// resolved to <see cref="ViaShape.LandingLayer"/> once reconciliation has decided the keys. Null for
/// every other shape kind — carried here rather than in a side table so a via and its landing layer
/// cannot be separated by a list copy.</param>
public sealed record PcbImportedShape(LayoutShape Shape, string LayerName, string? LandingLayerName = null);

/// <summary>A pin plus the source layer NAME its copper sits on (R-L4d-17).</summary>
public sealed record PcbImportedPin(LayoutPin Pin, string LayerName);

/// <summary>
/// One footprint DEFINITION — the cell-local geometry, with the placement stripped off.
///
/// <para>Two placements of the same library part yield two <see cref="PcbPlacement"/>s pointing at one
/// of these (R-L4d-15). <see cref="ContentKey"/> is what makes that true without comparing geometry
/// pairwise.</para>
/// </summary>
public sealed class PcbFootprintCell
{
    /// <summary>The library part name as the file states it (<c>"Resistor_SMD:R_0603"</c>).</summary>
    public string LibraryName { get; set; } = "";

    /// <summary>Cell-local geometry, origin at the footprint's own origin, Y already flipped up.</summary>
    public List<PcbImportedShape> Shapes { get; } = [];

    /// <summary>Cell-local pins, one per pad (R-L4d-17), each with the SOURCE name of its copper
    /// layer — resolved to a <see cref="LayerKey"/> only after reconciliation has decided the keys.</summary>
    public List<PcbImportedPin> Pins { get; } = [];

    /// <summary>Content address (R-L4d-15) — every placement whose local geometry hashes here shares
    /// one cell.</summary>
    public string ContentKey { get; set; } = "";
}

/// <summary>One placement of a <see cref="PcbFootprintCell"/>.</summary>
/// <param name="ContentKey">Which cell this places.</param>
/// <param name="X">Board X in DBU.</param>
/// <param name="Y">Board Y in DBU, already flipped up.</param>
/// <param name="RotationDegrees">CCW in the Y-up frame — the source angle unchanged (see
/// <see cref="PcbUnits.Angle"/>).</param>
/// <param name="Reference">The part's reference designator, when it declares one.</param>
public sealed record PcbPlacement(string ContentKey, long X, long Y, double RotationDegrees, string? Reference);

/// <summary>What a stackup entry says, before it becomes a <see cref="StackupLayer"/>.</summary>
/// <param name="Name">The entry's own name — a copper layer's canonical name, or <c>dielectric 1</c>.</param>
/// <param name="Type">The quoted type word: <c>copper</c>, <c>core</c>, <c>prepreg</c>, or one of the
/// non-electrical ones (<c>"Top Solder Mask"</c>, <c>"Top Silk Screen"</c>) that map to nothing.</param>
/// <param name="ThicknessMm">Null when the entry states none (a paste layer usually does not).</param>
/// <param name="EpsilonR">Null unless stated.</param>
/// <param name="LossTangent">Null unless stated.</param>
public sealed record PcbStackupEntry(string Name, string Type, double? ThicknessMm, double? EpsilonR, double? LossTangent);

/// <summary>Everything <c>PcbReader</c> recovered, and everything it could not.</summary>
public sealed class PcbBoard
{
    /// <summary>The format's own version stamp — reported, never branched on (R-L4d-1).</summary>
    public string? Version { get; set; }

    public List<PcbLayerTableEntry> LayerTable { get; } = [];

    /// <summary>Null when the file carries no <c>(setup (stackup …))</c> at all — which is NOT an
    /// error and must never be papered over with an invented substrate (R-L4d-6).</summary>
    public List<PcbStackupEntry>? Stackup { get; set; }

    /// <summary>The board's overall thickness in mm from <c>(general (thickness …))</c>, when stated —
    /// the ONLY substrate fact a file without a stackup section carries, and worth naming in the
    /// message that says the stackup is empty.</summary>
    public double? OverallThicknessMm { get; set; }

    /// <summary>Board-level geometry, already in board coordinates.</summary>
    public List<PcbImportedShape> Shapes { get; } = [];

    /// <summary>Distinct footprint definitions, keyed by <see cref="PcbFootprintCell.ContentKey"/>.</summary>
    public Dictionary<string, PcbFootprintCell> FootprintCells { get; } = [];

    public List<PcbPlacement> Placements { get; } = [];

    // ── What was read, and what was not — every one of these is REPORTED, never silent (§2) ────────

    /// <summary>Tokens the reader does not understand, by name with a count — reported ONCE each
    /// (gate 14), never per occurrence.</summary>
    public Dictionary<string, int> UnknownTokenCounts { get; } = [];

    /// <summary>Entities deliberately NOT imported, by type with a count: images, dimensions, groups,
    /// keepout zones, unfilled zones, mask apertures.</summary>
    public Dictionary<string, int> SkippedCounts { get; } = [];

    /// <summary>
    /// Entities that WERE imported but not at full fidelity, by kind with a count — a blind via placed
    /// on one layer, an oval drill rounded, an outline whose fill flag the file never stated.
    ///
    /// <para>Deliberately separate from <see cref="SkippedCounts"/>. Reporting both under one heading
    /// forces one sentence to cover both cases, and the sentence that fits "not imported" says
    /// something FALSE about the ones that were — which is the kind of small dishonesty a user builds a
    /// wrong mental model of the import on.</para>
    /// </summary>
    public Dictionary<string, int> DegradedCounts { get; } = [];

    public List<string> Diagnostics { get; } = [];

    /// <summary>Counters, never wall clock (R-L4d-21).</summary>
    public int EntitiesRead { get; set; }
    public int ShapesProduced { get; set; }
}
