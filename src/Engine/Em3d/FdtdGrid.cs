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

/// <summary>A grid axis.</summary>
public enum FdtdAxis { X, Y, Z }

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
        return p;
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
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
    public string Sentence =>
        $"On {FdtdGrid.AxisName(Axis)}: {string.Join(" and ", First.Select(s => s.Describe(Axis)))} and " +
        $"{string.Join(" and ", Second.Select(s => s.Describe(Axis)))} want lines " +
        $"{FdtdGrid.FormatLength(DistanceM)} apart, closer than MinCell; merged to one line at " +
        $"{FdtdGrid.AxisName(Axis)} = {FdtdGrid.FormatLength(MergedAtM)}.";
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
/// here, so <c>explain</c> can show the size that was refused.</param>
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
    public FdtdAxisGrid Axis(FdtdAxis a) => a switch { FdtdAxis.X => X, FdtdAxis.Y => Y, _ => Z };

    /// <summary>The axis holding the smallest cell of the whole grid — the one that sets Δt most.</summary>
    public FdtdAxisGrid Smallest => new[] { X, Y, Z }.MinBy(g => g.SmallestCellM)!;
}

/// <summary>
/// <b>The per-axis grid generator</b> (brief-em3d-8). <see cref="Build"/> is the whole thing; the
/// three steps it is made of — <see cref="CollectRequired"/>, <see cref="Merge"/> and
/// <see cref="Fill"/> — are public so each is tested on its own.
/// </summary>
public static class FdtdGrid
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

        var ctx = new Context(problem, settings);
        var merges = new List<FdtdMerge>();
        var warnings = new List<string>();

        FdtdAxisGrid Axis(FdtdAxis a)
        {
            var required = Merge(CollectRequired(problem, a, settings, ctx.MinCell), a, ctx.MinCell, merges, warnings);
            var (lo, hi) = ctx.Faces(a);
            var lines = Fill(required, iv => ctx.MaxCell(a, iv.Lo, iv.Hi), settings.GradingRatio,
                             lo == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0,
                             hi == Em3dBoundaryKind.Absorbing ? settings.PmlCells : 0);
            return Describe(a, lines, required, ctx.BoxMin(a), ctx.BoxMax(a));
        }

        var x = Axis(FdtdAxis.X);
        var y = Axis(FdtdAxis.Y);
        var z = Axis(FdtdAxis.Z);

        long cells = checked((long)x.Lines.Count * y.Lines.Count * z.Lines.Count);
        double dt = CourantTimeStep(x.SmallestCellM, y.SmallestCellM, z.SmallestCellM);
        double pulse = ExcitationLength(problem.Frequency);
        long steps = (long)Math.Ceiling(pulse * (1 + RingDownPulses) / dt);
        long memory = Em3dSizeEstimate.OpenEmsMemoryBytes(cells);
        long limit = availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        var result = new FdtdGridResult(x, y, z, ctx.MinCell, cells, dt, pulse, steps, memory, merges, warnings, null);
        return memory > limit ? result with { Refusal = MemoryRefusal(result, limit) } : result;
    }

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
    {
        var ctx = new Context(problem, settings);
        var lines = new List<FdtdRequiredLine>();
        void Add(double at, bool fix, FdtdLineSource src, double hint = double.PositiveInfinity)
            => lines.Add(new FdtdRequiredLine(at, fix, [src], hint));

        foreach (var shape in ctx.Shapes)
        {
            if (axis == FdtdAxis.Z)
            {
                if (shape.IsSheet)
                    Add(shape.Z0, true, new FdtdLineSource(shape.Name, FdtdLineKind.SheetPlane, shape.Z0));
                else
                {
                    var kind = shape.Conductor ? FdtdLineKind.MetalEdge : FdtdLineKind.MaterialFace;
                    Add(shape.Z0, false, new FdtdLineSource(shape.Name, kind, shape.Z0));
                    Add(shape.Z1, false, new FdtdLineSource(shape.Name, kind, shape.Z1));
                }
                continue;
            }

            if (shape.Rings is null)
            {
                // Curved or swept: its extremes only; the grid staircases the rest (em-3d.md §3).
                var kind = shape.Conductor ? FdtdLineKind.MetalExtreme : FdtdLineKind.MaterialFace;
                var (lo, hi) = shape.Range(axis);
                Add(lo, false, new FdtdLineSource(shape.Name, kind, lo));
                Add(hi, false, new FdtdLineSource(shape.Name, kind, hi));
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
                        Add(e, false, new FdtdLineSource(shape.Name, FdtdLineKind.MetalEdge, e));
                        continue;
                    }

                    var (h, setBy) = ctx.LocalCell(axis, shape, e, metalSide, Math.Min(pb, qb), Math.Max(pb, qb));
                    double outside = e - metalSide * 2 * h / 3;
                    // Too narrow for two lines that the merge would not undo, an edge on the air box
                    // whose outside line would leave it, or an edge SHORTER than its local cell — a facet
                    // of a tessellated circle, whose thirds pair would only crowd its neighbours' lines:
                    // the line goes on the edge.
                    if (!(h >= ThirdsMinCells * minCellM) || outside < ctx.BoxMin(axis) || outside > ctx.BoxMax(axis)
                        || Math.Abs(qb - pb) < h)
                    {
                        Add(e, false, new FdtdLineSource(shape.Name, FdtdLineKind.MetalEdge, e));
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
                        Add(ex, false, new FdtdLineSource(shape.Name, extremeKind, ex));
            }
        }

        foreach (var port in problem.Ports)
        {
            double lo = A3(port.Min, axis), hi = A3(port.Max, axis);
            Add(lo, true, new FdtdLineSource(port.Name, FdtdLineKind.PortExtent, lo));
            if (hi != lo) Add(hi, true, new FdtdLineSource(port.Name, FdtdLineKind.PortExtent, hi));
        }

        string n = AxisName(axis);
        Add(ctx.BoxMin(axis), true, new FdtdLineSource(Em3dAirBox.FaceName(n + "min"), FdtdLineKind.AirBoxFace, ctx.BoxMin(axis)));
        Add(ctx.BoxMax(axis), true, new FdtdLineSource(Em3dAirBox.FaceName(n + "max"), FdtdLineKind.AirBoxFace, ctx.BoxMax(axis)));
        return lines;
    }

    // ── step 2: merging (R-em3d8-3) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sorts <paramref name="lines"/> and merges any two closer than <paramref name="minCellM"/>
    /// (R-em3d8-3a): two movable lines go to their midpoint; a movable line goes onto a fixed one; two
    /// fixed lines are both kept and a warning names them and the cell they force. Lines that coincide
    /// to rounding become one line carrying every source, and are not reported — two features sharing a
    /// coordinate is the ordinary case (a strip's bottom on its substrate's top), not a merge.
    /// </summary>
    public static List<FdtdRequiredLine> Merge(IReadOnlyList<FdtdRequiredLine> lines, FdtdAxis axis, double minCellM,
                                               List<FdtdMerge> merges, List<string> warnings)
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
        for (int i = 1; i < one.Count; i++)
        {
            var k = kept[^1];
            var l = one[i];
            double d = l.PositionM - k.PositionM;
            if (d >= minCellM) { kept.Add(l); continue; }

            if (k.Fixed && l.Fixed)
            {
                kept.Add(l);
                warnings.Add($"On {AxisName(axis)}: {string.Join(" and ", k.Sources.Select(s => s.Describe(axis)))} and " +
                             $"{string.Join(" and ", l.Sources.Select(s => s.Describe(axis)))} are " +
                             $"{FormatLength(d)} apart, closer than MinCell ({FormatLength(minCellM)}), and neither may " +
                             $"move, so they force a {FormatLength(d)} cell — and the time step with it.");
                continue;
            }
            double at = k.Fixed ? k.PositionM : l.Fixed ? l.PositionM : (k.PositionM + l.PositionM) / 2;
            merges.Add(new FdtdMerge(axis, k.Sources, k.PositionM, l.Sources, l.PositionM, d, at));
            kept[^1] = new FdtdRequiredLine(at, k.Fixed || l.Fixed, [.. k.Sources, .. l.Sources],
                                            Math.Min(k.SizeHintM, l.SizeHintM));
        }
        return kept;
    }

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

    public static string AxisName(FdtdAxis a) => a switch { FdtdAxis.X => "x", FdtdAxis.Y => "y", _ => "z" };

    /// <summary>A length for a sentence: nm, µm or mm, four significant figures.</summary>
    public static string FormatLength(double m)
    {
        double a = Math.Abs(m);
        return a == 0     ? "0 µm"
             : a < 1e-6   ? (m * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " nm"
             : a < 1e-3   ? (m * 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " µm"
             :              (m * 1e3).ToString("0.####", CultureInfo.InvariantCulture) + " mm";
    }

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
                IReadOnlyList<IReadOnlyList<Point2>>? rings = s.Primitive switch
                {
                    Em3dExtrudedPolygon e => [e.Outline, .. e.Holes],
                    Em3dBox b => [[new(b.Min.X, b.Min.Y), new(b.Max.X, b.Min.Y), new(b.Max.X, b.Max.Y), new(b.Min.X, b.Max.Y)]],
                    _ => null,
                };
                bool conductor = s.Role == Em3dRole.Conductor;
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
                Shapes.Add(new Shape
                {
                    Name = sh.Name, Conductor = true, IsSheet = true, Thin = true,
                    X0 = sh.Outline.Min(q => q.X), X1 = sh.Outline.Max(q => q.X),
                    Y0 = sh.Outline.Min(q => q.Y), Y1 = sh.Outline.Max(q => q.Y),
                    Z0 = sh.Z, Z1 = sh.Z, Rings = [sh.Outline, .. sh.Holes],
                });

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
            double n = 1;
            foreach (var s in Shapes)
            {
                if (s.Conductor) continue;
                var (s0, s1) = s.Range(a);
                if (s0 < hi - Tol && s1 > lo + Tol) n = Math.Max(n, s.Index);
            }
            return C0 / (_p.Frequency.StopHz * n * _settings.CellsPerWavelength);
        }

        private double MaxCellAt(FdtdAxis a, double at)
        {
            double n = 1;
            foreach (var s in Shapes)
            {
                if (s.Conductor) continue;
                var (s0, s1) = s.Range(a);
                if (s0 <= at + Tol && s1 >= at - Tol) n = Math.Max(n, s.Index);
            }
            return C0 / (_p.Frequency.StopHz * n * _settings.CellsPerWavelength);
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
