namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>Whether a boundary cell follows the GRID or the METAL.</b>
///
/// <para><b>This is the fourth user control, and D3 said there were exactly three.</b> The reversal
/// is on the owner's explicit instruction (brief-conformal-boundary-cells.md §5) and is recorded
/// rather than slipped in; D3's reasoning still stands for everything else and is not generally
/// relaxed. What earns this one is that it is not the same KIND of control as the three: cells per
/// wavelength and edge cells change how finely the same structure is discretised, while this changes
/// <i>which structure is discretised at all</i> — a staircased disc and a conformal disc are
/// different geometry, not two resolutions of one. That is a modelling decision, and modelling
/// decisions belong to the user.</para>
///
/// <para>It also needs an off switch on evidence rather than on taste: <b>every L8/L9 measurement in
/// this repository was taken on the staircase</b>, and a user reproducing one of them must be able
/// to.</para>
/// </summary>
public enum PlanarBoundaryCells
{
    /// <summary>L8b's rule as shipped: a cell is a whole grid rectangle, and a diagonal or curved
    /// outline is approximated by a staircase (D2). <b>The default</b>, and the model every measured
    /// number in this directory's <c>CLAUDE.md</c> was taken on.</summary>
    Staircase,

    /// <summary>A boundary cell is the grid rectangle intersected with the metal, so the union of the
    /// cells is the drawn polygon to round-off (R-cut-1). The interior of the mesh is unchanged and
    /// a Manhattan mesh is bit-identical (R-cut-2).</summary>
    Conformal,
}

/// <summary>
/// <b>What the current on this metal DOES — a line, a sheet, or "do not ask".</b> ANT-3's intent,
/// and the ninth user control.
///
/// <para><b>It is one three-way choice rather than two booleans, and that is a decision.</b> The two
/// intents are mutually exclusive: metal is being meshed as a line or as a sheet, never both, and
/// two independent flags that can both be true is a state nothing downstream could act on. It is
/// also not a resolution — it says what the resolutions are FOR — so it survives
/// <see cref="PlanarMeshSettings.Auto"/> exactly as <see cref="PlanarBoundaryCells"/> does.</para>
///
/// <para><b>Nothing infers it from the artwork, deliberately.</b> It is a statement about what the
/// current does, which is a modelling decision and therefore the user's — the same test
/// <see cref="PlanarBoundaryCells"/>' own note sets for what earns a control. An auto-detector would
/// guess "sheet" on a wide bend and under-resolve it silently, which is precisely the failure
/// <see cref="TransmissionLine"/>'s own direction decline exists to prevent.</para>
/// </summary>
public enum PlanarCurrentModel
{
    /// <summary>No statement — the per-axis rule, <c>h = min(λ_g/CellsPerWavelength,
    /// narrowest/MinCellsAcrossConductor)</c> in both directions. <b>The default</b>, and the model
    /// every measured number in this directory's <c>HISTORY.md</c> was taken on.</summary>
    None,

    /// <summary>
    /// <b>A transmission line: the current has a DIRECTION, and the mesher follows it.</b> λ along
    /// the current with no narrowness floor, the metal's own width across it, measured per point so
    /// a bend is followed rather than averaged. <b>It DECLINES where it cannot tell</b> — see
    /// <c>PlanarMeshPitchField</c>'s own header — and that decline is right and is not weakened by
    /// <see cref="Sheet"/> existing.
    /// </summary>
    TransmissionLine,

    /// <summary>
    /// <b>A radiating sheet: the current varies on the scale of a wavelength in BOTH axes, and there
    /// is no "along".</b> ANT-3, 2026-09-10, for the case <see cref="TransmissionLine"/> declines by
    /// construction and for which the direction question does not apply at all.
    ///
    /// <para>The dominant patch mode is a half-cosine along the resonant dimension and near-uniform
    /// across the width — smooth on the scale of a wavelength, with no singularity in the interior
    /// to resolve. So the bulk pitch is λ_g/<see cref="PlanarMeshSettings.CellsPerWavelength"/> in
    /// both axes, floored locally by <c>localWidth/MinCellsAcrossConductor</c> only where the metal
    /// is genuinely narrower than that — which on a patch is the feed and nothing else.</para>
    ///
    /// <para><b>The rim is NOT skipped, and the temptation to skip it is the trap.</b> A patch
    /// interior is smooth and the rim is where the cells go, so dropping the edge fan looks like
    /// free money. The two radiating edges set the effective length, hence the resonant frequency,
    /// hence every number downstream; the two non-radiating edges carry the transverse 1/√d
    /// singularity. ANT-2's per-attractor grading is what makes that affordable.</para>
    ///
    /// <para><b>It never declines</b>, because nothing about it is ambiguous: there is no direction
    /// to be unsure of. It is still bounded above by the per-axis rule on every input — the same
    /// one-way invariant <c>PlanarMeshPitchField</c> and
    /// <see cref="PlanarEdgeReference.LocalConductorWidth"/> already state — so a line meshed as a
    /// sheet comes out coarse along its length and correctly fine across it, which is exactly the
    /// per-axis rule. The mis-choice that costs is a SHEET meshed as a line.</para>
    /// </summary>
    Sheet,
}

/// <summary>
/// D3 asked for <b>exactly three user controls</b> — <c>Auto</c>, <c>Cells per wavelength</c>,
/// <c>Edge mesh on/off + cell count</c> — and that is §10.5's own list, verbatim. <b>There are nine
/// now, and every addition past the third was an explicit owner decision recorded at the parameter
/// it added</b> (<see cref="PlanarBoundaryCells"/>, <see cref="MeshFrequencyHz"/>,
/// <see cref="MinCellsAcrossConductor"/>, <see cref="CurrentModel"/>,
/// <see cref="DetailFloorDivisor"/>, and — the ninth — ANT-3's <see cref="PlanarCurrentModel.Sheet"/>,
/// a second VALUE for <see cref="CurrentModel"/> rather than a second field, because the two intents
/// are mutually exclusive). D3's REASONING still governs what may be added: a control earns its place
/// by being a modelling or responsibility decision that is the user's to make, never by being a
/// number that happens to exist in the mesher.
///
/// <para><b>Kernel A's <see cref="EmMeshSettings"/> has six, and the temptation is to mirror it. Do
/// not.</b> Its six exist because a boundary mesher over infinite dielectric interfaces has a
/// TRUNCATION problem (R-mom-10: truncation half-extent, tail cells) that a surface mesher over a
/// bounded piece of artwork does not have at all, and because a cross-section's edge cell is a
/// fraction of the metal THICKNESS, which a zero-thickness sheet does not have either. Everything
/// the user does not need to think about is auto-derived from the analysis — §10.5's own
/// instruction, and what makes §10.10's 30-second target reachable.</para>
/// </summary>
/// <param name="Auto">
/// When true the other three are ignored and the defaults below are used. This is the control the
/// user actually sees first, and leaving it on is meant to be the normal way to run.
/// </param>
/// <param name="CellsPerWavelength">§10.5's "20" in <c>max cell ≤ λ_g/20</c>.</param>
/// <param name="EdgeMesh">Graded cells at every conductor edge (R-msh-5).</param>
/// <param name="EdgeCells">How many, when on. §10.5 asks for 2–4.</param>
/// <param name="BoundaryCells">
/// <b>Conformal (cut) boundary cells — the fourth control, added as a TRAILING parameter</b> so every
/// existing positional construction is unchanged. <b>It ships OFF</b>; see
/// <see cref="PlanarBoundaryCells"/> for why it is a control at all, and this directory's
/// <c>CLAUDE.md</c> for the measurement that decides whether the default ever flips.
/// </param>
/// <param name="CurrentModel">
/// <b>What the current on this metal DOES — the SEVENTH control (as
/// <c>TransmissionLineMesh</c>, owner instruction 2026-09-09, asked three times before it was
/// understood) and, since ANT-3, the NINTH as well, because the same question has a second
/// answer.</b> See <see cref="PlanarCurrentModel"/> for the three values; this note is the
/// <see cref="PlanarCurrentModel.TransmissionLine"/> half's own history, kept because it is the
/// record of a real defect.
///
/// <para><b>The complaint:</b> lowering <see cref="CellsPerWavelength"/>, or lowering
/// <see cref="MeshFrequencyHz"/>, did not reduce the cell count. Not "reduced it less than
/// expected" — did not move it, at any value. <b>The cause is that both axes take the SAME
/// min:</b> <c>h = min(λ_g/CellsPerWavelength, narrowest/MinCellsAcrossConductor)</c>, so on any
/// artwork whose metal is narrower than a λ cell the geometry term wins in BOTH directions and the
/// two wavelength knobs are structurally inert. The mesher already SAID so in a note; a note about
/// a dead control is not a working control.</para>
///
/// <para><b>The owner's own statement of the fix, and it is the right one: the two settings are
/// ORTHOGONAL, not competing.</b> For a line running east–west,
/// <c>narrowest/MinCellsAcrossConductor</c> is a NORTH–SOUTH quantity and
/// <c>λ_g/CellsPerWavelength</c> is an EAST–WEST one. Each governs its own axis and neither
/// overrides the other. That is the physics as well as the ergonomics: ACROSS a line the current
/// carries the 1/√d edge singularity and needs the width resolved; ALONG it the current varies on
/// the scale of a wavelength and λ_g/20 is plenty. Taking the min in the along direction refines
/// for a singularity that is not there.</para>
///
/// <para>So with <see cref="PlanarCurrentModel.TransmissionLine"/>: <b>the along pitch is
/// λ_g/CellsPerWavelength with NO narrowness floor</b>, and the across pitch is
/// <c>min(λ_g/CellsPerWavelength, acrossNarrow/MinCellsAcrossConductor)</c> exactly as before. Both
/// knobs then always move the mesh, in their own direction.</para>
///
/// <para><b>Which way is "along" is measured PER POINT from the metal's own longest local chord,
/// so a bend is followed rather than averaged</b>; the ports only CHECK the answer, because two
/// ports of an east-then-north bend both state 45°, which is the direction of neither arm. Where
/// the metal states no usable direction the mode <b>DECLINES and meshes as before</b>, saying so —
/// a branched structure has no single current direction and inventing one would under-resolve a
/// real bend. <b>A wide radiating sheet is the case it declines by construction</b>, which is what
/// <see cref="PlanarCurrentModel.Sheet"/> exists for.</para>
///
/// <para><b>Default <see cref="PlanarCurrentModel.None"/></b>, because either intent changes the
/// mesh on every piece of artwork and every number in this directory's <c>HISTORY.md</c> must stay
/// reproducible. Neither is bit-identical when on, even on axis-aligned artwork — dropping the
/// along floor is the whole point of the first, and dropping the direction is the whole point of
/// the second.</para>
/// </param>
/// <param name="MeshFrequencyHz">
/// <b>The frequency the λ_g/N cell-size cap is sized at — the FIFTH control, and the only one that
/// is a PERFORMANCE knob rather than a resolution or a modelling choice.</b> <c>null</c> (the
/// default) means <i>the sweep's own top frequency</i>, which is exactly what the mesher did before
/// this parameter existed — so an unset value reproduces the shipped behaviour bit for bit.
///
/// <para><b>It is NOT <see cref="PlanarProblem.MaxFrequencyHz"/>, and the distinction is
/// load-bearing.</b> That property answers <i>how high does this sweep go</i> and has three
/// consumers that must keep reading it: <c>PlanarKernel.CanSolve</c>'s electrical via bound (a
/// physics refusal, which a performance knob must never be able to widen), the ρ/λ validated-range
/// note, and the geometry hash. This one answers only <i>what frequency was the mesh sized at</i>.
/// Conflating them would let a user silently relax a refusal by turning down a mesh setting.</para>
///
/// <para><b>The saving is AXIAL ONLY.</b> The transverse pitch is normally set by
/// <see cref="MinCellsAcrossConductor"/> rather than by λ, so halving the mesh frequency does not
/// halve the unknown count in both directions — do not describe it as quadratic. See this
/// directory's <c>CLAUDE.md</c> for the measured table.</para>
/// </param>
/// <param name="DetailFloorDivisor">
/// <b>The DETAIL FLOOR — the EIGHTH control. A piece of metal narrower than λ_g/this does not get to
/// drive the cell pitch.</b> 0 switches it off.
///
/// <para><b>Why it exists.</b> Every width measurement in the mesher — <see cref="SurfaceMesher.MeasureNarrowness"/>'s
/// global 5th percentile, <c>PlanarMeshPitchField</c>'s local across-chord, and
/// <see cref="PlanarEdgeReference.LocalConductorWidth"/>'s per-edge run — answers <i>how narrow is the
/// metal</i> and none of them has any notion of a feature being <i>too small to matter
/// electrically</i>. On an imported board that is not a corner case, it is the normal case: measured
/// on a 1.74 GHz patch board, the narrowest metal was 310 µm of connector via land and
/// aperture-rounded corner — λ_g/265, sitting ~9 mm from anything electrically interesting — and
/// deleting only those features moved the default mesh from 704,482 unknowns to 51,031, a
/// <b>14×</b> swing bought by geometry no field cares about.</para>
///
/// <para><b>It is λ-RELATIVE, not absolute, and that is the whole reason it can have a default at
/// all.</b> An absolute number in µm is a different decision on a 1.7 GHz board and a 40 GHz one and
/// would be wrong on one of them. It is derived from the same λ_g the cell-size cap uses, at the same
/// <see cref="MeshFrequencyHz"/>, so it scales with the analysis exactly as λ_g/N does.</para>
///
/// <para><b>It FLOORS, it never refines.</b> Every measured width is raised to the floor and none is
/// lowered, so the size field is pointwise ≥ what it was and the cell count is bounded above by
/// today's — the same invariant <see cref="PlanarEdgeReference.LocalConductorWidth"/> already states
/// for its own per-edge c0. No configuration can get worse by turning it on.</para>
///
/// <para><b>The number is a DIVISOR, so a SMALLER one is a COARSER floor.</b> Worth saying out loud
/// wherever a remedy names it: "raise the detail floor" and "raise this number" are opposite
/// instructions, and the refusal text says "coarsen the Detail floor — a SMALLER divisor" for exactly
/// that reason.</para>
///
/// <para><b>It SURVIVES <see cref="Auto"/></b>, on the settled taxonomy: Auto means <i>choose the
/// resolution for me</i>, and which geometry is electrically real is not a resolution. Same reasoning
/// that carried <see cref="BoundaryCells"/>, <see cref="MeshFrequencyHz"/> and
/// <see cref="CurrentModel"/> through <see cref="Resolved"/>.</para>
///
/// <para><b>The default is <see cref="DefaultDetailFloorDivisor"/> and it is a MEASURED answer</b> —
/// see that constant, and this directory's <c>RESOLVED.md</c> for the convergence table (off /
/// λ_g/1000 / λ_g/500 / λ_g/200 / λ_g/100, cell count and de-embedded S₁₁ at resonance) that chose
/// it.</para>
/// </param>
/// <param name="MemoryLimitBytes">
/// <b>The memory one frequency point of this run is sized on — the CHANNEL the run service hands the
/// machine's memory down in</b> (<c>PlanarMemoryCheck.WithMachineCeiling</c>: the Settings ▸ Solvers
/// limit, else physical memory). No document carries it: an EM setup has no memory limit of its own.
/// <c>null</c> (the default, and what every engine-direct caller passes) keeps the shipped ceilings
/// (<see cref="SurfaceMesher.UnknownCeiling"/> dense, <see cref="SurfaceMesher.AcceleratedUnknownCeiling"/>
/// accelerated), which refuse. Set, the ceiling is the largest count whose
/// <see cref="PlanarSystem.ResidentBytes"/> fits it (never below 12,000 accelerated), and it is ADVISORY:
/// past it the mesh warns and the run asks (<see cref="SurfaceMesher.LimitOwner"/>'s note).
///
/// <para>It changes no cell and no answer, so it enters no provenance hash, and it SURVIVES
/// <see cref="Auto"/> on the taxonomy <see cref="Resolved"/> states.</para>
/// </param>
/// <param name="RooftopMerging">
/// <b>PM-2 — solve a MERGED basis rather than the elemental one.</b> Off by default. Where a run of
/// elemental rooftops sits in cells more than <see cref="PlanarRooftopMerge.SpreadFactor"/>× finer
/// than the size field asks for at that point — with every edge attractor's fan localised to the
/// feature that raised it, instead of running the length of its gridline — the rooftops of one
/// direction are grouped into runs along that direction and one MACRO-rooftop replaces every rooftop
/// between two neighbouring runs. A macro
/// is a fixed-weight sum of elemental rooftops, so the merged space is a subspace of the elemental
/// one and normal current is continuous at every join by construction. The fill is still of the
/// elemental rooftops (in panels, never as a whole N×N matrix); what is held and factored is
/// <c>Tᵀ Z T</c>. See <see cref="PlanarRooftopMerge"/>.
///
/// <para>The GRID is unchanged by it — every cell and every elemental rooftop is the one the flag
/// off produces — so it is not a resolution and it SURVIVES <see cref="Auto"/>. It applies to a
/// single-level mesh on the dense path only; an accelerated or multi-level run meshes as before and
/// says so.</para>
/// </param>
/// <param name="StackedRegions">
/// <b>PMS-3 — mesh a via-joined stacked region as one conductor with a rim.</b> Off in the ENGINE, on in the PRODUCT
/// (<c>EmSetup.DefaultPlanarMesh</c>, PMS-7), the pattern <see cref="RooftopMerging"/> set. Where one via group (or a
/// chain of groups with one footprint) joins levels that carry metal on every cell of a region at least
/// <see cref="StackedRegionMinCells"/> cells and <see cref="StackedRegionMinSideM"/> across, the region's interior keeps
/// only the LOWEST level's cells, carrying every level's sheet in parallel, and emits no vertical basis; a rim
/// <see cref="StackedRegionRimCells"/> cells wide keeps every level and today's vertical bases. Cells are removed
/// before any rooftop is built, so R-msh-2 / R-via-5 hold on what remains. It changes the mesh, so it is hashed
/// (when on — which, since PMS-7 made it the product default, is the default) and does not survive as a resolution choice: it SURVIVES <see cref="Auto"/>.
/// </param>
/// <param name="LocalGrid">
/// <b>PMS-4 — keep each gridline near the feature that asked for it.</b> Off in the ENGINE, on in the PRODUCT
/// (<c>EmSetup.DefaultPlanarMesh</c>, PMS-7), as <see cref="StackedRegions"/> is. The shared grid's lines are built exactly as before; a cell then becomes a BLOCK of its elementary cells
/// wherever no line inside the block is needed — a hard line within <see cref="LocalReachCells"/> of its own feature,
/// or a finer pitch the size field asks for there — with a 2:1 balance between neighbouring blocks and port feeds kept
/// elementary. Every level shares the one partition, so a via's blocks still pair in z. It changes the mesh, so it is
/// hashed (when on — the product default) and SURVIVES <see cref="Auto"/>. The fill reads a block mesh on its support mesh
/// (<see cref="PlanarLocalGridFill.SupportMesh"/>, PMS-5), and since PMS-6 every other reader does too — ports and their
/// standards (a port's feed window is elementary along the port and carries only the feed's own lines across it), the
/// accelerated solve, the current-density map, the far field and the DC point — so it solves end to end. Rooftop
/// merging is not applied on one (the local grid already coarsened those runs).
/// </param>
public sealed record PlanarMeshSettings(
    bool Auto               = true,
    int  CellsPerWavelength = 20,      // = DefaultCellsPerWavelength (a record's own const cannot
    bool EdgeMesh           = true,    //   be referenced from its primary-constructor defaults;
    int  EdgeCells          = 3,       //   the Default* consts below pin the two together)
    PlanarBoundaryCells BoundaryCells = PlanarBoundaryCells.Staircase,
    double? MeshFrequencyHz = null,
    int  MinCellsAcrossConductor = 4,
    PlanarCurrentModel CurrentModel = PlanarCurrentModel.None,
    int  DetailFloorDivisor = 200,
    long? MemoryLimitBytes  = null,
    bool RooftopMerging     = false,
    bool StackedRegions     = false,
    bool LocalGrid          = false)
{
    /// <summary>
    /// <b>PMS-4 — how many elementary cells beyond its feature's own stretch a hard line, or an edge fan, stays needed
    /// on the local grid.</b> Engine-only: no <c>.cem</c> key. Measured: see <c>RESOLVED.md</c> §PMS-4.
    /// </summary>
    public int LocalReachCells { get; init; } = DefaultLocalReachCells;

    /// <summary>
    /// <b>PMS-4 — the local grid's balance</b>: a cell face meets at most this many neighbouring faces on a level they
    /// share. 2 is the 2:1 rule D-pms4-2 names. Engine-only: no <c>.cem</c> key. Measured: see <c>RESOLVED.md</c> §PMS-4.
    /// </summary>
    public int LocalGridBalance { get; init; } = DefaultLocalGridBalance;

    /// <summary>
    /// <b>PMS-4 — a test hook: every line is needed everywhere</b>, so every block is 1 × 1 and the local grid must
    /// reproduce the tensor grid's cells, bases and order exactly (gate 1). Never a user control; no <c>.cem</c> key.
    /// </summary>
    public bool LocalGridForceElementary { get; init; }

    /// <summary>PMS-4 — <see cref="LocalReachCells"/>' default.</summary>
    public const int DefaultLocalReachCells = 2;

    /// <summary>PMS-4 — <see cref="LocalGridBalance"/>' default.</summary>
    public const int DefaultLocalGridBalance = 2;

    /// <summary>
    /// <b>PMS-3 — how many cells across, in BOTH axes, a stacked region must be before it is merged.</b> An ordinary
    /// post is a few cells and never qualifies. Engine-only: no <c>.cem</c> key.
    /// </summary>
    public int StackedRegionMinCells { get; init; } = DefaultStackedRegionMinCells;

    /// <summary><b>PMS-3 — the rim, in cells</b>, inside a stacked region's boundary that keeps every level and its
    /// vertical bases. Engine-only: no <c>.cem</c> key.</summary>
    public int StackedRegionRimCells { get; init; } = DefaultStackedRegionRimCells;

    /// <summary>
    /// <b>PMS-3 — the interior carries its stack's sheets in parallel (the default), or only the kept level's own.</b>
    /// False is the oracle R-pms3-3 measures the parallel sheet against; it is kept reachable for that alone, on
    /// <see cref="PlanarFillSettings.PerfectConductor"/>'s pattern. Engine-only: no <c>.cem</c> key.
    /// </summary>
    public bool StackedRegionParallelSheet { get; init; } = true;

    /// <summary>
    /// <b>PMS-7 — how wide, in metres and in BOTH axes, a stacked region must be before it is merged</b>, as well as
    /// <see cref="StackedRegionMinCells"/> cells. A cell count measures the mesh rather than the artwork, so on a fine
    /// grid a post can reach it; this floor keeps a post whole whatever the mesh. Engine-only: no <c>.cem</c> key.
    /// </summary>
    public double StackedRegionMinSideM { get; init; } = DefaultStackedRegionMinSideM;

    /// <summary>PMS-7 — <see cref="StackedRegionMinSideM"/>' default, 30 µm: PMS-2's estimator's own floor, under the
    /// smallest MIM window of the fixture family (32 µm) and over every post measured (14 µm pads).</summary>
    public const double DefaultStackedRegionMinSideM = 30e-6;

    /// <summary>PMS-3 — <see cref="StackedRegionMinCells"/>' default. Measured: see <c>RESOLVED.md</c> §PMS-3.</summary>
    public const int DefaultStackedRegionMinCells = 18;

    /// <summary>PMS-3 — <see cref="StackedRegionRimCells"/>' default.</summary>
    public const int DefaultStackedRegionRimCells = 1;

    /// <summary>
    /// <b>CAP-1 — whose number <see cref="MemoryLimitBytes"/> is</b>: the machine-wide limit (the default)
    /// or the machine's physical memory. The engine never reads the machine; this changes only the
    /// WORDING of the warning. Outside the positional list and in no provenance hash, for
    /// <see cref="MemoryLimitBytes"/>' own reason.
    /// </summary>
    public PlanarMemoryLimitSource MemoryLimitSource { get; init; }

    public const int  DefaultCellsPerWavelength = 20;
    public const bool DefaultEdgeMesh           = true;
    public const int  DefaultEdgeCells          = 3;
    public const PlanarBoundaryCells DefaultBoundaryCells = PlanarBoundaryCells.Staircase;

    /// <summary>
    /// R-msh-4 — §10.5's "at least 3–5 cells across any conductor width". 4 sits in the middle of
    /// the range the design note asks for, and it is still the default.
    ///
    /// <para><b>It stopped being a constant on 2026-09-09, on the owner's explicit instruction:
    /// the user is responsible for mesh density, and a tool that will not give them a bad mesh when
    /// they ask for one is deciding on their behalf.</b> So
    /// <see cref="PlanarMeshSettings.MinCellsAcrossConductor"/> is now an ordinary setting and 1 is
    /// reachable. The concern that was raised and OVERRULED is recorded rather than dropped, because
    /// of what chasing it down found: <b>the cell count was not monotonic in this number.</b>
    /// Measured on a plain 200 µm × 10 mm FR-4 line at cells/λ = 20 — 8 → 220 cells, 6 → 200,
    /// 4 → 180, 3 → 160, 2 → 180, <b>1 → 200</b>. <b>Accuracy is not what 4 was buying</b> —
    /// de-embedded S21 moved under 0.001 dB from 1 to 8 across.
    ///
    /// <para><b>That non-monotonicity was a DEFECT elsewhere, not a property of this control, and it
    /// is FIXED (M0, 2026-09-09).</b> It was never the edge fan legitimately spending back what the
    /// bulk saved — that was the first diagnosis and it was wrong. It was <c>BuildGridLines</c>'
    /// grading marcher: the half-step look-ahead in <c>BoundaryMesher.PartitionFractions</c> is
    /// clamped to the interval end, where the size field is <c>c0</c> by definition, so the approach
    /// to a conductor's far edge collapsed into a uniform run at the FINEST cell size instead of
    /// grading down — and coarsening the bulk pitch made that run longer.
    /// <see cref="SurfaceMesher.PartitionGraded"/> replaced it and the ladder now reads
    /// 264 / 220 / 198 / 176 / 154 / 154, gated by
    /// <c>MeshGradingTests.LoweringCellsAcrossTheConductor_NeverRaisesTheCellCount</c>. <b>The
    /// mesher's warning about it is gone rather than softened</b>, and the numbers above are kept
    /// only as the record of what was measured before the fix.</para>
    /// </summary>
    public const int DefaultMinCellsAcrossConductor = 4;

    /// <summary>
    /// <b>Neither intent, i.e. the per-axis rule</b> — and the default for the reason every mesh
    /// control past the third carries: every number in this directory's <c>HISTORY.md</c> was taken
    /// on it. ANT-3 replaced the <c>bool TransmissionLineMesh</c> this used to spell with a
    /// three-way <see cref="PlanarCurrentModel"/> rather than adding a second boolean, because the
    /// two intents are mutually exclusive and two flags that can both be true is a state nothing
    /// downstream could act on.
    /// </summary>
    public const PlanarCurrentModel DefaultCurrentModel = PlanarCurrentModel.None;

    /// <summary>
    /// <b>λ_g ÷ this is the detail floor</b> — see <see cref="PlanarMeshSettings.DetailFloorDivisor"/>.
    ///
    /// <para><b>200 is a MEASURED answer, not a round number.</b> ANT-2 §3's convergence check, taken
    /// once in a scratch harness on a reconstruction of the reported patch board (41.3 × 49.4 mm
    /// patch, 349.3 µm feed, 310 µm connector via lands, 203.2 µm FR-4) and reported in
    /// <c>RESOLVED.md</c>: de-embedded |S₁₁| at the 1.740 GHz resonance reads <b>0.7516 with the
    /// floor off, at λ_g/1000 and at λ_g/500 alike — those three are the same mesh — then 0.7527 at
    /// λ_g/200 and 0.7533 at λ_g/100</b>. The whole ladder moves the answer by 1.7e-3, which is well
    /// inside this kernel's own de-embedding residual (~4e-3 at 2 GHz on 1.6 mm FR-4), so the
    /// connector detail is <i>not</i> electrically live and the floor is free to ignore it.</para>
    ///
    /// <para><b>λ_g/500 was the first candidate and it is not defensible</b>: on that board it is
    /// 164 µm against 310 µm of connector detail, so it produces a mesh identical to the floor being
    /// switched off — a default that does nothing on the board it was written for.</para>
    ///
    /// <para><b>λ_g/100 was NOT taken</b> even though the measurement permits it. It buys a further
    /// 1.4× and doubles the amount of genuinely drawn metal a floor can coarsen, on the evidence of
    /// one board; λ_g/200 is 10.4 µm at 40 GHz on GaAs and 411 µm at 1.74 GHz on FR-4, which floors
    /// nothing either starter technology draws at a shipped resolution.</para>
    /// </summary>
    public const int DefaultDetailFloorDivisor = 200;

    /// <summary>
    /// How far the port direction and the artwork's own principal axis may disagree before
    /// <see cref="PlanarCurrentModel.TransmissionLine"/> declines to pick one. A straight line puts them within a
    /// fraction of a degree of each other; a right-angle bend puts them 45° apart, which is the
    /// case that must NOT get a direction, because a directed pitch there coarsens one of the two
    /// arms along its own transverse axis. 15° admits an ordinary mitre and a feed that leaves at a
    /// slight angle, and refuses everything that is genuinely two directions.
    /// </summary>
    public const double DirectionAgreementDegrees = 15.0;

    /// <summary>
    /// <b>The largest cell aspect ratio the directed pitch may ask for.</b> A high-aspect cell is
    /// not new — the per-axis rule already ships 60:1 on a 200 µm × 50 mm trace — but an UNBOUNDED
    /// one is, and the fill's τ binning (centroid separation ÷ larger cell diagonal) and
    /// <c>SingularExtraction</c> were measured on moderate aspects. Measured against
    /// <c>PlanarFillAccuracy</c>'s own 5.0e-6 yardstick; see <c>RESOLVED.md</c> §M2 for the table
    /// that chose the number. The mesher's own gate is only that the cap is enforced.
    /// </summary>
    public const double MaxCellAspect = 64.0;

    /// <summary>
    /// R-msh-5 — the outermost edge cell as a fraction of the reference length, §10.5's own 2–5%.
    /// <b>Which length that fraction is OF is the measured question</b>; see
    /// <see cref="SurfaceMesher.EdgeReferenceLength"/>.
    /// </summary>
    public const double EdgeFractionOfReference = 0.03;

    /// <summary>Geometric growth ratio inward from an edge — §10.5's "~1.5–2".</summary>
    public const double EdgeGrowthRatio = 1.7;

    /// <summary>
    /// <b>EFAN — how many times finer than the bulk pitch an edge cell may be, and it is a MEASURED
    /// resolution rather than a tenth user control.</b>
    ///
    /// <para><b>What it is for.</b> <see cref="EdgeFractionOfReference"/> is 3% of the METAL and says
    /// nothing about the mesh that metal is being meshed at; the across pitch is the same metal
    /// divided by <see cref="MinCellsAcrossConductor"/>. So the climb the graded fan has to make is
    /// <c>1/(0.03·MinCellsAcross)</c> — <b>8.3 at the default and 33.3 at 1 across</b> — and the
    /// user's own density control makes the fan LONGER the coarser they ask for. Measured on the
    /// shipped 3-turn MMIC coil: taking Cells across 4 → 1 fell 23,195 → 10,392 unknowns with the
    /// edge mesh on (2.2×) against 6,456 → 228 with it off (28×). The realised edge fraction is
    /// therefore <c>max(3%, 1/(MinCellsAcross · MaxEdgeRefinement))</c>, so the climb is capped at
    /// this number however coarse the mesh is asked to be.
    ///
    /// <para><b>10 is measured, on an oracle that is not this kernel.</b> The sweep is in
    /// <c>RESOLVED.md</c> §EFAN: the edge fraction was forced over h/c₀ = 1…96 on the FR-4 and GaAs
    /// heroes, and each rung's Z_c, ε_eff and de-embedded S compared against kernel A's own
    /// cross-section extraction of the SAME line — a comparison a self-convergence study cannot
    /// make. Both stacks flatten in the same place:</para>
    ///
    /// <code>
    /// h/c₀        96     48     24     17     12    8.5      6      4      2      1
    /// GaAs  Z_c  +0.52  +0.53  +0.53  +0.59  +0.68  +0.71  +0.83  +1.06  +1.64  +2.87   % vs kernel A
    ///       |ΔS| .0195  .0151  .0133  .0132  .0174  .0352  .0587  .100   .223   .456
    /// FR-4  Z_c  −0.24  −0.13  −0.09  −0.01  −0.04  +0.13  +0.31  +0.60  +1.35  +2.90
    ///       |ΔS| .0161  .0138  .0153  .0245  .0351  .0558  .081   .124   .248   .489
    /// </code>
    ///
    /// <para>Nothing improves above ~17 — <b>on GaAs the finest rung is measurably WORSE than 17
    /// or 34</b>, because shrinking the outermost cell at a port's end face raises the error box's
    /// own 1/a₂₁² amplification — and the knee is at 8–12. <b>10 sits on the flat part and is also
    /// the largest round number that leaves the SHIPPED DEFAULT bit-identical</b>: the floor binds
    /// only when <c>1/(MinCellsAcross·10) &gt; 3%</c>, i.e. at 3 cells across and below, so every
    /// number in this directory's <c>HISTORY.md</c> — all taken at 4 — is untouched to the bit.</para>
    ///
    /// <para><b>It FLOORS, it never refines</b>, and the rate is left at the one derived from the
    /// UNFLOORED c₀, so <c>h(x) = min_i[c₀_i + g_i·|x − a_i|]</c> is pointwise ≥ what it was and the
    /// cell count is bounded above by today's — the same one-way invariant
    /// <see cref="PlanarEdgeReference.LocalConductorWidth"/> and <c>DetailFloorDivisor</c> already
    /// carry. <b>It is NOT a setting and must not become one</b>:
    /// <see cref="MinCellsAcrossConductor"/> is already the control for how finely this metal is
    /// meshed, and the whole point of this constant is that the edge fan should OBEY that control
    /// instead of ignoring it. D3's test — a control earns its place by being a modelling or
    /// responsibility decision — makes this a resolution, and resolutions are derived.</para>
    /// </summary>
    public const double MaxEdgeRefinement = 10.0;

    public static readonly PlanarMeshSettings Default = new();

    /// <summary>
    /// The settings actually used: <see cref="Auto"/> collapses to the defaults, so there is exactly
    /// one place the "what does Auto mean" question is answered and no code path downstream has to
    /// ask it again.
    ///
    /// <para><b><see cref="BoundaryCells"/> SURVIVES Auto, and that is a decision rather than an
    /// oversight.</b> <c>Auto = true</c> throwing the whole record away is the shape that already
    /// cost this area once — a fixture that sets a control and leaves Auto on then silently meshes
    /// the other way. The rule that resolves it is the same one that earned this control a place at
    /// all: Auto means "choose the RESOLUTION for me", and boundary cells are not a resolution. A
    /// user who asks for the metal to be followed is asking about the structure, and Auto has no
    /// opinion about the structure. Gated by <c>SurfaceMesherConformalTests</c>.</para>
    ///
    /// <para><b><see cref="MeshFrequencyHz"/> SURVIVES Auto too, for the same reason.</b> Auto decides
    /// cells/λ and edge cells — a RESOLUTION. Which frequency that resolution is applied AT is a
    /// different question, and Auto has no opinion about it. Throwing it away here would mean a user
    /// who set a mesh frequency and left Auto on silently got the sweep's top instead — the exact
    /// shape of failure the boundary-cell control above already had to be protected from.</para>
    ///
    /// <para><b><see cref="CurrentModel"/> SURVIVES Auto too, in BOTH of its non-default values.</b>
    /// It is not a resolution at all — it says what the resolutions are FOR, and Auto has no opinion
    /// about whether this metal is a line or a radiator. It is also the control that exists because a
    /// setting was being silently ignored, so having Auto silently ignore it would be a joke at the
    /// user's expense.</para>
    ///
    /// <para><b><see cref="DetailFloorDivisor"/> SURVIVES Auto too</b>, on the taxonomy rather than
    /// against it: it does not say how finely to discretise anything, it says which drawn geometry is
    /// electrically real enough to be worth discretising at all. Auto has no opinion about that, and a
    /// user who raised the floor to get an imported board through the ceiling must not have it thrown
    /// away because a checkbox is ticked.</para>
    ///
    /// <para><b><see cref="MinCellsAcrossConductor"/> SURVIVES Auto as well, and here the argument is
    /// the OWNER'S rather than the taxonomy's.</b> It is a resolution, so the taxonomy would have Auto
    /// reset it; but the whole reason it is a control is that the user is responsible for mesh density,
    /// and a number they typed being silently discarded because a checkbox is ticked is exactly the
    /// failure the two paragraphs above exist to prevent. It carries.</para>
    ///
    /// <para><b><see cref="EdgeMesh"/> SURVIVES Auto too, since EMB-4 R-emb4-7</b>, on the owner's rule above: a stated
    /// value wins and Auto fills only what is unstated. Auto chose the edge mesh ON whatever the file said, so a
    /// hand-written <c>"EdgeMesh": false</c> beside <c>"Auto": true</c> was silently meshed with the fan — which
    /// <c>check</c> could only warn about. The panel clears Auto when the edge mesh is toggled, so the combination
    /// arises only in a written file, and the default (on) is unchanged. Auto still chooses
    /// <see cref="CellsPerWavelength"/> and <see cref="EdgeCells"/>.</para>
    /// </summary>
    public PlanarMeshSettings Resolved => Auto
        ? new PlanarMeshSettings(Auto: false, EdgeMesh: EdgeMesh, BoundaryCells: BoundaryCells,
                                 MeshFrequencyHz: MeshFrequencyHz,
                                 MinCellsAcrossConductor: MinCellsAcrossConductor,
                                 CurrentModel: CurrentModel,
                                 DetailFloorDivisor: DetailFloorDivisor,
                                 MemoryLimitBytes: MemoryLimitBytes,
                                 RooftopMerging: RooftopMerging,
                                 StackedRegions: StackedRegions,
                                 LocalGrid: LocalGrid)
          {
              MemoryLimitSource     = MemoryLimitSource,
              LocalReachCells       = LocalReachCells,
              LocalGridBalance      = LocalGridBalance,
              LocalGridForceElementary = LocalGridForceElementary,
              StackedRegionMinCells = StackedRegionMinCells,
              StackedRegionRimCells = StackedRegionRimCells,
              StackedRegionMinSideM = StackedRegionMinSideM,
              StackedRegionParallelSheet = StackedRegionParallelSheet,
          }
        : this with
        {
            CellsPerWavelength      = Math.Max(2, CellsPerWavelength),
            EdgeCells               = Math.Max(0, EdgeCells),
            MinCellsAcrossConductor = Math.Max(1, MinCellsAcrossConductor),
            DetailFloorDivisor      = Math.Max(0, DetailFloorDivisor),
            StackedRegionMinCells   = Math.Max(3, StackedRegionMinCells),
            StackedRegionRimCells   = Math.Max(1, StackedRegionRimCells),
            StackedRegionMinSideM   = Math.Max(0.0, StackedRegionMinSideM),
            LocalReachCells         = Math.Max(0, LocalReachCells),
            LocalGridBalance        = Math.Max(1, LocalGridBalance),
        };
}

/// <summary>
/// <b>CAP-1 — where a planar run's memory limit came from</b>, so its warning can quote the right owner
/// of the number. The engine is told; it never asks the machine. Every memory limit is advisory.
/// </summary>
public enum PlanarMemoryLimitSource
{
    /// <summary>The machine-wide memory limit (Settings ▸ Solvers, or <c>em --memory-limit</c> / MCP
    /// <c>memoryLimitGB</c>) — and what a limit with no stated source is taken to be.</summary>
    MachineLimit = 0,
    /// <summary>The machine's physical memory — what a run gets when nothing states a limit.</summary>
    PhysicalMemory,
}
