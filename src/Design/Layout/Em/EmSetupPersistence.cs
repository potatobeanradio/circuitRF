// .cem file format — rev 1 (alpha, no back-compat per policy).
// Mirrors TechPersistence exactly: System.Text.Json, WriteIndented, enum-as-string,
// WhenWritingNull, reject-on-newer-format-version, AtomicFile write, gzip sniff on load.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;
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

    /// <summary>A copy, so an editor can change one without touching a setup that shares it.</summary>
    public CemPalace Clone() => (CemPalace)MemberwiseClone();

    /// <summary>True when no field is set: the same run as an omitted section.</summary>
    public bool IsEmpty =>
        Quality is null && MaxElementWavelengths is null && EdgeRefinement is null && Grading is null && ElementOrder is null &&
        AdaptiveTol is null && AdaptiveMaxIterations is null && SweepAdaptiveTol is null;
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
            section.SweepAdaptiveTol      ?? p.SweepAdaptiveTol);
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
        return p;
    }
}

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

    /// <summary>A copy, so an editor can change one without touching a setup that shares it.</summary>
    public CemOpenEms Clone() => (CemOpenEms)MemberwiseClone();

    /// <summary>True when no field is set: the same run as an omitted section.</summary>
    public bool IsEmpty =>
        CellsPerWavelength is null && GradingRatio is null && ThirdsRule is null && MinCellUm is null && PmlCells is null &&
        EndCriterionDb is null && MaxTimeSteps is null;

    /// <summary>
    /// brief-em3d-8 R-em3d8-6 — the section's grid fields RESOLVED, each omitted one taking
    /// <see cref="CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default"/>'s. The defaults live there, in the engine, because the
    /// grid generator is what reads them.
    /// </summary>
    public static CircuitRF.Engine.Em3d.OpenEmsGridSettings ResolveGrid(CemOpenEms? section)
    {
        var d = CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default;
        return section is null ? d : new(
            section.CellsPerWavelength ?? d.CellsPerWavelength,
            section.GradingRatio       ?? d.GradingRatio,
            section.ThirdsRule         ?? d.ThirdsRule,
            section.MinCellUm is { } um ? um * 1e-6 : d.MinCellM,
            section.PmlCells           ?? d.PmlCells);
    }

    /// <summary>brief-em3d-9 R-em3d9-6 — the section's run fields RESOLVED, each omitted one taking
    /// <see cref="OpenEmsRunSettings.Default"/>'s.</summary>
    public static OpenEmsRunSettings ResolveRun(CemOpenEms? section)
    {
        var d = OpenEmsRunSettings.Default;
        return section is null ? d : new(section.EndCriterionDb ?? d.EndCriterionDb, section.MaxTimeSteps ?? d.MaxTimeSteps);
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
        return p;
    }
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
    /// ANT-12's radiation pattern — <b>null means off</b>, which is what every <c>.cem</c> written
    /// before the far field was reachable means. Same nullable + omit-at-default rule as
    /// <see cref="AcceleratedSolve"/> directly above, so such a file loads AND re-serialises
    /// byte-identically.
    /// </summary>
    public bool?   RadiationPattern      { get; set; }

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
    /// are kept but not read. Chosen by name only; Auto never resolves to a 3D solver.
    /// </summary>
    public Em3dSolver? Solver3D { get; set; }

    /// <summary>The temperature a 3D setup evaluates every conductor's σ at, °C. <b>Null means
    /// 20 °C.</b> Read by 3D setups only.</summary>
    public double? OperatingTempC { get; set; }

    /// <summary>A 3D setup's air box, per face. Null takes the default on every face.</summary>
    public CemAirBox? AirBox { get; set; }

    /// <summary>Palace's own section. Null takes the defaults.</summary>
    public CemPalace? Palace { get; set; }

    /// <summary>openEMS's own section. Null takes the defaults.</summary>
    public CemOpenEms? OpenEms { get; set; }

    /// <summary>brief-em3d-22 — what a 3D setup solves: Driven (S over the sweep), Electrostatic (a
    /// capacitance matrix) or Magnetostatic (an inductance matrix). <b>Null means Driven.</b> The two
    /// static problems run on Palace only.</summary>
    public CircuitRF.Engine.Em3d.Em3dProblemType? Problem3D { get; set; }

    /// <summary>A static 3D setup's terminals, in matrix order. Null when there are none.</summary>
    public List<CemTerminal3D>? Terminals3D { get; set; }

    /// <summary>The net that is a static solve's reference. Null is the ground-reference conductors
    /// (and the PEC floor when there is one).</summary>
    public string? Ground3D { get; set; }
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
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert ───────────────────────────────────────────────────────────────

    private static CemFile ToFileModel(EmSetup s) => new()
    {
        FormatVersion          = CurrentFormatVersion,
        Name                   = s.Name,
        LayoutRef              = s.LayoutRef,
        SignalStackupLayerName = s.SignalStackupLayerName is { Length: > 0 } n ? n : null,
        GroundStackupLayerName = s.GroundStackupLayerName is { Length: > 0 } g ? g : null,
        Frequency = new CemFrequency
        {
            StartExpr = s.Frequency.StartExpr,
            StopExpr  = s.Frequency.StopExpr,
            StepExpr  = s.Frequency.StepExpr,
            NumPoints = s.Frequency.NumPoints,
            Mode      = s.Frequency.Mode,
            Kind      = s.Frequency.Kind,
            StartUnit = s.Frequency.StartUnit,
            StopUnit  = s.Frequency.StopUnit,
            StepUnit  = s.Frequency.StepUnit,
        },
        Port1Z0Real = s.Port1Z0.Real,
        Port1Z0Imag = s.Port1Z0.Imaginary,
        Port2Z0Real = s.Port2Z0.Real,
        Port2Z0Imag = s.Port2Z0.Imaginary,
        PortZ0s     = FlattenPortZ0s(s.PortZ0s),
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
        DeembedOutsideCalibrationValidity =
            s.DeembedOutsideCalibrationValidity ? true : null,
        RadiationPattern      = s.RadiationPattern ? true : null,
        ReferenceInputPowerDbm = s.ReferenceInputPowerDbm != 0.0 ? s.ReferenceInputPowerDbm : null,
        SnpOutputPathOverride = s.SnpOutputPathOverride is { Length: > 0 } p ? p : null,
        AnalysisKind          = s.AnalysisKind == EmAnalysisKind.Auto ? null : s.AnalysisKind,
        SolveRegion           = s.SolveRegion is { } r
            ? new CemSolveRegion { XMinUm = r.XMinUm, YMinUm = r.YMinUm, XMaxUm = r.XMaxUm, YMaxUm = r.YMaxUm }
            : null,
        PlanarMesh            = s.PlanarMesh == PlanarMeshSettings.Default ? null : new CemPlanarMesh
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
        },
        Solver3D       = s.Solver3D == Em3dSolver.None ? null : s.Solver3D,
        OperatingTempC = s.OperatingTempC,
        AirBox         = s.AirBox is { } box ? new CemAirBox
        {
            XMin = ToFace(box.XMin), XMax = ToFace(box.XMax),
            YMin = ToFace(box.YMin), YMax = ToFace(box.YMax),
            ZMin = ToFace(box.ZMin), ZMax = ToFace(box.ZMax),
        } : null,
        Palace         = s.Palace,
        OpenEms        = s.OpenEms,
        Problem3D      = s.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Driven ? null : s.Problem3D,
        Terminals3D    = s.Terminals3D.Count == 0 ? null
            : [.. s.Terminals3D.Select(t => new CemTerminal3D { Name = t.Name, Net = t.Net,
                                                                 Source = t.Source is { Length: > 0 } src ? src : null })],
        Ground3D       = s.Ground3D is { Length: > 0 } g3 ? g3 : null,
    };

    private static CemAirBoxFace? ToFace(EmAirBoxFace? f)
        => f is null ? null : new CemAirBoxFace { PaddingUm = f.PaddingUm, Boundary = f.Boundary };

    private static EmAirBoxFace? FromFace(CemAirBoxFace? f)
        => f is null ? null : new EmAirBoxFace(f.PaddingUm, f.Boundary);

    private static EmSetup FromFileModel(CemFile f) => new()
    {
        Name                   = f.Name,
        LayoutRef              = f.LayoutRef,
        SignalStackupLayerName = f.SignalStackupLayerName ?? "",
        GroundStackupLayerName = f.GroundStackupLayerName ?? "",
        Frequency              = BuildSpec(f.Frequency),
        Port1Z0                = new Complex(f.Port1Z0Real, f.Port1Z0Imag),
        Port2Z0                = new Complex(f.Port2Z0Real, f.Port2Z0Imag),
        PortZ0s                = UnflattenPortZ0s(f.PortZ0s),
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
        LegacyRawSolveRequested = f.Deembed == false,
        DeembedOutsideCalibrationValidity = f.DeembedOutsideCalibrationValidity ?? false,
        RadiationPattern      = f.RadiationPattern ?? false,
        ReferenceInputPowerDbm = f.ReferenceInputPowerDbm ?? 0.0,
        SnpOutputPathOverride = f.SnpOutputPathOverride ?? "",
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
                                         ?? PlanarMeshSettings.DefaultDetailFloorDivisor)
            : PlanarMeshSettings.Default,
        Solver3D              = f.Solver3D ?? Em3dSolver.None,
        OperatingTempC        = f.OperatingTempC,
        AirBox                = f.AirBox is { } box
            ? new EmAirBox(FromFace(box.XMin), FromFace(box.XMax), FromFace(box.YMin),
                           FromFace(box.YMax), FromFace(box.ZMin), FromFace(box.ZMax))
            : null,
        Palace                = f.Palace,
        OpenEms               = f.OpenEms,
        Problem3D             = f.Problem3D ?? CircuitRF.Engine.Em3d.Em3dProblemType.Driven,
        Terminals3D           = f.Terminals3D is { } ts ? [.. ts.Select(t => new EmTerminal3D(t.Name ?? "", t.Net ?? "", t.Source))] : [],
        Ground3D              = f.Ground3D ?? "",
    };

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

    private static FrequencySpec BuildSpec(CemFrequency f)
        => f.Mode == FreqSpecMode.StepSize
            ? new FrequencySpec(f.StartExpr, f.StopExpr, f.StepExpr, f.Kind, f.StartUnit, f.StopUnit, f.StepUnit)
            : new FrequencySpec(f.StartExpr, f.StopExpr, Math.Max(1, f.NumPoints ?? 101), f.Kind, f.StartUnit, f.StopUnit);
}
