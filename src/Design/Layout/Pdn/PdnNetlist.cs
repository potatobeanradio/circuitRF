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
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>What one element of the extraction CAME FROM.</summary>
public enum PdnOriginKind
{
    /// <summary>One cell edge of the mesh — a square of copper on one conductor. Also one edge of
    /// the fast model's COARSE mesh over a region it classified as spreading.</summary>
    MeshEdge,

    /// <summary>
    /// One trace section of the fast model's graph reading — <c>R = ρ·L/(W·T)</c> between two
    /// junctions, with L and W integrated ALONG the section rather than sampled at a point
    /// (R-rail4-1, R-rail4-6).
    ///
    /// <para>Distinct from <see cref="MeshEdge"/> on purpose: a ranked breakdown that could not tell
    /// a closed-form section from a meshed cell could not tell a reader which rows of it carry the
    /// fast model's own assumption.</para>
    /// </summary>
    TraceSection,

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

    /// <summary>
    /// §4.1's shunt branch — one cell's own <c>C = ε₀εᵣA/h</c> to the reference plane, or the
    /// <c>G = ωC·tan δ</c> across it (R-rail14-2).
    ///
    /// <para><b>Distinct from <see cref="Shunt"/> on purpose, and it is not a fussy distinction.</b>
    /// A <see cref="Shunt"/> is a PART: it has a refdes, a user can take it off the board, and
    /// §2.4's removal ranking is about exactly that set. The plane pair's own capacitance is the
    /// BOARD, it has no refdes, and a ranking that offered to remove it would be offering to remove
    /// the stackup.</para>
    /// </summary>
    PlaneShunt,
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
/// <param name="InductanceHenries">Its series inductance, where it has one — §4.1's
/// <c>L = µ₀·h</c> per square on a mesh edge, and the same law over a section's own square count on
/// a trace (R-rail13-1, R-rail13-7). <b>Null at DC, and that is not the same as zero</b>: at ω = 0
/// §4.1's inductance vanishes and the element IS a resistor, so a null here says the extraction was
/// at DC while a zero would say the copper has no inductance.</param>
/// <param name="LengthMetres">The run this element represents, for the breakdown's own sentence.</param>
/// <param name="WidthMetres">The conductor width it represents, same reason.</param>
/// <param name="Barrel">The plated barrel this element IS, on a <see cref="PdnOriginKind.Via"/> and
/// null on everything else. <b>Structured rather than only in <paramref name="Description"/></b>,
/// because brief 6's via check computes a current limit from the drill, the plating, the plating's
/// own basis and the span — and a check that had to parse those back out of a sentence would be a
/// second reading of the geometry that could disagree with the first one.</param>
/// <param name="At">Where the thing this element IS actually sits on the board, where the question
/// has an answer — the centroid of the pads its anchor named. <b>Not the same as
/// <paramref name="From"/></b>, and that difference is a reported defect: a source or load anchor
/// resolves to a set of cells that are TIED into one node, and the cell a tied node reports is the
/// union-find representative — an arbitrary member of the set, which on a via-stitched rail can be
/// on a different LAYER from the pad. A marker drawn there moves when nothing about the design has
/// (owner, 2026-09-19: the example board's U2 source appeared inside the IN3 pour on one reading
/// and above it on another). The anchor's own pads do not move, so that is what is carried.</param>
public sealed record PdnElementOrigin(
    int ComponentIndex,
    PdnOriginKind Kind,
    string Description,
    PdnCellRef? From,
    PdnCellRef? To,
    string? Refdes,
    double? ResistanceOhms,
    double? LengthMetres = null,
    double? WidthMetres = null,
    PdnViaBarrel? Barrel = null,
    double? InductanceHenries = null,
    (long X, long Y)? At = null);

/// <summary>
/// Why <see cref="PdnProvenance.PlaneCapacitanceFarads"/> is what it is (R-rail18-2b).
/// </summary>
public enum PdnPlaneCapacitanceBasis
{
    /// <summary>The extraction computed it — from the mesh's per-cell overlap, or from the graph
    /// model's polygon intersection. A zero here means the two conductors genuinely do not
    /// overlap.</summary>
    Computed,

    /// <summary>The stackup puts no dielectric between this rail's copper and its reference, so
    /// <c>ε₀εᵣA/h</c> has no <c>h</c>. <b>The one case the stackup sentence was written for</b>, and
    /// the only one it may be printed for.</summary>
    NoDielectricStated,

    /// <summary>This model does not compute it at all. Says so, naming itself — never the
    /// user's stackup.</summary>
    NotComputed,
}

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
/// <param name="At">Where this port sits on the board — the centroid of the pads its anchor named,
/// or null where the anchor resolved to none. <b>Read in preference to <paramref name="Cells"/> by
/// anything that draws a mark</b>, for the reason <c>PdnElementOrigin.At</c> states.</param>
public sealed record PdnPortBinding(
    int Index,
    string Name,
    RailPortAnchor Anchor,
    int PowerNode,
    int ReferenceNode,
    IReadOnlyList<PdnCellRef> Cells,
    double? DcCurrentA,
    (long X, long Y)? At = null);

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
    /// <summary>
    /// Which of §2.9's two readings produced this — <b>required, and that is R-rail4-2</b>.
    ///
    /// <para>"A pass in Fast mode is reported as a FAST-MODEL pass, never as a pass." That is only
    /// enforceable if a result cannot be built without saying which model made it: a result object
    /// that can be constructed without a <see cref="PdnModelKind"/> is a result that can reach a user
    /// without one. It is carried onto the plot, onto each table, into the status strip and into the
    /// provenance of every export.</para>
    /// </summary>
    public required PdnModelKind ModelKind { get; init; }

    /// <summary>The same fact as a sentence a report prints — "Accurate (mesh)", "Fast (graph)".
    /// <see cref="ModelKind"/> is what code branches on; this is what a reader reads.</summary>
    public required string Model { get; init; }

    /// <summary>The rail this netlist is of. The extractor extracts ONE rail (R-rail3-13).</summary>
    public required string RailName { get; init; }

    /// <summary>What the reference conductor was taken to be. Two of the three are OPTIMISTIC and a
    /// reader who does not know which was used cannot tell (R-rail3-5).</summary>
    public required RailReferenceExtent ReferenceExtent { get; init; }

    /// <summary>The frequency this extraction is at. Zero is DC, which is the FIRST POINT OF THE
    /// SWEEP rather than a mode bolted on (§2.8): at ω = 0 §4.1's inductance vanishes and every
    /// copper element is a plain resistor. Above it each carries <c>R + jωL</c> (R-rail13-1).
    /// The shunt branch is brief 14 and is absent at every frequency here.</summary>
    public double FrequencyHz { get; init; }

    /// <summary>
    /// §4.1's <c>h</c> — the dielectric separation between the rail's plane and its reference, in
    /// METRES, which is what set every cell edge's <c>L = µ₀·h</c>. Zero where the stackup could
    /// not give one, in which case the extraction carries no inductance and says so in a note.
    ///
    /// <para><b>On the provenance because it is the number §4.3 says the form factor moves.</b> A
    /// four-layer pair at 100 µm and a two-layer board at 1.5 mm differ by fifteen times here and by
    /// fifteen times in every inductance derived from it, and a reader comparing two extractions
    /// needs to see which stackup each was of.</para>
    /// </summary>
    public double PlaneSeparationMetres { get; init; }

    /// <summary>
    /// R-rail13-2, as a STATED CONDITION rather than an implicit assumption: the lowest frequency at
    /// which any conductor in this extraction reaches two skin depths, in hertz, or
    /// <see cref="double.PositiveInfinity"/> where none does.
    ///
    /// <para>Below it the R matrix does not depend on frequency at all and only L and the solve
    /// change per point — 57 MHz at 0.5 oz, 14 MHz at 1 oz, 3.6 MHz at 2 oz, so on the thin inner
    /// copper these boards use ONE R MATRIX SERVES AN UNUSUALLY WIDE BAND (§2.8). Reported so that
    /// the point at which R starts varying is visible, because a caller reusing a factorisation
    /// across a sweep has to know where the reuse stops being valid.</para>
    /// </summary>
    public double SkinCrossoverHz { get; init; } = double.PositiveInfinity;

    /// <summary>
    /// <b>R-rail14-3 — the extracted plane capacitance, in FARADS, and §9 says it must not be
    /// buried.</b>
    ///
    /// <para>"The stackup is usually wrong. Designers copy a stackup from the last board. … railRF
    /// shows the extracted plane capacitance as A SINGLE NUMBER EARLY AND PROMINENTLY, because a
    /// designer recognises a wrong one instantly and would never notice it buried in a curve." It is
    /// <c>ε₀εᵣA/h</c> over the real OVERLAP of the two conductors — see
    /// <see cref="PlaneOverlapSquareMetres"/> — so one glance at it checks the permittivity, the
    /// area and the dielectric thickness at once, which is what makes it the cheapest real gate in
    /// the whole tool.</para>
    ///
    /// <para><b>Present on a DC extraction too</b>, where nothing was stamped from it: at ω = 0
    /// §4.1's shunt branch vanishes (<see cref="ShuntBranchPresent"/> says so) but the stackup is
    /// exactly as worth checking. Zero where the stackup gives no dielectric between the pair, which
    /// is stated in <see cref="Notes"/> rather than defaulted.</para>
    /// </summary>
    public double PlaneCapacitanceFarads { get; init; }

    /// <summary>
    /// <b>Where <see cref="PlaneCapacitanceFarads"/>' zero came from</b> (R-rail18-2b), in
    /// <see cref="PdnPlatingBasis"/>' style: a defaulted number and an uncomputed one are not the
    /// same state, and a report that cannot tell them apart says the wrong thing about whichever it
    /// guesses.
    /// </summary>
    /// <remarks>
    /// The sentence <see cref="PdnCavity"/>'s absent medium was written for — <i>the stackup states
    /// no dielectric between this rail's copper and its reference</i> — is a statement about the
    /// USER'S STACKUP. Printing it for a model that simply never computed the number tells every
    /// user of that model their stackup is wrong, which is what the whole check exists to prevent
    /// somebody else doing. So the two are distinguished here rather than inferred from a zero.
    /// </remarks>
    public PdnPlaneCapacitanceBasis PlaneCapacitanceBasis { get; init; } =
        PdnPlaneCapacitanceBasis.Computed;

    /// <summary>
    /// The area <see cref="PlaneCapacitanceFarads"/> is over, in SQUARE METRES — <b>the OVERLAP of
    /// the rail's copper and its reference, never either one's outline</b> (R-rail14-3).
    ///
    /// <para>On a board with a cutout, an antipad field or a split the overlap is smaller than
    /// either plane, and reading the outline instead over-states the capacitance by exactly the
    /// fraction that is not plane pair. Carried beside the capacitance because the two together are
    /// what let a reader check <c>h</c>: <c>h = ε₀εᵣA/C</c>.</para>
    /// </summary>
    public double PlaneOverlapSquareMetres { get; init; }

    /// <summary>The relative permittivity the shunt branch was computed with — the SERIES-effective
    /// one where the pair is separated by more than one dielectric entry
    /// (<see cref="PdnCavity.MediumBetween"/>). Zero where the stackup gave none.</summary>
    public double RelativePermittivity { get; init; }

    /// <summary>
    /// The loss tangent <c>G = ωC·tan δ</c> was computed with. <b>§2.2 names it as one of the two
    /// stackup numbers most often wrong</b> — "it sets how sharp the cavity resonances are, which is
    /// the difference between a 6 dB bump and a 20 dB one" — which is why it is reported rather than
    /// merely used.
    /// </summary>
    public double LossTangent { get; init; }

    /// <summary>True where the stackup stated no tan δ and brief 11's per-class figure supplied one
    /// (R-rail11-5). <b>Every peak height in the cavity band is then indicative</b>, in exactly the
    /// sense an ESR from a class default is.</summary>
    public bool LossTangentIsClassDefault { get; init; }

    /// <summary>The sentence naming which dielectric entries produced
    /// <see cref="RelativePermittivity"/> and <see cref="LossTangent"/>, and who stated what.</summary>
    public string DielectricBasis { get; init; } = "";

    /// <summary>
    /// Whether §4.1's shunt branch is actually IN this netlist.
    ///
    /// <para><b>Not the same question as "is <see cref="PlaneCapacitanceFarads"/> non-zero".</b> A DC
    /// extraction carries the capacitance as a readout and stamps nothing from it, and a reader
    /// comparing two curves has to be able to tell a run that modelled the cavity from one that
    /// only measured its stackup.</para>
    /// </summary>
    public bool ShuntBranchPresent { get; init; }

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

    /// <summary>
    /// R-rail31-1 — which NET the return was taken to be, and where that came from: named in the
    /// document, measured from the copper on the reference layer, or neither.
    /// </summary>
    /// <remarks>
    /// <b>On the provenance because it decides which copper is the return</b>, and so which copper
    /// is NOT the rail. A reader comparing two results needs to see whether both were solved against
    /// the same return, and "measured" and "named" can disagree on a board with two returns.
    /// </remarks>
    public PdnReturnNet ReturnNet { get; init; }

    /// <summary>
    /// How many holes carry no barrel because their layer span could not be resolved (R-rail3-10).
    ///
    /// <para><b>A COUNT rather than a diagnostic sentence, because brief 6 has to act on it.</b>
    /// R-rail6-5: a transition whose span is unresolved gets no flag and a NOTE — a limit computed
    /// from an assumed 1.6 mm span is a number with no basis at all. The via check reads the result,
    /// and the extraction's diagnostics do not travel on the result, so the fact does.</para>
    /// </summary>
    public int UnresolvedViaSpans { get; init; }

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
    /// <remarks>Set after construction in one place: <c>RailDcRun</c> writes the MEASURED
    /// discretisation error onto an Accurate result once it has solved the second mesh (R-rail32-2)
    /// — a fact about the answer that the extraction, which never solves, cannot know.</remarks>
    public PdnProvenance Provenance { get; internal set; } = null!;
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
    /// <summary>
    /// Which copper the fast model treated as a trace and which it meshed, with the reason for each
    /// (R-rail4-3). Empty from <see cref="PdnMeshExtractor"/>, which meshes everything and so
    /// classifies nothing.
    ///
    /// <para><b>Carried on a REFUSAL as well as on a result</b>, because the pour-dominated refusal
    /// of R-rail4-5 is a statement about the classification and a user who cannot see the
    /// classification cannot act on it — the answer to that refusal is either Accuracy or an
    /// override on the class tab, and the second one needs this list.</para>
    /// </summary>
    public IReadOnlyList<PdnClassification> Classification { get; init; } = [];

    /// <summary>
    /// R-rail34-2 — the anchors a refusal was about because each stands on more than one net and
    /// does not say which it means, with what each could mean. Empty on every other outcome. Carried
    /// so the window can offer the choice as one click per candidate rather than as a sentence.
    /// </summary>
    public IReadOnlyList<PdnAnchorAmbiguity> AnchorAmbiguities { get; init; } = [];

    internal static PdnExtraction Refused(string why, PdnRailRegionSet? regions = null) =>
        new(why, null, regions, []);
}
