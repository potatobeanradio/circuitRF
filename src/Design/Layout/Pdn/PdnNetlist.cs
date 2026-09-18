// The extraction's RESULT — an ElaboratedNetlist plus the map back to the copper it came from
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-1, docs/design/railrf.md §3).
//
// THE DELIVERABLE OF THE EXTRACTION STEP IS A NETLIST, NOT AN EmProblem. That sentence is §3 of the
// design note and it is the architectural heart of the whole series: everything downstream already
// exists — MnaSystem and CSparse solve it, SParameterEngine sweeps it, DataSet/DataCube carry the
// answer, the Data Display plots it, .npy/MATLAB/Touchstone export it. A second solve path here
// would not announce itself; the DC answer and the AC answer would simply disagree by a few percent
// on boards nobody checked by hand.
//
// NOTHING IN THIS FOLDER BUILDS A MATRIX, FACTORISES ANYTHING, OWNS A RESULT TYPE OR NAMES
// CircuitRF.Engine.Mom (R-rail3-2). A comment-stripped source scan holds it —
// tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs.

using CircuitRF.Core.Elaboration;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>What one element of the extraction CAME FROM.</summary>
public enum PdnOriginKind
{
    /// <summary>One cell edge of the mesh — a square of copper on one conductor.</summary>
    MeshEdge,

    /// <summary>A plated barrel joining two conductors (<see cref="PdnViaModel"/>).</summary>
    Via,

    /// <summary>A source's own series resistance (§4.3, "the R of the R-L").</summary>
    SourceResistance,

    /// <summary>A source's DC voltage branch.</summary>
    SourceBranch,

    /// <summary>A load's current injection.</summary>
    LoadCurrent,

    /// <summary>A load's observation port.</summary>
    Port,

    /// <summary>A series part on the path — the protection FET, the ferrite. §4.3: at DC these are
    /// the largest terms after the source, so they are ELEMENTS, never annotations.</summary>
    SeriesElement,

    /// <summary>A part that bridges the rail to its reference — a decoupling capacitor. Present in
    /// the netlist and contributing no DC path, which is correct and occasionally surprising
    /// (R-rail3-4).</summary>
    Shunt,
}

/// <summary>
/// One cell of the mesh: which conductor it is on, where it sits in the grid, and the DBU coordinate
/// of its centre. This is what makes the drop map of briefs 8 and 15 drawable — a node that cannot
/// name its cell cannot be coloured on a board.
/// </summary>
/// <param name="Layer">The drawing layer the cell's copper is on.</param>
/// <param name="Ix">Column index in the extraction's own grid.</param>
/// <param name="Iy">Row index.</param>
/// <param name="CentreX">Cell centre, DBU.</param>
/// <param name="CentreY">Cell centre, DBU.</param>
/// <param name="IsReference">True on the reference conductor, false on the rail's own copper.</param>
public readonly record struct PdnCellRef(
    LayerKey Layer, int Ix, int Iy, long CentreX, long CentreY, bool IsReference);

/// <summary>
/// What one element of <see cref="PdnNetlist.Netlist"/> came from.
///
/// <para><b>Without this the ranked breakdown of §2.4 is impossible.</b> "This 42 mm run of 0.3 mm
/// inner-layer copper is 38 % of your drop" needs the element to name its trace section, and the
/// drop map needs every node to name its cell. A total with no breakdown is the output rev 3 of the
/// design note exists to replace.</para>
/// </summary>
/// <param name="ComponentIndex">Index into <see cref="ElaboratedNetlist.Components"/>.</param>
/// <param name="Kind">Which of the §4.3 rows produced it.</param>
/// <param name="Description">One readable sentence for a table row.</param>
/// <param name="From">The cell this element leaves, where it has one.</param>
/// <param name="To">The cell it arrives at, where it has one.</param>
/// <param name="Refdes">The part it belongs to, for an attachment; null for copper.</param>
/// <param name="ResistanceOhms">Its DC resistance, where it has one — the number the ranked
/// breakdown sorts on.</param>
/// <param name="LengthMetres">The run this element represents, for the breakdown's own sentence.</param>
/// <param name="WidthMetres">The conductor width it represents, same reason.</param>
public sealed record PdnElementOrigin(
    int ComponentIndex,
    PdnOriginKind Kind,
    string Description,
    PdnCellRef? From,
    PdnCellRef? To,
    string? Refdes,
    double? ResistanceOhms,
    double? LengthMetres = null,
    double? WidthMetres = null);

/// <summary>
/// One observation port of the extraction, and the anchor a user actually typed.
///
/// <para><b>A result names the load a user typed</b>, not a node number — <c>U1.VDD</c>, not
/// <c>node 4,117</c>. A pin FIELD resolves to every matching pad and railRF ties them into one port,
/// because that is what the die sees (§2.2); <see cref="Cells"/> is that set.</para>
/// </summary>
/// <param name="Index">0-based port index, in the order the rail declares its loads.</param>
/// <param name="Name">What the report calls it — the anchor's own spelling.</param>
/// <param name="Anchor">The anchor it resolved from.</param>
/// <param name="PowerNode">The netlist node on the rail's own copper.</param>
/// <param name="ReferenceNode">The netlist node on the reference conductor.</param>
/// <param name="Cells">Every cell tied into this port, power and reference together.</param>
/// <param name="DcCurrentA">The current it draws, or null where this is an observation port and
/// nothing else (§2.2, Q-16 — an unstated current is never a defaulted zero).</param>
public sealed record PdnPortBinding(
    int Index,
    string Name,
    RailPortAnchor Anchor,
    int PowerNode,
    int ReferenceNode,
    IReadOnlyList<PdnCellRef> Cells,
    double? DcCurrentA);

/// <summary>
/// Which model produced this, which reference extent was used, what was defaulted, what was refused
/// (overview §4 rule 1).
///
/// <para><b>Carried into every result and every export.</b> §9 of the design note is a list of risks
/// every one of which is SILENT without it: a fast-model pass reported as a pass, an optimistic
/// reference extent reported as the copper, a defaulted plating thickness reported as a measured
/// one. A result that does not carry this is a result a reader cannot interpret.</para>
/// </summary>
public sealed record PdnProvenance
{
    /// <summary>Which of §2.9's two readings produced this — <c>Accurate</c> here. Brief 4's graph
    /// extractor writes <c>Fast</c> into the same field, which is what makes "a pass in Fast mode is
    /// reported as a FAST-MODEL pass, never as a pass" enforceable rather than remembered.</summary>
    public required string Model { get; init; }

    /// <summary>The rail this netlist is of. The extractor extracts ONE rail (R-rail3-13).</summary>
    public required string RailName { get; init; }

    /// <summary>What the reference conductor was taken to be. Two of the three are OPTIMISTIC and a
    /// reader who does not know which was used cannot tell (R-rail3-5).</summary>
    public required RailReferenceExtent ReferenceExtent { get; init; }

    /// <summary>The frequency this extraction is at. Exactly zero in this brief — briefs 13 and 14
    /// add L and the shunt branch, and DC is the FIRST POINT OF THE SWEEP rather than a mode bolted
    /// on (§2.8).</summary>
    public double FrequencyHz { get; init; }

    /// <summary>The mesh pitch, in METRES, before local refinement.</summary>
    public required double CellSizeMetres { get; init; }

    /// <summary>What set it — the minimum feature width on the rail, or a stated override
    /// (R-rail3-14).</summary>
    public required string CellSizeBasis { get; init; }

    /// <summary>The refinement ratio applied under every port region and every via field
    /// (R-rail3-8). 1 means none was applied.</summary>
    public int PortRefinementRatio { get; init; } = 1;

    /// <summary>The temperature every resistance here was computed at, in °C. There is no thermal
    /// model (§2.7) and this is not one: it is the single stated basis, so a report can say which.</summary>
    public required double CopperTemperatureCelsius { get; init; }

    /// <summary>How many cells the mesh has, power and reference together.</summary>
    public int CellCount { get; init; }

    /// <summary>
    /// The copper those cells account for, in SQUARE METRES.
    ///
    /// <para><b>This is R-rail3-7's whole gate in one number.</b> The mesh follows the copper — a
    /// cutout, an antipad field, a split or a board edge simply removes cells — and the way to know
    /// it did is that the area the cells carry equals the area the artwork draws. A mesh that filled
    /// a cutout in would exceed it; one that dropped a trace would fall short.</para>
    /// </summary>
    public double MeshedAreaSquareMetres { get; init; }

    /// <summary>How the rail's copper partitioned — <see cref="PdnRailRegionSet.IslandReport"/>.
    /// Two islands joined by nothing at DC and by a capacitor at AC is not an error and is not
    /// reported as one; it is two regions, STATED (R-rail3-4).</summary>
    public required string IslandReport { get; init; }

    /// <summary>Which node the whole answer is measured from, and why it is that one.</summary>
    public required string ReferencePoint { get; init; }

    /// <summary>Everything the extraction WORKED OUT that the document did not state — a defaulted
    /// plating thickness, a span it could not resolve, a refinement it applied. Never mixed with
    /// warnings: a note says circuitRF established something, a warning says something may be
    /// wrong.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// The extraction, as the engine consumes it, plus the map back to the copper.
///
/// <para><b>Ordinary <c>ResistorModel</c> / <c>CapacitorModel</c> / <c>PortModel</c> /
/// <c>VdcModel</c> instances on ordinary nodes.</b> No new <c>ComponentModel</c> exists anywhere in
/// this series (overview §1f) — a mesh cell edge is a resistor, a decoupling capacitor is a
/// capacitor, a protection FET's on-resistance at DC is a resistor.</para>
/// </summary>
public sealed class PdnNetlist
{
    /// <summary>The extraction, as the engine consumes it.</summary>
    public required ElaboratedNetlist Netlist { get; init; }

    /// <summary>What each element CAME FROM, in netlist order.</summary>
    public required IReadOnlyList<PdnElementOrigin> Origins { get; init; }

    /// <summary>
    /// Node index → the cell it sits at, for the map overlays (briefs 8 and 15).
    ///
    /// <para><b>A port's pin field is ONE node over several cells</b> (§4.3, "tied together"), so
    /// that node maps to the field's first cell and the whole set is on
    /// <see cref="PdnPortBinding.Cells"/>. Ground is absent: node 0 is the reference point named in
    /// <see cref="PdnProvenance.ReferencePoint"/> and every cell tied to it.</para>
    /// </summary>
    public required IReadOnlyDictionary<int, PdnCellRef> NodeCells { get; init; }

    /// <summary>Port index → the anchor it resolved from, so a result names the load a user typed.</summary>
    public required IReadOnlyList<PdnPortBinding> Ports { get; init; }

    /// <summary>Which model produced this, which reference extent was used, what was defaulted.</summary>
    public required PdnProvenance Provenance { get; init; }
}

/// <summary>
/// Everything one extraction produced. <see cref="Refusal"/> non-null means NOTHING was built — the
/// contract <c>DrillPairingResult</c> and <c>ExcellonReadResult</c> already state.
///
/// <para><b>A refusal names the flag that answers it</b> (overview §4 rule 2): a rail with no
/// reference layer, a reference extent that needs a board outline nobody supplied, an anchor that
/// resolves to no copper.</para>
/// </summary>
/// <param name="Refusal">Why nothing was built, or null.</param>
/// <param name="Netlist">The extraction. Null exactly when <paramref name="Refusal"/> is not.</param>
/// <param name="Regions">The galvanic region walk, which is a first-class OUTPUT rather than a
/// diagnostic — §2.3 step 2 says that alone has caught real problems (R-rail3-4).</param>
/// <param name="Diagnostics">Everything worth saying that did not stop the extraction.</param>
public sealed record PdnExtraction(
    string? Refusal,
    PdnNetlist? Netlist,
    PdnRailRegionSet? Regions,
    IReadOnlyList<string> Diagnostics)
{
    internal static PdnExtraction Refused(string why, PdnRailRegionSet? regions = null) =>
        new(why, null, regions, []);
}
