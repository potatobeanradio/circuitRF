// .cem file format — rev 1 (alpha, no back-compat per policy).
// Mirrors TechPersistence exactly: System.Text.Json, WriteIndented, enum-as-string,
// WhenWritingNull, reject-on-newer-format-version, AtomicFile write, gzip sniff on load.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Em;

public sealed class CemFrequency
{
    public string       StartExpr { get; set; } = "1";
    public string       StopExpr  { get; set; } = "20";
    public string       StepExpr  { get; set; } = "";
    public int?         NumPoints { get; set; } = 101;
    public FreqSpecMode Mode      { get; set; } = FreqSpecMode.PointCount;
    public SweepKind    Kind      { get; set; } = SweepKind.Linear;
    public string       StartUnit { get; set; } = "GHz";
    public string       StopUnit  { get; set; } = "GHz";
    public string       StepUnit  { get; set; } = "GHz";
}

public sealed class CemMesh
{
    public int    MinCellsAcrossWidth { get; set; } = EmMeshSettings.Default.MinCellsAcrossWidth;
    public int    EdgeCells           { get; set; } = EmMeshSettings.Default.EdgeCells;
    public double EdgeFractionOfWidth { get; set; } = EmMeshSettings.Default.EdgeFractionOfWidth;
    public double EdgeGrowthRatio     { get; set; } = EmMeshSettings.Default.EdgeGrowthRatio;
    public double TruncationHeights   { get; set; } = EmMeshSettings.Default.TruncationHeights;
    public int    TruncationTailCells { get; set; } = EmMeshSettings.Default.TruncationTailCells;
}

/// <summary>
/// D3's three planar-mesh controls. A separate block from <see cref="CemMesh"/> because the two
/// meshers' settings are genuinely different quantities, not two spellings of one.
/// </summary>
public sealed class CemPlanarMesh
{
    public bool Auto               { get; set; } = true;
    public int  CellsPerWavelength { get; set; } = PlanarMeshSettings.DefaultCellsPerWavelength;
    public bool EdgeMesh           { get; set; } = PlanarMeshSettings.DefaultEdgeMesh;
    public int  EdgeCells          { get; set; } = PlanarMeshSettings.DefaultEdgeCells;

    /// <summary>
    /// Conformal (cut) boundary cells. <b>Nullable, and omitted at the default</b>, so a <c>.cem</c>
    /// written before this phase round-trips byte-identically — the same omit-at-default rule
    /// <see cref="CemFile.DirectVerticalKernel"/> follows, and for the same reason.
    /// </summary>
    public PlanarBoundaryCells? BoundaryCells { get; set; }

    /// <summary>
    /// The frequency the mesh is sized at, in HERTZ. <b>Nullable, and omitted at its default</b>
    /// (null = max sweep's own top), so every <c>.cem</c> written before this phase gains no
    /// byte and re-serialises byte-identically — the same rule <see cref="BoundaryCells"/> and
    /// <see cref="CemFile.DirectVerticalKernel"/> already follow. This is an asserted property of
    /// the format, not a nicety.
    /// </summary>
    public double? MeshFrequencyHz { get; set; }

    /// <summary>
    /// Cells across the narrowest conductor. <b>Nullable, and omitted at its default of 4</b>, so a
    /// <c>.cem</c> written before this control existed gains no byte and re-serialises byte-identically
    /// — the same rule <see cref="BoundaryCells"/> and <see cref="MeshFrequencyHz"/> follow.
    /// </summary>
    public int? MinCellsAcrossConductor { get; set; }

    /// <summary>
    /// <b>The LEGACY spelling of the transmission-line mesh, and it is READ-ONLY: nothing writes it
    /// any more.</b> ANT-3 replaced the boolean with the three-way
    /// <see cref="CurrentModel"/>, because a second boolean beside it could be true at the same time
    /// and the two intents are mutually exclusive.
    ///
    /// <para>A file carrying only this key reads as
    /// <see cref="PlanarCurrentModel.TransmissionLine"/> (or <see cref="PlanarCurrentModel.None"/>
    /// for an explicit <c>false</c>) and is written back on the new key, which is the whole of the
    /// migration. A file carrying BOTH and DISAGREEING is a <b>refusal naming both keys</b> rather
    /// than a precedence rule — a precedence rule here means a user who set one control watched the
    /// other one win, which is the defect the transmission-line mesh itself existed to fix.</para>
    /// </summary>
    public bool? TransmissionLineMesh { get; set; }

    /// <summary>
    /// ANT-3's current model — what the metal on this layout IS. <b>Nullable, and omitted at its
    /// default of <see cref="PlanarCurrentModel.None"/></b>, so a <c>.cem</c> written before it
    /// existed gains no byte and its hash is unchanged; the same rule every control added after the
    /// first three follows. See <see cref="TransmissionLineMesh"/> for the migration off the legacy
    /// boolean.
    /// </summary>
    public PlanarCurrentModel? CurrentModel { get; set; }

    /// <summary>
    /// ANT-2's detail floor divisor (λ_g ÷ this). <b>Nullable, and omitted at its default</b>, same
    /// rule as every control added after the first three — a <c>.cem</c> written before it existed
    /// gains no byte and reads back on the shipped default.
    /// </summary>
    public int? DetailFloorDivisor { get; set; }

    // A `MemoryLimitGb` key (a per-setup memory limit, 2026-10-29) is no longer a member: Settings ▸ Solvers is the
    // one memory limit. A .cem that carries it opens with the key skipped — System.Text.Json ignores an unknown
    // member by default — and, having no member to land in, it is not written back on save.

    /// <summary>
    /// <b>Mesh as Merged Shapes</b> (<see cref="PlanarMeshSettings.RooftopMerging"/>): on a single
    /// conductor level solved densely, rooftops lying in cells much finer than the local field asks
    /// for are solved as merged shapes along their own direction, for fewer unknowns on the same
    /// grid. <b>Null means ON</b>, the product default (<see cref="EmSetup.DefaultPlanarMesh"/>), so a
    /// <c>.cem</c> written before the setting existed gains no byte and reads back on; only an explicit
    /// <c>false</c> is ever written. <c>AdaptiveSampling</c>'s polarity, for the same reason.
    /// </summary>
    public bool? RooftopMerging { get; set; }
}

/// <summary>The solve region, micrometres in layout coordinates. See <see cref="EmSolveRegion"/>.</summary>
public sealed class CemSolveRegion
{
    public double XMinUm { get; set; }
    public double YMinUm { get; set; }
    public double XMaxUm { get; set; }
    public double YMaxUm { get; set; }
}

// ── brief-em3d-3 R-em3d3-3 — the 3D setup's file types ─────────────────────────────────────────
//
// Declared HERE, in CircuitRF.Design, and not beside Em3dProblem in the engine, because the
// generated reference page (`circuitrf reference em-setup`) expands a type only when it lives in
// CemFile's own assembly (R-em3d3-3c). The enum they name comes from the engine: enums are expanded
// wherever they live.

/// <summary>One face of the air box. Either half may be omitted; an omitted half takes the
/// generator's default for that face.</summary>
public sealed class CemAirBoxFace
{
    /// <summary>Distance from the outermost geometry to this face, micrometres.</summary>
    public double? PaddingUm { get; set; }

    /// <summary>The same distance as a percentage of the design's extent along this face's axis: 10 on
    /// XMin and XMax pads a tenth of the x-extent on each side. A face states PaddingUm or PaddingPercent,
    /// not both. A design with no extent along the axis (a flat sheet's z) pads by the default there.</summary>
    public double? PaddingPercent { get; set; }

    /// <summary>What this face does to the field.</summary>
    public CircuitRF.Engine.Em3d.Em3dBoundaryKind? Boundary { get; set; }
}

/// <summary>A 3D setup's air box, one entry per face. An omitted face takes the default: a fraction
/// of the longest wavelength in the band on the four sides and the top, and the floor on the lowest
/// ground plane (PEC) when there is one, absorbing otherwise.</summary>
public sealed class CemAirBox
{
    public CemAirBoxFace? XMin { get; set; }
    public CemAirBoxFace? XMax { get; set; }
    public CemAirBoxFace? YMin { get; set; }
    public CemAirBoxFace? YMax { get; set; }
    public CemAirBoxFace? ZMin { get; set; }
    public CemAirBoxFace? ZMax { get; set; }
}

/// <summary>brief-em3d-22 R-em3d22-2a — one terminal of a static 3D solve.</summary>
public sealed class CemTerminal3D
{
    /// <summary>The terminal's name: the label of its matrix row and column.</summary>
    public string Name { get; set; } = "";

    /// <summary>A net of the layout, or a <c>.wBond</c> wire array's name. Every conductor on it is in
    /// the terminal.</summary>
    public string Net { get; set; } = "";

    /// <summary>Magnetostatic only: the port whose sheet drives this terminal's current — its number,
    /// or <c>port/N</c>. Omitted for an electrostatic terminal.</summary>
    public string? Source { get; set; }

    /// <summary>brief-em3d-49 — the terminal's conductors by NAME, instead of a Net: what a 3D view's terminal
    /// states, since a drawn object carries no net. A terminal stating both is refused. Omitted when unused.</summary>
    public List<string>? Objects { get; set; }
}

/// <summary>
/// brief-em3d-23 R-em3d23-2 — one port's 3D settings, by its number. Every field but the number may be
/// omitted; an entry that states nothing but <c>Lumped</c> is not written at all.
/// </summary>
public sealed class CemPort3D
{
    /// <summary>The port's number, as its label states it.</summary>
    public int Port { get; set; }

    /// <summary>Lumped (a sheet with a resistance across it) or Wave (a region of an air-box face, fed by
    /// the line's own mode). <b>Omitted means Lumped.</b></summary>
    public CircuitRF.Engine.Em3d.Em3dPortKind? Kind { get; set; }

    /// <summary>A wave port's width, in multiples of the line's width. Omitted takes the sizing rule's.</summary>
    public double? Width { get; set; }

    /// <summary>A wave port's height, in multiples of the line's height above its return plane. Omitted
    /// takes the sizing rule's.</summary>
    public double? Height { get; set; }

    /// <summary>A wave port's de-embedding distance into the structure, micrometres. Omitted is 0: the
    /// reference plane is the port's face.</summary>
    public double? OffsetUm { get; set; }

    /// <summary>brief-dfr28-4 R-dfr28-4-5 — a lumped port's return: a conductor layer's stackup name.
    /// <b>Omitted means Automatic</b> — the plane the generator finds by walking the artwork from the port.</summary>
    public string? Return { get; set; }
}

/// <summary>brief-em3d-23 R-em3d23-4a — what an eigenmode solve finds.</summary>
public sealed class CemEigenmode
{
    /// <summary>How many modes. Omitted is 3.</summary>
    public int? Count { get; set; }

    /// <summary>The frequency above which modes are found, GHz. Omitted is the sweep's start.</summary>
    public double? TargetGHz { get; set; }
}

/// <summary>
/// brief-em3d-21 R-em3d21-4 — a Palace quality preset: a named set of the section's cost-deciding
/// fields. Each is a claim about cost and accuracy, so each was MEASURED on F0's cases A and B
/// (src/Design/RESOLVED.md §brief-em3d-21 holds the table); <see cref="Standard"/> is exactly the
/// defaults every run had before presets existed.
/// </summary>
public enum PalaceQuality
{
    /// <summary>Element order 1, no refinement passes, sweep tolerance 1e-3: the cheapest honest run.</summary>
    Draft,
    /// <summary>Today's defaults — <see cref="PalaceSettings.Default"/>, field for field.</summary>
    Standard,
    /// <summary>Order 2, up to 3 refinement passes at tolerance 0.005, sweep tolerance 1e-5.</summary>
    Accurate,
}

/// <summary>
/// brief-em3d-52 — how Palace solves each linear system. <see cref="Iterative"/> (GMRES preconditioned by
/// p-multigrid and the auxiliary-space AMS, F0's choice, every reference's) is the default: its memory grows
/// gently with the mesh. <see cref="Direct"/> factors the operator with SuperLU_DIST, which every Palace
/// circuitRF installs is built with: it takes more memory, and it is the one that makes a shift-and-invert
/// EIGENMODE solve fast, because there the shifted operator is near-singular and GMRES needs a hundred
/// iterations or more per solve. On the 3D Package example's lid mode, 128 k unknowns: GMRES had not
/// finished after ten minutes; Direct took 35 s and 4.0 GB.
/// </summary>
public enum PalaceLinearSolver
{
    Iterative,
    Direct,
}

/// <summary>Palace's own settings (em-3d.md §4.2). Every field may be omitted, and an omitted field
/// takes the preset's value for it (<see cref="Quality"/>, Standard when omitted); an omitted section
/// takes every default. The initial mesh these size is only a starting point: Palace's adaptive
/// refinement converges the answer.</summary>
public sealed class CemPalace
{
    /// <summary>The quality preset: Draft, Standard or Accurate. Omitted means Standard, which is the
    /// defaults below. An explicit field overrides the preset's value for that field only.</summary>
    public PalaceQuality? Quality { get; set; }

    /// <summary>The largest element allowed in each material, as a fraction of the wavelength in that
    /// material at the sweep's top frequency. Default 0.1 (a tenth of a wavelength).</summary>
    public double? MaxElementWavelengths { get; set; }

    /// <summary>The element size at conductor surfaces, sheet edges and port sheets, as a fraction of
    /// the smallest per-material maximum. Default 0.2.</summary>
    public double? EdgeRefinement { get; set; }

    /// <summary>How fast elements may grow away from a refined surface: the ratio between neighbouring
    /// element sizes. Default 1.3.</summary>
    public double? Grading { get; set; }

    /// <summary>The finite-element order Palace solves with. Default 2.</summary>
    public int? ElementOrder { get; set; }

    /// <summary>The relative error at which Palace's adaptive mesh refinement stops. Default 0.01.</summary>
    public double? AdaptiveTol { get; set; }

    /// <summary>The most adaptive mesh refinement passes Palace makes; 0 solves on the initial mesh.
    /// Default 2.</summary>
    public int? AdaptiveMaxIterations { get; set; }

    /// <summary>The error tolerance of Palace's adaptive frequency sweep, which solves a few frequencies
    /// and interpolates the rest. Default 0.0001.</summary>
    public double? SweepAdaptiveTol { get; set; }

    /// <summary>
    /// brief-em3d-29 R-em3d29-1b — the frequencies, GHz, whose fields Palace saves for the 3D view. Omitted
    /// saves the sweep's CENTRE frequency only (owner decision D7); <c>[]</c> saves no field at all; a list
    /// saves those. An eigenmode run saves every mode it computes and a static run every terminal's
    /// solution unless this is <c>[]</c> (their fields are not per frequency, so the list's values are not
    /// read). Fields are large — see the reference page for the measured size per frequency.
    /// </summary>
    public List<double>? SaveFieldsGHz { get; set; }

    /// <summary>brief-em3d-52 — Iterative (the default) or Direct: see <see cref="PalaceLinearSolver"/>. Not a
    /// preset's concern: every preset solves the same systems.</summary>
    public PalaceLinearSolver? LinearSolver { get; set; }

    /// <summary>brief-emb-2 R-emb2-1 — how long, in minutes, Gmsh may spend curving and optimising second-order elements on
    /// genuinely curved geometry before it is restarted with straight-sided ones (the run says so). Omitted is
    /// <see cref="PalaceSettings.DefaultMeshCurvingMinutes"/>. A problem whose only curves are a layout's via barrels is never
    /// curved, so this is not read for it.</summary>
    public double? MeshCurvingMinutes { get; set; }

    /// <summary>A copy, so an editor can change one without touching a setup that shares it.</summary>
    public CemPalace Clone()
    {
        var c = (CemPalace)MemberwiseClone();
        c.SaveFieldsGHz = SaveFieldsGHz is { } f ? [.. f] : null;
        return c;
    }

    /// <summary>True when no field is set: the same run as an omitted section. Never written: an embedded setup
    /// (a .c3d's) is serialised by reflection, which writes a get-only property too (brief-em3d-52).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Quality is null && MaxElementWavelengths is null && EdgeRefinement is null && Grading is null && ElementOrder is null &&
        AdaptiveTol is null && AdaptiveMaxIterations is null && SweepAdaptiveTol is null && SaveFieldsGHz is null &&
        LinearSolver is null && MeshCurvingMinutes is null;
}

/// <summary>
/// brief-em3d-7 R-em3d7-6a — the Palace section RESOLVED: every field a value. <b>The defaults live
/// here and nowhere else</b>; the panel, the writers and <c>explain</c> all read them from
/// <see cref="Default"/>.
/// </summary>
public sealed record PalaceSettings(
    double MaxElementWavelengths,
    double EdgeRefinement,
    double Grading,
    int    ElementOrder,
    double AdaptiveTol,
    int    AdaptiveMaxIterations,
    double SweepAdaptiveTol)
{
    /// <summary>
    /// The shipped defaults. λ/10 per material and a fifth of that at metal and ports is F0's case B
    /// order of magnitude (docs/design/em-3d-f0-findings.md: 250 µm in the board, 60 µm at the signal,
    /// growth ~1.3). Refinement tolerance 0.01 is Palace's own default; two passes bound the memory a
    /// first run can take (F0 §4: two passes cost 1.5× the peak of none). The sweep tolerance is the
    /// one every F0 reference ran at.
    /// </summary>
    public static PalaceSettings Default { get; } = new(0.1, 0.2, 1.3, 2, 0.01, 2, 1e-4);

    /// <summary>brief-em3d-29 R-em3d29-1b — the field-save frequencies, GHz: null is the sweep's centre
    /// (D7), empty saves none. Not a preset's concern: every preset saves the same fields.</summary>
    public IReadOnlyList<double>? SaveFieldsGHz { get; init; }

    /// <summary>brief-em3d-29 R-em3d29-4 — points, metres, at which Palace writes E and B itself
    /// (<c>probe-E.csv</c>, <c>probe-B.csv</c>): the check on the field reader that does not go through it.
    /// Never set from a .cem; a gate sets it.</summary>
    public IReadOnlyList<Point3> ProbesM { get; init; } = [];

    /// <summary>brief-em3d-52 — how each linear system is solved (<see cref="PalaceLinearSolver"/>).</summary>
    public PalaceLinearSolver LinearSolver { get; init; } = PalaceLinearSolver.Iterative;

    /// <summary>brief-emb-2 R-emb2-1 — the default wall-clock cap on Gmsh's curving of second-order elements, minutes.</summary>
    public const double DefaultMeshCurvingMinutes = 5;

    /// <summary>brief-emb-2 R-emb2-1 — the cap on Gmsh's high-order curving and optimisation, minutes (<see cref="CemPalace.MeshCurvingMinutes"/>).
    /// Past it the mesh is made again with straight-sided second-order elements. Not part of the script: it changes no mesh
    /// that finishes inside it.</summary>
    public double MeshCurvingMinutes { get; init; } = DefaultMeshCurvingMinutes;

    /// <summary>brief-emb-2 R-emb2-1 — straight-sided second-order elements whatever the geometry: the restart after
    /// <see cref="MeshCurvingMinutes"/>. Never set from a <c>.cem</c>.</summary>
    public bool StraightSided { get; init; }

    /// <summary>
    /// brief-em3d-21 R-em3d21-4 — a preset's values. <b>Standard is <see cref="Default"/></b>, so no
    /// golden and no existing answer moves. Draft and Accurate change only the element order, the
    /// refinement and the sweep tolerance: the starting mesh's sizes are the same in all three, so a
    /// preset change reuses the mesh.
    /// </summary>
    public static PalaceSettings Preset(PalaceQuality quality) => quality switch
    {
        PalaceQuality.Draft    => Default with { ElementOrder = 1, AdaptiveMaxIterations = 0, SweepAdaptiveTol = 1e-3 },
        PalaceQuality.Accurate => Default with { ElementOrder = 2, AdaptiveMaxIterations = 3, AdaptiveTol = 0.005,
                                                 SweepAdaptiveTol = 1e-5 },
        _                      => Default,
    };

    /// <summary>The section's values: the preset first (Standard when the section names none), then
    /// every field the section sets, each overriding the preset's value for that field.</summary>
    public static PalaceSettings Resolve(CemPalace? section)
    {
        if (section is null) return Default;
        var p = Preset(section.Quality ?? PalaceQuality.Standard);
        return new(
            section.MaxElementWavelengths ?? p.MaxElementWavelengths,
            section.EdgeRefinement        ?? p.EdgeRefinement,
            section.Grading               ?? p.Grading,
            section.ElementOrder          ?? p.ElementOrder,
            section.AdaptiveTol           ?? p.AdaptiveTol,
            section.AdaptiveMaxIterations ?? p.AdaptiveMaxIterations,
            section.SweepAdaptiveTol      ?? p.SweepAdaptiveTol)
        {
            SaveFieldsGHz = section.SaveFieldsGHz,
            LinearSolver  = section.LinearSolver ?? PalaceLinearSolver.Iterative,
            MeshCurvingMinutes = section.MeshCurvingMinutes ?? DefaultMeshCurvingMinutes,
        };
    }

    /// <summary>Every value that cannot be run, as sentences naming the field — empty when all can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!(MaxElementWavelengths > 0) || double.IsInfinity(MaxElementWavelengths))
            p.Add($"Palace.MaxElementWavelengths is {MaxElementWavelengths}; it must be a positive fraction of a wavelength.");
        if (!(EdgeRefinement > 0 && EdgeRefinement <= 1))
            p.Add($"Palace.EdgeRefinement is {EdgeRefinement}; it must be above 0 and at most 1.");
        if (!(Grading > 1) || double.IsInfinity(Grading))
            p.Add($"Palace.Grading is {Grading}; it must be above 1.");
        if (ElementOrder is < 1 or > 6)
            p.Add($"Palace.ElementOrder is {ElementOrder}; it must be 1 to 6.");
        if (!(AdaptiveTol > 0))
            p.Add($"Palace.AdaptiveTol is {AdaptiveTol}; it must be positive.");
        if (AdaptiveMaxIterations < 0)
            p.Add($"Palace.AdaptiveMaxIterations is {AdaptiveMaxIterations}; it must be 0 or more.");
        if (!(SweepAdaptiveTol >= 0))
            p.Add($"Palace.SweepAdaptiveTol is {SweepAdaptiveTol}; it must be 0 or more.");
        if (!(MeshCurvingMinutes > 0) || double.IsInfinity(MeshCurvingMinutes))
            p.Add($"Palace.MeshCurvingMinutes is {MeshCurvingMinutes}; it must be a positive number of minutes.");
        foreach (double f in SaveFieldsGHz ?? [])
            if (!(f > 0) || double.IsInfinity(f))
            {
                p.Add($"Palace.SaveFieldsGHz holds {f}; every save frequency must be a positive number of GHz.");
                break;
            }
        return p;
    }
}

/// <summary>brief-em3d-120 — an openEMS grid's coordinates.</summary>
public enum OpenEmsGridKind { Cartesian, Cylindrical }

/// <summary>brief-em3d-120 — the world axis a cylindrical openEMS grid is round about.</summary>
public enum OpenEmsGridAxis { X, Y, Z }

/// <summary>openEMS's own settings (em-3d.md §4.2). Every field may be omitted, and an omitted field
/// takes circuitRF's default for it; an omitted section takes every default. These place the grid
/// lines openEMS solves on — circuitRF writes the grid itself, and `explain` reports it before a run.</summary>
public sealed class CemOpenEms
{
    /// <summary>The largest grid cell allowed, as the number of cells per wavelength in the densest
    /// material the cell passes through, at the sweep's top frequency. Default 20.</summary>
    public double? CellsPerWavelength { get; set; }

    /// <summary>The largest ratio between two neighbouring grid cells. Default 1.3.</summary>
    public double? GradingRatio { get; set; }

    /// <summary>Whether the edge of a sheet or thin conductor gets its grid lines a third of the local
    /// cell inside the metal and two thirds outside, where the field's edge singularity lives, rather
    /// than one line on the edge. Default true; false matches a model gridded with lines on the edges.</summary>
    public bool? ThirdsRule { get; set; }

    /// <summary>Grid lines the geometry asks for that are closer than this are merged into one, and
    /// every merge is reported, micrometres. Default: a tenth of the smallest metal width or thickness
    /// in the problem.</summary>
    public double? MinCellUm { get; set; }

    /// <summary>The absorbing layer's thickness, in uniform cells added outside each absorbing face of
    /// the air box. Default 8.</summary>
    public int? PmlCells { get; set; }

    /// <summary>How far the port signals must fall below their peak before a run stops, in dB (negative).
    /// circuitRF watches every port's voltage and current and stops openEMS when all of them have decayed
    /// this far; openEMS's own field-energy criterion is set to the same level. Default −50.</summary>
    public double? EndCriterionDb { get; set; }

    /// <summary>The most time steps one run may take. A run that reaches it before the end criterion has
    /// not converged: its result is still written, with a warning stating how far the signals fell.
    /// Default: ten times the steps the grid estimates for the pulse and its ring-down.</summary>
    public long? MaxTimeSteps { get; set; }

    /// <summary>
    /// brief-em3d-29 R-em3d29-6a — the frequencies, GHz, at which openEMS dumps the electric field over the
    /// problem's extent for the 3D view. Omitted dumps the sweep's CENTRE frequency; <c>[]</c> dumps none;
    /// a list dumps those, each within the sweep (outside it, the pulse carries no energy to show).
    /// </summary>
    public List<double>? SaveFieldsGHz { get; set; }

    /// <summary>
    /// brief-em3d-82 R-em3d82-4 — also dump the MAGNETIC field H at each saved frequency, beside E, so the 3D view offers
    /// |H| for an openEMS run. Off unless set: it doubles the field files on disk per saved frequency (the owner's choice —
    /// an opt-in, not a default). Nothing is dumped when <c>SaveFieldsGHz</c> is <c>[]</c>, whatever this says.
    /// </summary>
    public bool? SaveH { get; set; }

    /// <summary>
    /// brief-em3d-120 R-em3d120-2 C1 — the grid's coordinates: <c>Cartesian</c> (omitted: what every existing file means) or
    /// <c>Cylindrical</c>, round about <see cref="Axis"/> through <see cref="AxisOriginUm"/>. Never inferred. A 3D view's own
    /// setup only (C5): a <c>.cem</c> stating it is refused.
    /// </summary>
    public OpenEmsGridKind? Grid { get; set; }

    /// <summary>brief-em3d-120 — the world axis a cylindrical grid is round about. Default Z. Read only with <c>Grid: Cylindrical</c>.</summary>
    public OpenEmsGridAxis? Axis { get; set; }

    /// <summary>brief-em3d-120 — a point the axis passes through, [x, y, z] in µm. Default the origin. Read only with
    /// <c>Grid: Cylindrical</c>.</summary>
    public List<double>? AxisOriginUm { get; set; }

    /// <summary>brief-em3d-120 — the azimuth lines of a cylindrical grid, 0 and 2π both counted. Default: the arc at the
    /// outermost conductor radius no longer than the radial cell there (R-em3d120-1c).</summary>
    public int? AzimuthLines { get; set; }

    /// <summary>
    /// brief-sar-8b R-sar8b-3 — openEMS's cylindrical sub-grids (its <c>MultiGrid</c>): radii, µm, inside which the azimuth lines
    /// are halved once more each, so the cells near the axis are not the thin wedges that would set the time step. Omitted: none.
    /// Read only with <c>Grid: Cylindrical</c>; openEMS needs an odd number of azimuth lines at every level.
    /// </summary>
    public List<double>? MultiGridRadiiUm { get; set; }

    /// <summary>
    /// brief-sar-3 R-sar3-4 — each FDTD cell takes the material at its centre rather than an average over its edges
    /// (openEMS's <c>CellConstantMaterial</c>): less accurate at a material's face, and what averaging SAR over cubes needs.
    /// Default: on for a setup that requests SAR, off otherwise, so every setup written before it runs as it did.
    /// </summary>
    public bool? CellConstantMaterial { get; set; }

    /// <summary>
    /// brief-sar-3 R-sar3-5 — the grid lines to run on, instead of the ones circuitRF places: per axis, a list of coordinates in
    /// <c>Unit</c>, or a CSXCAD XML <c>File</c> whose <c>RectilinearGrid</c> is taken verbatim. With it no line of circuitRF's
    /// own is placed. How an openEMS script is ported with its mesh, and how a replication runs on the reference's own grid.
    /// </summary>
    public CemGridLines? GridLines { get; set; }

    /// <summary>A copy, so an editor can change one without touching a setup that shares it.</summary>
    public CemOpenEms Clone()
    {
        var c = (CemOpenEms)MemberwiseClone();
        c.SaveFieldsGHz = SaveFieldsGHz is { } f ? [.. f] : null;
        c.AxisOriginUm = AxisOriginUm is { } o ? [.. o] : null;
        c.MultiGridRadiiUm = MultiGridRadiiUm is { } mg ? [.. mg] : null;
        c.GridLines = GridLines?.Clone();
        return c;
    }

    /// <summary>True when no field is set: the same run as an omitted section. Never written: an embedded setup
    /// (a .c3d's) is serialised by reflection, which writes a get-only property too (brief-em3d-52).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        CellsPerWavelength is null && GradingRatio is null && ThirdsRule is null && MinCellUm is null && PmlCells is null &&
        EndCriterionDb is null && MaxTimeSteps is null && SaveFieldsGHz is null && SaveH is null &&
        Grid is null && Axis is null && AxisOriginUm is null && AzimuthLines is null && CellConstantMaterial is null && GridLines is null &&
        MultiGridRadiiUm is null;

    /// <summary>
    /// brief-em3d-8 R-em3d8-6 — the section's grid fields RESOLVED, each omitted one taking
    /// <see cref="CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default"/>'s. The defaults live there, in the engine, because the
    /// grid generator is what reads them.
    /// </summary>
    public static CircuitRF.Engine.Em3d.OpenEmsGridSettings ResolveGrid(CemOpenEms? section)
    {
        var d = CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default;
        if (section is null) return d;
        return Resolved(section, d) with { ExplicitLines = section.GridLines?.Resolve() };
    }

    /// <summary>brief-sar-3 R-sar3-5 — <see cref="ResolveGrid(CemOpenEms?)"/> for a setup read from
    /// <paramref name="documentPath"/> (the <c>.c3d</c> or <c>.cem</c>): a relative <c>GridLines.File</c> is read beside it.</summary>
    public static CircuitRF.Engine.Em3d.OpenEmsGridSettings ResolveGrid(CemOpenEms? section, string? documentPath)
        => ResolveGrid(Anchored(section, documentPath));

    /// <summary>brief-sar-3 R-sar3-5 — <paramref name="section"/> with its grid file's folder set to
    /// <paramref name="documentPath"/>'s, as a copy; the section itself when there is nothing to anchor or it is anchored
    /// already (a setup read through <c>C3dSetups.ForRun</c> or <c>LoadFromFile</c> keeps its own document's folder).</summary>
    public static CemOpenEms? Anchored(CemOpenEms? section, string? documentPath)
    {
        if (section?.GridLines is not { File: { Length: > 0 }, BaseDirectory: null } || documentPath is null) return section;
        var c = section.Clone();
        c.GridLines!.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
        return c;
    }

    private static CircuitRF.Engine.Em3d.OpenEmsGridSettings Resolved(CemOpenEms section, CircuitRF.Engine.Em3d.OpenEmsGridSettings d)
    {
        return new(
            section.CellsPerWavelength ?? d.CellsPerWavelength,
            section.GradingRatio       ?? d.GradingRatio,
            section.ThirdsRule         ?? d.ThirdsRule,
            section.MinCellUm is { } um ? um * 1e-6 : d.MinCellM,
            section.PmlCells           ?? d.PmlCells)
        {
            // brief-em3d-120 — a malformed origin resolves to NaN, which OpenEmsGridSettings.Problems names.
            Cylindrical = section.Grid != OpenEmsGridKind.Cylindrical ? null : new CircuitRF.Engine.Em3d.OpenEmsCylindrical(
                (CircuitRF.Engine.Em3d.FdtdAxis)(int)(section.Axis ?? OpenEmsGridAxis.Z),
                section.AxisOriginUm is null ? default
                    : section.AxisOriginUm is [var x, var y, var z] ? new CircuitRF.Engine.Em3d.Point3(x * 1e-6, y * 1e-6, z * 1e-6)
                    : new CircuitRF.Engine.Em3d.Point3(double.NaN, double.NaN, double.NaN),
                section.AzimuthLines)
                { MultiGridRadiiM = section.MultiGridRadiiUm is { } mg ? [.. mg.Select(r => r * 1e-6)] : null },
        };
    }

    /// <summary>brief-em3d-120 C5 — the refusal of a cylindrical grid in a <c>.cem</c>'s own setup, or null.</summary>
    public static string? CemCylindricalRefusal(CemOpenEms? section)
        => section?.Grid == OpenEmsGridKind.Cylindrical
            ? "This .cem's openEMS section sets Grid to Cylindrical, and a cylindrical grid is written for a 3D view's own setup only: " +
              "a .cem is a layout problem, and stays on a Cartesian grid. Set OpenEms.Grid to Cartesian, or move the setup into the " +
              ".c3d it solves."
            : null;

    /// <summary>brief-em3d-9 R-em3d9-6 — the section's run fields RESOLVED, each omitted one taking
    /// <see cref="OpenEmsRunSettings.Default"/>'s.</summary>
    /// <param name="requestsSar">brief-sar-3 R-sar3-4 — whether the setup asks for SAR (SAR-6), which turns
    /// <see cref="CellConstantMaterial"/> on unless the section says otherwise. No setup asks yet, so the default is off.</param>
    public static OpenEmsRunSettings ResolveRun(CemOpenEms? section, bool requestsSar = false)
    {
        var d = OpenEmsRunSettings.Default;
        return section is null ? d with { CellConstantMaterial = requestsSar }
            : new(section.EndCriterionDb ?? d.EndCriterionDb, section.MaxTimeSteps ?? d.MaxTimeSteps)
        {
            SaveFieldsGHz = section.SaveFieldsGHz,
            SaveH = section.SaveH ?? d.SaveH,
            CellConstantMaterial = section.CellConstantMaterial ?? requestsSar,
        };
    }
}

/// <summary>
/// brief-sar-3 R-sar3-5 — <c>OpenEms.GridLines</c>: the lines an openEMS run uses, given rather than placed. Either a CSXCAD
/// XML <see cref="File"/> (what an openEMS script's <c>Write2XML</c> or <c>WriteOpenEMS</c> writes), whose
/// <c>RectilinearGrid</c> is taken verbatim, or three lists <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> in
/// <see cref="Unit"/> — never both.
/// </summary>
public sealed class CemGridLines
{
    /// <summary>A CSXCAD XML file whose <c>RectilinearGrid</c> lines are taken verbatim (scaled by its <c>DeltaUnit</c>):
    /// absolute, or relative to the file the setup is in (the <c>.c3d</c> or <c>.cem</c>).</summary>
    public string? File { get; set; }

    /// <summary>The unit of <see cref="X"/>, <see cref="Y"/> and <see cref="Z"/>: <c>m</c>, <c>mm</c>, <c>um</c>, <c>nm</c>,
    /// <c>mil</c> or <c>in</c>. Required with them: a bare coordinate could be any of them.</summary>
    public string? Unit { get; set; }

    /// <summary>The x lines, strictly increasing, in <see cref="Unit"/>.</summary>
    public List<double>? X { get; set; }

    /// <summary>The y lines, strictly increasing, in <see cref="Unit"/>.</summary>
    public List<double>? Y { get; set; }

    /// <summary>The z lines, strictly increasing, in <see cref="Unit"/>.</summary>
    public List<double>? Z { get; set; }

    /// <summary>The folder a relative <see cref="File"/> is read from: the setup's own file's, set by whoever read the setup
    /// from it. Never written.</summary>
    [JsonIgnore]
    public string? BaseDirectory { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    public CemGridLines Clone()
    {
        var c = (CemGridLines)MemberwiseClone();
        c.X = X is { } x ? [.. x] : null;
        c.Y = Y is { } y ? [.. y] : null;
        c.Z = Z is { } z ? [.. z] : null;
        return c;
    }

    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1, ["mm"] = 1e-3, ["um"] = 1e-6, ["µm"] = 1e-6, ["nm"] = 1e-9, ["mil"] = 25.4e-6, ["in"] = 0.0254, ["inch"] = 0.0254,
    };

    /// <summary>The lines in metres, or — in <see cref="CircuitRF.Engine.Em3d.FdtdExplicitLines.Problem"/> — why they could not be
    /// read. The file is read here, at resolution, so <c>explain</c>, <c>check</c> and a run all read the same lines.</summary>
    public CircuitRF.Engine.Em3d.FdtdExplicitLines Resolve()
    {
        static CircuitRF.Engine.Em3d.FdtdExplicitLines Bad(string source, string why) => new([], [], [], source) { Problem = why };
        bool lists = X is not null || Y is not null || Z is not null;
        if (File is { Length: > 0 } file)
        {
            if (lists || Unit is not null)
                return Bad(file, "OpenEms.GridLines gives both a File and lists of lines; give one or the other.");
            return ReadFile(file);
        }
        if (!lists) return Bad("nothing", "OpenEms.GridLines gives neither a File nor X, Y and Z lists.");
        string source = $"lists in {Unit}";
        if (X is null || Y is null || Z is null)
            return Bad(source, "OpenEms.GridLines gives lines for some axes only; give X, Y and Z, or a File.");
        if (Unit is not { Length: > 0 } u || !Units.TryGetValue(u.Trim(), out double scale))
            return Bad(source, $"OpenEms.GridLines' Unit is {(Unit is null ? "missing" : $"'{Unit}'")}: give m, mm, um, nm, mil or in — " +
                               "a bare coordinate could be any of them.");
        return new([.. X.Select(v => v * scale)], [.. Y.Select(v => v * scale)], [.. Z.Select(v => v * scale)], source);
    }

    private CircuitRF.Engine.Em3d.FdtdExplicitLines ReadFile(string file)
    {
        string name = Path.GetFileName(file);
        CircuitRF.Engine.Em3d.FdtdExplicitLines Bad(string why) => new([], [], [], name) { Problem = why };
        string path;
        if (Path.IsPathRooted(file)) path = file;
        else if (BaseDirectory is { } dir) path = Path.GetFullPath(Path.Combine(dir, file));
        else return Bad($"OpenEms.GridLines.File '{file}' is relative, and this setup was not read from a file it could be relative to. " +
                        "Give its full path.");
        System.Xml.Linq.XElement grid;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            grid = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RectilinearGrid")
                   ?? throw new FormatException("it holds no RectilinearGrid");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            return Bad($"OpenEms.GridLines.File '{file}' cannot be read as a CSXCAD grid: {e.Message}");
        }
        // brief-sar-8b R-sar8b-3 — a cylindrical grid (CoordSystem 1) is ρ, α, z: ρ and z in DeltaUnit, α in radians as written (the
        // closed circle's last line its first plus 2π, as openEMS reads it).
        bool cylindrical = false;
        if ((string?)grid.Attribute("CoordSystem") is { } cs && cs.Trim() != "0")
        {
            if (cs.Trim() != "1") return Bad($"OpenEms.GridLines.File '{file}' holds a grid in coordinate system {cs}; circuitRF reads Cartesian (0) and cylindrical (1).");
            cylindrical = true;
        }
        double delta = 1;
        if ((string?)grid.Attribute("DeltaUnit") is { } du &&
            !double.TryParse(du, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delta))
            return Bad($"OpenEms.GridLines.File '{file}''s DeltaUnit '{du}' is not a number.");
        var axes = new List<double>[3];
        string[] tags = ["XLines", "YLines", "ZLines"];
        for (int k = 0; k < 3; k++)
        {
            var e = grid.Elements().FirstOrDefault(x => x.Name.LocalName == tags[k]);
            if (e is null) return Bad($"OpenEms.GridLines.File '{file}' has no {tags[k]}.");
            axes[k] = [];
            foreach (string t in e.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                    return Bad($"OpenEms.GridLines.File '{file}''s {tags[k]} holds '{t}', which is not a number.");
                axes[k].Add(cylindrical && k == 1 ? v : v * delta);
            }
        }
        return new(axes[0], axes[1], axes[2], name) { Cylindrical = cylindrical };
    }
}

/// <summary>
/// brief-em3d-9 R-em3d9-6 — the openEMS section's RUN fields, resolved. <b>The defaults live here and
/// nowhere else</b>; the grid's live in <see cref="CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default"/>.
/// </summary>
/// <param name="EndCriterionDb">The port-signal decay a run stops at, dB below peak (negative).</param>
/// <param name="MaxTimeSteps">The step ceiling; null takes <see cref="DefaultStepsFactor"/> times the
/// grid's own estimate (<see cref="CircuitRF.Engine.Em3d.FdtdGridResult.Steps"/>).</param>
public sealed record OpenEmsRunSettings(double EndCriterionDb, long? MaxTimeSteps)
{
    /// <summary>
    /// −50 dB is openEMS's own customary end criterion and F0's (em-3d-f0-findings.md Q11). The ceiling
    /// is ten times the grid's estimate of the pulse plus six pulse lengths of ring-down, which both F0
    /// cases met (6.8 and 7.0 pulses), so a run that reaches it has rung for seventy.
    /// </summary>
    public static OpenEmsRunSettings Default { get; } = new(-50, null);

    /// <summary>brief-em3d-29 R-em3d29-6a — the field-dump frequencies, GHz: null is the sweep's centre,
    /// empty dumps none.</summary>
    public IReadOnlyList<double>? SaveFieldsGHz { get; init; }

    /// <summary>brief-em3d-82 R-em3d82-4 — dump H beside E at each saved frequency. Off by default: twice the disk.</summary>
    public bool SaveH { get; init; }

    /// <summary>brief-sar-3 R-sar3-4 — write <c>CellConstantMaterial="1"</c>. Off by default: what every run wrote before it.</summary>
    public bool CellConstantMaterial { get; init; }

    /// <summary>brief-sar-6 R-sar6-2 — the SAR the run is asked for, resolved against its problem: the tissue's densities are
    /// written and the raw SAR and B1 dumps added. Null — every run without a Sar group — writes exactly what it wrote before.</summary>
    public CircuitRF.Design.Em3d.Em3dSarPlan? Sar { get; init; }

    /// <summary>The default ceiling as a multiple of the grid's step estimate.</summary>
    public const int DefaultStepsFactor = 10;

    /// <summary>The ceiling for a grid that estimates <paramref name="estimatedSteps"/>.</summary>
    public long StepCeiling(long estimatedSteps) => MaxTimeSteps ?? Math.Max(1, estimatedSteps) * DefaultStepsFactor;

    /// <summary>Every value that cannot be run, as sentences naming the field — empty when all can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!(EndCriterionDb < 0) || !(EndCriterionDb >= -200))
            p.Add($"OpenEms.EndCriterionDb is {EndCriterionDb.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}; " +
                  "it must be a decay below 0 dB, and at least −200.");
        if (MaxTimeSteps is { } m && m < 1)
            p.Add($"OpenEms.MaxTimeSteps is {m}; it must be at least 1.");
        foreach (double f in SaveFieldsGHz ?? [])
            if (!(f > 0) || double.IsInfinity(f))
            {
                p.Add($"OpenEms.SaveFieldsGHz holds {f}; every save frequency must be a positive number of GHz.");
                break;
            }
        return p;
    }
}

// ── brief-em3d-73 R-em3d73-5 — the thermal setup's file types ──────────────────────────────────────
//
// Declared here for R-em3d3-3c's reason: the generated reference page expands a type only when it lives in CemFile's own
// assembly. EVERY VALUE THE USER SWEEPS IS AN EXPRESSION in the document's variables (brief 51's machinery), so a power, a
// temperature or an h sweeps like any other variable; the solver's own controls (Mesh, Balance) are numbers.

/// <summary>What a thermal boundary does to its face.</summary>
public enum ThermalBoundaryKind
{
    /// <summary>The face is held at <c>TempC</c>.</summary>
    FixedT,
    /// <summary>Heat leaves the face at <c>H</c>·(T − <c>AmbientC</c>).</summary>
    Convection,
}

/// <summary>One heat source's power in this setup, overriding the document's default for it.</summary>
public sealed class CemThermalSource
{
    /// <summary>A heat source of the document, by name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The power, an expression (<c>Pdiss</c>, <c>0.5 W</c>), in the source's own Density unit.</summary>
    public string Power { get; set; } = "";

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>A condition on a face. Every face no boundary names is insulated (adiabatic).</summary>
public sealed class CemThermalBoundary
{
    /// <summary>A named face, <c>object/face</c>, or <c>*exposed*</c>: every face that touches no other solid and no other
    /// boundary names.</summary>
    public string Face { get; set; } = "";

    /// <summary><c>FixedT</c> or <c>Convection</c>.</summary>
    public ThermalBoundaryKind Kind { get; set; }

    /// <summary>FixedT: the face's temperature, °C, an expression.</summary>
    public string? TempC { get; set; }

    /// <summary>Convection: the heat-transfer coefficient, W/(m²·K), an expression.</summary>
    public string? H { get; set; }

    /// <summary>Convection: the ambient temperature, °C, an expression.</summary>
    public string? AmbientC { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>One sweep axis: a document variable from <c>Start</c> to <c>Stop</c> in <c>Points</c> linear steps. A variable
/// any geometry expression reads is refused in this version (a geometric sweep re-meshes per point).</summary>
public sealed class CemThermalSweep
{
    /// <summary>The variable, a VAR (or cell parameter) of the document.</summary>
    public string Var { get; set; } = "";

    /// <summary>The first value, an expression.</summary>
    public string Start { get; set; } = "";

    /// <summary>The last value, an expression.</summary>
    public string Stop { get; set; } = "";

    /// <summary>How many values, at least 1.</summary>
    public int Points { get; set; } = 1;

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>The thermal mesh's controls. Every field may be omitted; brief 74 states the defaults.</summary>
public sealed class CemThermalMesh
{
    /// <summary>The element order, 1 or 2.</summary>
    public int? Order { get; set; }

    /// <summary>Elements across a heat source's smallest dimension.</summary>
    public double? SizeFromSources { get; set; }

    /// <summary>Elements through the thinnest solid's thickness, at least.</summary>
    public int? MinThroughThickness { get; set; }

    /// <summary>How fast elements may grow away from a refined region: the ratio between neighbours.</summary>
    public double? Grading { get; set; }

    /// <summary>brief-em3d-74 — <c>Direct</c> or <c>Iterative</c>; omitted, chosen by the number of unknowns (direct below
    /// 5,000).</summary>
    public ThermalMeshSolver? Solver { get; set; }

    /// <summary>brief-em3d-74 R-em3d74-5e — mesh a second time with every size × 0.7, solve the first sweep point, and report
    /// how far each probe moved. Off when omitted.</summary>
    public bool? Check { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>brief-em3d-74 — the thermal solve's linear solver.</summary>
public enum ThermalMeshSolver
{
    /// <summary>A sparse Cholesky (LU for a Newton step).</summary>
    Direct,
    /// <summary>Conjugate gradients with smoothed-aggregation multigrid (BiCGStab for a Newton step).</summary>
    Iterative,
}

/// <summary>
/// brief-em3d-76 R-em3d76-3a — a submodel: solve (or reuse the current result of) setup <see cref="From"/>, cut the model to
/// mesh region <see cref="Region"/>'s box, mesh it at that region's size, fix the CUT faces to the <see cref="From"/>
/// solution interpolated at their nodes, keep every other boundary as written, and solve. It runs at the points the
/// <see cref="From"/> result carries, so it states no Sweep of its own.
/// </summary>
public sealed class CemThermalSubmodel
{
    /// <summary>The thermal setup whose solution fixes the cut faces — the coarse, whole-model one.</summary>
    public string From { get; set; } = "";

    /// <summary>The mesh region whose box is the submodel.</summary>
    public string Region { get; set; } = "";

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-77 R-em3d77-1a — a port's current in a thermal setup: it enters the port's positive conductor and leaves its
/// negative one, each through an equipotential contact (the face the port's edge lies on, or the one named here). A port no
/// entry names carries nothing.
///
/// <para>brief-em3d-78 R-em3d78-1/-2 — and its HARMONIC currents: at RF a port's current is not solved through the metal but
/// assigned to the one wire array with an end on the port's positive conductor, and shared among that array's wires by their
/// inductance. <see cref="Array"/> in place of <see cref="Port"/> states an array's harmonics directly (no Dc: a DC current is
/// a port's, and the conduction solve shares it).</para>
/// </summary>
public sealed class CemThermalCurrent
{
    /// <summary>The port's number; null for an <see cref="Array"/> entry.</summary>
    public int? Port { get; set; }

    /// <summary>brief-em3d-78 R-em3d78-2 — a wire array's name, in place of <see cref="Port"/>: its harmonics go to that array.</summary>
    public string? Array { get; set; }

    /// <summary>brief-em3d-78 R-em3d78-1 — the fundamental frequency, Hz (an expression with a unit, or a bare number in Hz);
    /// required when <see cref="Harmonics"/> is not empty.</summary>
    public string? F0 { get; set; }

    /// <summary>brief-em3d-78 R-em3d78-1 — the harmonic currents, each at N × F0, each stated Peak or Rms (never inferred).</summary>
    public List<CemThermalHarmonic>? Harmonics { get; set; }

    /// <summary>The DC current, amperes, an expression (<c>Id</c>, <c>2.5</c>): sweepable like any value.</summary>
    public string? Dc { get; set; }

    /// <summary>The face the current enters through, <c>object/face</c>; omitted, the smallest face of the positive conductor
    /// the port's edge lies on.</summary>
    public string? EnterFace { get; set; }

    /// <summary>The face the current leaves through; omitted, inferred on the negative conductor as <see cref="EnterFace"/> is.</summary>
    public string? LeaveFace { get; set; }

    /// <summary>brief-em3d-79 R-em3d79-1 — every port's currents from a circuit's harmonic-balance run instead: the pins of the
    /// instance whose S-parameter model is this 3D view's EM result, at every point of the HB's sweep. An entry with this
    /// states nothing else, and no other entry of the setup states a Dc or Harmonics.</summary>
    public CemThermalFromCircuit? FromCircuit { get; set; }

    /// <summary>What a later version states here, carried as written.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? More { get; set; }

    /// <summary>What the entry names: <c>port 2</c>, <c>array 'D1'</c> or <c>the circuit 'pa.csch'</c>.</summary>
    [JsonIgnore]
    public string Subject => FromCircuit is { } fc ? $"the circuit '{fc.Schematic}'" : Array is { } a ? $"array '{a}'" : $"port {Port}";
}

/// <summary>
/// brief-em3d-79 R-em3d79-1 — a thermal setup's link to a circuit: run <see cref="Analysis"/> of <see cref="Schematic"/> (a
/// harmonic-balance analysis, or the <c>parametric_sweep</c> wrapping one — a named inner analysis is promoted to its wrapper,
/// as the <c>hb</c> verb promotes it), and take the pin currents of <see cref="Instance"/> — the instance whose S-parameter
/// model is this 3D view's EM result. <b>Port p is pin p.</b> The thermal run is solved at every point of the HB's sweep, on
/// the HB's own axes. One way only: nothing goes back into the circuit.
/// </summary>
public sealed class CemThermalFromCircuit
{
    /// <summary>A <c>.csch</c> or <c>.cnl</c>, relative to the <c>.c3d</c>.</summary>
    public string Schematic { get; set; } = "";

    /// <summary>The HB analysis, or its <c>parametric_sweep</c> wrapper; omitted, the circuit's one HB chain.</summary>
    public string? Analysis { get; set; }

    /// <summary>The instance whose S-parameter model is this 3D view's EM result; omitted, the one such instance.</summary>
    public string? Instance { get; set; }

    /// <summary>HB cubes (measurements first, then the engine's) copied into the thermal result on the HB's axes; omitted,
    /// every scalar measure of the HB testbench.</summary>
    public List<string>? Carry { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>brief-em3d-78 R-em3d78-1 — how a harmonic's amplitude is stated. Internally every harmonic is a peak phasor.</summary>
public enum ThermalAmplitude
{
    /// <summary>The phasor's peak magnitude.</summary>
    Peak,
    /// <summary>Its RMS: the peak ÷ √2.</summary>
    Rms,
}

/// <summary>brief-em3d-78 R-em3d78-1 — one harmonic of a thermal current: at <see cref="N"/> × F0, amplitude <see cref="Amp"/>
/// amperes stated <see cref="As"/>. Phase is not taken: harmonics are orthogonal and add in power.</summary>
public sealed class CemThermalHarmonic
{
    /// <summary>The harmonic number, at least 1.</summary>
    public int N { get; set; } = 1;

    /// <summary>The amplitude, amperes, an expression.</summary>
    public string? Amp { get; set; }

    /// <summary>Peak or Rms — required; null is a missing statement, which <c>check</c> refuses.</summary>
    public ThermalAmplitude? As { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>Conductive balance: the Newton loop over σ(T) and k(T). Each switch defaults on.</summary>
public sealed class CemThermalBalance
{
    /// <summary>Electrical conductivity follows temperature. Off: the 20 °C value everywhere.</summary>
    public bool? SigmaOfT { get; set; }

    /// <summary>Thermal conductivity follows temperature. Off: the nominal value everywhere.</summary>
    public bool? KOfT { get; set; }

    /// <summary>The relative residual at which the loop stops.</summary>
    public double? Tolerance { get; set; }

    /// <summary>The most Newton steps.</summary>
    public int? MaxIterations { get; set; }

    /// <summary>brief-em3d-85 — a conductor (a die pad, a lead) whose resistance at 20 °C is below this fraction of the
    /// least-resistive wire's is solved as ONE potential, its Joule heat zero; omitted, 0.01. 0 solves every conductor in 3D.
    /// The run's notes name each conductor, which way it went, and its resistance.</summary>
    public double? EquipotentialBelow { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>brief-em3d-80 R-em3d80-1a — which statistic of a source's own place its rise in the Rth matrix is read as.</summary>
public enum ThermalRthStat
{
    /// <summary>The mean over the source's place: the matrix is symmetric.</summary>
    Avg,
    /// <summary>The hottest point of the source's place: not symmetric in general.</summary>
    Max,
}

/// <summary>
/// brief-em3d-80 R-em3d80-1 — the Rth matrix across heat sources: R_ij, the rise of source i's place per watt in source j, with
/// every boundary made homogeneous (fixed temperatures and ambients 0), so it is a property of the structure.
/// </summary>
public sealed class CemThermalRth
{
    /// <summary>The heat sources, by name, or <c>"*"</c> (also when omitted): every heat source of the document.</summary>
    [JsonConverter(typeof(ThermalNamesConverter))]
    public List<string>? Sources { get; set; }

    /// <summary><c>Avg</c> (omitted) or <c>Max</c>.</summary>
    public ThermalRthStat? Stat { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-80 R-em3d80-2 — Z_th(jω) from (K + jωC)·T = q: every source's own impedance, and each probe's per watt in each
/// source. Every meshed material needs DensityKgM3 and SpecificHeat. A Foster network is fitted to each and written as a
/// <c>.cnl</c> subcircuit per source.
/// </summary>
public sealed class CemThermalZth
{
    /// <summary>The heat sources driven, by name, or <c>"*"</c> (also when omitted).</summary>
    [JsonConverter(typeof(ThermalNamesConverter))]
    public List<string>? Sources { get; set; }

    /// <summary>Probes read per watt in each source (their mean — a phasor has no maximum); omitted, the sources' own places only.</summary>
    public List<string>? Probes { get; set; }

    /// <summary>The lowest frequency, Hz, an expression; omitted, 0.01.</summary>
    public string? StartHz { get; set; }

    /// <summary>The highest frequency, Hz, an expression; omitted, 1e6.</summary>
    public string? StopHz { get; set; }

    /// <summary>Frequencies per decade, logarithmic; omitted, 10. DC is always the first point.</summary>
    public int? PerDecade { get; set; }

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-em3d-80 R-em3d80-4 — a periodic rectangular pulse train through the fitted Z_th: the peak, single-pulse and average
/// temperature of every Z_th place. Needs <see cref="CemThermal.Zth"/>. Every field is an expression, so a duty or a period
/// sweeps like any value.
/// </summary>
public sealed class CemThermalPulse
{
    /// <summary>The total power during the pulse, W; the sources share it in proportion to their own powers. Omitted, every
    /// source at its own power.</summary>
    public string? PeakPower { get; set; }

    /// <summary>The period, seconds: an expression, optionally ending in a spaced <c>s</c>, <c>ms</c>, <c>us</c> or <c>ns</c>
    /// (<c>1 ms</c>).</summary>
    public string Period { get; set; } = "";

    /// <summary>The on-fraction of the period, 0 to 1.</summary>
    public string Duty { get; set; } = "";

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>
/// brief-emi-1 R-emi1-3 — the key <c>Solver3D</c>: a solver's spelling (<c>Palace</c>, <c>OpenEms</c>), <c>Both</c> or (brief-emi-6)
/// <c>Compare</c>, read in any case as the enum it replaced read them, and written back in the canonical spelling, so a file
/// opened and saved with no edit is byte-identical. A spelling no solver answers to — a solver whose integration was removed —
/// is refused with the list of the ones that do.
/// </summary>
public sealed class Em3dSolverSpellingConverter : JsonConverter<Em3dSolverSpelling?>
{
    public override Em3dSolverSpelling? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException(CircuitRF.Design.Em3d.Em3dSolvers.UnknownSpelling(reader.TokenType.ToString()));
        string spelling = reader.GetString() ?? "";
        if (CircuitRF.Design.Em3d.Em3dSolvers.IsCompareSpelling(spelling)) return new(CircuitRF.Design.Em3d.Em3dSolvers.CompareSpelling);
        return CircuitRF.Design.Em3d.Em3dSolvers.FromSpelling(spelling) is { } choice
            ? new(CircuitRF.Design.Em3d.Em3dSolvers.Spelling(choice))
            : throw new JsonException(CircuitRF.Design.Em3d.Em3dSolvers.UnknownSpelling(spelling));
    }

    public override void Write(Utf8JsonWriter writer, Em3dSolverSpelling? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.Text);
    }
}

/// <summary>brief-emi-6 R-emi6-1 — what <c>"Solver3D": "Compare"</c> runs: two or more solvers by their <c>Solver3D</c>
/// spellings, and the one the others are compared against. Written only beside <c>Compare</c>.</summary>
/// <summary>
/// brief-emb-1 R-emb1-2 — <c>ComponentPorts</c>: the string <c>"*"</c>, or a list of refdes strings and objects. A lone <c>*</c> with
/// no override is written back as the string; any other list keeps each plain entry a string.
/// </summary>
public sealed class ComponentPortsConverter : JsonConverter<List<EmComponentPort>?>
{
    public override List<EmComponentPort>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return [new EmComponentPort(reader.GetString() ?? "")];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("ComponentPorts is \"*\" or a list of reference designators.");
        var list = new List<EmComponentPort>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String) { list.Add(new EmComponentPort(reader.GetString() ?? "")); continue; }
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("A ComponentPorts entry is a reference designator or an object with Refdes, Z0, Reference and WidthUm.");
            using var doc = JsonDocument.ParseValue(ref reader);
            string? Get(string key)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.GetRawText(),
                            _ => null,
                        };
                return null;
            }
            double? width = Get("WidthUm") is { } w && double.TryParse(w, System.Globalization.NumberStyles.Float,
                                                                       System.Globalization.CultureInfo.InvariantCulture, out double wv) ? wv : null;
            list.Add(new EmComponentPort(Get("Refdes") ?? "", Get("Z0"), Get("Reference"), width));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<EmComponentPort>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (value is [{ Refdes: EmSetup.AllComponents, IsPlain: true }]) { writer.WriteStringValue(EmSetup.AllComponents); return; }
        writer.WriteStartArray();
        foreach (var c in value)
        {
            if (c.IsPlain) { writer.WriteStringValue(c.Refdes); continue; }
            writer.WriteStartObject();
            writer.WriteString("Refdes", c.Refdes);
            if (c.Z0 is { } z) writer.WriteString("Z0", z);
            if (c.Reference is { } r) writer.WriteString("Reference", r);
            if (c.WidthUm is { } w) writer.WriteNumber("WidthUm", w);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

public sealed class CemCompare
{
    /// <summary>The solvers, by spelling (<c>Palace</c>, <c>OpenEms</c>, <c>Emerge</c>); run in circuitRF's solver order.</summary>
    public List<string>? Solvers { get; set; }

    /// <summary>The solver the others are compared against. Omitted is Palace when it is one of them, else the first.</summary>
    public string? Reference { get; set; }
}

/// <summary>brief-em3d-80 — a list of names that may be written as the one string <c>"*"</c>.</summary>
public sealed class ThermalNamesConverter : JsonConverter<List<string>?>
{
    /// <summary>The spelling of "every heat source".</summary>
    public const string All = "*";

    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString() ?? ""];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a name list or \"*\"");
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            list.Add(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : throw new JsonException("a name is a string"));
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (value is [All]) { writer.WriteStringValue(All); return; }
        writer.WriteStartArray();
        foreach (string s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>
/// brief-em3d-73 R-em3d73-5b — a thermal setup's section: its values, over the document's places. Only a setup whose
/// <c>Problem3D</c> is <c>Thermal</c> reads it, and only a <c>.c3d</c> embeds one (D1).
/// </summary>
public sealed class CemThermal
{
    /// <summary>Power overrides by source name. A source this list does not name uses its own default.</summary>
    public List<CemThermalSource>? Sources { get; set; }

    /// <summary>Fixed-temperature and convection faces; at least one is required — a problem with neither has no steady
    /// state.</summary>
    public List<CemThermalBoundary>? Boundaries { get; set; }

    /// <summary>brief-em3d-77 — DC currents at the ports: each port's current flows through the model's metal and its bond wires,
    /// which are solved as 1D elements, and heats them. brief-em3d-78: and harmonic currents, which heat the wires only.</summary>
    public List<CemThermalCurrent>? Currents { get; set; }

    /// <summary>brief-em3d-77 R-em3d77-3e — heat a bond wire loses where it runs through AIR, W/(m²·K) over its surface, an
    /// expression; omitted, none. A wire in a mould compound loses heat into it whatever this says.</summary>
    public string? WireConvectionH { get; set; }

    /// <summary>The ambient <see cref="WireConvectionH"/> loses heat to, °C, an expression; required with it.</summary>
    public string? WireAmbientC { get; set; }

    /// <summary>brief-em3d-77 R-em3d77-3d — a bond's interface resistance, m²·K/W, an expression; omitted, a perfect bond (the
    /// heel takes its pad's temperature). Stated, the foot is coupled to its pad along its length.</summary>
    public string? BondThermalResistance { get; set; }

    /// <summary>A bond's specific contact resistance, Ω·m², an expression; omitted, a perfect bond.</summary>
    public string? BondElectricalResistance { get; set; }

    /// <summary>Up to two axes, a product.</summary>
    public List<CemThermalSweep>? Sweep { get; set; }

    /// <summary>Named expressions over probe results — <c>Rth = (Tmax(die_top) - Tavg(flange_bot)) / Pdiss</c> — using
    /// <c>Tmax</c>, <c>Tmin</c>, <c>Tavg</c> and <c>T</c> of a probe, and any variable.</summary>
    public List<string>? Measures { get; set; }

    /// <summary>The mesh's controls.</summary>
    public CemThermalMesh? Mesh { get; set; }

    /// <summary>Conductive balance's switches and tolerances.</summary>
    public CemThermalBalance? Balance { get; set; }

    /// <summary>brief-em3d-76 R-em3d76-3a — a two-step solve: this setup solves only a mesh region's box, finely, with its cut
    /// faces fixed to another thermal setup's solution. Omitted: the whole model.</summary>
    public CemThermalSubmodel? Submodel { get; set; }

    /// <summary>brief-em3d-80 R-em3d80-1 — the Rth matrix across heat sources. Omitted: none.</summary>
    public CemThermalRth? Rth { get; set; }

    /// <summary>brief-em3d-80 R-em3d80-2 — Z_th(jω), its Foster fits and their <c>.cnl</c> networks. Omitted: none.</summary>
    public CemThermalZth? Zth { get; set; }

    /// <summary>brief-em3d-80 R-em3d80-4 — a radar pulse train's temperatures, from the Z_th fits. Omitted: none.</summary>
    public CemThermalPulse? Pulse { get; set; }

    /// <summary>A deep copy, through the setup's own serializer.</summary>
    public CemThermal Clone() => JsonSerializer.Deserialize<CemThermal>(JsonSerializer.Serialize(this))!;

    /// <summary>Keys this build does not read — a misspelt one among them. Kept and written back; <c>check</c> names each.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }
}

/// <summary>brief-sdl-5: one entry of <see cref="CemFile.SchematicPortZ0s"/>.</summary>
public sealed class CemSchematicPortZ0
{
    public int Port { get; set; }
    public double Real { get; set; }
    public double Imag { get; set; }
}

public sealed class CemFile
{
    public int    FormatVersion { get; set; } = 1;
    public string Name          { get; set; } = "";
    public string LayoutRef     { get; set; } = "";
    public string? SignalStackupLayerName { get; set; }

    /// <summary>
    /// RP-1: the conductor stackup entry every port returns through. <b>Null when unused</b>
    /// (WhenWritingNull), so a <c>.cem</c> written before RP-1 loads unchanged and a setup that
    /// overrides nothing re-serialises byte-identically — no <c>FormatVersion</c> bump, exactly as
    /// <see cref="AnalysisLevelNames"/> and <see cref="PortZ0s"/> already do.
    /// </summary>
    public string? GroundStackupLayerName { get; set; }

    public CemFrequency Frequency { get; set; } = new();

    /// <summary>GitHub discussion 26 — the plan's further segments (<see cref="EmSetup.AdditionalFrequencies"/>). Null
    /// when there are none (WhenWritingNull), so a single-sweep <c>.cem</c> is written exactly as before.</summary>
    public List<CemFrequency>? AdditionalFrequencies { get; set; }

    public double Port1Z0Real { get; set; } = 50;
    public double Port1Z0Imag { get; set; }
    public double Port2Z0Real { get; set; } = 50;
    public double Port2Z0Imag { get; set; }

    /// <summary>
    /// R-cpl-6: optional per-port reference impedances in D3 order, as flat [re, im] pairs.
    /// <b>Null when unused</b> (WhenWritingNull), so a .cem written before L7b loads unchanged and a
    /// setup that overrides nothing re-serializes byte-identically — which is why this is additive
    /// alongside Port1Z0/Port2Z0 rather than replacing them.
    /// </summary>
    public List<double>? PortZ0s { get; set; }

    /// <summary>brief-sdl-5: <see cref="EmSetup.SchematicPortZ0s"/> — what Update Layout from Schematic last wrote, by
    /// port number. <b>Null when empty</b> (WhenWritingNull), so a setup it never wrote to is written exactly as
    /// before.</summary>
    public List<CemSchematicPortZ0>? SchematicPortZ0s { get; set; }

    /// <summary>
    /// Optional per-port TYPE (edge / internal delta gap) in extractor order, written as enum names
    /// by the shared <c>JsonStringEnumConverter</c>. <b>Null when every port is an edge port</b>,
    /// which is the normal case and what every <c>.cem</c> written before internal ports existed
    /// means — so such a file loads and re-serialises byte-identically, exactly as
    /// <see cref="PortZ0s"/> does.
    /// </summary>
    public List<PlanarPortKind>? PortKinds { get; set; }

    /// <summary>
    /// L9d/D5: which conductor stackup entries the planar analysis includes, bottom-to-top.
    /// <b>Null when unused</b> (WhenWritingNull) — a <c>.cem</c> that never named its levels writes
    /// no field and re-serialises byte-identically, exactly as <see cref="PortZ0s"/> does.
    /// </summary>
    public List<string>? AnalysisLevelNames { get; set; }

    public CemMesh Mesh { get; set; } = new();

    /// <summary>
    /// <b>Non-nullable on purpose, unlike every other flag here.</b> The model's default flipped to
    /// <c>true</c>, and every <c>.cem</c> ever written carries this field explicitly — so an
    /// existing setup keeps whatever it recorded and only a newly created one picks the new default
    /// up. Making it nullable-and-omitted would silently change the answer for every file on disk.
    /// </summary>
    public bool    DispersionCorrection  { get; set; }

    /// <summary>
    /// <b>Null means ON</b> — the opposite polarity to the flags around it, because the default is
    /// on. A <c>.cem</c> written before adaptive sampling existed has no field, loads with it
    /// enabled, and re-serialises with no field; only an explicit opt-OUT is ever written.
    /// </summary>
    public bool?   AdaptiveSampling      { get; set; }

    /// <summary>
    /// ANT-9 — <b>null means off</b>, which is what every <c>.cem</c> written before the resonance
    /// search existed means. Same nullable + omit-at-default rule as <see cref="DirectVerticalKernel"/>,
    /// so such a file loads AND re-serialises byte-identically.
    /// </summary>
    public bool?   ResonanceSearch       { get; set; }

    /// <summary>
    /// M2 — <b>null means off</b>, which is what every <c>.cem</c> written before it means. Nullable
    /// + <c>WhenWritingNull</c> (the document-wide default) so such a file loads AND re-serialises
    /// byte-identically, exactly as <see cref="AnalysisKind"/> and <see cref="PlanarMesh"/> do.
    /// </summary>
    public bool?   DirectVerticalKernel  { get; set; }

    /// <summary>
    /// M5's accelerator — <b>null means off</b>, which is what every <c>.cem</c> written before it was
    /// reachable means. Same nullable + omit-at-default rule as
    /// <see cref="DirectVerticalKernel"/> directly above, so such a file loads AND re-serialises
    /// byte-identically. That is an asserted property of this format, not a nicety.
    /// </summary>
    public bool?   AcceleratedSolve      { get; set; }

    /// <summary>
    /// The planar run's conductor-thickness correction (<c>PlanarThicknessCorrection</c>) — <b>null
    /// means off</b>, which is what every <c>.cem</c> written before it existed means. Same nullable +
    /// omit-at-default rule as <see cref="AcceleratedSolve"/>, so such a file loads AND re-serialises
    /// byte-identically.
    /// </summary>
    public bool?   ConductorThicknessCorrection { get; set; }

    /// <summary>
    /// LFE — the planar edge port's return (<c>EmSetup.EdgePortReturn</c>). <b>Null means the
    /// default</b> (<c>Ground</c>), and is what every file written before LFE carries — which means an
    /// older file now runs the grounded port. That is the point of LFE rather than an accident of it:
    /// the series gap's low-frequency error is a defect, not a preference a file chose. A file can
    /// still ask for <c>"SeriesGap"</c> to reproduce a pre-LFE result.
    /// </summary>
    public PlanarEdgePortReturn? EdgePortReturn { get; set; }

    /// <summary>
    /// ANT-12's radiation pattern — <b>null means off</b>, which is what every <c>.cem</c> written
    /// before the far field was reachable means. Same nullable + omit-at-default rule as
    /// <see cref="AcceleratedSolve"/> directly above, so such a file loads AND re-serialises
    /// byte-identically.
    /// </summary>
    public bool?   RadiationPattern      { get; set; }

    /// <summary>brief-dfr34-2 — the frequency the pattern is read at, GHz (<see cref="EmSetup.PatternFrequencyGHz"/>);
    /// omitted when null, so a file written before it existed loads and re-serialises byte-identically.</summary>
    public double? PatternFrequencyGHz   { get; set; }

    /// <summary>The frequency a driven 3D run sizes its mesh at, GHz (<see cref="EmSetup.MeshFrequencyGHz"/>); omitted when null, the
    /// sweep's top.</summary>
    public double? MeshFrequencyGHz      { get; set; }

    /// <summary>
    /// <b>LEGACY, read-only. The switch this came from has been removed — see
    /// <see cref="EmSetup.LegacyRawSolveRequested"/> for the measurement that removed it.</b>
    ///
    /// <para>Still deserialised so a file carrying <c>"Deembed": false</c> opens rather than being
    /// refused, and so the run can WARN about it rather than silently changing what the file asked
    /// for. Never written back: <see cref="ToFile"/> does not set it, so the field leaves the
    /// document on the next save.</para>
    /// </summary>
    public bool?   Deembed               { get; set; }

    /// <summary>
    /// PCAL2/R-pcal2-2 — <b>null means off</b>, which is what every <c>.cem</c> written before the
    /// clearance refusal existed means and what the refusal itself means. Same nullable +
    /// omit-at-default rule as <see cref="AcceleratedSolve"/>, so such a file loads AND
    /// re-serialises byte-identically.
    /// </summary>
    public bool?   DeembedOutsideCalibrationValidity { get; set; }

    /// <summary>The dBm TRP and peak EIRP are referenced to. Same nullable + omit-at-default rule
    /// as <see cref="RadiationPattern"/> above: 0 dBm is the default and writes nothing, so a
    /// `.cem` from before this existed loads AND re-serialises byte-identically.</summary>
    public double? ReferenceInputPowerDbm { get; set; }
    public string? SnpOutputPathOverride { get; set; }

    /// <summary>brief-dfr31-4 — <b>null means off</b>; written only when on, so a file written before it existed loads
    /// AND re-serialises byte-identically.</summary>
    public bool?   CreateSnpSchematic    { get; set; }

    /// <summary>
    /// D7 — <b>null means the cross-section analysis</b>, which is what every <c>.cem</c> written
    /// before L8b means. Nullable + <c>WhenWritingNull</c> so such a file loads AND re-serialises
    /// byte-identically; a setup that has never been switched to the planar kernel writes neither
    /// this field nor <see cref="PlanarMesh"/>.
    /// </summary>
    public EmAnalysisKind? AnalysisKind { get; set; }

    /// <summary>Null when unused, for the same byte-identity reason as <see cref="AnalysisKind"/>.</summary>
    public CemPlanarMesh? PlanarMesh { get; set; }

    /// <summary>
    /// <b>Null means the whole layout</b>, which is what every <c>.cem</c> written before the solve
    /// region existed means — so such a file loads AND re-serialises byte-identically, the same
    /// omit-at-default rule every field added after the first release follows.
    /// </summary>
    public CemSolveRegion? SolveRegion { get; set; }

    /// <summary>
    /// brief-em3d-3 — <b>null means None</b>: a planar setup, which is what every <c>.cem</c> written
    /// before 3D existed means. Anything else makes this a 3D setup and the planar-only fields above
    /// are kept but not read. Chosen by name only; Auto never resolves to a 3D solver. brief-emi-1 R-emi1-3 — spelt
    /// <c>Palace</c>, <c>OpenEms</c> or <c>Both</c> exactly as before the solver seam, or <c>Compare</c> (<see cref="Em3dSolverSpellingConverter"/>).
    /// </summary>
    [JsonConverter(typeof(Em3dSolverSpellingConverter))]
    public Em3dSolverSpelling? Solver3D { get; set; }

    /// <summary>brief-emi-6 R-emi6-1 — the solvers and reference of <c>"Solver3D": "Compare"</c>. Null for every other choice,
    /// <c>Both</c> included.</summary>
    public CemCompare? Compare { get; set; }

    /// <summary>The temperature a 3D setup evaluates every conductor's σ at, °C. <b>Null means
    /// 20 °C.</b> Read by 3D setups only.</summary>
    public double? OperatingTempC { get; set; }

    /// <summary>A 3D setup's air box, per face. Null takes the default on every face.</summary>
    public CemAirBox? AirBox { get; set; }

    /// <summary>brief-dfr28-3 — <see cref="EmSetup.RobustMeshing"/>. brief-emb-2 R-emb2-3: <b>null means ON</b> — a setup that
    /// does not state it is robust, as every setup the GUI makes already was — so only <c>false</c> is ever written.</summary>
    public bool? RobustMeshing { get; set; }

    /// <summary>EM-TFR — <see cref="EmSetup.Conductors"/>. Omitted at Auto, which is what every setup written before it means.</summary>
    public Em3dConductorMode? Conductors { get; set; }

    /// <summary>Palace's own section. Null takes the defaults.</summary>
    public CemPalace? Palace { get; set; }

    /// <summary>openEMS's own section. Null takes the defaults.</summary>
    public CemOpenEms? OpenEms { get; set; }

    /// <summary>brief-emi-4 R-emi4-1 — EMerge's own section, spelt <c>EMerge</c>. Null takes the defaults, and an empty one
    /// is never written.</summary>
    [JsonPropertyName("EMerge")]
    public CemEmerge? Emerge { get; set; }

    /// <summary>brief-em3d-22 — what a 3D setup solves: Driven (S over the sweep), Electrostatic (a
    /// capacitance matrix) or Magnetostatic (an inductance matrix). <b>Null means Driven.</b> The two
    /// static problems run on Palace only.</summary>
    public CircuitRF.Engine.Em3d.Em3dProblemType? Problem3D { get; set; }

    /// <summary>A static 3D setup's terminals, in matrix order. Null when there are none.</summary>
    public List<CemTerminal3D>? Terminals3D { get; set; }

    /// <summary>The net that is a static solve's reference. Null is the ground-reference conductors
    /// (and the PEC floor when there is one).</summary>
    public string? Ground3D { get; set; }

    /// <summary>brief-dfr31-1 R-dfr31-1-5 — what an electrostatic solve does with metal in no terminal and not ground. Null is
    /// Float, the default, so a file written before it gains no byte.</summary>
    public EmUnassignedConductors? UnassignedConductors3D { get; set; }

    /// <summary>brief-emb-1 R-emb1-2 — the component ports a 3D setup builds on its layout's placed parts: <c>"*"</c> for every
    /// two-terminal part, or a list whose entries are a refdes or <c>{ "Refdes", "Z0", "Reference" }</c>. Null when there are none.</summary>
    [JsonConverter(typeof(ComponentPortsConverter))]
    public List<EmComponentPort>? ComponentPorts { get; set; }

    /// <summary>brief-em3d-23 — per-port 3D settings (the port's kind, a wave port's extent and offset).
    /// Null when every port is lumped with nothing stated.</summary>
    public List<CemPort3D>? Ports3D { get; set; }

    /// <summary>brief-em3d-23 — an eigenmode solve's mode count and target. Null takes both defaults.</summary>
    public CemEigenmode? Eigenmode { get; set; }

    /// <summary>brief-em3d-73 — a thermal setup's section (read when <c>Problem3D</c> is <c>Thermal</c>). Null when there is
    /// none.</summary>
    public CemThermal? Thermal { get; set; }

    /// <summary>brief-sar-6 R-sar6-1 — SAR and B1 as results of a driven openEMS run. Null when the setup asks for none.</summary>
    public CemSar? Sar { get; set; }
}

/// <summary>Reads and writes <c>.cem</c> files. Framework-free (no Avalonia / Skia).</summary>
public static class EmSetupPersistence
{
    public const string Extension = ".cem";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public static string Serialize(EmSetup setup)
        => JsonSerializer.Serialize(ToFileModel(setup), JsonOpts);

    public static void SaveToFile(string path, EmSetup setup)
        => AtomicFile.WriteAllText(path, Serialize(setup));

    public static EmSetup Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<CemFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .cem file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".cem format_version {file.FormatVersion} is newer than expected " +
                $"{CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static EmSetup LoadFromFile(string path)
    {
        var setup = Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));
        // brief-sar-3 R-sar3-5 — a relative grid file is read beside the .cem that names it.
        setup.OpenEms = CemOpenEms.Anchored(setup.OpenEms, path);
        return setup;
    }

    /// <summary>
    /// brief-em3d-42 R-em3d42-5a — a setup as a <c>.c3d</c> embeds it: the <c>.cem</c> schema through this
    /// class's own serializer — ONE schema, two containers — less <c>LayoutRef</c>, because the geometry is
    /// the document that holds it.
    /// </summary>
    public static JsonElement ToEmbedded(EmSetup setup)
    {
        var node = JsonSerializer.SerializeToNode(ToFileModel(setup), JsonOpts)!.AsObject();
        node.Remove(nameof(CemFile.LayoutRef));
        return JsonSerializer.SerializeToElement(node, JsonOpts);
    }

    /// <summary>The inverse of <see cref="ToEmbedded"/>: one of a <c>.c3d</c>'s embedded setups, read by the
    /// <c>.cem</c> reader. Throws what that reader throws.</summary>
    public static EmSetup FromEmbedded(JsonElement element) => Deserialize(element.GetRawText());

    // ── Convert ───────────────────────────────────────────────────────────────

    private static CemFile ToFileModel(EmSetup s) => new()
    {
        FormatVersion          = CurrentFormatVersion,
        Name                   = s.Name,
        LayoutRef              = s.LayoutRef,
        SignalStackupLayerName = s.SignalStackupLayerName is { Length: > 0 } n ? n : null,
        GroundStackupLayerName = s.GroundStackupLayerName is { Length: > 0 } g ? g : null,
        Frequency = ToCem(s.Frequency),
        AdditionalFrequencies = s.AdditionalFrequencies.Count > 0 ? [.. s.AdditionalFrequencies.Select(ToCem)] : null,
        Port1Z0Real = s.Port1Z0.Real,
        Port1Z0Imag = s.Port1Z0.Imaginary,
        Port2Z0Real = s.Port2Z0.Real,
        Port2Z0Imag = s.Port2Z0.Imaginary,
        PortZ0s     = FlattenPortZ0s(s.PortZ0s),
        SchematicPortZ0s = s.SchematicPortZ0s.Count > 0
            ? [.. s.SchematicPortZ0s.Select(kv => new CemSchematicPortZ0 { Port = kv.Key, Real = kv.Value.Real, Imag = kv.Value.Imaginary })]
            : null,
        PortKinds   = FlattenPortKinds(s.PortKinds),
        AnalysisLevelNames = s.AnalysisLevelNames.Count > 0 ? [.. s.AnalysisLevelNames] : null,
        Mesh = new CemMesh
        {
            MinCellsAcrossWidth = s.Mesh.MinCellsAcrossWidth,
            EdgeCells           = s.Mesh.EdgeCells,
            EdgeFractionOfWidth = s.Mesh.EdgeFractionOfWidth,
            EdgeGrowthRatio     = s.Mesh.EdgeGrowthRatio,
            TruncationHeights   = s.Mesh.TruncationHeights,
            TruncationTailCells = s.Mesh.TruncationTailCells,
        },
        DispersionCorrection  = s.DispersionCorrection,
        AdaptiveSampling      = s.AdaptiveSampling ? null : false,
        ResonanceSearch       = s.ResonanceSearch ? true : null,
        DirectVerticalKernel  = s.DirectVerticalKernel ? true : null,
        AcceleratedSolve      = s.AcceleratedSolve ? true : null,
        ConductorThicknessCorrection = s.ConductorThicknessCorrection ? true : null,
        EdgePortReturn        = s.EdgePortReturn == EmSetup.DefaultEdgePortReturn ? null : s.EdgePortReturn,
        DeembedOutsideCalibrationValidity =
            s.DeembedOutsideCalibrationValidity ? true : null,
        RadiationPattern      = s.RadiationPattern ? true : null,
        PatternFrequencyGHz   = s.PatternFrequencyGHz is > 0 ? s.PatternFrequencyGHz : null,
        MeshFrequencyGHz      = s.MeshFrequencyGHz is > 0 ? s.MeshFrequencyGHz : null,
        ReferenceInputPowerDbm = s.ReferenceInputPowerDbm != 0.0 ? s.ReferenceInputPowerDbm : null,
        SnpOutputPathOverride = s.SnpOutputPathOverride is { Length: > 0 } p ? p : null,
        CreateSnpSchematic    = s.CreateSnpSchematic ? true : null,
        AnalysisKind          = s.AnalysisKind == EmAnalysisKind.Auto ? null : s.AnalysisKind,
        SolveRegion           = s.SolveRegion is { } r
            ? new CemSolveRegion { XMinUm = r.XMinUm, YMinUm = r.YMinUm, XMaxUm = r.XMaxUm, YMaxUm = r.YMaxUm }
            : null,
        PlanarMesh            = s.PlanarMesh == EmSetup.DefaultPlanarMesh ? null : new CemPlanarMesh
        {
            Auto               = s.PlanarMesh.Auto,
            CellsPerWavelength = s.PlanarMesh.CellsPerWavelength,
            EdgeMesh           = s.PlanarMesh.EdgeMesh,
            EdgeCells          = s.PlanarMesh.EdgeCells,
            BoundaryCells      = s.PlanarMesh.BoundaryCells == PlanarMeshSettings.DefaultBoundaryCells
                                     ? null : s.PlanarMesh.BoundaryCells,
            MeshFrequencyHz    = s.PlanarMesh.MeshFrequencyHz,
            MinCellsAcrossConductor =
                s.PlanarMesh.MinCellsAcrossConductor == PlanarMeshSettings.DefaultMinCellsAcrossConductor
                    ? null : s.PlanarMesh.MinCellsAcrossConductor,
            // The legacy key is never written — one setting, one spelling. A file that carried it
            // comes back out on CurrentModel, which is the migration.
            TransmissionLineMesh = null,
            CurrentModel = s.PlanarMesh.CurrentModel == PlanarMeshSettings.DefaultCurrentModel
                    ? null : s.PlanarMesh.CurrentModel,
            DetailFloorDivisor =
                s.PlanarMesh.DetailFloorDivisor == PlanarMeshSettings.DefaultDetailFloorDivisor
                    ? null : s.PlanarMesh.DetailFloorDivisor,
            // PM-3 — omitted at the PRODUCT default (on); an explicit off is the only value written.
            RooftopMerging = s.PlanarMesh.RooftopMerging == EmSetup.DefaultPlanarMesh.RooftopMerging
                                 ? null : s.PlanarMesh.RooftopMerging,
        },
        Solver3D       = s.Solver3D.IsNone ? null : new Em3dSolverSpelling(CircuitRF.Design.Em3d.Em3dSolvers.Spelling(s.Solver3D)),
        Compare        = CircuitRF.Design.Em3d.Em3dSolvers.IsCompareSpelling(CircuitRF.Design.Em3d.Em3dSolvers.Spelling(s.Solver3D))
            ? new CemCompare
            {
                Solvers   = [.. CircuitRF.Design.Em3d.Em3dSolvers.Of(s.Solver3D).Select(d => d.Spelling)],
                Reference = CircuitRF.Design.Em3d.Em3dSolvers.Get(s.Solver3D.Reference).Spelling,
            }
            : null,
        OperatingTempC = s.OperatingTempC,
        AirBox         = ToCem(s.AirBox),
        RobustMeshing  = s.RobustMeshing ? null : false,
        Conductors     = s.Conductors == Em3dConductorMode.Auto ? null : s.Conductors,
        Palace         = s.Palace,
        OpenEms        = s.OpenEms,
        Emerge         = s.Emerge is { IsEmpty: false } emerge ? emerge : null,
        Problem3D      = s.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Driven ? null : s.Problem3D,
        Terminals3D    = s.Terminals3D.Count == 0 ? null
            : [.. s.Terminals3D.Select(t => new CemTerminal3D { Name = t.Name, Net = t.Net,
                                                                 Source = t.Source is { Length: > 0 } src ? src : null,
                                                                 Objects = t.ByObjects ? [.. t.Objects!] : null })],
        Ground3D       = s.Ground3D is { Length: > 0 } g3 ? g3 : null,
        UnassignedConductors3D = s.UnassignedConductors3D == EmUnassignedConductors.Float ? null : s.UnassignedConductors3D,
        // R-em3d23-2a — a port with nothing but the default kind writes nothing, so every lumped-port
        // file keeps its bytes (gate 9).
        ComponentPorts = s.ComponentPorts.Count > 0 ? [.. s.ComponentPorts] : null,
        Ports3D        = s.Ports3D.Where(p => !p.IsDefault).ToList() is { Count: > 0 } p3
            ? [.. p3.Select(p => new CemPort3D
              {
                  Port = p.Port,
                  Kind = p.Kind == CircuitRF.Engine.Em3d.Em3dPortKind.Lumped ? null : p.Kind,
                  Width = p.WidthFactor, Height = p.HeightFactor, OffsetUm = p.OffsetUm,
                  Return = p.Return is { Length: > 0 } r ? r : null,
              })]
            : null,
        Eigenmode      = s.Eigenmode is { } e && (e.Count is not null || e.TargetGHz is not null)
            ? new CemEigenmode { Count = e.Count, TargetGHz = e.TargetGHz } : null,
        Thermal        = s.Thermal,
        Sar            = s.Sar,
    };

    /// <summary>An air box as a file spells it (a <c>.cem</c>'s, or a <c>.c3d</c>'s own); null for null.</summary>
    public static CemAirBox? ToCem(EmAirBox? box) => box is null ? null : new CemAirBox
    {
        XMin = ToFace(box.XMin), XMax = ToFace(box.XMax),
        YMin = ToFace(box.YMin), YMax = ToFace(box.YMax),
        ZMin = ToFace(box.ZMin), ZMax = ToFace(box.ZMax),
    };

    /// <summary>An air box as a file spells it, read; null for null.</summary>
    public static EmAirBox? FromCem(CemAirBox? box) => box is null ? null
        : new EmAirBox(FromFace(box.XMin), FromFace(box.XMax), FromFace(box.YMin),
                       FromFace(box.YMax), FromFace(box.ZMin), FromFace(box.ZMax));

    private static CemAirBoxFace? ToFace(EmAirBoxFace? f)
        => f is null ? null : new CemAirBoxFace { PaddingUm = f.PaddingUm, Boundary = f.Boundary, PaddingPercent = f.PaddingPercent };

    private static EmAirBoxFace? FromFace(CemAirBoxFace? f)
        => f is null ? null : new EmAirBoxFace(f.PaddingUm, f.Boundary, f.PaddingPercent);

    private static EmSetup FromFileModel(CemFile f) => new()
    {
        Name                   = f.Name,
        LayoutRef              = f.LayoutRef,
        SignalStackupLayerName = f.SignalStackupLayerName ?? "",
        GroundStackupLayerName = f.GroundStackupLayerName ?? "",
        Frequency              = BuildSpec(f.Frequency),
        AdditionalFrequencies  = f.AdditionalFrequencies is { } more ? [.. more.Select(BuildSpec)] : [],
        Port1Z0                = new Complex(f.Port1Z0Real, f.Port1Z0Imag),
        Port2Z0                = new Complex(f.Port2Z0Real, f.Port2Z0Imag),
        PortZ0s                = UnflattenPortZ0s(f.PortZ0s),
        SchematicPortZ0s       = f.SchematicPortZ0s is { } sz
            ? new SortedDictionary<int, Complex>(sz.GroupBy(e => e.Port).ToDictionary(g => g.Key, g => new Complex(g.Last().Real, g.Last().Imag)))
            : [],
        PortKinds              = f.PortKinds is { } pk ? [.. pk] : [],
        AnalysisLevelNames     = f.AnalysisLevelNames is { } lv ? [.. lv] : [],
        Mesh = new EmMeshSettings(
            f.Mesh.MinCellsAcrossWidth,
            f.Mesh.EdgeCells,
            f.Mesh.EdgeFractionOfWidth,
            f.Mesh.EdgeGrowthRatio,
            f.Mesh.TruncationHeights,
            f.Mesh.TruncationTailCells),
        DispersionCorrection  = f.DispersionCorrection,
        AdaptiveSampling      = f.AdaptiveSampling ?? true,
        ResonanceSearch       = f.ResonanceSearch ?? false,
        DirectVerticalKernel  = f.DirectVerticalKernel ?? false,
        AcceleratedSolve      = f.AcceleratedSolve ?? false,
        ConductorThicknessCorrection = f.ConductorThicknessCorrection ?? false,
        EdgePortReturn        = f.EdgePortReturn ?? EmSetup.DefaultEdgePortReturn,
        LegacyRawSolveRequested = f.Deembed == false,
        DeembedOutsideCalibrationValidity = f.DeembedOutsideCalibrationValidity ?? false,
        RadiationPattern      = f.RadiationPattern ?? false,
        PatternFrequencyGHz   = f.PatternFrequencyGHz is > 0 ? f.PatternFrequencyGHz : null,
        MeshFrequencyGHz      = f.MeshFrequencyGHz is > 0 ? f.MeshFrequencyGHz : null,
        ReferenceInputPowerDbm = f.ReferenceInputPowerDbm ?? 0.0,
        SnpOutputPathOverride = f.SnpOutputPathOverride ?? "",
        CreateSnpSchematic    = f.CreateSnpSchematic ?? false,
        AnalysisKind          = f.AnalysisKind ?? EmAnalysisKind.Auto,
        // Normalised on the way in, so a hand-edited file with its corners swapped means the box it
        // plainly describes rather than an inside-out one that clips everything away.
        SolveRegion           = f.SolveRegion is { } sr
            ? EmSolveRegion.FromCorners(sr.XMinUm, sr.YMinUm, sr.XMaxUm, sr.YMaxUm)
            : null,
        PlanarMesh            = f.PlanarMesh is { } pm
            ? new PlanarMeshSettings(pm.Auto, pm.CellsPerWavelength, pm.EdgeMesh, pm.EdgeCells,
                                     pm.BoundaryCells ?? PlanarMeshSettings.DefaultBoundaryCells,
                                     pm.MeshFrequencyHz,
                                     pm.MinCellsAcrossConductor
                                         ?? PlanarMeshSettings.DefaultMinCellsAcrossConductor,
                                     ResolveCurrentModel(pm),
                                     pm.DetailFloorDivisor
                                         ?? PlanarMeshSettings.DefaultDetailFloorDivisor,
                                     null,
                                     pm.RooftopMerging ?? EmSetup.DefaultPlanarMesh.RooftopMerging)
            : EmSetup.DefaultPlanarMesh,
        Solver3D              = Solver3DOf(f),
        OperatingTempC        = f.OperatingTempC,
        AirBox                = FromCem(f.AirBox),
        RobustMeshing         = f.RobustMeshing ?? true,
        Conductors            = f.Conductors ?? Em3dConductorMode.Auto,
        Palace                = f.Palace,
        OpenEms               = f.OpenEms,
        Emerge                = f.Emerge,
        Problem3D             = f.Problem3D ?? CircuitRF.Engine.Em3d.Em3dProblemType.Driven,
        Terminals3D           = f.Terminals3D is { } ts ? [.. ts.Select(t => new EmTerminal3D(t.Name ?? "", t.Net ?? "", t.Source, t.Objects is { Count: > 0 } o ? [.. o] : null))] : [],
        Ground3D              = f.Ground3D ?? "",
        UnassignedConductors3D = f.UnassignedConductors3D ?? EmUnassignedConductors.Float,
        ComponentPorts        = f.ComponentPorts is { } cps ? [.. cps] : [],
        Ports3D               = f.Ports3D is { } ps
            ? [.. ps.Select(p => new EmPort3D(p.Port, p.Kind ?? CircuitRF.Engine.Em3d.Em3dPortKind.Lumped, p.Width, p.Height, p.OffsetUm,
                                     p.Return is { Length: > 0 } r ? r : null))]
            : [],
        Eigenmode             = f.Eigenmode is { } em ? new EmEigenmode3D(em.Count, em.TargetGHz) : null,
        Thermal               = f.Thermal,
        Sar                   = f.Sar,
    };

    /// <summary>brief-emi-6 R-emi6-1 — the choice the keys <c>Solver3D</c> and <c>Compare</c> name together. A <c>Compare</c> block
    /// beside any other spelling is not read (and not written back).</summary>
    private static Em3dSolverChoice Solver3DOf(CemFile f)
    {
        if (f.Solver3D is not { } spelling) return Em3dSolverChoice.None;
        if (!CircuitRF.Design.Em3d.Em3dSolvers.IsCompareSpelling(spelling.Text))
            return CircuitRF.Design.Em3d.Em3dSolvers.FromSpelling(spelling.Text)!;
        var (choice, refusal) = CircuitRF.Design.Em3d.Em3dSolvers.FromCompareBlock(f.Compare?.Solvers, f.Compare?.Reference);
        return choice ?? throw new JsonException(refusal);
    }

    /// <summary>
    /// <b>ANT-3's persistence migration, and the refusal is the part that is a decision.</b>
    ///
    /// <para>The new key wins where it is the only one present, and the legacy
    /// <see cref="CemPlanarMesh.TransmissionLineMesh"/> is read as
    /// <see cref="PlanarCurrentModel.TransmissionLine"/> where IT is the only one present — that is
    /// the migration, and it is why a pre-ANT-3 <c>.cem</c> opens on exactly the mesh it described.
    /// </para>
    ///
    /// <para><b>A file carrying both keys and disagreeing is REFUSED, naming both.</b> The obvious
    /// alternative is a precedence rule ("the new key wins"), and it is the wrong answer for a
    /// reason this area has already paid for once: a precedence rule means a user who set one
    /// control watched the other one silently win, which is the exact defect the transmission-line
    /// mesh was built to fix. Two keys that say different things about one setting is a file nobody
    /// can act on, and the person holding it is the one who knows which they meant.</para>
    ///
    /// <para>Agreement is not a refusal: <c>true</c> beside <c>TransmissionLine</c>, and
    /// <c>false</c> beside <c>None</c>, are two spellings of one answer and load quietly.</para>
    /// </summary>
    private static PlanarCurrentModel ResolveCurrentModel(CemPlanarMesh pm)
    {
        if (pm.TransmissionLineMesh is not { } legacy)
            return pm.CurrentModel ?? PlanarMeshSettings.DefaultCurrentModel;

        var fromLegacy = legacy ? PlanarCurrentModel.TransmissionLine : PlanarCurrentModel.None;
        if (pm.CurrentModel is not { } current) return fromLegacy;
        if (current == fromLegacy) return current;

        throw new InvalidDataException(
            $"This .cem states its mesh current model twice and the two disagree: " +
            $"'PlanarMesh.CurrentModel' says '{current}' while the legacy " +
            $"'PlanarMesh.TransmissionLineMesh' says '{(legacy ? "true" : "false")}' " +
            $"(which means '{fromLegacy}'). These are one setting written two ways, so there is no " +
            "answer here that is not a guess about which one you meant. Delete whichever line is " +
            "wrong — 'TransmissionLineMesh' is the older spelling and is no longer written — and " +
            "open it again.");
    }

    /// <summary>Null for an empty list, so the field is omitted entirely rather than written as [].</summary>
    private static List<double>? FlattenPortZ0s(List<Complex> z)
    {
        if (z.Count == 0) return null;
        var flat = new List<double>(z.Count * 2);
        foreach (var c in z) { flat.Add(c.Real); flat.Add(c.Imaginary); }
        return flat;
    }

    /// <summary>
    /// Null when the list is empty <b>or is all-Edge</b>, so the field is omitted entirely rather
    /// than written as a run of the default. The second half matters: the panel materialises one row
    /// per port and a naive write would put <c>["Edge","Edge"]</c> into every <c>.cem</c> that has
    /// ever been opened, which is exactly the byte-identity the omit-at-default rule protects.
    /// </summary>
    private static List<PlanarPortKind>? FlattenPortKinds(List<PlanarPortKind> kinds)
    {
        foreach (var k in kinds)
            if (k != PlanarPortKind.Edge) return [.. kinds];
        return null;
    }

    /// <summary>A trailing half-pair is dropped rather than throwing — a hand-edited .cem must
    /// degrade to the near/far defaults, not fail to open.</summary>
    private static List<Complex> UnflattenPortZ0s(List<double>? flat)
    {
        var z = new List<Complex>();
        if (flat is null) return z;
        for (int i = 0; i + 1 < flat.Count; i += 2) z.Add(new Complex(flat[i], flat[i + 1]));
        return z;
    }

    private static CemFrequency ToCem(FrequencySpec f) => new()
    {
        StartExpr = f.StartExpr,
        StopExpr  = f.StopExpr,
        StepExpr  = f.StepExpr,
        NumPoints = f.NumPoints,
        Mode      = f.Mode,
        Kind      = f.Kind,
        StartUnit = f.StartUnit,
        StopUnit  = f.StopUnit,
        StepUnit  = f.StepUnit,
    };

    private static FrequencySpec BuildSpec(CemFrequency f)
        => f.Mode == FreqSpecMode.StepSize
            ? new FrequencySpec(f.StartExpr, f.StopExpr, f.StepExpr, f.Kind, f.StartUnit, f.StopUnit, f.StepUnit)
            : new FrequencySpec(f.StartExpr, f.StopExpr, Math.Max(1, f.NumPoints ?? 101), f.Kind, f.StartUnit, f.StopUnit);
}
