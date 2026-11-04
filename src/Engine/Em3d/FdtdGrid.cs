// brief-em3d-8 — the FDTD grid: where the lines go (docs/design/em-3d.md §6.5, §12).
//
// openEMS has no mesher circuitRF can use: its mesh is three sorted arrays of grid lines, and FDTD's
// accuracy is decided almost entirely by where they fall. So the grid is circuitRF's to write, and it
// is written here — managed, in the numeric layer, with no process and no file, testable with no
// openEMS installed.
//
// The problem is one-dimensional, three times (R-em3d8-1). Per axis:
//   1. COLLECT the required lines (R-em3d8-2): metal edges (the thirds rule at a sheet's or a thin
//      conductor's edge), material faces, port extents, sheet planes, air-box faces — each carrying
//      the feature it came from;
//   2. MERGE the near-coincident ones (R-em3d8-3), reporting every merge, because one 10 nm cell
//      sets the time step of the whole run and would otherwise do it invisibly (§12; F0 Q2 measured
//      exactly that: dt 1.2e-15 s on a 12.7 µm grid, from one fill line 0.6 µm off a fixed one);
//   3. FILL between them (R-em3d8-4) under a per-interval maximum cell and a grading ratio, with the
//      PML's uniform cells outside each absorbing face.
// Each step is its own function, tested on its own; the three axes share all of it.
//
// Everything is deterministic (overview rule 2): no hash order, no parallelism, and the fill of an
// interval depends only on that interval's ends, so a layout edit moves only the lines it reaches.

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>A grid axis. <see cref="Rho"/> and <see cref="Alpha"/> (brief-em3d-120) name a CYLINDRICAL grid's radial and
/// azimuthal axes in its sentences, never a world axis: they are held in <see cref="FdtdGridResult.X"/> and
/// <see cref="FdtdGridResult.Y"/>, as openEMS holds them in XLines and YLines.</summary>
public enum FdtdAxis { X, Y, Z, Rho, Alpha }

/// <summary>
/// The openEMS section's grid fields, RESOLVED (brief-em3d-8 R-em3d8-6). <b>The defaults live here
/// and nowhere else</b>: the <c>.cem</c> DTO resolves onto <see cref="Default"/>, and <c>explain</c>,
/// the panel and brief 9's writer read them from it.
/// </summary>
/// <param name="CellsPerWavelength">Cells per shortest wavelength in the densest material an interval
/// passes through, at the top frequency (R-em3d8-4a).</param>
/// <param name="GradingRatio">The largest ratio between neighbouring cells (R-em3d8-4b).</param>
/// <param name="ThirdsRule">Whether a sheet's or thin conductor's edge gets its lines a third inside
/// and two thirds outside (R-em3d8-2b) rather than on the edge.</param>
/// <param name="MinCellM">Lines closer than this are merged (R-em3d8-3); null takes
/// <see cref="FdtdGrid.MinCellFeatureFraction"/> of the smallest deliberate feature.</param>
/// <param name="PmlCells">Uniform cells outside each absorbing face (R-em3d8-4c).</param>
public sealed record OpenEmsGridSettings(
    double  CellsPerWavelength,
    double  GradingRatio,
    bool    ThirdsRule,
    double? MinCellM,
    int     PmlCells)
{
    /// <summary>
    /// The shipped defaults. λ/20 is the usual FDTD floor for a few-percent phase error; 1.3 is the
    /// grading the Palace section also defaults to; 8 PML cells is openEMS's own default thickness.
    /// </summary>
    public static OpenEmsGridSettings Default { get; } = new(20, 1.3, true, null, 8);

    /// <summary>brief-em3d-120 — a cylindrical grid about one axis; null (the default, and what every existing setup
    /// means) is the Cartesian grid.</summary>
    public OpenEmsCylindrical? Cylindrical { get; init; }

    /// <summary>brief-sar-3 R-sar3-5 — grid lines given by the setup (<c>OpenEms.GridLines</c>): the grid is exactly these and
    /// <see cref="FdtdGrid"/> places none of its own. Null (the default, and what every existing setup means) lets it place
    /// them.</summary>
    public FdtdExplicitLines? ExplicitLines { get; init; }

    /// <summary>Every value that cannot be built, as sentences naming the field — empty when all can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!(CellsPerWavelength >= 1) || double.IsInfinity(CellsPerWavelength))
            p.Add($"OpenEms.CellsPerWavelength is {G(CellsPerWavelength)}; it must be at least 1.");
        if (!(GradingRatio > 1) || !(GradingRatio <= 4))
            p.Add($"OpenEms.GradingRatio is {G(GradingRatio)}; it must be above 1 and at most 4.");
        if (MinCellM is { } m && (!(m > 0) || double.IsInfinity(m)))
            p.Add($"OpenEms.MinCellUm is {G(m * 1e6)}; it must be a positive length.");
        if (PmlCells < 0 || PmlCells > 64)
            p.Add($"OpenEms.PmlCells is {PmlCells}; it must be 0 to 64.");
        if (Cylindrical?.AzimuthLines is { } n && (n < OpenEmsCylindrical.MinAzimuthLines || n > OpenEmsCylindrical.MaxAzimuthLines))
            p.Add($"OpenEms.AzimuthLines is {n}; it must be {OpenEmsCylindrical.MinAzimuthLines} to {OpenEmsCylindrical.MaxAzimuthLines}.");
        if (Cylindrical is { } c && (c.Axis > FdtdAxis.Z || !double.IsFinite(c.Origin.X) || !double.IsFinite(c.Origin.Y) || !double.IsFinite(c.Origin.Z)))
            p.Add("OpenEms.Axis must be X, Y or Z, and OpenEms.AxisOriginUm three finite lengths.");
        if (Cylindrical?.MultiGridRadiiM is { } mg && (mg.Count == 0 || mg.Any(r => !(r > 0) || !double.IsFinite(r)) ||
                                                      Enumerable.Range(1, mg.Count - 1).Any(i => !(mg[i] > mg[i - 1]))))
            p.Add("OpenEms.MultiGridRadiiUm must be positive radii in increasing order.");
        if (ExplicitLines is { } ex)
        {
            if (ex.Problem is { } bad) p.Add(bad);
            else
            {
                if (Cylindrical is not null && !ex.Cylindrical)
                    p.Add("OpenEms.GridLines gives Cartesian lines, and OpenEms.Grid is Cylindrical: give one or the other.");
                if (Cylindrical is null && ex.Cylindrical)
                    p.Add($"OpenEms.GridLines gives a cylindrical grid ({ex.Source}), and OpenEms.Grid is Cartesian: set OpenEms.Grid to " +
                          "Cylindrical, with the Axis and AxisOriginUm the file's grid is round about.");
                if (ex.Cylindrical && ex.X.Count > 0 && ex.Y.Count > 1 &&
                    (ex.X[0] < 0 || Math.Abs(ex.Y[^1] - ex.Y[0] - 2 * Math.PI) > OpenEmsCylindrical.ClosedAlphaTolerance))
                    p.Add($"OpenEms.GridLines' cylindrical grid ({ex.Source}) needs ρ from 0 or above and α round the full circle (its last line " +
                          "its first plus 2π): circuitRF's cylindrical grid is a closed one.");
                foreach (var (name, lines) in new[] { ("X", ex.X), ("Y", ex.Y), ("Z", ex.Z) })
                {
                    if (lines.Count < 3)
                        p.Add($"OpenEms.GridLines gives {lines.Count} {name} line(s) ({ex.Source}); openEMS needs at least 3 on every axis.");
                    else if (lines.Any(v => !double.IsFinite(v)) || Enumerable.Range(1, lines.Count - 1).Any(i => !(lines[i] > lines[i - 1])))
                        p.Add($"OpenEms.GridLines' {name} lines ({ex.Source}) are not finite and strictly increasing.");
                }
            }
        }
        return p;
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// brief-em3d-120 R-em3d120-2 — the setup's cylindrical grid, resolved: the world axis it is round about, a point that axis
/// passes through (metres), and the azimuth line count when the setup states one (null: §2c's rule, see
/// <see cref="FdtdCylinder.AzimuthSetBy"/>).
/// </summary>
public sealed record OpenEmsCylindrical(FdtdAxis Axis, Point3 Origin, int? AzimuthLines = null)
{
    /// <summary>The fewest and most azimuth lines a setup may state (both counts include the closing line at 2π).</summary>
    public const int MinAzimuthLines = 9, MaxAzimuthLines = 4001;

    /// <summary>How close to 2π a given grid's α span must be for openEMS to close it (its OPERATOR_CYLINDER_CLOSED_ALPHA_THRESHOLD).</summary>
    public const double ClosedAlphaTolerance = 1e-4;

    /// <summary>brief-sar-8b R-sar8b-3 — openEMS's <c>MultiGrid</c> radii, metres, increasing; null for none. Inside each, openEMS
    /// takes every second azimuth line of the grid outside it.</summary>
    public IReadOnlyList<double>? MultiGridRadiiM { get; init; }
}

/// <summary>
/// brief-sar-3 R-sar3-5 — the grid lines a setup gives, metres, per axis, and where they came from (<c>a list in mm</c>, or
/// a CSXCAD file's name) for every sentence that names them. <see cref="Problem"/> is why they could not be read; the lists
/// are then empty.
/// </summary>
public sealed record FdtdExplicitLines(IReadOnlyList<double> X, IReadOnlyList<double> Y, IReadOnlyList<double> Z, string Source)
{
    public string? Problem { get; init; }

    /// <summary>brief-sar-8b R-sar8b-3 — the lines are a cylindrical grid's: <see cref="X"/> ρ (m), <see cref="Y"/> α (rad, a closed
    /// circle), <see cref="Z"/> along the axis (m).</summary>
    public bool Cylindrical { get; init; }

    public IReadOnlyList<double> Axis(FdtdAxis a) => a switch { FdtdAxis.X => X, FdtdAxis.Y => Y, _ => Z };
}

/// <summary>Why a required line exists (R-em3d8-2d).</summary>
public enum FdtdLineKind
{
    /// <summary>On an axis-aligned metal edge or a solid conductor's face.</summary>
    MetalEdge,
    /// <summary>The thirds rule's line a third of the local cell inside the metal.</summary>
    ThirdsInside,
    /// <summary>The thirds rule's line two thirds of the local cell outside the metal.</summary>
    ThirdsOutside,
    /// <summary>The extreme of a diagonal or curved metal outline — staircased between.</summary>
    MetalExtreme,
    /// <summary>A dielectric's (or an air solid's) face or extreme — a material interface.</summary>
    MaterialFace,
    /// <summary>A sheet's plane — never moved.</summary>
    SheetPlane,
    /// <summary>A port sheet's extent — never moved.</summary>
    PortExtent,
    /// <summary>A face of the air box — never moved.</summary>
    AirBoxFace,
    /// <summary>brief-em3d-116 — a wave port's feed: the source plane, the voltage planes and the uniform lattice between —
    /// never moved.</summary>
    WavePortPlane,
    /// <summary>brief-sar-3 R-sar3-5 — a line the setup gave (<c>OpenEms.GridLines</c>).</summary>
    Given,
}

/// <summary>
/// One feature behind a required line: the named object, what about it, and where the feature itself
/// is — which for a thirds line is the metal edge, not the line.
/// </summary>
/// <param name="Feature">The object's name in the problem (<c>port/1</c>, <c>airbox/xmin</c>, a
/// solid's or a sheet's name).</param>
/// <param name="LocalCellM">For a thirds line, the local cell the rule used; otherwise null.</param>
/// <param name="LocalCellSetBy">For a thirds line whose local cell was narrowed by a neighbour, the
/// neighbour's name — the feature that actually set it.</param>
public sealed record FdtdLineSource(
    string       Feature,
    FdtdLineKind Kind,
    double       FeatureAtM,
    double?      LocalCellM     = null,
    string?      LocalCellSetBy = null)
{
    /// <summary>brief-dfr27-5 R-dfr27-5-1 — for a conductor's line on its edge, face or extreme, the side of the line its
    /// metal is on (+1 above, −1 below); null where that is not known (a material face, a port, a polyhedron's edge). A
    /// thirds line's side follows from its kind and position, so it is not stored. The merge reads it to keep a conductor's
    /// own width and thickness, and a gap between two conductors.</summary>
    public int? MetalSide { get; init; }

    /// <summary>brief-dfr27-5 R-dfr27-5-1 — an extreme of a planar conductor's curved or diagonal outline (a pour's clearance
    /// hole, a round pad), which the grid staircases between its lines anyway; a solid's extreme (a via barrel's) is not.</summary>
    public bool Outline { get; init; }

    /// <summary>The source as a phrase: <c>'strip' edge at x = 300 µm (thirds rule, inside; local cell 75 µm, set by 'pad')</c>.</summary>
    public string Describe(FdtdAxis axis)
    {
        string at = $"{FdtdGrid.AxisName(axis)} = {FdtdGrid.FormatLength(FeatureAtM)}";
        string what = Kind switch
        {
            FdtdLineKind.MetalEdge     => $"edge at {at}",
            FdtdLineKind.ThirdsInside  => $"edge at {at} (thirds rule, inside",
            FdtdLineKind.ThirdsOutside => $"edge at {at} (thirds rule, outside",
            FdtdLineKind.MetalExtreme  => $"extreme at {at}",
            FdtdLineKind.MaterialFace  => $"material face at {at}",
            FdtdLineKind.SheetPlane    => $"sheet plane at {at}",
            FdtdLineKind.PortExtent    => $"port extent at {at}",
            FdtdLineKind.WavePortPlane => $"feed line at {at}",
            FdtdLineKind.Given         => $"given line at {at}",
            _                          => $"face at {at}",
        };
        if (Kind is FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside)
            what += $"; local cell {FdtdGrid.FormatLength(LocalCellM ?? 0)}" +
                    (LocalCellSetBy is { } by ? $", set by '{by}'" : "") + ")";
        return $"'{Feature}' {what}";
    }
}

/// <summary>
/// A line the geometry requires, before or after merging. <paramref name="Fixed"/> lines (ports,
/// sheet planes, air-box faces) are never moved. <paramref name="SizeHintM"/> is the cell the line
/// asks for beside it — the thirds rule's local cell — or +∞.
/// </summary>
public sealed record FdtdRequiredLine(
    double                        PositionM,
    bool                          Fixed,
    IReadOnlyList<FdtdLineSource> Sources,
    double                        SizeHintM = double.PositiveInfinity);

/// <summary>
/// One merge (R-em3d8-3c): two required lines closer than <c>MinCell</c>, and where they went. The
/// report is the feature, not the merge — a drawing error 10 nm wide is visible here and nowhere else.
/// </summary>
public sealed record FdtdMerge(
    FdtdAxis                      Axis,
    IReadOnlyList<FdtdLineSource> First,
    double                        FirstAtM,
    IReadOnlyList<FdtdLineSource> Second,
    double                        SecondAtM,
    double                        DistanceM,
    double                        MergedAtM)
{
    /// <summary>brief-dfr27-5 R-dfr27-5-1 — for a merge of two lines closer than
    /// <see cref="FdtdGrid.CrossFeatureMergeFraction"/> of their local cell (not MinCell), that local cell; null for a merge
    /// under MinCell.</summary>
    public double? LocalCellM { get; init; }

    public string Sentence =>
        $"On {FdtdGrid.AxisName(Axis)}: {string.Join(" and ", First.Select(s => s.Describe(Axis)))} and " +
        $"{string.Join(" and ", Second.Select(s => s.Describe(Axis)))} want lines " +
        $"{FdtdGrid.FormatLength(DistanceM)} apart, " +
        (LocalCellM is { } h
            ? $"closer than a quarter of their local cell ({FdtdGrid.FormatLength(h)})"
            : "closer than MinCell") +
        $"; merged to one line at {FdtdGrid.AxisName(Axis)} = {FdtdGrid.FormatLength(MergedAtM)}.";
}

/// <summary>
/// brief-em3d-122 R-em3d122-1 — a thirds pair with a grid line strictly between its two lines: the edge (its feature and
/// position are <paramref name="Source"/>'s), where its two lines are, and the lines between them.
/// </summary>
public sealed record FdtdThirdsViolation(
    FdtdAxis              Axis,
    FdtdLineSource        Source,
    double                InsideM,
    double                OutsideM,
    IReadOnlyList<double> Between)
{
    public string Sentence =>
        $"On {FdtdGrid.AxisName(Axis)}: {Source.Describe(Axis)} has {Between.Count} grid line(s) between its pair at " +
        $"{FdtdGrid.FormatLength(InsideM)} and {FdtdGrid.FormatLength(OutsideM)}: " +
        string.Join(", ", Between.Select(FdtdGrid.FormatLength)) + ".";
}

/// <summary>
/// brief-em3d-123 R-em3d123-2 — a port extent or a material face lying inside a thirds pair (<paramref name="Edge"/> is the
/// pair's edge), which is therefore not a grid line, and the line it lands on: the line a lumped port is written on.
/// </summary>
public sealed record FdtdSnap(FdtdAxis Axis, FdtdLineSource Source, FdtdLineSource Edge, double LandsOnM)
{
    public string Sentence =>
        $"On {FdtdGrid.AxisName(Axis)}: {Source.Describe(Axis)} lies inside the thirds pair of '{Edge.Feature}' edge at " +
        $"{FdtdGrid.AxisName(Axis)} = {FdtdGrid.FormatLength(Edge.FeatureAtM)}, so it is not a grid line; " +
        (Source.Kind == FdtdLineKind.PortExtent
            ? $"the port is written on the line at {FdtdGrid.AxisName(Axis)} = {FdtdGrid.FormatLength(LandsOnM)}, " +
              $"{FdtdGrid.FormatLength(Math.Abs(LandsOnM - Source.FeatureAtM))} from where it is drawn."
            : $"openEMS averages the material over the cells beside it, the nearest line at {FdtdGrid.AxisName(Axis)} = " +
              $"{FdtdGrid.FormatLength(LandsOnM)}.");
}

/// <summary>One axis of the grid, with the reasons behind it (R-em3d8-5a).</summary>
/// <param name="Lines">Sorted, metres, PML included.</param>
/// <param name="Required">The required lines after merging — the anchors the fill ran between.</param>
/// <param name="SmallestCellM">The smallest cell on this axis.</param>
/// <param name="SmallestCellAtM">Its lower line.</param>
/// <param name="SmallestCellFeatures">The sources of the required lines bounding every interval that
/// holds a cell of the smallest size — the features that set it.</param>
/// <param name="PmlLowM">How far the grid reaches below the air box's lower face for its PML (0 when
/// that face is not absorbing).</param>
/// <param name="PmlHighM">…and above the upper face.</param>
public sealed record FdtdAxisGrid(
    FdtdAxis                         Axis,
    IReadOnlyList<double>            Lines,
    IReadOnlyList<FdtdRequiredLine>  Required,
    double                           SmallestCellM,
    double                           SmallestCellAtM,
    IReadOnlyList<FdtdLineSource>    SmallestCellFeatures,
    double                           PmlLowM,
    double                           PmlHighM);

/// <summary>
/// The grid and everything said about it (R-em3d8-5). <see cref="Cells"/> is the product of the three
/// LINE counts — openEMS's own count ("FDTD simulation size: 83x75x32 --> 199200 FDTD cells", F0 case B),
/// and the denominator of <see cref="Em3dSizeEstimate.OpenEmsBytesPerCell"/>.
/// </summary>
/// <param name="TimeStepEstimateS">The Courant limit on the smallest cells — an ESTIMATE: openEMS
/// computes its own, and brief 9 compares the two.</param>
/// <param name="ExcitationS">The Gaussian pulse's length as openEMS sizes it, 9/(π·f_c).</param>
/// <param name="Steps">Time steps to cover the pulse plus <see cref="FdtdGrid.RingDownPulses"/> pulse
/// lengths of ring-down, at the estimated step.</param>
/// <param name="Refusal">Non-null when the run would not fit in memory (R-em3d8-5b): a sentence naming
/// the feature that set the smallest cell and the setting that would relax it. The lines are still
/// here, so <c>explain</c> can show the size that was refused. brief-sar-3 — on given lines, also why the
/// problem cannot be run on them (a port between the lines it needs).</param>
public sealed record FdtdGridResult(
    FdtdAxisGrid               X,
    FdtdAxisGrid               Y,
    FdtdAxisGrid               Z,
    double                     MinCellM,
    long                       Cells,
    double                     TimeStepEstimateS,
    double                     ExcitationS,
    long                       Steps,
    long                       MemoryBytes,
    IReadOnlyList<FdtdMerge>   Merges,
    IReadOnlyList<string>      Warnings,
    string?                    Refusal)
{
    public FdtdAxisGrid Axis(FdtdAxis a) => a switch { FdtdAxis.X or FdtdAxis.Rho => X, FdtdAxis.Y or FdtdAxis.Alpha => Y, _ => Z };

    /// <summary>brief-em3d-123 R-em3d123-2 — the port extents and material faces that lie inside a thirds pair and so are not
    /// lines, each with the line it lands on. Empty for a grid with none (and for a cylindrical grid).</summary>
    public IReadOnlyList<FdtdSnap> Snaps { get; init; } = [];

    /// <summary>brief-em3d-120 — the cylindrical grid this is, or null for a Cartesian one. On a cylindrical grid <see cref="X"/>
    /// holds ρ (metres), <see cref="Y"/> α (radians, 0 to 2π; its <see cref="FdtdAxisGrid.SmallestCellM"/> is the smallest
    /// ARC, metres) and <see cref="Z"/> the coordinate along the axis (metres, the world's own).</summary>
    public FdtdCylinder? Cylinder { get; init; }

    /// <summary>brief-em3d-116 R-em3d116-1 — the wave ports' feeds this grid was built with (their lattices are among its
    /// lines), or why openEMS cannot build them. <see cref="FdtdWavePortPlan.None"/> for a problem with no wave port.</summary>
    public FdtdWavePortPlan WavePorts { get; init; } = FdtdWavePortPlan.None;

    /// <summary>The axis holding the smallest cell of the whole grid — the one that sets Δt most.</summary>
    public FdtdAxisGrid Smallest => new[] { X, Y, Z }.MinBy(g => g.SmallestCellM)!;
}

/// <summary>
/// <b>The per-axis grid generator</b> (brief-em3d-8). <see cref="Build"/> is the whole thing; the
/// three steps it is made of — <see cref="CollectRequired"/>, <see cref="Merge"/> and
/// <see cref="Fill"/> — are public so each is tested on its own.
/// </summary>
public static partial class FdtdGrid
{
    /// <summary>The speed of light in vacuum, m/s.</summary>
    public const double C0 = 299_792_458.0;

    /// <summary>
    /// R-em3d8-3b. <c>MinCell</c>'s default is this fraction of the smallest DELIBERATE feature — the
    /// smallest metal width or thickness in the problem — so an MMIC and a backplane both get a sensible
    /// floor, rather than a fixed length that suits one of them. A tenth sits below the thirds rule's
    /// own spacing on the narrowest strip (a third of its width, since the local cell is never wider
    /// than the metal) so that no deliberate line is ever merged, and above the sub-micron slivers a
    /// DBU-rounded layout produces (F0 Q2: F0's hand merge used half the grid step, which is a quarter
    /// of the wire's radius there — the same order).
    /// </summary>
    public const double MinCellFeatureFraction = 0.1;

    /// <summary>
    /// brief-dfr27-5 R-dfr27-5-1. Two lines from DIFFERENT features closer than this fraction of the local cell they sit
    /// in — the smaller of their thirds-rule cells — are merged, not only those closer than MinCell. Imported board layers
    /// land sub-micron to tens of microns apart (a ground pour's edge on one layer, the same pour on the next), and each
    /// edge's own thirds pair, placed with no knowledge of the other layer's, left a 0.97 µm cell on a 2.4 GHz board: a
    /// time step 100 times smaller than the board needs. A quarter moves no line by more than a quarter of its own cell, so
    /// every thirds line stays on its own side of its edge (a third inside, two thirds outside). The thirds lines of one
    /// conductor's edges with the metal on opposite sides — a strip's two edges, a slot's two faces — keep MinCell, and so do
    /// two lines with no local cell (the z planes of a stackup, a via's wall) unless one is a planar outline's extreme.
    /// Same-side edges of one conductor a few microns apart (a pour's jog: one edge drawn twice) merge as two layers' do.
    /// </summary>
    public const double CrossFeatureMergeFraction = 0.25;

    /// <summary>
    /// R-em3d8-5a. The nominal ring-down after the pulse, in pulse lengths. From F0's two openEMS cases:
    /// case A reached −50 dB at 0.976 ns, 6.8 of its 0.143 ns pulses; case B's ports were down 129 dB by
    /// 1 ns and its bounded run stopped at 2.0 ns, 7.0 of its 0.286 ns pulses. Pulse plus six is both.
    /// It sizes an ESTIMATE; brief 9's run stops on the ports' own decay.
    /// </summary>
    public const double RingDownPulses = 6.0;

    /// <summary>
    /// R-em3d8-2b. A local cell narrower than this many <c>MinCell</c>s cannot place the thirds rule's
    /// two lines without the merge undoing them, so such an edge keeps its line on the edge instead —
    /// and a neighbour that close is merged and REPORTED, which is the point (gate 4).
    /// </summary>
    private const double ThirdsMinCells = 3.0;

    /// <summary>EM-TFR — the sheet resistance from which a sheet is a resistor rather than metal, Ω/sq.</summary>
    private const double ResistiveSheetFromOhmPerSq = 10;

    /// <summary>
    /// EM-TFR — cells across a resistive sheet. On a metal strip the grid has only the edge currents to resolve, because the
    /// current's distribution barely moves the answer. A film's resistance IS the distribution: its corners add 0.56 of a
    /// square by crowding, and two cells across the shipped zig-zag read 278 Ω where 306 Ω is right (RESOLVED, EM-TFR).
    /// </summary>
    private const double ResistiveSheetCellsAcross = 6;

    /// <summary>
    /// R-em3d8-2b. The local cell at an edge is at most this fraction of the metal's own width there.
    /// At exactly 3/5 a strip's two inside lines sit a fifth of the width in from each edge and the
    /// three cells from one outside line to the other are all equal — no narrower cell in the middle
    /// for the grading to repair.
    /// </summary>
    private const double ThirdsWidthFraction = 3.0 / 5.0;

    /// <summary>
    /// R-em3d8-2b. …and at most this fraction of the gap to separate metal beside it: two facing edges'
    /// outside lines, ⅔ of the cell out from each, then leave a middle cell equal to the others.
    /// </summary>
    private const double ThirdsGapFraction = 3.0 / 7.0;

    /// <summary>The fill builds to the grading ratio times this, so the ratio still holds after the
    /// positions are rounded to doubles (gate 2 checks it to 1e-12).</summary>
    private const double GradingSlack = 1 - 1e-9;

    // ── the whole thing ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The grid for <paramref name="problem"/>. <paramref name="availableMemoryBytes"/> is the limit a
    /// run is refused above (R-em3d8-5b); null uses the machine's physical memory as the runtime
    /// reports it. Tests inject it.
    /// </summary>
    public static FdtdGridResult Build(Em3dProblem problem, OpenEmsGridSettings settings,
                                       long? availableMemoryBytes = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Problems() is { Count: > 0 } bad)
            throw new ArgumentException(string.Join(" ", bad), nameof(settings));
        // brief-em3d-49 — a face boundary is a sheet coincident with its face here, so it gets that sheet's lines.
        problem = Em3dFaceSheets.Apply(problem);
        if (settings.Cylindrical is { } cylindrical) return BuildCylindrical(problem, settings, cylindrical, availableMemoryBytes);
        if (settings.ExplicitLines is { } given) return BuildExplicit(problem, settings, given, availableMemoryBytes);

        var ctx = new Context(problem, settings);
        // brief-em3d-116 R-em3d116-1 — a wave port's face is fed from behind: its feed's lattice is fixed lines past the face,
        // and the face is lowered absorbing (overview D9), so the PML begins where the feed ends.
        var wavePorts = FdtdWavePorts.Plan(problem, settings, ctx.MinCell);
        if (wavePorts.Feeds.Count > 0)
        {
            problem = FdtdWavePorts.WithFeedFacesAbsorbing(problem, wavePorts);
            ctx = new Context(problem, settings);
        }
        var merges = new List<FdtdMerge>();
        var warnings = new List<string>();
        var snaps = new List<FdtdSnap>();

        FdtdAxisGrid Axis(FdtdAxis a)
        {
            // brief-em3d-122 R-em3d122-2 — a thirds pair the fill split is repaired by lowering that edge's local cell and
            // building the axis again. A grid with no split pair is built exactly once, exactly as before.
            var localCells = new Dictionary<ThirdsEdge, (double H, string? By)>();
            var keepLines = new HashSet<ThirdsEdge>();
            var acrossPorts = problem.Ports.Where(p => PortWidthAxis(p) == (int)a).Select(p => p.Name)
                                     .Concat(problem.LumpedElements.Where(le => PortWidthAxis(le) == (int)a).Select(le => le.Name))
                                     .ToHashSet(StringComparer.Ordinal);
            for (int pass = 0; ; pass++)
            {
                var axisMerges = new List<FdtdMerge>();
                var axisWarnings = new List<string>();
                // brief-em3d-123 R-em3d123-2 — a port extent, a material face or a kernel solid's own face inside a thirds pair
                // is not a line: it snaps, as upstream's ports do.
                var (collected, snapped, snappedEdges) = SnapIntoThirdsPairs([.. CollectRequired(problem, a, settings, ctx.MinCell, localCells),
                                                                              .. wavePorts.ExtraLines(a)], keepLines, acrossPorts);
                var required = Merge(collected, a, ctx.MinCell, axisMerges, axisWarnings, nearFeatures: true);
                var (lo, hi) = ctx.Faces(a);
                var lines = Fill(required, iv => ctx.MaxCell(a, iv.Lo, iv.Hi), settings.GradingRatio,
                                 lo == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0,
                                 hi == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0);
                var grid = Describe(a, lines, required, ctx.BoxMin(a), ctx.BoxMax(a));
                var broken = ThirdsViolations(grid);
                if (broken.Count == 0)
                {
                    merges.AddRange(axisMerges);
                    warnings.AddRange(axisWarnings);
                    snaps.AddRange(snapped.Select(x => new FdtdSnap(a, x.Source, x.Edge, SnapToLine(grid.Lines, x.Source.FeatureAtM)))
                                          .Where(x => x.LandsOnM != x.Source.FeatureAtM));
                    return grid;
                }
                if (pass >= MaxThirdsRepairPasses || !RepairThirds(grid, broken, localCells, settings.GradingRatio))
                    throw new InvalidOperationException("The FDTD grid's thirds pairs could not be kept whole — a defect in " +
                                                        "FdtdGrid: " + string.Join(" ", broken.Select(v => v.Sentence)));
                // brief-em3d-123 — a repair that takes a snapped edge's cell below the rule's floor would leave that edge one line
                // and no pair, which measured worst of all. That edge keeps its feature's line and its own pair instead, exactly
                // as it was before features snapped.
                foreach (var e in snappedEdges)
                    if (localCells.TryGetValue(e, out var lowered) && !(lowered.H >= ThirdsMinCells * ctx.MinCell))
                    {
                        localCells.Remove(e);
                        keepLines.Add(e);
                    }
            }
        }

        var x = Axis(FdtdAxis.X);
        var y = Axis(FdtdAxis.Y);
        var z = Axis(FdtdAxis.Z);
        // brief-em3d-42 R-em3d42-1c — an oblique face has no line to lie on; the grid staircases it.
        var oblique = problem.Solids.Where(s => s.Primitive is Em3dPolyhedron ph && ph.Faces.Any(f => !AxisNormal(ph.Normal(f))))
                                    .Select(s => s.Name)
                                    .Concat(problem.Sheets.Where(sh => sh.Frame is { NormalAxis: null }).Select(sh => sh.Name))
                                    .ToList();
        if (oblique.Count > 0)
            warnings.Add($"{string.Join(", ", oblique.Select(n => $"'{n}'"))} {(oblique.Count == 1 ? "has" : "have")} faces " +
                         "normal to no axis, which the grid staircases between the lines at their edges: the answer there " +
                         "converges with the cell size, not with the geometry.");

        long cells = checked((long)x.Lines.Count * y.Lines.Count * z.Lines.Count);
        double dt = CourantTimeStep(x.SmallestCellM, y.SmallestCellM, z.SmallestCellM);
        double pulse = ExcitationLength(problem.Frequency);
        long steps = (long)Math.Ceiling(pulse * (1 + RingDownPulses) / dt);
        long memory = Em3dSizeEstimate.OpenEmsMemoryBytes(cells);
        long limit = availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        var result = new FdtdGridResult(x, y, z, ctx.MinCell, cells, dt, pulse, steps, memory, merges, warnings, null)
                     { WavePorts = wavePorts, Snaps = snaps };
        // brief-em3d-65 R-em3d65-4d — a kernel solid's rounded features the grid will not respect, beside the oblique-face
        // warning above; the NOTE rows are the writer's (CsxcadLowering.Notes), so a run says each row once.
        warnings.AddRange(Em3dFidelity.For(problem, Em3dFidelitySolver.OpenEms, result)
                                      .Where(r => r.Severity == Em3dFidelitySeverity.Warning).Select(r => r.Sentence));
        return memory > limit ? result with { Refusal = MemoryRefusal(result, limit) } : result;
    }

    private static bool AxisNormal(Point3 n)
        => (Math.Abs(n.X) > 1 - 1e-12 ? 1 : 0) + (Math.Abs(n.Y) > 1 - 1e-12 ? 1 : 0) + (Math.Abs(n.Z) > 1 - 1e-12 ? 1 : 0) == 1;

    /// <summary>R-em3d8-5b: the refusal names the feature that set the smallest cell, and the setting
    /// that would relax it.</summary>
    private static string MemoryRefusal(FdtdGridResult r, long limit)
    {
        var s = r.Smallest;
        bool thirds = s.SmallestCellFeatures.Any(f => f.Kind is FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside);
        return $"The openEMS grid would be {r.X.Lines.Count:N0} × {r.Y.Lines.Count:N0} × {r.Z.Lines.Count:N0} = " +
               $"{r.Cells:N0} cells, about {r.MemoryBytes / 1e9:0.#} GB, more than the {limit / 1e9:0.#} GB available. " +
               $"Its smallest cell, {FormatLength(s.SmallestCellM)} on {AxisName(s.Axis)}, is set by " +
               string.Join(" and ", s.SmallestCellFeatures.Select(f => f.Describe(s.Axis))) + ". " +
               $"Setting the openEMS section's MinCellUm above {FormatLength(s.SmallestCellM)} merges the lines " +
               "that make it" + (thirds ? ", and ThirdsRule false puts that edge's line on the edge" : "") +
               "; lowering CellsPerWavelength coarsens every other cell.";
    }

    // ── step 1: required lines (R-em3d8-2) ───────────────────────────────────────────────────

    /// <summary>
    /// Every line the geometry requires on <paramref name="axis"/>, each with its source, in the
    /// problem's own order (not sorted, not merged). <paramref name="minCellM"/> decides whether an
    /// edge's local cell is wide enough for the thirds rule.
    /// </summary>
    public static List<FdtdRequiredLine> CollectRequired(Em3dProblem problem, FdtdAxis axis,
                                                         OpenEmsGridSettings settings, double minCellM)
        => CollectRequired(problem, axis, settings, minCellM, null);

    /// <summary>…with <paramref name="localCells"/> lowering named edges' local cells (brief-em3d-122 R-em3d122-2): an edge
    /// listed there takes the smaller of its own local cell and the listed one, and the listed feature as what set it.</summary>
    private static List<FdtdRequiredLine> CollectRequired(Em3dProblem problem, FdtdAxis axis, OpenEmsGridSettings settings,
                                                          double minCellM, IReadOnlyDictionary<ThirdsEdge, (double H, string? By)>? localCells)
    {
        var ctx = new Context(problem, settings);
        (double H, string? By) Lowered(string feature, double e, int metalSide, double h, string? by)
            => localCells is not null && localCells.TryGetValue(new ThirdsEdge(feature, e, metalSide), out var l) && l.H < h
                   ? l : (h, by);
        var lines = new List<FdtdRequiredLine>();
        void Add(double at, bool fix, FdtdLineSource src, double hint = double.PositiveInfinity)
            => lines.Add(new FdtdRequiredLine(at, fix, [src], hint));

        foreach (var shape in ctx.Shapes)
        {
            // brief-em3d-42 R-em3d42-1c — a polyhedron, or a sheet not lying flat: a line on every coordinate
            // of every axis-aligned edge, and on its extremes. What lies between on an oblique face is
            // staircased, and Build warns naming it. A sheet's own plane is a fixed line, as a flat one's is.
            if (shape.EdgeLines is { } edgeLines)
            {
                var kind = shape.Conductor ? FdtdLineKind.MetalEdge : FdtdLineKind.MaterialFace;
                var extreme = shape.Conductor ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace;
                if (shape.IsSheet && shape.SheetAxis == axis)
                {
                    Add(A3Of(shape, axis), true, new FdtdLineSource(shape.Name, FdtdLineKind.SheetPlane, A3Of(shape, axis)));
                    continue;
                }
                var (lo, hi) = shape.Range(axis);
                var at = edgeLines[(int)axis];
                foreach (double e in at) Add(e, false, new FdtdLineSource(shape.Name, kind, e));
                foreach (double ex in new[] { lo, hi })
                    if (!at.Any(e => Math.Abs(e - ex) <= ctx.Tol))
                        Add(ex, false, new FdtdLineSource(shape.Name, extreme, ex));
                continue;
            }

            // brief-em3d-65 R-em3d65-3e — a kernel solid: its extremes, a line on every planar face normal to this axis,
            // each other face's extremes (a bore's, a chamfer's), and for a conductor the thirds rule at each straight
            // edge parallel to another axis. Between those, curved and oblique faces are staircased (Em3dFidelity says so).
            if (shape.Kernel is { } kernel)
            {
                KernelLines(kernel, shape, axis, settings, minCellM, ctx, Add, Lowered);
                continue;
            }

            if (axis == FdtdAxis.Z)
            {
                if (shape.IsSheet)
                    Add(shape.Z0, true, new FdtdLineSource(shape.Name, FdtdLineKind.SheetPlane, shape.Z0));
                else
                {
                    var kind = shape.Conductor ? FdtdLineKind.MetalEdge : FdtdLineKind.MaterialFace;
                    Add(shape.Z0, false, new FdtdLineSource(shape.Name, kind, shape.Z0) { MetalSide = shape.Conductor ? +1 : null });
                    Add(shape.Z1, false, new FdtdLineSource(shape.Name, kind, shape.Z1) { MetalSide = shape.Conductor ? -1 : null });
                }
                continue;
            }

            if (shape.Rings is null)
            {
                // Curved or swept: its extremes only; the grid staircases the rest (em-3d.md §3).
                var kind = shape.Conductor ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace;
                var (lo, hi) = shape.Range(axis);
                Add(lo, false, new FdtdLineSource(shape.Name, kind, lo) { MetalSide = shape.Conductor ? +1 : null });
                Add(hi, false, new FdtdLineSource(shape.Name, kind, hi) { MetalSide = shape.Conductor ? -1 : null });
                continue;
            }

            bool thirdsHere = settings.ThirdsRule && shape.Conductor && shape.Thin;
            foreach (var ring in shape.Rings)
            {
                var edgeAt = new List<double>();
                for (int i = 0; i < ring.Count; i++)
                {
                    var p = ring[i];
                    var q = ring[(i + 1) % ring.Count];
                    double pa = A(p, axis), qa = A(q, axis), pb = B(p, axis), qb = B(q, axis);
                    if (Math.Abs(pa - qa) > ctx.Tol || Math.Abs(pb - qb) <= ctx.Tol) continue;   // not aligned, or no length
                    double e = (pa + qa) / 2;
                    edgeAt.Add(e);
                    if (!shape.Conductor)
                    {
                        Add(e, false, new FdtdLineSource(shape.Name, FdtdLineKind.MaterialFace, e));
                        continue;
                    }

                    double mid = (pb + qb) / 2;
                    int metalSide = shape.Contains2D(Pt(e + ctx.Probe, mid, axis)) ? +1
                                  : shape.Contains2D(Pt(e - ctx.Probe, mid, axis)) ? -1 : 0;
                    bool bordersMetal = metalSide != 0 &&
                        ctx.ConductorAt(Pt(e - metalSide * ctx.Probe, mid, axis), shape.Z0, shape.Z1, shape);
                    if (!thirdsHere || metalSide == 0 || bordersMetal)
                    {
                        Add(e, false, new FdtdLineSource(shape.Name, FdtdLineKind.MetalEdge, e) { MetalSide = metalSide == 0 ? null : metalSide });
                        continue;
                    }

                    var (h0, by0) = ctx.LocalCell(axis, shape, e, metalSide, Math.Min(pb, qb), Math.Max(pb, qb));
                    var (h, setBy) = Lowered(shape.Name, e, metalSide, h0, by0);
                    double outside = e - metalSide * 2 * h / 3;
                    // Too narrow for two lines that the merge would not undo, an edge on the air box
                    // whose outside line would leave it, or an edge SHORTER than its local cell — a facet
                    // of a tessellated circle, whose thirds pair would only crowd its neighbours' lines:
                    // the line goes on the edge.
                    if (!(h >= ThirdsMinCells * minCellM) || outside < ctx.BoxMin(axis) || outside > ctx.BoxMax(axis)
                        || Math.Abs(qb - pb) < h)
                    {
                        Add(e, false, new FdtdLineSource(shape.Name, FdtdLineKind.MetalEdge, e) { MetalSide = metalSide });
                        continue;
                    }
                    Add(e + metalSide * h / 3, false,
                        new FdtdLineSource(shape.Name, FdtdLineKind.ThirdsInside, e, h, setBy), h);
                    Add(outside, false,
                        new FdtdLineSource(shape.Name, FdtdLineKind.ThirdsOutside, e, h, setBy), h);
                }

                // A diagonal or curved edge contributes no line beyond its extremes (R-em3d8-2a); an
                // extreme that IS an aligned edge already has its line (or its thirds pair) above.
                double min = ring.Min(q => A(q, axis)), max = ring.Max(q => A(q, axis));
                var extremeKind = shape.Conductor ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace;
                foreach (double ex in new[] { min, max })
                    if (!edgeAt.Any(e => Math.Abs(e - ex) <= ctx.Tol))
                        Add(ex, false, new FdtdLineSource(shape.Name, extremeKind, ex)
                                       {
                                           MetalSide = !shape.Conductor ? null : ReferenceEquals(ring, shape.Rings[0]) == (ex == min) ? +1 : -1,
                                           Outline = shape.Conductor && shape.Thin,
                                       });
            }
        }

        // brief-sar-11 R-sar11-3 — a voxel body with no grid step of its own: a line on every voxel boundary inside the box, so
        // each FDTD cell is one voxel and CSXCAD's sampling at the cell's centre reads that voxel exactly.
        foreach (var volume in problem.Solids.Select(s => s.Labels).OfType<Em3dLabelVolume>().Distinct())
        {
            if (volume.GridStepM > 0) continue;
            double[] at = axis switch { FdtdAxis.X => volume.X, FdtdAxis.Y => volume.Y, _ => volume.Z };
            foreach (double e in at)
                if (e >= ctx.BoxMin(axis) && e <= ctx.BoxMax(axis))
                    Add(e, false, new FdtdLineSource(volume.Body, FdtdLineKind.MaterialFace, e));
        }

        foreach (var port in problem.Ports)
        {
            double lo = A3(port.Min, axis), hi = A3(port.Max, axis);
            Add(lo, true, new FdtdLineSource(port.Name, FdtdLineKind.PortExtent, lo));
            if (hi != lo) Add(hi, true, new FdtdLineSource(port.Name, FdtdLineKind.PortExtent, hi));
        }
        // brief-sar-3 R-sar3-3 — a lumped element's sheet is gridded exactly as a lumped port's is, and snaps the same way.
        foreach (var le in problem.LumpedElements)
        {
            double lo = A3(le.Min, axis), hi = A3(le.Max, axis);
            Add(lo, true, new FdtdLineSource(le.Name, FdtdLineKind.PortExtent, lo));
            if (hi != lo) Add(hi, true, new FdtdLineSource(le.Name, FdtdLineKind.PortExtent, hi));
        }
        // brief-sar-8b — a sheet on a cylinder ends on two lines along its own axis (the cylindrical grid's z, the only axis it is
        // gridded along by this collection; ρ and α are FdtdGrid.Cylindrical's).
        foreach (var cs in problem.CylinderSheets.Where(c => c.Patch.Axis == (int)axis))
        {
            Add(cs.Patch.Lo, true, new FdtdLineSource(cs.Name, FdtdLineKind.MetalEdge, cs.Patch.Lo));
            if (cs.Patch.Hi != cs.Patch.Lo) Add(cs.Patch.Hi, true, new FdtdLineSource(cs.Name, FdtdLineKind.MetalEdge, cs.Patch.Hi));
        }

        string n = AxisName(axis);
        Add(ctx.BoxMin(axis), true, new FdtdLineSource(Em3dAirBox.FaceName(n + "min"), FdtdLineKind.AirBoxFace, ctx.BoxMin(axis)));
        Add(ctx.BoxMax(axis), true, new FdtdLineSource(Em3dAirBox.FaceName(n + "max"), FdtdLineKind.AirBoxFace, ctx.BoxMax(axis)));
        return lines;
    }

    /// <summary>brief-em3d-65 R-em3d65-3e — the required lines of one kernel solid on <paramref name="axis"/>.</summary>
    private static void KernelLines(Em3dShapeSolid k, Shape shape, FdtdAxis axis, OpenEmsGridSettings settings, double minCellM,
                                    Context ctx, Action<double, bool, FdtdLineSource, double> add,
                                    Func<string, double, int, double, string?, (double H, string? By)> lowered)
    {
        int a = (int)axis;
        var faceKind = shape.Conductor ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace;
        var placed = new List<double>();
        void Line(double at, FdtdLineKind kind)
        {
            if (placed.Any(p => Math.Abs(p - at) <= ctx.Tol)) return;
            placed.Add(at);
            add(at, false, new FdtdLineSource(shape.Name, kind, at), double.PositiveInfinity);
        }
        static double Lo((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, int a) => a == 0 ? b.X0 : a == 1 ? b.Y0 : b.Z0;
        static double Hi((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, int a) => a == 0 ? b.X1 : a == 1 ? b.Y1 : b.Z1;

        // Planar faces normal to this axis first, so an extreme that is one keeps the face's line.
        foreach (var f in k.Faces)
            if (Em3dShapeSolid.NormalAxis(f) == a) Line(Lo(f.Box, a), faceKind);
        var (lo, hi) = shape.Range(axis);
        Line(lo, faceKind);
        Line(hi, faceKind);
        foreach (var f in k.Faces)
            if (Em3dShapeSolid.NormalAxis(f) is null)
            {
                Line(Lo(f.Box, a), faceKind);
                Line(Hi(f.Box, a), faceKind);
            }

        if (!shape.Conductor || !settings.ThirdsRule) return;
        var byName = k.Faces.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var thirds = new HashSet<(double, int)>();
        foreach (var e in k.Edges)
        {
            if (e.Kind != "line" || e.Polyline.Count < 2) continue;
            var p = e.Polyline[0];
            var q = e.Polyline[^1];
            double[] pa = [p.X, p.Y, p.Z], qa = [q.X, q.Y, q.Z];
            int moving = 0, along = -1;
            for (int j = 0; j < 3; j++)
                if (Math.Abs(pa[j] - qa[j]) > ctx.Tol) { moving++; along = j; }
            if (moving != 1 || along == a) continue;
            double at = pa[a];
            // The face of the pair lying ON this line (normal to this axis) and the one leaving it; metal is behind the first.
            Em3dShapeFace? on = null, off = null;
            foreach (string name in new[] { e.FaceA, e.FaceB })
                foreach (var f in byName.GetValueOrDefault(name) ?? [])
                {
                    if (Em3dShapeSolid.NormalAxis(f) == a && Math.Abs(Lo(f.Box, a) - at) <= ctx.Tol) on ??= f;
                    else if (Lo(f.Box, a) <= at + ctx.Tol && Hi(f.Box, a) >= at - ctx.Tol) off ??= f;
                }
            if (on is null || off is null) continue;
            // Behind the face lying on the line: the side its neighbour extends to. A convex edge's neighbour runs back
            // over the metal; a concave edge's runs away from it, and keeps only its line on the face.
            int metalSide = Lo(off.Box, a) < at - ctx.Tol ? -1 : Hi(off.Box, a) > at + ctx.Tol ? +1 : 0;
            if (metalSide == 0) continue;
            if (Em3dPieceSide(on, k, a) is int outward && outward != -metalSide) continue;
            double width = Hi(off.Box, a) - Lo(off.Box, a);
            var (h, by) = lowered(shape.Name, at, metalSide, Math.Min(ctx.MaxCellAt(axis, at), ThirdsWidthFraction * width), null);
            double outside = at - metalSide * 2 * h / 3;
            if (!(h >= ThirdsMinCells * minCellM) || outside < ctx.BoxMin(axis) || outside > ctx.BoxMax(axis)) continue;
            if (!thirds.Add((at, metalSide))) continue;
            add(at + metalSide * h / 3, false, new FdtdLineSource(shape.Name, FdtdLineKind.ThirdsInside, at, h, by), h);
            add(outside, false, new FdtdLineSource(shape.Name, FdtdLineKind.ThirdsOutside, at, h, by), h);
        }
    }

    /// <summary>The sign of a planar face's outward normal along <paramref name="axis"/>, read from its triangles'
    /// winding; null when it has none.</summary>
    private static int? Em3dPieceSide(Em3dShapeFace f, Em3dShapeSolid k, int axis)
    {
        double sum = 0;
        for (int t = f.FirstTriangle; t < f.FirstTriangle + f.TriangleCount && t < k.Display.Triangles.Count; t++)
        {
            var tri = k.Display.Triangles[t];
            Point3 p = k.Display.Vertices[tri.A], q = k.Display.Vertices[tri.B], r = k.Display.Vertices[tri.C];
            sum += axis switch
            {
                0 => (q.Y - p.Y) * (r.Z - p.Z) - (q.Z - p.Z) * (r.Y - p.Y),
                1 => (q.Z - p.Z) * (r.X - p.X) - (q.X - p.X) * (r.Z - p.Z),
                _ => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X),
            };
        }
        return sum > 0 ? 1 : sum < 0 ? -1 : null;
    }

    // ── step 2: merging (R-em3d8-3) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sorts <paramref name="lines"/> and merges any two closer than <paramref name="minCellM"/>
    /// (R-em3d8-3a): two movable lines go to their midpoint; a movable line goes onto a fixed one; two
    /// fixed lines are both kept and a warning names them and the cell they force. Lines that coincide
    /// to rounding become one line carrying every source, and are not reported — two features sharing a
    /// coordinate is the ordinary case (a strip's bottom on its substrate's top), not a merge.
    /// <para>brief-dfr27-5 R-dfr27-5-1 — with <paramref name="nearFeatures"/>, two lines also merge when closer than
    /// <see cref="CrossFeatureMergeFraction"/> of the local cell they sit in, the smaller of their thirds-rule cells
    /// (<see cref="FdtdRequiredLine.SizeHintM"/>), and only while every line the merged one stands for lies within that distance
    /// of the first, so a run of near features cannot carry a line away from any of them. A thirds line goes onto a feature's
    /// own line (an edge, an extreme, a face) rather than meeting it halfway: the pair is a refinement, the feature is the
    /// geometry. One conductor's lines from edges with its metal on opposite sides — a strip's width, a slot — keep MinCell,
    /// and so do two lines neither of which has a thirds cell (a via's wall, a stackup's planes).</para>
    /// </summary>
    /// <param name="nearFeatures">The rule above; false merges under MinCell only, as before brief-dfr27-5. The Cartesian
    /// builder passes true; a cylindrical grid's radial lines are circles, which the rule was not measured on.</param>
    public static List<FdtdRequiredLine> Merge(IReadOnlyList<FdtdRequiredLine> lines, FdtdAxis axis, double minCellM,
                                               List<FdtdMerge> merges, List<string> warnings, bool nearFeatures = false)
    {
        var sorted = lines.OrderBy(l => l.PositionM).ToList();    // stable: ties keep the problem's order
        if (sorted.Count == 0) return sorted;
        double span = sorted[^1].PositionM - sorted[0].PositionM;
        double tol = CoincidenceTolerance(span);

        // Coincident first, so a fixed line and a movable one on the same coordinate are one fixed line.
        var one = new List<FdtdRequiredLine> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var k = one[^1];
            var l = sorted[i];
            if (l.PositionM - k.PositionM <= tol)
                one[^1] = new FdtdRequiredLine(k.Fixed || !l.Fixed ? k.PositionM : l.PositionM, k.Fixed || l.Fixed,
                                               [.. k.Sources, .. l.Sources], Math.Min(k.SizeHintM, l.SizeHintM));
            else
                one.Add(l);
        }

        var kept = new List<FdtdRequiredLine> { one[0] };
        var members = new List<FdtdRequiredLine> { one[0] };    // the original lines the last kept line stands for
        for (int i = 1; i < one.Count; i++)
        {
            var k = kept[^1];
            var l = one[i];
            double d = l.PositionM - k.PositionM;
            double? local = null;
            if (d >= minCellM)
            {
                double h = Math.Min(k.SizeHintM, l.SizeHintM);
                // An outline's extreme with no thirds cell beside it sits between the required lines either side.
                if (nearFeatures && !double.IsFinite(h) && (OnlyOutline(k) || OnlyOutline(l)) && !SharesFeature(k, l))
                    h = Math.Min(kept.Count > 1 ? members[0].PositionM - kept[^2].PositionM : double.PositiveInfinity,
                                 NextBeyond(i, l.PositionM - members[0].PositionM));
                if (!nearFeatures || !double.IsFinite(h) || k.Fixed && l.Fixed ||
                    !(l.PositionM - members[0].PositionM < CrossFeatureMergeFraction * h) ||
                    members.Any(m => l.PositionM - m.PositionM >= minCellM && OneConductorsSides(m, l)))
                {
                    kept.Add(l);
                    members = [l];
                    continue;
                }
                local = h;
            }

            if (k.Fixed && l.Fixed)
            {
                kept.Add(l);
                members = [l];
                warnings.Add($"On {AxisName(axis)}: {string.Join(" and ", k.Sources.Select(s => s.Describe(axis)))} and " +
                             $"{string.Join(" and ", l.Sources.Select(s => s.Describe(axis)))} are " +
                             $"{FormatLength(d)} apart, closer than MinCell ({FormatLength(minCellM)}), and neither may " +
                             $"move, so they force a {FormatLength(d)} cell — and the time step with it.");
                continue;
            }
            double at = k.Fixed ? k.PositionM : l.Fixed ? l.PositionM
                      : local is not null && Feature(k) != Feature(l) ? (Feature(k) ? k.PositionM : l.PositionM)
                      : local is not null && OnlyOutline(k) != OnlyOutline(l) ? (OnlyOutline(k) ? l.PositionM : k.PositionM)
                      : (k.PositionM + l.PositionM) / 2;
            merges.Add(new FdtdMerge(axis, k.Sources, k.PositionM, l.Sources, l.PositionM, d, at) { LocalCellM = local });
            kept[^1] = new FdtdRequiredLine(at, k.Fixed || l.Fixed, [.. k.Sources, .. l.Sources],
                                            Math.Min(k.SizeHintM, l.SizeHintM));
            members.Add(l);
        }
        return kept;

        static bool Feature(FdtdRequiredLine a)
            => a.Sources.Any(x => x.Kind is not (FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside));

        static bool OnlyOutline(FdtdRequiredLine a) => a.Sources.All(x => x.Outline);

        static bool SharesFeature(FdtdRequiredLine a, FdtdRequiredLine b)
            => a.Sources.Any(x => b.Sources.Any(y => string.Equals(x.Feature, y.Feature, StringComparison.Ordinal)));

        // The interval from line i to the next line farther from it than the run being merged is wide: nearer ones are the
        // same cluster of near features, not the cell beside it.
        double NextBeyond(int i, double run)
        {
            for (int j = i + 1; j < one.Count; j++)
                if (one[j].PositionM - one[i].PositionM > run) return one[j].PositionM - one[i].PositionM;
            return double.PositiveInfinity;
        }
    }

    /// <summary>
    /// brief-dfr27-5 R-dfr27-5-1 — whether two lines (<paramref name="lo"/> below <paramref name="hi"/>) come from one
    /// conductor's edges with its metal on opposite sides: its width or thickness between them, or a slot in it. Those are
    /// drawn on purpose and merge under MinCell only. Two same-side edges of one conductor a few microns apart are one edge
    /// drawn twice (an imported pour's jog), and two conductors' lines face no gap the rule could close: facing in one layer,
    /// the thirds rule's gap clamp already keeps their lines a whole local cell apart.
    /// </summary>
    private static bool OneConductorsSides(FdtdRequiredLine lo, FdtdRequiredLine hi)
        => lo.Sources.Any(x => MetalSide(x, lo.PositionM) is { } sx &&
                               hi.Sources.Any(y => string.Equals(x.Feature, y.Feature, StringComparison.Ordinal) &&
                                                   MetalSide(y, hi.PositionM) is { } sy && sy != sx));

    private static int? MetalSide(FdtdLineSource src, double at) => src.Kind switch
    {
        FdtdLineKind.ThirdsInside  => Math.Sign(at - src.FeatureAtM),
        FdtdLineKind.ThirdsOutside => -Math.Sign(at - src.FeatureAtM),
        _                          => src.MetalSide,
    };

    // ── step 3: fill and smooth (R-em3d8-4) ──────────────────────────────────────────────────

    /// <summary>
    /// The lines between and around <paramref name="required"/> (sorted, merged; the first and last are
    /// the air box's faces). Every cell of an interval is at most <paramref name="maxCell"/> of it, every
    /// two neighbouring cells differ by at most <paramref name="ratio"/>, and each interval is
    /// partitioned exactly — no remainder cell. <paramref name="pmlLow"/>/<paramref name="pmlHigh"/>
    /// uniform cells, each the size of the cell at that face, are added OUTSIDE the first and last line
    /// (R-em3d8-4c): the box grows outward to hold its PML, never inward over the structure.
    /// </summary>
    /// <remarks>
    /// Each required line i gets a size s_i — the cell it wants beside it: the smaller interval's cap,
    /// its thirds-rule hint, no wider than either interval beside it, and grown at most (ratio − 1) per
    /// unit distance from any other line's (the size field is Lipschitz, which is what makes grading
    /// between two lines possible). Every interval then starts and ends with cells within √ratio of its
    /// end sizes, so the two cells meeting at a line differ by at most the ratio. An interval too short
    /// to do that — a gap between the partitions of 1, 2 and 3 cells — lowers its larger end until it
    /// can (or, failing that, makes both ends uniform), and the sizes are re-smoothed until none is. The fill of one interval reads only its own two
    /// sizes, so a far edit changes nothing here (R-em3d8-4d).
    /// </remarks>
    public static List<double> Fill(IReadOnlyList<FdtdRequiredLine> required,
                                    Func<(double Lo, double Hi), double> maxCell,
                                    double ratio, int pmlLow, int pmlHigh)
    {
        int m = required.Count;
        if (m < 2) throw new ArgumentException("The fill needs the two air-box faces at least.", nameof(required));
        double r = ratio * GradingSlack;
        double g = r - 1;
        var x = required.Select(l => l.PositionM).ToArray();
        var len = new double[m - 1];
        var cap = new double[m - 1];
        for (int i = 0; i < m - 1; i++)
        {
            len[i] = x[i + 1] - x[i];
            cap[i] = maxCell((x[i], x[i + 1]));
        }

        var s = new double[m];
        for (int j = 0; j < m; j++)
        {
            double v = required[j].SizeHintM;
            if (j > 0)     v = Math.Min(v, Math.Min(cap[j - 1], len[j - 1]));
            if (j < m - 1) v = Math.Min(v, Math.Min(cap[j], len[j]));
            s[j] = v;
        }

        // Smooth (Lipschitz, slope g), then make every interval partitionable; repeat until stable.
        // Sizes only ever fall, and each pass that changes one is forced by an interval that is
        // otherwise impossible, so this terminates; the bound is a guard against a defect, not a limit.
        for (int pass = 0; ; pass++)
        {
            if (pass > 4 * m + 64)
                throw new InvalidOperationException("The FDTD grid's size smoothing did not settle — a defect in FdtdGrid.Fill.");
            Smooth(x, s, g);
            bool changed = false;
            for (int i = 0; i < m - 1; i++)
            {
                if (Plan(len[i], s[i], s[i + 1], cap[i], r) is not null) continue;
                changed = true;
                // Lower the LARGER end first, down a ladder fine enough not to step over a feasible
                // window, and keep the smaller one: lowering both would push the problem into the
                // interval on the other side of the smaller end, and between short intervals that
                // cascades into cells far smaller than any feature asks for.
                bool leftSmaller = s[i] <= s[i + 1];
                double keep = leftSmaller ? s[i] : s[i + 1];
                double big = leftSmaller ? s[i + 1] : s[i];
                double? found = null;
                double step = Math.Pow(r, -1.0 / 8);
                for (double c = big * step; c >= keep / (r * r); c *= step)
                {
                    bool ok = leftSmaller ? Plan(len[i], keep, c, cap[i], r) is not null
                                          : Plan(len[i], c, keep, cap[i], r) is not null;
                    if (ok) { found = c; break; }
                }
                if (found is { } f)
                {
                    if (leftSmaller) s[i + 1] = f; else s[i] = f;
                    continue;
                }
                // Nothing with the smaller end kept: both ends to a uniform fill no wider than it.
                double across = Math.Ceiling(len[i] / keep - 1e-9);
                double u = len[i] / across;
                if (u > keep) u = len[i] / (across + 1);
                s[i] = Math.Min(s[i], u);
                s[i + 1] = Math.Min(s[i + 1], u);
            }
            if (!changed) break;
        }

        var lines = new List<double>();
        for (int k = pmlLow; k >= 1; k--) lines.Add(x[0] - k * s[0]);
        for (int i = 0; i < m - 1; i++)
        {
            lines.Add(x[i]);
            var cells = Plan(len[i], s[i], s[i + 1], cap[i], r)
                        ?? throw new InvalidOperationException("An FDTD grid interval could not be partitioned after smoothing.");
            // Compensated prefix sums: a long interval of small cells must not accumulate a rounding
            // error comparable to a cell.
            double sum = 0, comp = 0;
            for (int k = 0; k < cells.Length - 1; k++)
            {
                double yk = cells[k] - comp;
                double t = sum + yk;
                comp = (t - sum) - yk;
                sum = t;
                lines.Add(x[i] + sum);
            }
        }
        lines.Add(x[m - 1]);
        for (int k = 1; k <= pmlHigh; k++) lines.Add(x[m - 1] + k * s[m - 1]);
        return lines;
    }

    /// <summary>s_j ← min over k of s_k + g·|x_j − x_k|, in two linear sweeps.</summary>
    private static void Smooth(double[] x, double[] s, double g)
    {
        for (int j = 1; j < s.Length; j++) s[j] = Math.Min(s[j], s[j - 1] + g * (x[j] - x[j - 1]));
        for (int j = s.Length - 2; j >= 0; j--) s[j] = Math.Min(s[j], s[j + 1] + g * (x[j + 1] - x[j]));
    }

    /// <summary>
    /// The cells partitioning an interval of length <paramref name="L"/> whose first cell is within √r of
    /// <paramref name="sa"/>, whose last is within √r of <paramref name="sb"/>, with neighbours within r
    /// and none above <paramref name="H"/> — or null when no such partition exists.
    /// </summary>
    /// <remarks>
    /// For n cells the LARGEST such partition is c_k = min(H, A·r^k, B·r^(n−k+1)) and the SMALLEST is
    /// max(A·r^−(k−1), B·r^−(n−k)), with A = sa/√r and B = sb/√r; each is graded within r because it is a
    /// min (or max) of sequences that each are. Their log-linear blend c_k = min_k^(1−t)·max_k^t is graded
    /// within r for every t, and its sum rises continuously with t — so n is the fewest cells whose
    /// largest partition reaches L, and t is found by bisection.
    /// </remarks>
    internal static double[]? Plan(double L, double sa, double sb, double H, double r)
    {
        if (!(L > 0)) return null;
        double lr = Math.Log(r);
        double lA = Math.Log(sa) - lr / 2, lB = Math.Log(sb) - lr / 2, lH = Math.Log(H);
        if (lA > lH || lB > lH) return null;
        int nGrow = (int)Math.Ceiling(Math.Abs(lA - lB) / lr - 1e-12);

        // In logs: the smallest profile decays geometrically into the middle of a long interval and
        // would underflow to zero-width cells as plain numbers.
        double LnMax(int n, int k) => Math.Min(lH, Math.Min(lA + k * lr, lB + (n - k + 1) * lr));
        double LnMin(int n, int k) => Math.Max(lA - (k - 1) * lr, lB - (n - k) * lr);
        double Lmax(int n) { double t = 0; for (int k = 1; k <= n; k++) t += Math.Exp(LnMax(n, k)); return t; }
        double Lmin(int n) { double t = 0; for (int k = 1; k <= n; k++) t += Math.Exp(LnMin(n, k)); return t; }

        // The fewest cells whose largest partition reaches L: Lmax rises with n, so bisect on n. "Reaches"
        // is to rounding: a uniform interval at its cap sums to L only within an ulp or two, and one
        // cell more than that can be too many for the smallest partition.
        double reach = L * (1 - 1e-12);
        int lo = Math.Max(1, nGrow), hi = lo;
        while (Lmax(hi) < reach)
        {
            lo = hi + 1;
            hi = checked(hi * 2);
        }
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Lmax(mid) >= reach) hi = mid; else lo = mid + 1;
        }
        int n = lo;
        if (Lmin(n) > L * (1 + 1e-12)) return null;

        var lnMin = new double[n];
        var lnMax = new double[n];
        for (int k = 1; k <= n; k++) { lnMin[k - 1] = LnMin(n, k); lnMax[k - 1] = LnMax(n, k); }
        double Sum(double t)
        {
            double acc = 0;
            for (int k = 0; k < n; k++) acc += Math.Exp(lnMin[k] + t * (lnMax[k] - lnMin[k]));
            return acc;
        }
        double a = 0, b = 1;
        for (int it = 0; it < 200 && b - a > 1e-16; it++)
        {
            double mid = (a + b) / 2;
            if (Sum(mid) < L) a = mid; else b = mid;
        }
        double tt = (a + b) / 2;
        var cells = new double[n];
        double total = 0;
        for (int k = 0; k < n; k++) total += cells[k] = Math.Exp(lnMin[k] + tt * (lnMax[k] - lnMin[k]));
        // A uniform rescale to the exact length keeps every ratio.
        double f = L / total;
        for (int k = 0; k < n; k++) cells[k] *= f;
        return cells;
    }

    // ── a feature's line inside a thirds pair (brief-em3d-123 R-em3d123-2) ──────────────────────

    /// <summary>
    /// Removes from <paramref name="raw"/> every line strictly inside a thirds pair that is only a port extent, a material face,
    /// or the pair's own conductor's face ON its edge (a kernel solid's: <see cref="KernelLines"/> puts a line on every planar
    /// face, and the ring path never does on a thirds edge — D3). Such a line, almost always on the edge itself, gave the edge
    /// three lines with the middle one on it: the grid the rule exists to avoid. Measured against closed forms (RESOLVED.md,
    /// brief-em3d-123), keeping the pair and letting the feature snap — as upstream's MSL_NotchFilter tutorial leaves its port
    /// extents off the lines — took a strip's Z₀ from 6-19 % low to within 2.5 %, with fewer cells and a longer time step.
    /// <para>A port extent snaps only ACROSS the port (<see cref="PortWidthAxis"/>): its plane and the two ends it drives between
    /// stay lines, since moving the plane a third of a cell moves the reference plane with it. Any other line inside a pair
    /// (an air-box face, a sheet plane, a wave port's feed, another solid's edge or extreme, another pair's line) stays, and
    /// so does the pair: dropping the pair for an edge line instead measured worst of all.</para>
    /// <para>The removed port extents and material faces come back as snaps, so a run's notes and <c>explain</c> say where
    /// each one landed; <c>CsxcadWriter</c> writes a lumped port on the line <see cref="SnapToLine"/> names, since openEMS's
    /// excitation, unlike its lumped element, does not snap.</para>
    /// </summary>
    private static (List<FdtdRequiredLine> Lines, List<(FdtdLineSource Source, FdtdLineSource Edge)> Snapped, List<ThirdsEdge> Edges)
        SnapIntoThirdsPairs(List<FdtdRequiredLine> raw, IReadOnlySet<ThirdsEdge> keepLines, IReadOnlySet<string> acrossPorts)
    {
        var snapped = new List<(FdtdLineSource, FdtdLineSource)>();
        var edges = new List<ThirdsEdge>();
        var drop = new HashSet<FdtdRequiredLine>(ReferenceEqualityComparer.Instance);
        foreach (var pair in ThirdsPairs(raw))
        {
            var edge = new ThirdsEdge(pair.Source.Feature, pair.Source.FeatureAtM, pair.MetalSide);
            if (keepLines.Contains(edge)) continue;
            double lo = Math.Min(pair.InsideM, pair.OutsideM), hi = Math.Max(pair.InsideM, pair.OutsideM);
            double tol = 1e-9 * (hi - lo);
            foreach (var line in raw)
            {
                if (!(line.PositionM > lo + tol && line.PositionM < hi - tol) || drop.Contains(line)) continue;
                bool onEdge = Math.Abs(line.PositionM - pair.Source.FeatureAtM) <= tol;
                if (!line.Sources.All(src => src.Kind == FdtdLineKind.PortExtent && acrossPorts.Contains(src.Feature) ||
                                             src.Kind == FdtdLineKind.MaterialFace ||
                                             src.Kind == FdtdLineKind.MetalExtreme && src.Feature == pair.Source.Feature && onEdge))
                    continue;
                drop.Add(line);
                if (!edges.Contains(edge)) edges.Add(edge);
                foreach (var src in line.Sources)
                    if (src.Kind is FdtdLineKind.PortExtent or FdtdLineKind.MaterialFace) snapped.Add((src, pair.Source));
            }
        }
        return drop.Count == 0 ? (raw, snapped, edges) : ([.. raw.Where(l => !drop.Contains(l))], snapped, edges);
    }

    /// <summary>
    /// The axis a rectangular lumped port spans ACROSS — neither its sheet's normal (its extent there is zero) nor the axis it
    /// drives along — or null for a wave port, a coaxial one, or a sheet with no such axis. Only this extent snaps.
    /// </summary>
    public static int? PortWidthAxis(Em3dPort p)
        => p.Kind != Em3dPortKind.Lumped || p.Annulus is not null ? null : WidthAxis(p.Min, p.Max, p.Direction);

    /// <summary>brief-sar-3 — a lumped element's, by the same rule as a lumped port's.</summary>
    public static int? PortWidthAxis(Em3dLumpedElement e) => WidthAxis(e.Min, e.Max, e.Direction);

    private static int? WidthAxis(Point3 min, Point3 max, Point3 direction)
    {
        double[] lo = [min.X, min.Y, min.Z], hi = [max.X, max.Y, max.Z], d = [direction.X, direction.Y, direction.Z];
        int? axis = null;
        for (int i = 0; i < 3; i++)
            if (hi[i] > lo[i] && Math.Abs(d[i]) < 0.5)
            {
                if (axis is not null) return null;
                axis = i;
            }
        return axis;
    }

    /// <summary>
    /// The grid line a coordinate is written on: <paramref name="at"/> itself when a line lies there (to rounding), else the
    /// nearest line — for an extent inside a thirds pair, the pair's inside line, a third of a cell away against two thirds.
    /// openEMS snaps a lumped element's box the same way (<c>Operator::SnapBox2Mesh</c>, nearest line).
    /// </summary>
    public static double SnapToLine(IReadOnlyList<double> lines, double at)
    {
        if (lines.Count == 0) return at;
        double nearest = lines.MinBy(l => Math.Abs(l - at));
        return Math.Abs(nearest - at) <= CoincidenceTolerance(lines[^1] - lines[0]) ? at : nearest;
    }

    /// <summary>brief-sar-8 — the grid line nearest <paramref name="at"/>, as its own value: what a coordinate already known to
    /// be on a line (to rounding) is written as, since openEMS tests a box or polygon against its lines with no tolerance.</summary>
    public static double NearestLine(IReadOnlyList<double> lines, double at)
        => lines.Count == 0 ? at : lines.MinBy(l => Math.Abs(l - at));

    /// <summary>
    /// brief-sar-8b — <paramref name="at"/> as the grid line it lies on, to the given-line tolerance (<see cref="GivenLineTolerance"/>
    /// of the cell beside it), else itself: what a sheet's edge is written as. openEMS tests a box against each edge's position with
    /// no tolerance, so an edge a few ulps short of its line — a base plus a length in metres against the line's millimetres times
    /// 1e-3, or degrees turned to radians against the line's fifteen digits — holds none of that line's edges, and the sheet is a
    /// line narrower in silence (sar.md §10a.6). An edge that is not on a line is left where it was drawn.
    /// </summary>
    public static double LineOrSelf(IReadOnlyList<double> lines, double at)
        => lines.Count > 0 && OnLine(lines, at) ? lines[NearestIndex(lines, at)] : at;

    // ── the thirds pair's invariant (brief-em3d-122 R-em3d122-1) ─────────────────────────────

    /// <summary>
    /// Every thirds pair on <paramref name="axis"/> with a grid line strictly between its two lines. A pair is one edge's
    /// <see cref="FdtdLineKind.ThirdsInside"/> and <see cref="FdtdLineKind.ThirdsOutside"/> sources — the same feature, the
    /// same edge, and the edge between them — read from the required lines after merging, which is where they actually are.
    /// The rule exists to put the edge a third of a cell into ONE cell; a line between them grids the edge as if there were
    /// no rule, and nothing else would say so.
    /// <para>A pair with another REQUIRED line between its two is not one: that line is a feature's that cannot move (a port
    /// extent or a material face snaps instead — brief-em3d-123), every cell size keeps it there, and it is where the problem
    /// put it. What this finds is a line nothing asked for — one the fill made.</para>
    /// </summary>
    public static IReadOnlyList<FdtdThirdsViolation> ThirdsViolations(FdtdAxisGrid axis)
    {
        var result = new List<FdtdThirdsViolation>();
        foreach (var pair in ThirdsPairs(axis.Required))
        {
            double lo = Math.Min(pair.InsideM, pair.OutsideM), hi = Math.Max(pair.InsideM, pair.OutsideM);
            double tol = 1e-9 * (hi - lo);
            if (axis.Required.Any(r => r.PositionM > lo + tol && r.PositionM < hi - tol)) continue;
            var between = axis.Lines.Where(v => v > lo + tol && v < hi - tol).ToList();
            if (between.Count > 0) result.Add(new FdtdThirdsViolation(axis.Axis, pair.Source, pair.InsideM, pair.OutsideM, between));
        }
        return result;
    }

    /// <summary>The thirds pairs among <paramref name="required"/>, in position order of their inside lines.</summary>
    private static List<ThirdsPair> ThirdsPairs(IReadOnlyList<FdtdRequiredLine> required)
    {
        // (feature, edge, metal side) → the inside line's position and the outside line's.
        var inside = new Dictionary<(string, double, int), (double At, FdtdLineSource Src)>();
        var outside = new Dictionary<(string, double, int), double>();
        foreach (var line in required)
            foreach (var s in line.Sources)
            {
                if (s.Kind == FdtdLineKind.ThirdsInside)
                    inside.TryAdd((s.Feature, s.FeatureAtM, Math.Sign(line.PositionM - s.FeatureAtM)), (line.PositionM, s));
                else if (s.Kind == FdtdLineKind.ThirdsOutside)
                    outside.TryAdd((s.Feature, s.FeatureAtM, -Math.Sign(line.PositionM - s.FeatureAtM)), line.PositionM);
            }
        var pairs = new List<ThirdsPair>();
        foreach (var (key, (at, src)) in inside)
            if (outside.TryGetValue(key, out double o)) pairs.Add(new ThirdsPair(src, key.Item3, at, o));
        return [.. pairs.OrderBy(p => p.InsideM)];
    }

    /// <param name="MetalSide">+1 when the metal is above the edge on this axis, −1 below.</param>
    private sealed record ThirdsPair(FdtdLineSource Source, int MetalSide, double InsideM, double OutsideM);

    /// <summary>One edge's thirds pair, as <see cref="CollectRequired(Em3dProblem, FdtdAxis, OpenEmsGridSettings, double)"/>
    /// keys it: the feature, the edge's coordinate, and the side its metal is on.</summary>
    private readonly record struct ThirdsEdge(string Feature, double AtM, int MetalSide);

    /// <summary>The repair's bound (D2). Each pass only lowers local cells, and an edge whose cell falls below
    /// <see cref="ThirdsMinCells"/> MinCells keeps one line on the edge and has no pair to split, so the loop ends; this
    /// guards against a defect.</summary>
    private const int MaxThirdsRepairPasses = 64;

    /// <summary>
    /// brief-em3d-122 R-em3d122-2 — lowers the local cell of each split pair's edge in <paramref name="localCells"/>; false
    /// when nothing was lowered. The fill split a pair because the interval beside one of its lines is a little more than a
    /// whole number of cells — a gap of S − 4h/3 just over h halves into cells near h/2, which grade back up to h INSIDE the
    /// pair. That side is the one whose cell inside the pair came out smaller. Its edge's cell becomes h′ = D/(k + f): D from
    /// this edge to what bounds the interval (the facing edge when that is another pair's line, else the line itself), k the
    /// interval's cells at today's cell rounded up, and f the thirds that the pairs on either end take of D (⅔ beside an
    /// outside line, ⅓ an inside one) — so the interval is exactly k cells of h′. A facing edge gets the same h′. For two
    /// edges facing across S this is S/(k + 4/3), and at k = 1 the 3/7 gap clamp. h′ ≤ h always, so this only refines.
    /// When the interval is whole already — the split came from a smaller cell grading in from farther away — the cell is
    /// lowered by √ratio, and the next pass makes it whole again.
    /// </summary>
    private static bool RepairThirds(FdtdAxisGrid grid, IReadOnlyList<FdtdThirdsViolation> broken,
                                     Dictionary<ThirdsEdge, (double H, string? By)> localCells, double ratio)
    {
        var req = grid.Required;
        bool changed = false;
        bool Lower(ThirdsEdge edge, double h, string? by)
        {
            if (localCells.TryGetValue(edge, out var cur) && cur.H <= h) return false;
            localCells[edge] = (h, by);
            return true;
        }

        foreach (var v in broken)
        {
            double e = v.Source.FeatureAtM, h0 = v.Source.LocalCellM ?? throw new InvalidOperationException("A thirds line with no local cell.");
            int side = Math.Sign(v.InsideM - e);
            var self = new ThirdsEdge(v.Source.Feature, e, side);
            // The pair's own end cells: the smaller one is beside the interval that split it.
            double lo = Math.Min(v.InsideM, v.OutsideM), hi = Math.Max(v.InsideM, v.OutsideM);
            double cellLo = v.Between[0] - lo, cellHi = hi - v.Between[^1];
            bool insideIsLo = v.InsideM < v.OutsideM;
            double cellIn = insideIsLo ? cellLo : cellHi, cellOut = insideIsLo ? cellHi : cellLo;

            foreach (bool outside in new[] { true, false })
            {
                if (outside ? cellOut > cellIn * (1 + 1e-9) : cellIn > cellOut * (1 + 1e-9)) continue;
                double p = outside ? v.OutsideM : v.InsideM;
                double f1 = outside ? 2.0 / 3 : 1.0 / 3;
                int dir = Math.Sign(p - e) is 0 ? (outside ? -side : side) : Math.Sign(p - e);
                double tol = 1e-9 * h0;
                var n = dir > 0 ? req.FirstOrDefault(r => r.PositionM > p + tol) : req.LastOrDefault(r => r.PositionM < p - tol);
                if (n is null) continue;
                double m0 = Math.Abs(n.PositionM - p);
                // The interval's far end: another edge's thirds line (that edge at or beyond it), or the line itself.
                var other = n.Fixed ? null : n.Sources.FirstOrDefault(s =>
                    s.Kind is FdtdLineKind.ThirdsInside or FdtdLineKind.ThirdsOutside && s.LocalCellM is not null &&
                    (s.FeatureAtM - n.PositionM) * dir >= -tol &&
                    !(s.Feature == self.Feature && s.FeatureAtM == e));
                double d, f2, hMax;
                if (other is not null)
                {
                    d = Math.Abs(other.FeatureAtM - e);
                    f2 = other.Kind == FdtdLineKind.ThirdsInside ? 1.0 / 3 : 2.0 / 3;
                    hMax = Math.Max(h0, other.LocalCellM!.Value);
                }
                else
                {
                    d = Math.Abs(n.PositionM - e);
                    f2 = 0;
                    hMax = h0;
                }
                int k = Math.Max(1, (int)Math.Ceiling(m0 / hMax - 1e-9));
                double h1 = d / (k + f1 + f2);
                if (!(h1 < h0 * (1 - 1e-9))) h1 = h0 / Math.Sqrt(ratio);
                changed |= Lower(self, h1, other?.Feature ?? n.Sources[0].Feature);
                if (other is not null)
                {
                    int otherSide = other.Kind == FdtdLineKind.ThirdsInside ? Math.Sign(n.PositionM - other.FeatureAtM)
                                                                            : -Math.Sign(n.PositionM - other.FeatureAtM);
                    changed |= Lower(new ThirdsEdge(other.Feature, other.FeatureAtM, otherSide), h1, v.Source.Feature);
                }
            }
        }
        return changed;
    }

    // ── what the result reports (R-em3d8-5) ──────────────────────────────────────────────────

    /// <summary>
    /// The Courant limit Δt = 1 / (c₀·√(1/Δx² + 1/Δy² + 1/Δz²)) on the smallest cell of each axis —
    /// an ESTIMATE (openEMS computes its own; brief 9 compares them).
    /// </summary>
    public static double CourantTimeStep(double dxMin, double dyMin, double dzMin)
        => 1.0 / (C0 * Math.Sqrt(1 / (dxMin * dxMin) + 1 / (dyMin * dyMin) + 1 / (dzMin * dzMin)));

    /// <summary>The Courant estimate for three line arrays.</summary>
    public static double CourantTimeStep(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> z)
        => CourantTimeStep(SmallestCell(x), SmallestCell(y), SmallestCell(z));

    /// <summary>
    /// The Gaussian pulse's length as openEMS sizes it: 9/(π·f_c) (F0 case B's log: "Excitation signal
    /// length is … 2.86481e-10s" at f_c = 10 GHz), with f_c the band's half-width as brief 9 excites it.
    /// A single-frequency sweep has no half-width; a pulse reaching from DC to the frequency is used.
    /// </summary>
    public static double ExcitationLength(Em3dFrequency f)
    {
        double fc = (f.StopHz - f.StartHz) / 2;
        if (!(fc > 0)) fc = f.StopHz / 2;
        return 9.0 / (Math.PI * fc);
    }

    private static double SmallestCell(IReadOnlyList<double> lines)
    {
        double min = double.PositiveInfinity;
        for (int i = 1; i < lines.Count; i++) min = Math.Min(min, lines[i] - lines[i - 1]);
        return min;
    }

    private static FdtdAxisGrid Describe(FdtdAxis axis, List<double> lines, List<FdtdRequiredLine> required,
                                         double boxMin, double boxMax)
    {
        double best = double.PositiveInfinity, at = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            double c = lines[i] - lines[i - 1];
            if (c < best) { best = c; at = lines[i - 1]; }
        }

        // The features that set it: the required lines bounding every interval holding a cell of that
        // size — a thirds pair and a port often make two equal cells, and naming one of them because
        // rounding put it first would be naming the wrong half of the answer. A PML cell is its face's.
        var features = new List<FdtdLineSource>();
        int j = 0;
        for (int i = 1; i < lines.Count; i++)
        {
            double lo = lines[i - 1];
            if (lines[i] - lo > best * (1 + 1e-9)) continue;
            IEnumerable<FdtdLineSource> these;
            if (lo < required[0].PositionM) these = required[0].Sources;
            else if (lo >= required[^1].PositionM) these = required[^1].Sources;
            else
            {
                while (j < required.Count - 2 && required[j + 1].PositionM <= lo) j++;
                these = required[j].Sources.Concat(required[j + 1].Sources);
            }
            foreach (var f in these) if (!features.Contains(f)) features.Add(f);
        }
        return new FdtdAxisGrid(axis, lines, required, best, at, features, boxMin - lines[0], lines[^1] - boxMax);
    }

    // ── shared helpers ───────────────────────────────────────────────────────────────────────

    public static string AxisName(FdtdAxis a) => a switch
    {
        FdtdAxis.X => "x", FdtdAxis.Y => "y", FdtdAxis.Rho => "ρ", FdtdAxis.Alpha => "α", _ => "z",
    };

    /// <summary>A length for a sentence: nm, µm or mm, four significant figures.</summary>
    public static string FormatLength(double m)
    {
        double a = Math.Abs(m);
        return a == 0     ? "0 µm"
             : a < 1e-6   ? (m * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " nm"
             : a < 1e-3   ? (m * 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " µm"
             :              (m * 1e3).ToString("0.####", CultureInfo.InvariantCulture) + " mm";
    }

    /// <summary>
    /// brief-em3d-42 — per axis, the coordinates of the loops' axis-aligned edges on it: an edge parallel to a
    /// world axis has a constant coordinate on each of the other two, and a face containing it that is normal
    /// to one of them lies on that line. Distinct, in first-seen order.
    /// </summary>
    private static IReadOnlyList<double>[] AlignedEdgeLines(IEnumerable<IReadOnlyList<Point3>> loops)
    {
        var at = new[] { new List<double>(), new List<double>(), new List<double>() };
        foreach (var r in loops)
            for (int i = 0; i < r.Count; i++)
            {
                var p = r[i]; var q = r[(i + 1) % r.Count];
                double[] a = [p.X, p.Y, p.Z], b = [q.X, q.Y, q.Z];
                int moving = 0, along = -1;
                for (int k = 0; k < 3; k++)
                    if (Math.Abs(a[k] - b[k]) > 1e-15 * Math.Max(1, Math.Abs(a[k]))) { moving++; along = k; }
                if (moving != 1) continue;
                for (int k = 0; k < 3; k++)
                    if (k != along && !at[k].Contains(a[k])) at[k].Add(a[k]);
            }
        return at;
    }

    private static double A3Of(Shape s, FdtdAxis a) => a switch { FdtdAxis.X => s.X0, FdtdAxis.Y => s.Y0, _ => s.Z0 };

    /// <summary>Two coordinates closer than this are the same coordinate, rounded.</summary>
    private static double CoincidenceTolerance(double span) => 1e-9 * Math.Max(span, 1e-6);

    private static double A(Point2 p, FdtdAxis a) => a == FdtdAxis.X ? p.X : p.Y;
    private static double B(Point2 p, FdtdAxis a) => a == FdtdAxis.X ? p.Y : p.X;
    private static double A3(Point3 p, FdtdAxis a) => a switch { FdtdAxis.X => p.X, FdtdAxis.Y => p.Y, _ => p.Z };
    private static Point2 Pt(double along, double across, FdtdAxis a)
        => a == FdtdAxis.X ? new Point2(along, across) : new Point2(across, along);

    /// <summary>A solid or sheet as the grid sees it: a plan-view outline (null for a curved or swept
    /// primitive, which contributes extremes only), a z range, and whether it is metal.</summary>
    private sealed class Shape
    {
        public required string Name;
        public required bool Conductor;
        public required bool IsSheet;
        public required bool Thin;
        public required double Z0, Z1, X0, X1, Y0, Y1;
        public required IReadOnlyList<IReadOnlyList<Point2>>? Rings;   // outline first, then holes
        public double Index = 1;                                        // √(εr·μr), non-conductors
        /// <summary>brief-em3d-42 — for a polyhedron or a sheet not lying flat: per axis (x, y, z), the
        /// coordinates of its axis-aligned edges on that axis. Null for every other shape.</summary>
        public IReadOnlyList<double>[]? EdgeLines;
        /// <summary>A sheet not lying flat: the axis its plane is normal to, or null for an oblique plane.</summary>
        public FdtdAxis? SheetAxis;
        /// <summary>brief-em3d-65 — a kernel solid, whose face and edge tables give its lines.</summary>
        public Em3dShapeSolid? Kernel;
        /// <summary>brief-sar-11 — the largest cell its own volume allows: a voxel body's GridStepM when it states one.</summary>
        public double CellCap = double.PositiveInfinity;

        public (double Lo, double Hi) Range(FdtdAxis a) => a switch
        {
            FdtdAxis.X => (X0, X1), FdtdAxis.Y => (Y0, Y1), _ => (Z0, Z1),
        };

        public bool Contains2D(Point2 p)
        {
            if (Rings is null) return p.X >= X0 && p.X <= X1 && p.Y >= Y0 && p.Y <= Y1;
            if (!InRing(Rings[0], p)) return false;
            for (int i = 1; i < Rings.Count; i++) if (InRing(Rings[i], p)) return false;
            return true;
        }

        private static bool InRing(IReadOnlyList<Point2> ring, Point2 p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>What every step reads from the problem, resolved once.</summary>
    private sealed class Context
    {
        public readonly List<Shape> Shapes = [];
        public readonly double MinCell;
        public readonly double Tol;
        public readonly double Probe;
        private readonly Em3dProblem _p;
        private readonly OpenEmsGridSettings _settings;
        private readonly List<(double X0, double X1, double Y0, double Y1, double Cell)> _resistive = [];

        /// <summary>2·Area/Perimeter: a strip's width, however it is folded.</summary>
        private static double CharacteristicWidth(IReadOnlyList<Point2> ring)
        {
            double twice = 0, perimeter = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                twice += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
                perimeter += Math.Sqrt(Math.Pow(ring[i].X - ring[j].X, 2) + Math.Pow(ring[i].Y - ring[j].Y, 2));
            }
            return perimeter > 0 ? Math.Abs(twice) / perimeter : double.PositiveInfinity;
        }

        public Context(Em3dProblem problem, OpenEmsGridSettings settings)
        {
            _p = problem;
            _settings = settings;
            var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
            double Index(string name) => materials.TryGetValue(name, out var m)
                ? Math.Sqrt(Math.Max(m.Epsr, m.EpsrTensor is { Count: > 0 } t ? t.Max() : 0) * Math.Max(m.Mur, 1e-300))
                : 1;

            foreach (var s in problem.Solids)
            {
                var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
                if (s.Primitive is Em3dPolyhedron ph)
                {
                    bool metal = s.Role == Em3dRole.Conductor;
                    var loops = ph.Faces.SelectMany(f => f.Holes.Prepend(f.Outer))
                                        .Select(r => (IReadOnlyList<Point3>)[.. r.Select(i => ph.Vertices[i])]);
                    Shapes.Add(new Shape
                    {
                        Name = s.Name, Conductor = metal, IsSheet = false, Thin = false,
                        X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1, Rings = null,
                        Index = metal ? 1 : Index(s.Material), EdgeLines = AlignedEdgeLines(loops),
                    });
                    continue;
                }
                IReadOnlyList<IReadOnlyList<Point2>>? rings = s.Primitive switch
                {
                    Em3dExtrudedPolygon e => [e.Outline, .. e.Holes],
                    Em3dBox b => [[new(b.Min.X, b.Min.Y), new(b.Max.X, b.Min.Y), new(b.Max.X, b.Max.Y), new(b.Min.X, b.Max.Y)]],
                    _ => null,
                };
                bool conductor = s.Role == Em3dRole.Conductor;
                // brief-sar-11 — a voxel body's tissue: its extremes here, its voxel boundaries in CollectRequired, and its own
                // grid step as a cap on the cell inside it. Its primitive is a drawing, so nothing else is read from it.
                if (s.Labels is { } volume)
                {
                    Shapes.Add(new Shape
                    {
                        Name = s.Name, Conductor = false, IsSheet = false, Thin = false,
                        X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1, Rings = null,
                        Index = Index(s.Material), CellCap = volume.GridStepM > 0 ? volume.GridStepM : double.PositiveInfinity,
                    });
                    continue;
                }
                if (s.Primitive is Em3dShapeSolid kernel)
                {
                    Shapes.Add(new Shape
                    {
                        Name = s.Name, Conductor = conductor, IsSheet = false, Thin = false,
                        X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1, Rings = null,
                        Index = conductor ? 1 : Index(s.Material), Kernel = kernel,
                    });
                    continue;
                }
                Shapes.Add(new Shape
                {
                    Name = s.Name, Conductor = conductor, IsSheet = false,
                    // A plate, not a post: thinner than it is wide on both lateral axes. Its edges carry
                    // the edge singularity the thirds rule is for; a solid conductor's faces keep lines
                    // on the face (R-em3d8-2b).
                    Thin = rings is not null && z1 - z0 < Math.Min(x1 - x0, y1 - y0),
                    X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1, Rings = rings,
                    Index = conductor ? 1 : Index(s.Material),
                });
            }
            foreach (var sh in problem.Sheets)
            {
                if (sh.Frame is { } frame)
                {
                    var (fx0, fy0, fz0, fx1, fy1, fz1) = sh.WorldBounds();
                    Shapes.Add(new Shape
                    {
                        Name = sh.Name, Conductor = true, IsSheet = true, Thin = true,
                        X0 = fx0, X1 = fx1, Y0 = fy0, Y1 = fy1, Z0 = fz0, Z1 = fz1, Rings = null,
                        SheetAxis = frame.NormalAxis is { } n ? (FdtdAxis)n : null,
                        EdgeLines = AlignedEdgeLines(sh.Holes.Prepend(sh.Outline)
                                                       .Select(r => (IReadOnlyList<Point3>)[.. r.Select(sh.World)])),
                    });
                    continue;
                }
                // EM-TFR — a resistive sheet's current is the answer, so it is resolved across its width.
                if (materials.TryGetValue(sh.Material, out var sheetMetal) && sheetMetal.SigmaSm is > 0 and var g && double.IsFinite(g)
                    && sh.ThicknessM > 0 && 1.0 / (g * sh.ThicknessM) >= ResistiveSheetFromOhmPerSq)
                    _resistive.Add((sh.Outline.Min(q => q.X), sh.Outline.Max(q => q.X), sh.Outline.Min(q => q.Y), sh.Outline.Max(q => q.Y),
                                    CharacteristicWidth(sh.Outline) / ResistiveSheetCellsAcross));
                Shapes.Add(new Shape
                {
                    Name = sh.Name, Conductor = true, IsSheet = true, Thin = true,
                    X0 = sh.Outline.Min(q => q.X), X1 = sh.Outline.Max(q => q.X),
                    Y0 = sh.Outline.Min(q => q.Y), Y1 = sh.Outline.Max(q => q.Y),
                    Z0 = sh.Z, Z1 = sh.Z, Rings = [sh.Outline, .. sh.Holes],
                });
            }

            var box = problem.Boundary;
            double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
            Tol = CoincidenceTolerance(span);
            Probe = 10 * Tol;
            MinCell = settings.MinCellM ?? MinCellFeatureFraction * SmallestFeature(problem, span);
        }

        public double BoxMin(FdtdAxis a) => A3(_p.Boundary.Min, a);
        public double BoxMax(FdtdAxis a) => A3(_p.Boundary.Max, a);

        public (Em3dBoundaryKind Lo, Em3dBoundaryKind Hi) Faces(FdtdAxis a) => a switch
        {
            FdtdAxis.X => (_p.Boundary.Faces.XMin, _p.Boundary.Faces.XMax),
            FdtdAxis.Y => (_p.Boundary.Faces.YMin, _p.Boundary.Faces.YMax),
            _          => (_p.Boundary.Faces.ZMin, _p.Boundary.Faces.ZMax),
        };

        /// <summary>
        /// R-em3d8-4a. The largest cell allowed between lo and hi on an axis: the shortest wavelength at
        /// the top frequency in the densest material any non-conductor overlapping that slab is made of,
        /// over CellsPerWavelength. The box itself is air.
        /// </summary>
        public double MaxCell(FdtdAxis a, double lo, double hi)
        {
            double n = 1, cap = double.PositiveInfinity;
            foreach (var s in Shapes)
            {
                if (s.Conductor) continue;
                var (s0, s1) = s.Range(a);
                if (s0 < hi - Tol && s1 > lo + Tol) { n = Math.Max(n, s.Index); cap = Math.Min(cap, s.CellCap); }
            }
            foreach (var (x0, x1, y0, y1, cell) in _resistive)
            {
                if (a == FdtdAxis.Z) break;
                var (r0, r1) = a == FdtdAxis.X ? (x0, x1) : (y0, y1);
                if (r0 < hi - Tol && r1 > lo + Tol) cap = Math.Min(cap, cell);
            }
            return Math.Min(cap, C0 / (_p.Frequency.StopHz * n * _settings.CellsPerWavelength));
        }

        public double MaxCellAt(FdtdAxis a, double at)
        {
            double n = 1, cap = double.PositiveInfinity;
            foreach (var s in Shapes)
            {
                if (s.Conductor) continue;
                var (s0, s1) = s.Range(a);
                if (s0 <= at + Tol && s1 >= at - Tol) { n = Math.Max(n, s.Index); cap = Math.Min(cap, s.CellCap); }
            }
            foreach (var (x0, x1, y0, y1, cell) in _resistive)
            {
                if (a == FdtdAxis.Z) break;
                var (r0, r1) = a == FdtdAxis.X ? (x0, x1) : (y0, y1);
                if (r0 <= at + Tol && r1 >= at - Tol) cap = Math.Min(cap, cell);
            }
            return Math.Min(cap, C0 / (_p.Frequency.StopHz * n * _settings.CellsPerWavelength));
        }

        /// <summary>Is there metal other than <paramref name="self"/> at a plan point within a z range?</summary>
        public bool ConductorAt(Point2 p, double z0, double z1, Shape self)
        {
            foreach (var s in Shapes)
                if (s.Conductor && !ReferenceEquals(s, self) && s.Z0 <= z1 + Tol && s.Z1 >= z0 - Tol && s.Contains2D(p))
                    return true;
            return false;
        }

        /// <summary>
        /// R-em3d8-2b. The local cell at a metal edge: the target size at that point (R-em3d8-4a), at most
        /// <see cref="ThirdsWidthFraction"/> of the metal's own width there, and at most
        /// <see cref="ThirdsGapFraction"/> of the gap to SEPARATE metal beside it in the same layer —
        /// metal whose extent meets this shape's (a via pad on the line's end, a barrel through it) is
        /// the same conductor, not a neighbour across a gap. Returns the neighbour when it is the one
        /// that set the cell.
        /// </summary>
        public (double H, string? SetBy) LocalCell(FdtdAxis a, Shape shape, double e, int metalSide, double b0, double b1)
        {
            double h = MaxCellAt(a, e);
            string? by = null;

            // The metal's width ACROSS THIS EDGE: rays cast from a quarter, half and three quarters along
            // the edge into the metal, to the first boundary each meets. The nearest vertex anywhere on
            // the outline was used before, and a line fused to its via pad took its "width" from a pad
            // vertex beyond the edge's end: 19.8 µm for a 400 µm line, a 3.48 µm cell beside the port
            // line, and two identical lines gridded differently depending on the pad's tessellation.
            double width = double.PositiveInfinity;
            foreach (double f in new[] { 0.25, 0.5, 0.75 })
                width = Math.Min(width, RayToBoundary(shape.Rings!, a, e, b0 + f * (b1 - b0), metalSide));
            h = Math.Min(h, ThirdsWidthFraction * width);

            // brief-dfr27-5 — and the gap across this edge to the shape's OWN metal: a slot or notch in one pour. Only separate
            // metal was clamped before, so a 200 µm slot in a ground pour took a 336 µm cell and its two edges' pairs crossed.
            double own = double.PositiveInfinity;
            foreach (double f in new[] { 0.25, 0.5, 0.75 })
                own = Math.Min(own, RayToBoundary(shape.Rings!, a, e, b0 + f * (b1 - b0), -metalSide));
            if (ThirdsGapFraction * own < h)
            {
                h = ThirdsGapFraction * own;
                by = shape.Name;
            }

            // The gap: the nearest coordinate, outside this edge, of separate metal in this layer alongside it.
            var across = a == FdtdAxis.X ? FdtdAxis.Y : FdtdAxis.X;
            foreach (var s in Shapes)
            {
                if (!s.Conductor || ReferenceEquals(s, shape) || !(s.Z0 <= shape.Z1 + Tol && s.Z1 >= shape.Z0 - Tol)) continue;
                var (o0, o1) = s.Range(across);
                if (o1 < b0 - Tol || o0 > b1 + Tol) continue;

                if (s.Rings is not null)
                {
                    // An outline: its gap is measured by the same rays, outward, across this edge's span.
                    // Metal just outside the edge is the SAME conductor (a pad on the line's end), not a
                    // neighbour. Judging that by bounding boxes skipped every pour whose box contains the
                    // line — a coplanar waveguide's ground — and left its slot with no gap clamp at all.
                    double gap = double.PositiveInfinity;
                    bool touching = false;
                    foreach (double f in new[] { 0.25, 0.5, 0.75 })
                    {
                        double b = b0 + f * (b1 - b0);
                        if (s.Contains2D(Pt(e - metalSide * Probe, b, a))) { touching = true; break; }
                        gap = Math.Min(gap, RayToBoundary(s.Rings, a, e, b, -metalSide));
                    }
                    if (!touching && ThirdsGapFraction * gap < h)
                    {
                        h = ThirdsGapFraction * gap;
                        by = s.Name;
                    }
                    continue;
                }

                // A curved or swept solid (a barrel, a wire): its extremes, and its box decides contact.
                if (s.X0 <= shape.X1 + Tol && s.X1 >= shape.X0 - Tol && s.Y0 <= shape.Y1 + Tol && s.Y1 >= shape.Y0 - Tol)
                    continue;
                foreach (double c in Coordinates(s, a))
                {
                    double d = (e - c) * metalSide;    // positive: outside this edge
                    if (d > Tol && ThirdsGapFraction * d < h)
                    {
                        h = ThirdsGapFraction * d;
                        by = s.Name;
                    }
                }
            }
            return (h, by);
        }

        /// <summary>
        /// How far a ray from (<paramref name="e"/>, <paramref name="b"/>) travels along axis
        /// <paramref name="a"/>, in direction <paramref name="side"/>, before crossing any ring's boundary
        /// — ignoring crossings within <see cref="Tol"/> of the start, which are the edge itself.
        /// </summary>
        private double RayToBoundary(IReadOnlyList<IReadOnlyList<Point2>> rings, FdtdAxis a, double e, double b, int side)
        {
            double best = double.PositiveInfinity;
            foreach (var r in rings)
                for (int i = 0; i < r.Count; i++)
                {
                    var p = r[i];
                    var q = r[(i + 1) % r.Count];
                    double pb = B(p, a), qb = B(q, a);
                    if ((pb > b) == (qb > b)) continue;                   // does not straddle the ray's line
                    double at = A(p, a) + (b - pb) / (qb - pb) * (A(q, a) - A(p, a));
                    double d = (at - e) * side;
                    if (d > Tol) best = Math.Min(best, d);
                }
            return best;
        }

        private static IEnumerable<double> Coordinates(Shape s, FdtdAxis a)
        {
            if (s.Rings is null)
            {
                var (lo, hi) = s.Range(a);
                yield return lo;
                yield return hi;
                yield break;
            }
            foreach (var r in s.Rings)
                foreach (var q in r)
                    yield return A(q, a);
        }

        /// <summary>
        /// R-em3d8-3b. The smallest DELIBERATE feature: over every conductor, its smallest extent — a
        /// plate's width or thickness, a wire's diameter, a via's diameter or length, a sheet's width.
        /// An outline's width is its bounding extent. With no metal, the box's own span.
        /// </summary>
        private static double SmallestFeature(Em3dProblem p, double span)
        {
            double min = double.PositiveInfinity;
            foreach (var s in p.Solids.Where(s => s.Role == Em3dRole.Conductor))
            {
                double f = s.Primitive switch
                {
                    Em3dSweep w => w.Diameter,
                    Em3dCylinder c => Math.Min(2 * c.Radius, Distance(c.AxisStart, c.AxisEnd)),
                    Em3dSphere sp => 2 * sp.Radius,
                    Em3dTruncatedSphere t => Math.Min(2 * t.Radius, t.ZMax - t.ZMin),
                    Em3dEllipsoid el => 2 * Math.Min(el.A, Math.Min(el.B, el.C)),
                    var prim => Extent(Em3dProblem.Bounds(prim)),
                };
                min = Math.Min(min, f);
            }
            foreach (var sh in p.Sheets)
                min = Math.Min(min, Math.Min(sh.Outline.Max(q => q.X) - sh.Outline.Min(q => q.X),
                                             sh.Outline.Max(q => q.Y) - sh.Outline.Min(q => q.Y)));
            return double.IsFinite(min) && min > 0 ? min : span;

            static double Extent((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
                => Math.Min(b.X1 - b.X0, Math.Min(b.Y1 - b.Y0, b.Z1 - b.Z0));
            static double Distance(Point3 a, Point3 b)
                => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        }
    }
}
