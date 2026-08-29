// L8b — the 2-D surface mesher.
//
// THIS FILE ADDS NO PHYSICS. Its entire job is to turn drawn geometry into cells, count them, and
// hand the count back before anything is solved. If you find yourself evaluating a Green's function
// here, stop — that is L8c.
//
// Two properties everything downstream silently assumes, both asserted by SurfaceMesherTests rather
// than by inspection:
//
//  * R-msh-1 — the mesh TILES its input. The union of the cells is the input geometry, to the last
//    DBU, for Manhattan artwork: no gaps, no overlaps, no cell straying outside the metal. A fill
//    over a mesh with a sliver gap solves a slightly different structure and reports a smooth,
//    plausible, wrong s-parameter. Non-Manhattan artwork is STAIRCASED (D2) and the deviation is
//    measured rather than hidden — see PlanarMeshReport.StaircasedPolygons and Tier 6.
//  * R-msh-2 — cell ORDER is a permanent contract; see PlanarMesh.cs.
//
// D9 — THE MESH IS A THIRD DISCRETIZATION AND MUST NOT INHERIT EITHER OF THE OTHER TWO. R-tap-2
// already settled that there are two: the ELECTRICAL sectioning MicrostripCascadeSectioning answers,
// and the ARTWORK tessellation a PCell emits (MKlopfPCell's fixed 96-point outline, "fixed geometric
// fidelity, independent of electrical N"). A mesher that snapped cell boundaries to input polygon
// VERTICES would silently inherit that 96 — a mesh derived from a drawing decision rather than from
// the analysis, which on the §10.7 taper happens to land near λ_g/20 at 10 GHz and over-refines by
// ~10× at 1 GHz.
//
// The rule that avoids it, stated once: **a gridline comes from an AXIS-PARALLEL boundary edge, never
// from a vertex.** A Manhattan polygon's every edge is axis-parallel, so its grid is conformal and the
// tiling is exact. A smooth 96-point tessellation has axis-parallel edges only at its two end caps,
// so it contributes exactly two gridlines however finely it was tessellated. Vertices are geometry to
// be COVERED, not gridlines to be ADOPTED.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// R-msh-5's measurement seam. <b>Not a user control</b> — D3 permits exactly three and this is not
/// one of them; it exists so the two candidate reference lengths can be measured against each other
/// and the answer recorded, exactly as R-mom-8 records kernel A's.
/// </summary>
public enum PlanarEdgeReference
{
    /// <summary>The narrowest conductor dimension in the layout — the in-plane analogue of §10.5's
    /// "a small fraction of the width".</summary>
    ConductorWidth,

    /// <summary>The wavelength-derived cell size. A mesh-relative reference: the edge cell is a fixed
    /// fraction of what the bulk cell would have been, so it scales with frequency rather than with
    /// geometry.</summary>
    CellSize,
}

/// <summary>
/// Whether an OBLIQUE boundary run contributes edge attractors, and how many.
///
/// <para><b>Not a user control</b> — D3 permits exactly three and this is not a fourth. It is a
/// measurement seam of the same kind as <see cref="PlanarEdgeReference"/>: brief-edge-mesh-on-curved-
/// geometry.md §2 asks whether a graded fan on a STAIRCASED rim buys anything at all, and that
/// question cannot be answered without being able to build both meshes.</para>
///
/// <para><b>The default is <see cref="None"/>, and that is a MEASURED answer rather than an
/// omission</b> — see the "edge mesh on curved geometry" section of this directory's own
/// <c>CLAUDE.md</c> for the convergence table that decided it.</para>
/// </summary>
public enum PlanarRimGrading
{
    /// <summary>L8b's rule as shipped: an oblique edge contributes neither a hard gridline nor an
    /// attractor. D9's guarantee, by exclusion.</summary>
    None,

    /// <summary>
    /// One attractor per oblique RUN per axis at each end of the run's own coordinate range in that
    /// axis — a run being a maximal chain of consecutive oblique edges. D9 then holds NUMERICALLY:
    /// a 96-point disc is one run and contributes at most four attractors however finely it was
    /// tessellated.
    /// </summary>
    PerRun,

    /// <summary>
    /// <see cref="PerRun"/> plus three interior samples spread along the run's own ARC LENGTH, each
    /// contributing one attractor in the axis TRANSVERSE to the local tangent. Spreading by
    /// coordinate rather than by arc length is wrong for a closed curve — the midpoint of a disc's
    /// y-range is the disc's centre, which is not on the rim at all.
    /// </summary>
    PerRunSampled,
}

public static class SurfaceMesher
{
    /// <summary>
    /// R-msh-7 / R17 — <b>the hard ceiling, declared</b>. §10.7's own table: 5,000 unknowns is a
    /// 400 MB dense complex matrix and "the practical ceiling for lightweight". Above it the mesher
    /// refuses by name. A "lightweight" simulator that silently tries to allocate 12 GB is not
    /// lightweight. <b>This is the DENSE path's ceiling and it does not move</b> — 16N² plus the LU's
    /// own working set is a real, measured cost and more RAM does not change that a "lightweight"
    /// simulator should not chase it. See <see cref="AcceleratedUnknownCeiling"/> for the accelerated
    /// solver's own, higher, separately-measured ceiling.
    /// </summary>
    public const int UnknownCeiling = 5000;

    /// <summary>
    /// brief-em-aim-ceiling.md's answer to "does R17's ceiling move for the accelerated solver, and to
    /// what" — **YES, to this, on a SINGLE-LEVEL mesh (no vias, no second metal level).** M5 shipped
    /// the AIM accelerator measured only to N = 3,731; this is the number the ladder built past it.
    ///
    /// <para><b>Chosen from two ladder constructions that told two different stories, and the number
    /// sits with margin under the worse one.</b> Growing the LENGTH of the §10.7 hero at the shipping
    /// mesh (cells/λ = 20 — the construction that actually matches how a real board gets big, a
    /// wide-to-narrow taper included) stayed flat all the way to N = 12,894: near-field entries per row
    /// 392 → 399, GMRES 6 → 7 iterations, accelerator working set 53 → 188 MB (against a dense matrix
    /// that was never built — 16N² there is 2.66 GB, structurally unreachable through
    /// <see cref="UnknownCeiling"/>). REFINING the RESOLUTION at a FIXED 64 mm footprint instead — the
    /// brief's own trap check — is a genuinely different regime: iterations held at 5-8 through
    /// N = 3,454, then climbed 21 → 143 → 372 as cells/λ went 80 → 100 → 120 (N = 5,437 → 10,708,
    /// still converging, just slower), and FAILED TO CONVERGE at cells/λ = 140 (N = 13,967, residual
    /// 9.1e-5 against a 1e-8 tolerance, GMRES's own cap of 400 iterations reached). A non-converged
    /// GMRES throws rather than returning a smooth-but-wrong current distribution
    /// (<see cref="PlanarAimOperator.Solve"/>) — that is the backstop this ceiling leans on for the
    /// residual risk an over-refined mesh still carries above it.</para>
    ///
    /// <para><b>12,000 sits at the top of the construction that is actually representative</b> (measured
    /// healthy there) <b>and with real margin under the construction that broke</b> (measured failing at
    /// 13,967). It covers the 2026-08-14 owner report (N = 7,749) with 1.55× headroom. A conformally CUT
    /// mesh carries no penalty of its own — measured on a straight-flanked taper, 4-5 GMRES iterations
    /// and |Δcurrent| 1.6e-6 to 5.5e-5 across N = 1,538 to 2,232 — so this ceiling does not depend on
    /// <see cref="PlanarBoundaryCells"/>.</para>
    ///
    /// <para><b>Never applied to a multi-level or via-bearing mesh</b> — <c>PlanarAimOperator.Build</c>
    /// refuses that class by name regardless, so the effective ceiling for such a mesh is always
    /// <see cref="UnknownCeiling"/> even when the accelerator is requested. See
    /// <c>SurfaceMesher.Mesh</c>'s own <c>accelerated</c> parameter and
    /// <c>PlanarSolveContext</c>'s constructor, the two places (with <see cref="UnknownCeiling"/>'s own
    /// dense-path call sites) this is enforced.</para>
    /// </summary>
    public const int AcceleratedUnknownCeiling = 12_000;

    /// <summary>Warn — do not refuse — from this fraction of the ceiling upward.</summary>
    public const double WarnFraction = 0.6;

    /// <summary>
    /// A guard on the GRID, not on N: past this many candidate grid cells the mesh cannot be built
    /// at all, so the report is a refusal carrying the estimate rather than an out-of-memory. It sits
    /// far above anything R17 would let through, so in practice R17 refuses first and this never
    /// fires.
    /// </summary>
    public const long MaxGridCells = 4_000_000;

    /// <summary>
    /// R-fil-10's defense-in-depth, for the ONE call site that is not <see cref="Mesh"/> itself:
    /// <c>PlanarSolveContext</c>'s constructor, before it decides which cores to build. Solving is
    /// asked to mesh first and check <see cref="PlanarMeshReport.CanSolve"/> — this exists for the
    /// same reason <c>PlanarSystem.GuardCeiling</c> and <c>PlanarFill</c>'s own copy exist: so a caller
    /// that skips the report cannot reach an allocation past either ceiling. Throws the same wording
    /// <see cref="Mesh"/>'s own refusal would have used, minus the mesh-specific "why" clause, which
    /// this call site has no mesh geometry left to derive.
    /// </summary>
    public static void GuardCeiling(int n, bool accelerated, int cellCount = 0)
    {
        int ceiling = accelerated ? AcceleratedUnknownCeiling : UnknownCeiling;
        if (n <= ceiling) return;
        throw new InvalidOperationException(
            $"This mesh has {n:N0} unknowns, which is past the {ceiling:N0}-unknown " +
            (accelerated ? "ACCELERATED " : "") + "ceiling this kernel is built for" +
            (accelerated
                ? " (brief-em-aim-ceiling.md; the accelerator's own working set stays under 200 MB " +
                  "even at this ceiling — it is not a memory limit either)."
                : $" ({PlanarSystem.ResidentPhrase(n, cellCount)})."));
    }

    /// <summary>
    /// Builds the surface mesh and the N report. <b>Nothing is solved</b>; nothing here evaluates a
    /// Green's function.
    /// </summary>
    /// <param name="edgeReference">R-msh-5's measurement seam — leave at the default.</param>
    /// <param name="control">Progress and cancellation, or null for neither.
    ///
    /// <para><b>The mesher reports through the STAGE counter only — never the outer one.</b> It runs
    /// inside a sweep as well as on its own, and the outer counter means "frequency points" there;
    /// ticking it from here would count meshing as points solved. Owner, 2026-08-09: "I've seen
    /// geometry in commercial MoM take 2 min to mesh (or longer). It depends on geometry." Ours is
    /// sub-millisecond on a single-polygon line (measured: 0.1-0.4 ms, N = 552 and N = 6,497), but
    /// the dominant term is layers x grid rows x polygons in the span scan, and only the CELL count
    /// is bounded by R17's unknown ceiling — the polygon count is not. So the row scan is where the
    /// ticks go, and cancellation is answered there too.</para></param>
    /// <param name="rimGrading">brief-edge-mesh-on-curved-geometry.md's measurement seam — leave at
    /// the default. See <see cref="PlanarRimGrading"/> for why the default is the one it is.</param>
    /// <param name="sliverAreaFraction">R-cut-3's MEASURED threshold — the fraction of a grid
    /// rectangle below which a cut cell is absorbed into a neighbour rather than solved. Only reachable
    /// so the sweep that chose it can be re-run; see <see cref="DefaultSliverAreaFraction"/>.</param>
    /// <param name="diagnostics">brief-convex-decomposition.md's M0 instrument, or null for none.
    /// <b>The mesh is bit-identical either way</b> — nothing on it feeds back.</param>
    /// <param name="accelerated">
    /// brief-em-aim-ceiling.md — whether the run this report is FOR would use the AIM accelerator
    /// (<c>PlanarFillSettings.Aim</c> non-null). <b>Affects only which ceiling the verdict is judged
    /// against</b> — <see cref="AcceleratedUnknownCeiling"/> in place of <see cref="UnknownCeiling"/> —
    /// never the mesh itself, which is identical either way. Ignored (falls back to
    /// <see cref="UnknownCeiling"/>) whenever <c>problem.RequiresGeneralKernel</c>, because the
    /// accelerator refuses a multi-level or via-bearing mesh by name regardless of this flag
    /// (<c>PlanarAimOperator.Build</c>) — asking for the wider ceiling there would promise a run that
    /// cannot start.
    /// </param>
    /// <param name="lengthFormat">Owner request, 2026-08-15 — every distance this report's notes and
    /// refusal quote goes through this. <c>null</c> (the default) is SI engineering notation, byte for
    /// byte what every caller got before this parameter existed; a UI caller supplies one built from
    /// the open layout's own display unit.</param>
    public static PlanarMeshReport Mesh(
        PlanarProblem       problem,
        PlanarMeshSettings? settings      = null,
        PlanarEdgeReference edgeReference = PlanarEdgeReference.ConductorWidth,
        RunControl?         control       = null,
        PlanarRimGrading    rimGrading    = PlanarRimGrading.None,
        double              sliverAreaFraction = DefaultSliverAreaFraction,
        ConformalDiagnostics? diagnostics = null,
        bool                accelerated   = false,
        PlanarLengthFormat? lengthFormat  = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var s = (settings ?? PlanarMeshSettings.Default).Resolved;
        bool accel = accelerated && !problem.RequiresGeneralKernel;
        int  ceiling = accel ? AcceleratedUnknownCeiling : UnknownCeiling;
        var  fmt = lengthFormat ?? DefaultLengthFormat;

        var notes = new List<string>();

        // ── M0: the mesh is sized at MeshFrequencyHz, which defaults to the sweep's own top ───────
        //
        // The `with` pattern is PlanarKernel.cs:197's, reused rather than re-derived — there is one
        // way to ask a problem for λ_g at some other frequency, and this is it.
        double meshFreqHz = s.MeshFrequencyHz is { } mf && mf > 0 ? mf : problem.MaxFrequencyHz;
        double lambdaG = meshFreqHz == problem.MaxFrequencyHz
            ? problem.GuidedWavelengthM
            : (problem with { MaxFrequencyHz = meshFreqHz }).GuidedWavelengthM;
        double hWave   = double.IsInfinity(lambdaG) ? double.PositiveInfinity
                                                    : lambdaG / s.CellsPerWavelength;

        var layerNames = new List<string>(problem.Layers.Count);
        foreach (var l in problem.Layers) layerNames.Add(l.Name);

        if (problem.PolygonCount == 0)
        {
            notes.Add("This EM setup's layout holds no conductor artwork, so there is nothing to mesh.");
            return Empty(layerNames, meshFreqHz, lambdaG, hWave, notes);
        }

        var (x0, y0, x1, y1) = problem.Bounds();
        if (!(x1 > x0) || !(y1 > y0))
        {
            notes.Add("The conductor artwork has zero extent in x or y, so it encloses no area to mesh.");
            return Empty(layerNames, meshFreqHz, lambdaG, hWave, notes);
        }

        // Sampling every polygon on every layer — the one pre-grid pass whose cost scales with the
        // ARTWORK rather than with the cell count, and therefore the one R17's ceiling does not bound.
        control?.BeginStage("measuring the artwork");
        // ── R-msh-4: the transverse cell size, from the NARROWEST conductor, per axis ──────────
        var (narrowX, narrowY) = MeasureNarrowness(problem);
        double narrowest = Math.Min(narrowX, narrowY);

        double hx = Math.Min(hWave, narrowX / PlanarMeshSettings.MinCellsAcrossConductor);
        double hy = Math.Min(hWave, narrowY / PlanarMeshSettings.MinCellsAcrossConductor);
        if (!(hx > 0) || double.IsInfinity(hx)) hx = (x1 - x0) / 8.0;
        if (!(hy > 0) || double.IsInfinity(hy)) hy = (y1 - y0) / 8.0;

        // ── R-msh-5: the edge reference length and the grading ratio ──────────────────────────
        double edgeRef = EdgeReferenceLength(edgeReference, narrowest, Math.Min(hx, hy));
        double c0      = s.EdgeMesh && s.EdgeCells > 0
            ? PlanarMeshSettings.EdgeFractionOfReference * edgeRef
            : 0.0;

        // The growth ratio is DERIVED from the requested cell count rather than fixed, so that the
        // geometric run c₀, c₀r, c₀r², … reaches the bulk cell size in exactly EdgeCells cells.
        //
        // The alternative — fix r at §10.5's 1.7 and stop grading at the accumulated distance
        // c₀(rⁿ−1)/(r−1) — was written first and is WRONG in a way that only shows up under
        // translation: the marcher lands EXACTLY on that accumulation point (its own steps are that
        // same geometric series), so whether the next cell is graded or bulk is decided by the last
        // bit of a floating-point comparison. Moving the same rectangle 3.7 mm flipped it and changed
        // the mesh by 33%. Deriving r instead makes the size field continuous, and the knife edge
        // disappears rather than being tolerated. The realised ratio is reported in the notes.
        double ratioX = GrowthRatioFor(c0, hx, s.EdgeCells);
        double ratioY = GrowthRatioFor(c0, hy, s.EdgeCells);

        // ── The grid (D8: one tensor-product grid, shared by every layer) ─────────────────────
        control?.BeginStage("building the grid");
        var (hardX, hardY, attractX, attractY, staircased) =
            CollectBoundaryLines(problem, x0, y0, x1, y1, rimGrading);

        long estX = EstimateLineCount(x0, x1, hardX, hx);
        long estY = EstimateLineCount(y0, y1, hardY, hy);
        if (estX * estY > MaxGridCells)
        {
            notes.Add($"The mesh this geometry demands is about {estX * estY:N0} grid cells before " +
                      "any of them are tested against the metal, which is past what can be built at all.");
            return Refused(layerNames, meshFreqHz, lambdaG, hWave, narrowest, edgeRef, staircased, notes,
                $"This geometry needs on the order of {estX * estY:N0} mesh cells, far past the " +
                $"{UnknownCeiling:N0}-unknown ceiling this kernel is built for — the grid alone cannot " +
                "be built, let alone solved. " +
                (hWave <= Math.Min(narrowX, narrowY) / PlanarMeshSettings.MinCellsAcrossConductor
                    ? $"The cell size is set by wavelength (λ_g/{s.CellsPerWavelength} = {fmt(hWave)}): " +
                      "lower Cells per wavelength, size the mesh at a lower Mesh frequency, or analyse " +
                      "a smaller region."
                    : $"The cell size is set by the narrowest metal ({fmt(Math.Min(narrowX, narrowY))}, " +
                      $"meshed {PlanarMeshSettings.MinCellsAcrossConductor} cells across), not by " +
                      "wavelength, so Cells per wavelength will not reduce it — narrow the range of " +
                      "widths in the analysed region, or analyse a smaller region."));
        }

        double[] gx = BuildGridLines(x0, x1, hardX, attractX, hx, c0, ratioX - 1.0);
        double[] gy = BuildGridLines(y0, y1, hardY, attractY, hy, c0, ratioY - 1.0);

        // ── Cells ─────────────────────────────────────────────────────────────────────────────
        var cells  = new List<PlanarCell>();
        var bases  = new List<PlanarBasis>();
        var perLayerCells    = new int[problem.Layers.Count];
        var perLayerUnknowns = new int[problem.Layers.Count];

        int nx = gx.Length - 1, ny = gy.Length - 1;

        // One continuous stage bar across the whole scan: every conductor layer's rows, then every
        // via's. Declared up front so the bar is honest from the first row rather than restarting
        // per layer, which would read as progress going backwards on a multi-level design.
        long scanRows = (long)ny * (problem.Layers.Count + problem.ViaList.Count);
        control?.BeginStage($"testing {ny:N0} row(s) against the metal", scanRows);

        var cellAtPerLayer = new int[problem.Layers.Count][];
        var conformal = new ConformalCounts();
        for (int li = 0; li < problem.Layers.Count; li++)
        {
            var layer = problem.Layers[li];
            control?.SetStageLabel($"scanning '{layer.Name}' ({li + 1} of {problem.Layers.Count})");

            // cellAt is a plain int[] indexed by (iy * nx + ix), never a dictionary: R-msh-2 forbids
            // any set/dictionary ITERATION on this path, and an array also makes the adjacency scan
            // that produces the rooftop pairs an exact integer question.
            var cellAt = new int[nx * ny];
            Array.Fill(cellAt, -1);
            cellAtPerLayer[li] = cellAt;

            int firstCell = cells.Count;
            if (s.BoundaryCells == PlanarBoundaryCells.Conformal)
            {
                BuildConformalCells(layer, li, gx, gy, nx, ny, sliverAreaFraction,
                                    cells, cellAt, conformal, control, diagnostics);
            }
            else
            {
                // L8b's own loop, unchanged and deliberately not merged with the conformal one:
                // R-cut-2 asks for a Manhattan mesh to be BIT-IDENTICAL, and the cheapest way to
                // promise that is for the shipped path to be the same expressions in the same order.
                for (int iy = 0; iy < ny; iy++)
                {
                    control?.TickStage();
                    double yc = 0.5 * (gy[iy] + gy[iy + 1]);
                    var spans = RowSpans(layer, yc);
                    if (spans.Count == 0) continue;

                    int si = 0;
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double xc = 0.5 * (gx[ix] + gx[ix + 1]);
                        while (si < spans.Count && spans[si].Hi < xc) si++;
                        if (si >= spans.Count) break;
                        if (xc < spans[si].Lo) continue;

                        cellAt[iy * nx + ix] = cells.Count;
                        cells.Add(new PlanarCell(li, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
                    }
                }
            }
            perLayerCells[li] = cells.Count - firstCell;

            // R-msh-6: N is the number of SHARED INTERNAL EDGES — one rooftop per adjacent pair.
            //
            // R-cut-4 — TWO THINGS A MERGED CELL BREAKS, and both are decided HERE, in the mesher,
            // where the basis set is built, rather than in the fill where they would be a guard on a
            // division. A cell that absorbed a sliver covers TWO grid positions, so (a) the two can be
            // "adjacent" to each other, which is a rooftop from a cell to itself and is not a basis at
            // all; and (b) both can be adjacent to the same third cell, which would be the SAME
            // unknown counted twice and a singular matrix. The `seen` set is a MEMBERSHIP test only —
            // it is never iterated, so R-msh-2's ordering contract is untouched — and it is allocated
            // only when a merge actually happened.
            var seen = conformal.Merged > 0 ? new HashSet<(int, int, int)>() : null;
            int firstBasis = bases.Count;
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int a = cellAt[iy * nx + ix];
                    if (a < 0) continue;
                    if (ix + 1 < nx && cellAt[iy * nx + ix + 1] is var bx && bx >= 0 && bx != a &&
                        (seen is null || seen.Add((Math.Min(a, bx), Math.Max(a, bx), 0))))
                    {
                        if (FaceCarriesABasis(cells[a], cells[bx], PlanarBasisDirection.X, gx[ix + 1], conformal))
                            bases.Add(new PlanarBasis(li, a, bx, PlanarBasisDirection.X));
                    }
                    if (iy + 1 < ny && cellAt[(iy + 1) * nx + ix] is var by && by >= 0 && by != a &&
                        (seen is null || seen.Add((Math.Min(a, by), Math.Max(a, by), 1))))
                    {
                        if (FaceCarriesABasis(cells[a], cells[by], PlanarBasisDirection.Y, gy[iy + 1], conformal))
                            bases.Add(new PlanarBasis(li, a, by, PlanarBasisDirection.Y));
                    }
                }
            perLayerUnknowns[li] = bases.Count - firstBasis;
        }

        // ── L9c — VERTICAL (via) bases, AFTER every horizontal basis of every level ────────────
        //
        // R-via-5, and the ordering is the contract rather than a consequence: adding a via
        // renumbers no horizontal unknown and adding a level renumbers no via. Interleaving them per
        // level would destroy both, and ports, the current-density map and de-embedding all index by
        // this vector. Within the vertical block the order is (via as given, IY, IX) — integers
        // throughout, exactly as R-msh-2 requires, with no dictionary or set on the path.
        //
        // A vertical basis exists where THREE things coincide: the via's footprint covers the cell,
        // and BOTH levels carry metal there. The third condition is what makes the basis an
        // attachment mode rather than a dangling filament — a via that lands where there is no metal
        // has nothing to conserve charge against, and is dropped and counted rather than solved.
        int firstVia = bases.Count;
        int viaFootprintCells = 0, viaUnattached = 0;
        int groundUnknowns = 0;
        foreach (var via in problem.ViaList)
        {
            control?.SetStageLabel("scanning via footprints");
            // ── The GROUND-ATTACHMENT path (R-gv-5) ───────────────────────────────────────────
            //
            // A via to the plane has only ONE meshed foot, so the three-way coincidence above
            // becomes a two-way one: the footprint covers the cell, and the MESHED level carries
            // metal there. The plane always does — that is what makes it the ground plane.
            //
            // Both of L9c's silent mesher failures apply to this path too and it does NOT inherit
            // their tests: the footprint must still contribute HARD GRIDLINES (or the via vanishes
            // with no error — measured at zero vertical unknowns on a 40 µm footprint) and must
            // still NOT get the edge grading a conductor rim gets (measured at 2,448 unknowns
            // against 424). Both are handled in CollectBoundaryLines, which walks problem.ViaList
            // without caring which terminal a via names — so a ground via is covered by
            // construction, and asserted for this path specifically in SurfaceMesherTests.
            var upper = cellAtPerLayer[via.UpperLayerIndex];
            var lower = via.ToGround ? null : cellAtPerLayer[via.LowerLayerIndex];

            for (int iy = 0; iy < ny; iy++)
            {
                control?.TickStage();
                double yc = 0.5 * (gy[iy] + gy[iy + 1]);
                for (int ix = 0; ix < nx; ix++)
                {
                    double xc = 0.5 * (gx[ix] + gx[ix + 1]);
                    bool covered = false;
                    foreach (var poly in via.Polygons)
                        if (poly.Contains(xc, yc)) { covered = true; break; }
                    if (!covered) continue;

                    viaFootprintCells++;
                    int b = upper[iy * nx + ix];

                    if (via.ToGround)
                    {
                        if (b < 0) { viaUnattached++; continue; }
                        // Both cells name the ONE meshed foot; the signs come from Halves, and the
                        // grounded half carries Sign = 0 so the fill's four-term sum drops it.
                        bases.Add(new PlanarBasis(via.UpperLayerIndex, b, b,
                                                  PlanarBasisDirection.Z, AttachesToGround: true));
                        groundUnknowns++;
                        continue;
                    }

                    int a = lower![iy * nx + ix];
                    if (a < 0 || b < 0) { viaUnattached++; continue; }
                    bases.Add(new PlanarBasis(via.LowerLayerIndex, a, b, PlanarBasisDirection.Z));
                }
            }
        }
        int viaUnknowns = bases.Count - firstVia;
        if (problem.ViaList.Count > 0)
        {
            notes.Add($"{problem.ViaList.Count} via(s) resolve onto {viaFootprintCells} cell(s) of the " +
                      $"shared grid and contribute {viaUnknowns} vertical unknown(s) — one per cell " +
                      "that carries metal on BOTH levels. The via mesh is not a separate mesh: L8b's " +
                      "D8 put every level on one tensor grid so that a vertical basis is a cell PAIR " +
                      "in z, exactly as a rooftop is a cell pair in x or y.");
            if (groundUnknowns > 0)
                notes.Add($"{groundUnknowns} of those are GROUND ATTACHMENTS — half rooftops whose " +
                          "lower terminal is the ground plane itself, which is the laterally " +
                          "infinite conductor the Green's function handles analytically and is never " +
                          "a meshed level. Their return charge is that plane's own image rather than " +
                          "a second divergence pulse on the metal, so unlike every other basis here " +
                          "their net charge is not zero.");
            if (viaUnattached > 0 || viaUnknowns == 0)
                notes.Add($"{viaUnattached} via footprint cell(s) were DROPPED because one of the two " +
                          $"levels carries no metal there, and {viaUnknowns} vertical unknown(s) " +
                          "survive. A via that lands on bare dielectric has nothing to conserve charge " +
                          "against; it is counted here rather than solved, because a dangling vertical " +
                          "filament would put a monopole at its foot and the wrongness would look like " +
                          "a bad mesh. A via that resolves onto NO cell at all is outside the meshed " +
                          "extent, which is the union of the conductor artwork.");
        }

        var mesh = new PlanarMesh(cells, bases, layerNames, gx, gy);

        double minEdge = double.PositiveInfinity, maxEdge = 0, meshedArea = 0;
        foreach (var c in cells)
        {
            // R-msh's own quantities are asked of the GRID rectangle even for a cut cell: they exist
            // to police the λ_g/N cap and the transverse resolution, and both are properties of the
            // grid the cell was carved out of. The METAL's own extent is PlanarCell.Area, and it is
            // what MeshedAreaM2 sums — the tiling gate's quantity, kept separate on purpose.
            minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
            maxEdge = Math.Max(maxEdge, c.LongestEdge);
            meshedArea += c.Area;
        }
        if (cells.Count == 0) { minEdge = 0; maxEdge = 0; }

        int across = MinCellsAcrossRun(mesh, nx, ny, problem.Layers.Count);

        // ── The notes a user would otherwise have to guess at ─────────────────────────────────
        if (double.IsInfinity(lambdaG))
            notes.Add("No sweep frequency was given, so the mesh is driven purely by geometry — " +
                      "the λ_g/N cell-size cap did not apply.");
        else
        {
            // R-emp-2 — the note quotes the MESH frequency, because that is what λ_g was taken at.
            // Leaving it reading "the highest frequency of the sweep" once MeshFrequencyHz exists
            // would produce a report claiming a mesh was sized at 20 GHz when it was sized at 10 —
            // the exact class of silently wrong statement this area keeps finding.
            bool sizedAtSweepTop = !(s.MeshFrequencyHz is { } setF && setF > 0)
                                   || meshFreqHz >= problem.MaxFrequencyHz;
            notes.Add($"Cell size capped at λ_g/{s.CellsPerWavelength} = {fmt(hWave)} — λ_g = {fmt(lambdaG)} " +
                      $"in εᵣ = {problem.Slab.Material.EpsR:G4} at {Eng(meshFreqHz)}Hz, " +
                      (sizedAtSweepTop
                          ? "the highest frequency of the sweep. Widening the sweep upward will change " +
                            "this, and with it the unknown count."
                          : "the frequency the mesh is sized at. Changing it changes the unknown count."));

            // The second note quantifies the trade in the unit the user set, and fires ONLY below the
            // sweep's top — at or above it there is nothing under-resolved to report.
            //
            // …AND ONLY WHEN THE CAP ACTUALLY BINDS (2026-08-14). `effCellsPerLambda` is computed from
            // the CAP, not from the realised cell size, so where MinCellsAcrossConductor sets the
            // pitch instead this note states a flatly false number and then recommends two knobs that
            // change nothing. On the owner's reported taper it read "at 5 GHz the cells are λ_g/1"
            // while the cells were in fact 56 µm — λ_g/1120 — and told the user to raise the very
            // controls the refusal beside it had just said were inert. Same defect as the refusal's
            // own, in a note.
            bool capBinds = hWave <= narrowX / PlanarMeshSettings.MinCellsAcrossConductor
                         || hWave <= narrowY / PlanarMeshSettings.MinCellsAcrossConductor;
            if (!sizedAtSweepTop && problem.MaxFrequencyHz > 0 && capBinds)
            {
                double effCellsPerLambda =
                    s.CellsPerWavelength * meshFreqHz / problem.MaxFrequencyHz;
                notes.Add($"The mesh was sized at {Eng(meshFreqHz)}Hz, not at the sweep's " +
                          $"{Eng(problem.MaxFrequencyHz)}Hz top. At {Eng(problem.MaxFrequencyHz)}Hz the " +
                          $"cells are λ_g/{effCellsPerLambda:G3} rather than the λ_g/{s.CellsPerWavelength} " +
                          "you asked for. Raise Mesh frequency, or raise Cells per wavelength, if the top " +
                          "of the band matters.");
            }
        }

        notes.Add($"Narrowest conductor dimension {fmt(narrowest)}, meshed {across} cell(s) across " +
                  $"(target {PlanarMeshSettings.MinCellsAcrossConductor}).");

        if (s.EdgeMesh && s.EdgeCells > 0)
        {
            notes.Add($"Edge mesh on: {s.EdgeCells} graded cell(s) at every axis-parallel conductor edge, " +
                      $"outermost {fmt(c0)} ({PlanarMeshSettings.EdgeFractionOfReference:P0} of {fmt(edgeRef)}, " +
                      $"{DescribeReference(edgeReference)}), growing by {ratioX:G3}× across and {ratioY:G3}× along.");

            // …and when there is no such edge, SAY SO. "at every axis-parallel conductor edge" is
            // accurate and nobody reads the qualifier, so on an all-curved part the note above
            // reports an edge mesh that does not exist anywhere on the artwork. Same class as the
            // EffectiveEdgeCells clamp note below it: a control that silently does nothing is worse
            // than one that says why.
            if (attractX.Count == 0 || attractY.Count == 0)
            {
                bool neither = attractX.Count == 0 && attractY.Count == 0;
                string where = neither             ? "in either direction"
                             : attractX.Count == 0 ? "across (x)"
                                                   : "along (y)";
                notes.Add($"…but NO edge grading was actually applied {where}: no conductor edge on " +
                          "this artwork is both axis-parallel and long enough to carry the 1/√d edge " +
                          "current. An oblique or curved outline contributes NEITHER a gridline nor a " +
                          "graded fan" +
                          (s.BoundaryCells == PlanarBoundaryCells.Conformal
                              ? " — the boundary CELLS follow it, but their sizes still come from the "
                                + "λ_g marcher alone"
                              : " — it is approximated by a staircase instead") +
                          (staircased > 0 ? " (see below)" : "") + ". " +
                          (neither ? "Raising Edge cells will not change this mesh at all."
                                   : "Raising Edge cells acts on the other direction only."));
            }

            // The growth ratio is CLAMPED (GrowthRatioFor), so past a geometry-dependent point the
            // requested cell count cannot be honoured and raising it further changes nothing at all.
            // Saying so is the difference between "this control does not do what I expected" and
            // "this control did nothing and never told me" (owner report, 2026-08-09: "I set my Edge
            // cells to 10 and expected the mesh to increase near the edges, but it appeared the same").
            int usedX = EffectiveEdgeCells(c0, hx, ratioX);
            int usedY = EffectiveEdgeCells(c0, hy, ratioY);
            int used  = Math.Max(usedX, usedY);
            if (used > 0 && used != s.EdgeCells)
                notes.Add($"The edge grading ratio is bounded to {MinGrowthRatio:G3}–{MaxGrowthRatio:G3}×, so " +
                          $"the ramp from {fmt(c0)} to the bulk cell reaches it in about {used} cell(s), " +
                          $"not the {s.EdgeCells} requested — on this geometry any value " +
                          (used < s.EdgeCells ? "above" : "below") + $" ~{used} meshes the same. " +
                          "Edge cells sets how far the refinement REACHES, never how fine the finest " +
                          "cell is: that is fixed at " +
                          $"{PlanarMeshSettings.EdgeFractionOfReference:P0} of the reference length.");
        }
        else
        {
            notes.Add("Edge mesh off — the 1/√d edge current is not resolved, so loss and Z₀ will read low.");
        }

        // ── The boundary model, said in the notes because this phase's whole visible effect is here
        //    and in the overlay. StaircasedPolygons must stop CLAIMING a staircase once the cells are
        //    conformal — the count still means "polygons that are not axis-aligned", but what happens
        //    to them is now the opposite of what the old wording said.
        if (staircased > 0 && s.BoundaryCells == PlanarBoundaryCells.Staircase)
            notes.Add($"{staircased} polygon(s) are not axis-aligned and are approximated by a " +
                      "STAIRCASE — this mesh builds rectangular cells only (D2). A mitred bend, a " +
                      "taper or a curve therefore carries a quantisation error that scales with the " +
                      "cell size. Turn Boundary cells to \"Conformal\" to cut the boundary cells to " +
                      "the metal instead.");
        else if (s.BoundaryCells == PlanarBoundaryCells.Conformal)
        {
            notes.Add(staircased > 0
                ? $"Boundary cells are CONFORMAL: {conformal.Cut} cell(s) on {staircased} " +
                  "non-axis-aligned polygon(s) are cut to follow the metal rather than staircased, so " +
                  "the union of the cells is the drawn outline to round-off rather than to the cell " +
                  "size. The interior of the mesh is unchanged."
                : "Boundary cells are CONFORMAL, but this artwork is entirely axis-aligned so no cell " +
                  "needed cutting — the mesh is identical to the staircased one, which is what an " +
                  "axis-aligned outline means.");

            if (conformal.Merged > 0)
                notes.Add($"{conformal.Merged} sliver cell(s) were absorbed into the neighbour they " +
                          $"share their largest face with (below {DefaultSliverAreaFraction:P0} of a " +
                          "grid cell's area). A sliver is normalised by its own area, so leaving one " +
                          "in puts an enormous row in the matrix and destroys the conditioning " +
                          "silently — the matrix stays symmetric and still factors.");

            if (conformal.UnmergedSlivers > 0)
                notes.Add($"{conformal.UnmergedSlivers} sliver cell(s) had no ordinary neighbour to be " +
                          "absorbed into and were kept as they are. Their rows are poorly scaled; " +
                          "raising Cells per wavelength moves the grid off the outline and usually " +
                          "removes them.");

            if (conformal.RefusedFaces > 0)
                notes.Add($"{conformal.RefusedFaces} grid adjacency(ies) carry NO basis function: the " +
                          "two cells touch on the grid but their shared edge is outside the metal, or " +
                          "one of them is not swept by that edge — a rooftop there would push its unit " +
                          "current out through the metal's rim rather than across the edge. The " +
                          "unknown count already excludes them.");

            if (conformal.Fallback > 0)
                notes.Add($"{conformal.Fallback} cell(s) could NOT be cut and are staircased: " +
                          $"{conformal.FallbackMultiPolygon} touched by more than one drawn shape, " +
                          $"{conformal.FallbackHole} touched by a hole, {conformal.FallbackNotFlowSimple} " +
                          "met by the outline in more than one run in BOTH directions at once. A cell " +
                          "crossed twice along every line through it is a mesh that is too coarse for " +
                          "its own artwork, so raise Cells per wavelength and this count falls.");

            // R-cvx-2 — the second count, kept separate from the first. A mesher that silently drops
            // a basis is as bad as one that silently re-shapes a cell.
            if (conformal.OneDirectionOnly > 0)
                notes.Add($"{conformal.OneDirectionOnly} cell(s) follow the metal but are describable " +
                          $"in ONE current direction only, and {conformal.RefusedFacesNotFlowSimple} " +
                          "basis function(s) across them were declined on that. The cell itself is cut " +
                          "and tiles the metal exactly — what it cannot carry is current running along " +
                          "the axis in which the outline crosses it twice. Raising Cells per wavelength " +
                          "separates the two crossings into different cells and the count falls.");
        }

        // R-msh-8a — and the WORDING here is the owner's correction of 2026-08-14, not a rephrasing.
        //
        // This note used to open "X already has a validated analytic model, which is effectively free"
        // and then argue for it. A user reading it is standing in the EM setup, having deliberately
        // chosen the full-wave kernel for this exact part; being told the cheap model exists is
        // something they already know, and leading with it reads as "you are doing this wrong". The
        // note's real job is the opposite one: say what the expensive run BUYS over the cheap one, so
        // the user can tell whether they want it — and, when the two answers land close together, know
        // that is agreement rather than a wasted afternoon.
        foreach (var alt in problem.Alternatives)
            notes.Add($"{alt.Subject} — {alt.Reason}");

        int n = bases.Count;
        var verdict = n > ceiling ? PlanarBudgetVerdict.Refused
                    : n >= (int)(ceiling * WarnFraction) ? PlanarBudgetVerdict.Warn
                    : PlanarBudgetVerdict.Ok;

        string? refusal = null;
        if (verdict == PlanarBudgetVerdict.Refused)
            refusal = BuildRefusal(n, cells.Count, s, narrowX, narrowY, hx, hy, hWave, x1 - x0, y1 - y0,
                                   accelerated: accel,
                                   acceleratedWouldFit: !accel && n <= AcceleratedUnknownCeiling
                                                      && !problem.RequiresGeneralKernel,
                                   fmt: fmt);
        else if (verdict == PlanarBudgetVerdict.Warn)
            notes.Add(accel
                ? $"{n:N0} unknowns is within {1 - WarnFraction:P0} of the {ceiling:N0}-unknown " +
                  "ACCELERATED ceiling (brief-em-aim-ceiling.md). It will solve, but a coarser mesh " +
                  "will solve much faster, and GMRES's own iteration count grows well before this " +
                  "ceiling on a mesh refined mainly by resolution rather than by extent."
                : $"{n:N0} unknowns is within {1 - WarnFraction:P0} of the {ceiling:N0} " +
                  $"ceiling ({PlanarSystem.ResidentBytes(n, cells.Count) / (1024.0 * 1024.0):N0} MB " +
                  "resident at the peak of one frequency point: matrix + factors + cached cores). " +
                  "It will solve, but a coarser mesh will solve much faster.");

        return new PlanarMeshReport(
            Mesh:                          mesh,
            CellCount:                     cells.Count,
            UnknownCount:                  n,
            CellsPerLayer:                 perLayerCells,
            UnknownsPerLayer:              perLayerUnknowns,
            MinCellEdgeM:                  minEdge,
            MaxCellEdgeM:                  maxEdge,
            CellsAcrossNarrowestConductor: across,
            NarrowestConductorWidthM:      narrowest,
            FrequencyHz:                   meshFreqHz,
            GuidedWavelengthM:             lambdaG,
            MaxCellSizeM:                  hWave,
            EdgeReferenceLengthM:          edgeRef,
            StaircasedPolygons:            staircased,
            ViaUnknownCount:               viaUnknowns,
            Verdict:                       verdict,
            Refusal:                       refusal,
            Notes:                         notes,
            BoundaryCells:                 s.BoundaryCells,
            CutCellCount:                  conformal.Cut,
            MergedSliverCount:             conformal.Merged,
            StaircaseFallbackCells:        conformal.Fallback,
            MeshedAreaM2:                  meshedArea,
            OneDirectionCells:             conformal.OneDirectionOnly);
    }

    /// <summary>
    /// R-msh-7's wording, in one place: <b>the predicted N, the ceiling, and WHAT TO CHANGE</b>. A
    /// refusal that names only the first two leaves the user with nothing to do about it.
    ///
    /// <para><b>It named the wrong things to change, and a real user turned one of them (owner report,
    /// 2026-08-14).</b> The old text said "Lower Cells per wavelength, turn the edge mesh off, or
    /// analyse a smaller region" unconditionally. On the reported geometry — a 6.9 → 100 Ω
    /// Klopfenstein taper, 13.1 mm of metal at one end and 299 µm at the other — <b>Cells per
    /// wavelength does not move the unknown count by one</b>, and neither does Mesh frequency:
    /// <see cref="PlanarMeshSettings.MinCellsAcrossConductor"/> sets the pitch from the NARROWEST run
    /// (74.6 µm) and the λ_g cap sits 42× coarser at 3.13 mm, so it never binds. Measured:
    /// 7,749 unknowns at 5, 10 and 20 cells/λ alike; 5,772 with the edge mesh off; still refused. The
    /// user halved the knob the message named, saw the identical number, and stopped.
    ///
    /// <para>So the refusal now asks which quantity is actually binding and names only remedies that
    /// act on THAT one — and, when the wavelength cap is not binding, says so outright rather than
    /// leaving the user to discover it by experiment. The megabytes moved to the end and lost the
    /// lead, because quoting them first reads as a memory limit and invites "does my machine need
    /// more RAM": <see cref="UnknownCeiling"/> is a compile-time constant, identical on every
    /// machine, and the same file refuses identically everywhere.</para>
    ///
    /// <para><b>brief-em-aim-ceiling.md, 2026-08-14 — the accelerated solve moved from an inert remedy
    /// to a real one, and the wording has to tell the three resulting cases apart.</b> When this mesh
    /// would fit under <see cref="AcceleratedUnknownCeiling"/> but was checked against the dense one
    /// (<paramref name="acceleratedWouldFit"/>), turning the accelerator on is named FIRST — it is the
    /// only remedy here that changes nothing about the drawn geometry or the mesh settings. When the
    /// accelerator is already what was asked for and this mesh is STILL past its own, higher, ceiling
    /// (<paramref name="accelerated"/>), the old "does not move this ceiling" sentence is false on its
    /// face — it moved, and the mesh is past the new one too — so it is replaced with that ceiling's
    /// own number instead of the dense path's.</para>
    /// </summary>
    private static string BuildRefusal(
        int n, int cellCount, PlanarMeshSettings s,
        double narrowX, double narrowY, double hx, double hy, double hWave,
        double extentX, double extentY, bool accelerated, bool acceleratedWouldFit,
        PlanarLengthFormat fmt)
    {
        double pitch     = Math.Min(hx, hy);
        double narrowest = Math.Min(narrowX, narrowY);
        int    ceiling   = accelerated ? AcceleratedUnknownCeiling : UnknownCeiling;

        // WHICH quantity set the cell size. This is the whole difference between a refusal a user can
        // act on and one they can only argue with: hx/hy are Math.Min(hWave, narrow/MinCells), so the
        // wavelength cap is binding exactly when it is the smaller of the two on at least one axis.
        bool waveBinds = hWave <= narrowX / PlanarMeshSettings.MinCellsAcrossConductor
                      || hWave <= narrowY / PlanarMeshSettings.MinCellsAcrossConductor;

        var why = waveBinds
            ? $" The cell size is set by wavelength here — λ_g/{s.CellsPerWavelength} = {fmt(hWave)} " +
              $"across {fmt(extentX)} × {fmt(extentY)} of artwork."
            : $" The cell size is set by the NARROWEST metal, not by wavelength: the narrowest " +
              $"conductor run is {fmt(narrowest)}, and meshing it " +
              $"{PlanarMeshSettings.MinCellsAcrossConductor} cells across forces a {fmt(pitch)} " +
              $"pitch over all {fmt(extentX)} × {fmt(extentY)} of the artwork — the grid is one " +
              $"tensor product over the whole layout, so the narrow end is paid for everywhere. The " +
              $"λ_g/{s.CellsPerWavelength} cap is {fmt(hWave)}, {hWave / pitch:G3}× coarser, so " +
              "LOWERING CELLS PER WAVELENGTH OR MESH FREQUENCY WILL NOT REDUCE THIS COUNT.";

        // Short imperatives only. An action carrying its own explanatory clause reads as part of the
        // NEXT action once the list is joined ("…its narrowest, turn the edge mesh off or analyse a
        // smaller region"), so the explanation goes in its own sentence after the list.
        var acts = new List<string>();
        // FIRST, when it is real: the one remedy that touches neither the drawn geometry nor the mesh
        // settings — see the class-level note on AcceleratedUnknownCeiling for the measurement.
        if (acceleratedWouldFit)
            acts.Add("turn on the accelerated solve (Solver options)");
        if (waveBinds)
        {
            acts.Add("lower Cells per wavelength");
            acts.Add("size the mesh at a lower Mesh frequency");
        }
        else
        {
            acts.Add("narrow the range of widths in the analysed region");
        }
        if (s.EdgeMesh && s.EdgeCells > 0)
            acts.Add("turn the edge mesh off");
        acts.Add("analyse a smaller region");

        string how = waveBinds
            ? ""
            : " Splitting the structure at a uniform-width plane, analysing the pieces separately and " +
              "cascading them is the usual way through a part whose widest metal is many times its " +
              "narrowest.";

        string costNote = accelerated
            ? // The dense byte count is meaningless here — this mesh will never see a dense matrix —
              // and the accelerator's own working set has no closed form (it depends on geometry), so
              // this states the measured ballpark rather than a number nothing computed for this run.
              $"(This is already the ACCELERATED ceiling — {AcceleratedUnknownCeiling:N0} unknowns, " +
              "measured healthy on a wide-to-narrow taper's own growth pattern and, separately, on a " +
              "conformally cut mesh; the accelerator's own working set stays under 200 MB even at that " +
              "ceiling. A mesh refined mainly by RESOLUTION rather than by extent can fail to converge " +
              "before reaching it — GMRES throws rather than returning a wrong answer when that " +
              "happens, so it is reported at solve time, not here. Solving a mesh this size at all " +
              "needs matrix compression, which is not built.)"
            : acceleratedWouldFit
              ? // The remedy above already says what to do; this says WHY it works, in the same terms
                // the dense-only sentence used, so a user comparing the two ceilings sees real numbers.
                $"(The dense path's {UnknownCeiling:N0}-unknown ceiling is a fixed property of this " +
                $"kernel, not of your machine — {n:N0} unknowns is " +
                $"{PlanarSystem.ResidentPhrase(n, cellCount)}, and " +
                "the same geometry refuses identically everywhere. The accelerated solve's own ceiling " +
                $"is higher — {AcceleratedUnknownCeiling:N0} unknowns, measured — because its working " +
                "set is a near-field sparse correction plus a uniform auxiliary grid rather than the " +
                "full N×N matrix, which is why turning it on is the first remedy above rather than a " +
                "change to this mesh.)"
              : $"(The ceiling is a fixed property of this kernel, not of your machine — {n:N0} " +
                $"unknowns is {PlanarSystem.ResidentPhrase(n, cellCount)}, and the same geometry " +
                "refuses identically everywhere. Solving a mesh this size directly needs matrix " +
                "compression, which is not built; the accelerated solve would not help either — this " +
                $"mesh is past its {AcceleratedUnknownCeiling:N0}-unknown ceiling too.)";

        return $"This geometry needs {n:N0} unknowns, past the {ceiling:N0}-unknown ceiling " +
               "this kernel is built for." + why +
               " What acts on the count here: " + JoinOr(acts) + "." + how + " " + costNote;
    }

    /// <summary>"a, b or c" — a remedy list reads as prose, and a bare comma list reads as a checklist
    /// the user has to do all of.</summary>
    private static string JoinOr(IReadOnlyList<string> parts)
        => parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " or " + parts[^1],
        };

    private static double MatrixMegabytes(int n) => (double)n * n * 16.0 / (1024.0 * 1024.0);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // CONFORMAL (CUT) BOUNDARY CELLS — brief-conformal-boundary-cells.md, M1
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-cut-3's sliver threshold, as a fraction of the grid rectangle's area.</b>
    ///
    /// <para>A cut that leaves a vanishing fraction of a cell is the classic cut-cell failure: the
    /// basis normalisation is 1/Area, so a sliver puts an enormous row in the matrix and destroys the
    /// conditioning — <i>silently</i>, because the matrix is still symmetric and still factors. Below
    /// this fraction the sliver is absorbed into the neighbour it shares its largest face with, giving
    /// one cell whose region has two pieces rather than two cells one of which is degenerate.</para>
    ///
    /// <para><b>The value is CONSERVATIVE, and that is a narrower claim than an optimum.</b>
    /// <c>ConformalSliverTests</c> sweeps it and reports the matrix condition number and the answer
    /// either side; see this directory's <c>CLAUDE.md</c> for the table. What the sweep establishes
    /// is that 0.05 sits on a wide plateau (0.02 … 0.10 produce the identical mesh on the disc) and
    /// comfortably above the thinnest cut cell the mesher was observed to produce — <b>4.4e-4 of a
    /// grid rectangle, at cells/λ = 250</b>, a 1/Area factor of ~2,270. It is NOT claimed to be the
    /// point where conditioning turns over; nothing here located such a point.</para>
    /// </summary>
    public const double DefaultSliverAreaFraction = 0.05;

    /// <summary>What the conformal pass did, carried out of the per-layer loop so the notes and the
    /// report say it once. Mutable and single-threaded on purpose — the cell scan is sequential.</summary>
    internal sealed class ConformalCounts
    {
        public int Cut;
        public int Merged;
        public int UnmergedSlivers;
        public int FallbackMultiPolygon;
        public int FallbackHole;

        /// <summary>M1's own meaning: the clipped region is flow-simple in NEITHER direction, so no
        /// basis through it can be described by strips and the cell is staircased. This is what the
        /// old <c>FallbackNonConvex</c> became, and it is a strictly smaller set.</summary>
        public int FallbackNotFlowSimple;

        /// <summary>R-cvx-2's SECOND count, and a different event: the cell is CUT and kept, but one
        /// of its two directions is refused a basis. A user reading the notes needs the two apart.</summary>
        public int OneDirectionOnly;

        public int RefusedFaces;
        public int RefusedFacesNotFlowSimple;
        public int Fallback => FallbackMultiPolygon + FallbackHole + FallbackNotFlowSimple;
    }

    /// <summary>
    /// One conductor level's cells, following the METAL rather than the grid.
    ///
    /// <para>Three passes, and the middle one is the reason it cannot be a single scan: cells are
    /// CLASSIFIED, then slivers are ABSORBED into neighbours (R-cut-3), then the survivors are EMITTED
    /// in (IY, IX) order — R-msh-2's contract, which an in-place merge would break by removing a cell
    /// that had already been numbered.</para>
    /// </summary>
    private static void BuildConformalCells(
        PlanarConductorLayer layer, int li,
        double[] gx, double[] gy, int nx, int ny, double sliverFraction,
        List<PlanarCell> cells, int[] cellAt, ConformalCounts counts, RunControl? control,
        ConformalDiagnostics? diagnostics = null)
    {
        const byte Absent = 0, Whole = 1, Cut = 2;

        var kind    = new byte[nx * ny];
        var regions = new PlanarCellRegion?[nx * ny];

        var polys  = layer.Polygons;
        var bounds = new (double X0, double Y0, double X1, double Y1)[polys.Count];
        for (int p = 0; p < polys.Count; p++) bounds[p] = polys[p].Bounds();

        // ── Pass 1: classify every grid position ──────────────────────────────────────────────
        for (int iy = 0; iy < ny; iy++)
        {
            control?.TickStage();
            double ry0 = gy[iy], ry1 = gy[iy + 1];

            for (int ix = 0; ix < nx; ix++)
            {
                double rx0 = gx[ix], rx1 = gx[ix + 1];
                double xc = 0.5 * (rx0 + rx1), yc = 0.5 * (ry0 + ry1);
                double rectArea = (rx1 - rx0) * (ry1 - ry0);
                double tol      = 1e-9 * Math.Min(rx1 - rx0, ry1 - ry0);

                int touching = 0, touchIndex = -1;
                bool holeTouched = false, coveredWhole = false;

                for (int p = 0; p < polys.Count; p++)
                {
                    var (bx0, by0, bx1, by1) = bounds[p];
                    if (bx1 < rx0 || bx0 > rx1 || by1 < ry0 || by0 > ry1) continue;

                    bool touches = RingTouchesRect(polys[p].Outer, rx0, ry0, rx1, ry1);
                    bool hole    = false;
                    foreach (var h in polys[p].HoleRings)
                        if (RingTouchesRect(h, rx0, ry0, rx1, ry1)) { hole = true; break; }

                    if (touches || hole)
                    {
                        touching++;
                        touchIndex  = p;
                        holeTouched |= hole;
                    }
                    else if (polys[p].Contains(xc, yc))
                    {
                        // No boundary of this polygon meets the rectangle and its centre is inside, so
                        // the WHOLE rectangle is inside it — whatever else touches, the union covers
                        // the cell and there is nothing to cut.
                        coveredWhole = true;
                        break;
                    }
                }

                int q = iy * nx + ix;

                if (coveredWhole) { kind[q] = Whole; continue; }
                if (touching == 0) { kind[q] = Absent; continue; }

                // ── The three configurations §2 requires be a refinement instruction rather than a
                //    silently-wrong cell. All fall back to L8b's own staircase decision for this cell
                //    alone, and all are counted so a user can refine and watch the count reach zero.
                if (touching > 1 || holeTouched)
                {
                    if (touching > 1) counts.FallbackMultiPolygon++;
                    else              counts.FallbackHole++;
                    kind[q] = InMetal(polys, xc, yc) ? Whole : Absent;
                    continue;
                }

                var clipped = PlanarCellRegion.Simplify(
                    PlanarCellRegion.ClipToRect(polys[touchIndex].Outer, rx0, ry0, rx1, ry1), tol);
                if (clipped.Count < 3) { kind[q] = Absent; continue; }

                var region = PlanarCellRegion.FromPiece(clipped);
                double area = Math.Abs(region.Area);

                // Round-off either way is the UNCUT answer, and taking it keeps the interior of the
                // mesh bit-identical to the staircase's — an edge that runs exactly along a gridline
                // (every Manhattan edge does) clips to the whole rectangle.
                if (area <= 1e-12 * rectArea) { kind[q] = Absent; continue; }
                if (area >= (1.0 - 1e-12) * rectArea) { kind[q] = Whole; continue; }

                // ── M1 — THE PREDICATE IS FLOW-SIMPLICITY, NOT CONVEXITY ──────────────────────
                //
                // brief-convex-decomposition.md §1: what the strip construction in RooftopSupport
                // needs is that the region meet EVERY transverse line in ONE interval, because Build
                // spans one trapezoid from the crossing set's outer hull — a region that meets the
                // line twice therefore carries source over a gap where there is no metal. Convexity
                // implies that in both directions and is much stronger; a merged L-shaped cell has
                // never been convex and has always worked, which is the standing counter-example.
                //
                // It is asked PER DIRECTION and the refusal moves with it: a cell simple in x only
                // is CUT and contributes an x basis, and FaceCarriesABasis declines its y one. Only a
                // cell simple in NEITHER direction is staircased, and M0 measured zero of those over
                // three shipping PCells × two starters × three densities.
                bool xSimple = RooftopSupport.IsFlowSimple(region, alongX: true,  tol);
                bool ySimple = RooftopSupport.IsFlowSimple(region, alongX: false, tol);

                if (!xSimple && !ySimple)
                {
                    counts.FallbackNotFlowSimple++;
                    diagnostics?.Fallbacks.Add(Classify(li, ix, iy, "flow-simple in neither direction",
                                                        region, clipped, area / rectArea, tol));
                    kind[q] = InMetal(polys, xc, yc) ? Whole : Absent;
                    continue;
                }

                if (!xSimple || !ySimple)
                {
                    counts.OneDirectionOnly++;
                    diagnostics?.OneDirectionOnly.Add(Classify(li, ix, iy, "one direction only",
                                                               region, clipped, area / rectArea, tol));
                }

                // M0's table, kept live: which of the admitted cells the OLD convexity predicate would
                // have turned away. Computed only when the instrument is attached.
                if (diagnostics is not null && !PlanarCellRegion.IsConvex(clipped, 1e-9 * rectArea))
                    diagnostics.AdmittedNonConvex.Add(Classify(li, ix, iy, "non-convex, admitted",
                                                               region, clipped, area / rectArea, tol));

                kind[q]    = Cut;
                regions[q] = region;
            }
        }

        // ── Pass 2: R-cut-3 — absorb slivers ──────────────────────────────────────────────────
        //
        // A sliver merges only into a NON-sliver neighbour, which is what keeps a chain of slivers
        // from collapsing into one enormous cell: at most one absorption per surviving cell per side,
        // and a sliver with no ordinary neighbour is left alone and REPORTED rather than dropped.
        var absorbedInto = new int[nx * ny];
        Array.Fill(absorbedInto, -1);

        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                int q = iy * nx + ix;
                if (kind[q] != Cut) continue;
                double rectArea = (gx[ix + 1] - gx[ix]) * (gy[iy + 1] - gy[iy]);
                if (regions[q]!.Area >= sliverFraction * rectArea) continue;

                double tol = 1e-9 * Math.Min(gx[ix + 1] - gx[ix], gy[iy + 1] - gy[iy]);
                int best = -1; double bestFace = 0;
                Consider(ix - 1, iy, true,  gx[ix]);
                Consider(ix + 1, iy, true,  gx[ix + 1]);
                Consider(ix, iy - 1, false, gy[iy]);
                Consider(ix, iy + 1, false, gy[iy + 1]);

                if (best < 0) { counts.UnmergedSlivers++; continue; }
                absorbedInto[q] = best;
                counts.Merged++;

                void Consider(int jx, int jy, bool vertical, double line)
                {
                    if (jx < 0 || jy < 0 || jx >= nx || jy >= ny) return;
                    int t = jy * nx + jx;
                    if (kind[t] == Absent) return;
                    // Only into an ORDINARY cell: a sliver absorbing a sliver is a chain.
                    if (kind[t] == Cut)
                    {
                        double ta = (gx[jx + 1] - gx[jx]) * (gy[jy + 1] - gy[jy]);
                        if (regions[t]!.Area < sliverFraction * ta) return;
                    }
                    double face = FaceLength(regions[q]!, vertical, line, tol);
                    if (face > bestFace) { bestFace = face; best = t; }
                }
            }

        // ── Pass 3: emit, in (IY, IX) order ───────────────────────────────────────────────────
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                int q = iy * nx + ix;
                if (kind[q] == Absent || absorbedInto[q] >= 0) continue;

                double x0 = gx[ix], y0 = gy[iy], x1 = gx[ix + 1], y1 = gy[iy + 1];
                var region = kind[q] == Cut ? regions[q] : null;

                // The four neighbours are the only positions that could have chosen this cell.
                for (int side = 0; side < 4; side++)
                {
                    int tx = ix + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    int ty = iy + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (tx < 0 || ty < 0 || tx >= nx || ty >= ny) continue;
                    int t = ty * nx + tx;
                    if (absorbedInto[t] != q) continue;

                    region = (region ?? PlanarCellRegion.WholeRectangle(x0, y0, x1, y1))
                             .Absorb(regions[t], gx[tx], gy[ty], gx[tx + 1], gy[ty + 1]);
                    x0 = Math.Min(x0, gx[tx]); y0 = Math.Min(y0, gy[ty]);
                    x1 = Math.Max(x1, gx[tx + 1]); y1 = Math.Max(y1, gy[ty + 1]);
                    cellAt[t] = cells.Count;
                }

                if (region is not null) counts.Cut++;

                cellAt[q] = cells.Count;
                cells.Add(new PlanarCell(li, ix, iy, x0, y0, x1, y1, region));
            }
    }

    /// <summary>
    /// <b>R-cut-4, decided in the MESHER because that is where the basis set is built.</b>
    ///
    /// <para>Three ways a grid adjacency fails to be a rooftop once cells follow the metal, and all
    /// three are silent if they are not asked here: the shared edge can be entirely outside the metal
    /// (adjacent on the grid, not connected on the conductor); it can be a sliver, which is R-cut-3's
    /// problem in the other axis; and a half can fail to be SWEPT by the face, in which case the unit
    /// current leaks out through the rim instead of crossing the edge — see
    /// <see cref="RooftopSupport"/>'s header. A whole-rectangle pair passes all three by construction,
    /// which is why the staircase path does not pay for this at all.</para>
    /// </summary>
    private static bool FaceCarriesABasis(PlanarCell a, PlanarCell b, PlanarBasisDirection dir,
                                          double sharedCoord, ConformalCounts counts)
    {
        if (a.Region is null && b.Region is null) return true;

        var sa = RooftopSupport.Build(a, dir, sharedIsHigh: true,  sharedCoord);
        var sb = RooftopSupport.Build(b, dir, sharedIsHigh: false, sharedCoord);

        double face = Math.Min(sa.SharedFaceLength, sb.SharedFaceLength);
        double refLen = dir == PlanarBasisDirection.X
            ? Math.Min(a.Height, b.Height) : Math.Min(a.Width, b.Width);

        // M1's R-cvx-2 — the FOURTH way, and the one the predicate swap introduced. A cell may be cut
        // and still be describable by strips in one direction only; the basis across the other one
        // would integrate source over a gap in the metal. Counted apart from the other three,
        // because "this cell was staircased" and "this cell kept one of its two bases" are different
        // events and a user reading the notes has to be able to tell them apart.
        bool simple = sa.FlowSimple && sb.FlowSimple;

        bool sound = simple
                  && sa.Anchored && sb.Anchored
                  && face > 1e-9 * refLen
                  && Math.Abs(sa.Area - a.Area) <= 1e-9 * a.Area
                  && Math.Abs(sb.Area - b.Area) <= 1e-9 * b.Area;

        if (!sound)
        {
            if (simple) counts.RefusedFaces++;
            else        counts.RefusedFacesNotFlowSimple++;
        }
        return sound;
    }

    /// <summary>M0's classification of one refused cell — see <see cref="ConformalFallbackCell"/>.</summary>
    private static ConformalFallbackCell Classify(int li, int ix, int iy, string reason,
                                                  PlanarCellRegion region,
                                                  IReadOnlyList<EmPoint> ring,
                                                  double areaFraction, double tol)
    {
        int reflex = 0;
        for (int i = 0, n = ring.Count; i < n; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % n]; var c = ring[(i + 2) % n];
            // ClipToRect returns counter-clockwise, so a NEGATIVE turn is the reflex one.
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < 0) reflex++;
        }
        return new ConformalFallbackCell(li, ix, iy, reason, areaFraction, reflex,
                                         RooftopSupport.IsFlowSimple(region, alongX: true,  tol),
                                         RooftopSupport.IsFlowSimple(region, alongX: false, tol));
    }

    private static bool InMetal(IReadOnlyList<PlanarPolygon> polys, double x, double y)
    {
        foreach (var p in polys) if (p.Contains(x, y)) return true;
        return false;
    }

    /// <summary>The length of the region's boundary lying on one grid line — R-cut-3's "the face it
    /// shares". Because the cells tile, this same segment IS the neighbour's face, so only one of the
    /// two has to be measured.</summary>
    private static double FaceLength(PlanarCellRegion region, bool vertical, double line, double tol)
    {
        double total = 0;
        foreach (var piece in region.Pieces)
            for (int i = 0, n = piece.Count, j = n - 1; i < n; j = i++)
            {
                var a = piece[j];
                var b = piece[i];
                double ca = vertical ? a.X : a.Y, cb = vertical ? b.X : b.Y;
                if (Math.Abs(ca - line) > tol || Math.Abs(cb - line) > tol) continue;
                total += vertical ? Math.Abs(b.Y - a.Y) : Math.Abs(b.X - a.X);
            }
        return total;
    }

    /// <summary>
    /// Whether any part of the ring lies in or on the rectangle: a vertex inside, or an edge crossing
    /// it. <b>Both halves are needed</b> — an edge can cross a cell with neither endpoint in it, and a
    /// whole hole ring can sit inside a cell with no edge crossing its sides.
    /// </summary>
    private static bool RingTouchesRect(IReadOnlyList<EmPoint> ring,
                                        double x0, double y0, double x1, double y1)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var p = ring[i];
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) return true;
        }
        for (int i = 0, j = n - 1; i < n; j = i++)
            if (SegmentMeetsRect(ring[j].X, ring[j].Y, ring[i].X, ring[i].Y, x0, y0, x1, y1))
                return true;
        return false;
    }

    /// <summary>Liang–Barsky: does the segment intersect the closed rectangle?</summary>
    private static bool SegmentMeetsRect(double ax, double ay, double bx, double by,
                                         double x0, double y0, double x1, double y1)
    {
        double dx = bx - ax, dy = by - ay;
        double t0 = 0, t1 = 1;

        return Clip(-dx, ax - x0) && Clip(dx, x1 - ax) && Clip(-dy, ay - y0) && Clip(dy, y1 - ay);

        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else       { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
    }

    // ── R-msh-5: the reference length ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The 1/√d edge singularity is the same physics kernel A meshes for, and R-mom-8's cell-size
    /// FIELD formulation is reused verbatim (<see cref="BoundaryMesher.PartitionFractions"/> and the
    /// linear h(x) = min over attractors of [c₀ + (r−1)|x − a|]) — it was written against segment
    /// geometry rather than "the microstrip case", and that claim holds.</b>
    ///
    /// <para><b>But R-mom-8's headline FINDING does not carry over, and must not be assumed to.</b>
    /// Kernel A's edge cell is a fraction of the conductor's smallest bounding-box dimension — the
    /// metal THICKNESS — because in a cross-section the charge singularity lives at the 90° corner,
    /// whose scale is the thickness. <b>A planar surface mesh sits on a sheet with no thickness in the
    /// model at all</b> (D2 puts the metal on the slab's top surface as a zero-thickness sheet), so
    /// there is no thickness to be a fraction of, and the analogous scale is an in-plane one. §10.5's
    /// original "a small fraction of the width (~2–5%)" is therefore the RIGHT rule here — kernel A's
    /// deviation from it existed only because a cross-section has a second, much smaller dimension,
    /// and this geometry does not.</para>
    ///
    /// <para><b>Measured</b> — see <c>SurfaceMesherTests</c>'s Tier 2 reference comparison, which
    /// reports N under both candidates for both starter technologies. The conductor-width reference
    /// is the default because it is the one that tracks the physics (the singularity's scale is the
    /// conductor's own transverse size, not the frequency); the cell-size reference is retained as a
    /// seam because it is the one that keeps the edge-cell count constant as the frequency changes,
    /// and that is the property a future adaptive-frequency path would want. <b>What is NOT available
    /// at L8b is the convergence half of R-mom-8's measurement</b> — kernel A could compare ε_eff
    /// against its own converged limit because it had a solver; this slice has none, by design. That
    /// comparison belongs to L8c and is named here rather than faked.</para>
    /// </summary>
    public static double EdgeReferenceLength(PlanarEdgeReference kind, double narrowestWidthM, double cellSizeM)
        => Math.Max(kind == PlanarEdgeReference.ConductorWidth ? narrowestWidthM : cellSizeM, 1e-15);

    /// <summary>
    /// The edge ATTRACTOR coordinates this artwork contributes, per axis — the quantity D9 is
    /// actually about.
    ///
    /// <para>Public because D9's guarantee ("a 96-point smooth outline cannot inflate the grid")
    /// has to be asserted on the COUNT and not on N: N is a consequence of the marcher, the growth
    /// ratio and the λ_g cap all at once, so a test written on it would pass or fail for reasons
    /// that have nothing to do with how many fans the geometry demanded.</para>
    /// </summary>
    public static (IReadOnlyList<double> X, IReadOnlyList<double> Y) EdgeAttractors(
        PlanarProblem problem, PlanarRimGrading rimGrading = PlanarRimGrading.None)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var (x0, y0, x1, y1) = problem.Bounds();
        var (_, _, attX, attY, _) = CollectBoundaryLines(problem, x0, y0, x1, y1, rimGrading);
        return (attX, attY);
    }

    private static string DescribeReference(PlanarEdgeReference kind) => kind switch
    {
        PlanarEdgeReference.ConductorWidth => "the narrowest conductor",
        _                                  => "the wavelength cell size",
    };

    // ── Narrowness ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The narrowest conductor dimension along each axis, measured from the geometry <b>before any
    /// grid exists</b>. Scan lines are cast through every polygon and the run lengths collected; the
    /// answer is the 5th percentile rather than the outright minimum, because the outright minimum on
    /// a staircased or mitred outline is a corner sliver a few nanometres long and would drive the
    /// mesh to absurdity for a feature that carries no current. A genuinely narrow feature — a 100 µm
    /// stub 2 mm long — contributes hundreds of runs at its own width and is picked up exactly.
    /// </summary>
    public static (double NarrowX, double NarrowY) MeasureNarrowness(PlanarProblem problem, int samples = 128)
    {
        double nx = double.PositiveInfinity, ny = double.PositiveInfinity;

        foreach (var layer in problem.Layers)
            foreach (var poly in layer.Polygons)
            {
                var (px0, py0, px1, py1) = poly.Bounds();
                if (!(px1 > px0) || !(py1 > py0)) continue;

                // Runs measured ALONG x (a horizontal scan) tell how narrow the metal is in x.
                var runsX = new List<double>();
                for (int i = 0; i < samples; i++)
                {
                    double y = py0 + (py1 - py0) * (i + 0.5) / samples;
                    foreach (var span in Spans(poly, y, horizontal: true)) runsX.Add(span.Hi - span.Lo);
                }
                var runsY = new List<double>();
                for (int i = 0; i < samples; i++)
                {
                    double x = px0 + (px1 - px0) * (i + 0.5) / samples;
                    foreach (var span in Spans(poly, x, horizontal: false)) runsY.Add(span.Hi - span.Lo);
                }

                nx = Math.Min(nx, Percentile(runsX, 0.05, px1 - px0));
                ny = Math.Min(ny, Percentile(runsY, 0.05, py1 - py0));
            }

        var (bx0, by0, bx1, by1) = problem.Bounds();
        if (double.IsInfinity(nx) || !(nx > 0)) nx = Math.Max(bx1 - bx0, 1e-12);
        if (double.IsInfinity(ny) || !(ny > 0)) ny = Math.Max(by1 - by0, 1e-12);
        return (nx, ny);
    }

    private static double Percentile(List<double> values, double p, double fallback)
    {
        if (values.Count == 0) return fallback;
        values.Sort();
        int i = (int)Math.Floor(p * (values.Count - 1));
        double v = values[Math.Clamp(i, 0, values.Count - 1)];
        return v > 0 ? v : fallback;
    }

    // ── Scan lines ────────────────────────────────────────────────────────────────────────────

    internal readonly record struct Span(double Lo, double Hi);

    /// <summary>
    /// Even–odd crossings of one scan line with a polygon (outer + every hole), paired into spans.
    /// Holes fall out of the even–odd rule with no special case, which is exactly why the rule is
    /// used rather than a winding one.
    /// </summary>
    private static List<Span> Spans(PlanarPolygon poly, double at, bool horizontal)
    {
        var xs = new List<double>();
        Cross(poly.Outer, at, horizontal, xs);
        foreach (var h in poly.HoleRings) Cross(h, at, horizontal, xs);
        xs.Sort();

        var spans = new List<Span>(xs.Count / 2);
        for (int i = 0; i + 1 < xs.Count; i += 2)
            if (xs[i + 1] > xs[i]) spans.Add(new Span(xs[i], xs[i + 1]));
        return spans;
    }

    private static void Cross(IReadOnlyList<EmPoint> ring, double at, bool horizontal, List<double> outCrossings)
    {
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double a = horizontal ? ring[j].Y : ring[j].X;
            double b = horizontal ? ring[i].Y : ring[i].X;
            if (a > at == b > at) continue;               // half-open: a vertex on the line counts once
            double t  = (at - a) / (b - a);
            double ca = horizontal ? ring[j].X : ring[j].Y;
            double cb = horizontal ? ring[i].X : ring[i].Y;
            outCrossings.Add(ca + t * (cb - ca));
        }
    }

    /// <summary>The merged x-spans of a whole LAYER at height <paramref name="y"/> — the union, so
    /// two overlapping shapes on one layer produce one covered run and therefore one set of cells
    /// (R-msh-1's "no overlaps" holds by construction rather than by a check).</summary>
    private static List<Span> RowSpans(PlanarConductorLayer layer, double y)
    {
        var all = new List<Span>();
        foreach (var poly in layer.Polygons)
        {
            var (_, py0, _, py1) = poly.Bounds();
            if (y < py0 || y > py1) continue;
            all.AddRange(Spans(poly, y, horizontal: true));
        }
        if (all.Count <= 1) return all;

        all.Sort(static (p, q) => p.Lo.CompareTo(q.Lo));
        var merged = new List<Span> { all[0] };
        for (int i = 1; i < all.Count; i++)
        {
            var last = merged[^1];
            if (all[i].Lo <= last.Hi) merged[^1] = new Span(last.Lo, Math.Max(last.Hi, all[i].Hi));
            else merged.Add(all[i]);
        }
        return merged;
    }

    // ── Gridlines ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The boundary coordinates that become HARD gridlines, plus the subset that also becomes an EDGE
    /// ATTRACTOR, plus a count of polygons that are not axis-aligned.
    ///
    /// <para><b>Hard lines make the tiling exact</b> (R-msh-1): every axis-parallel boundary edge's
    /// own coordinate is a gridline, so a Manhattan polygon is covered by whole cells with no gap and
    /// no overhang. <b>Attractors drive the edge grading</b> (R-msh-5): only edges long enough to
    /// carry the 1/√d crowding qualify, so a staircase's individual treads do not each demand their
    /// own graded fan. A non-axis-parallel edge contributes NEITHER — which is D9's guarantee that a
    /// 96-point smooth outline cannot inflate the grid.</para>
    /// </summary>
    private static (List<double> HardX, List<double> HardY, List<double> AttractX, List<double> AttractY, int Staircased)
        CollectBoundaryLines(PlanarProblem problem, double x0, double y0, double x1, double y1,
                             PlanarRimGrading rimGrading = PlanarRimGrading.None)
    {
        var hardX = new List<double> { x0, x1 };
        var hardY = new List<double> { y0, y1 };
        var attX  = new List<double>();
        var attY  = new List<double>();
        int staircased = 0;

        double spanX = x1 - x0, spanY = y1 - y0;
        double tolX = spanX * 1e-12, tolY = spanY * 1e-12;

        // L9c — A VIA'S FOOTPRINT IS ARTWORK AND CONTRIBUTES GRIDLINES EXACTLY AS A CONDUCTOR EDGE
        // DOES, and this is the one thing that makes the shared-grid via basis actually work.
        //
        // R-msh-1's rule is "a gridline comes from an AXIS-PARALLEL boundary edge", and a via
        // footprint is nothing but axis-parallel boundary edges. Leaving it out is not a small loss
        // of resolution — it is a via that vanishes: measured on the two-level MMIC fixture, a 40 µm
        // footprint sat between bulk cell centres at 169.6 µm and 269.3 µm and produced ZERO vertical
        // unknowns, silently. With the footprint in the hard set the grid tiles it exactly and the
        // basis count is a property of the geometry rather than of where the via happened to fall.
        // …but it does NOT get the EDGE GRADING a conductor rim gets, and that distinction is
        // measured rather than stylistic. The graded cells exist to resolve the 1/√d edge current at
        // a metal RIM; a via footprint is an interior feature of continuous metal and has no such
        // singularity. Grading it anyway measured 2,448 unknowns against 424 for the same fixture —
        // a 5.8× cost for one 40 µm via, from four graded edges at 3% of the reference length. Hard
        // gridlines only: the footprint is tiled exactly and nothing else changes.
        var artwork = new List<(IReadOnlyList<PlanarPolygon> Polys, bool Grade)>();
        foreach (var layer in problem.Layers) artwork.Add((layer.Polygons, true));
        foreach (var via in problem.ViaList) artwork.Add((via.Polygons, false));

        foreach (var (group, grade) in artwork)
            foreach (var poly in group)
            {
                var (pbx0, pby0, pbx1, pby1) = poly.Bounds();
                double extX = Math.Max(pbx1 - pbx0, 1e-30);
                double extY = Math.Max(pby1 - pby0, 1e-30);
                bool anyOblique = false;

                foreach (var ring in Rings(poly))
                {
                    int n = ring.Count;
                    var oblique = new bool[n];
                    var convex  = ConvexCorners(ring);
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        double ax = ring[j].X, ay = ring[j].Y, bx = ring[i].X, by = ring[i].Y;
                        bool vertical   = Math.Abs(bx - ax) <= tolX;
                        bool horizontal = Math.Abs(by - ay) <= tolY;
                        bool cap        = convex[j] && convex[i];

                        if (vertical && !horizontal)
                        {
                            hardX.Add(0.5 * (ax + bx));
                            if (grade && Crowds(Math.Abs(by - ay), extY, cap))
                                attX.Add(0.5 * (ax + bx));
                        }
                        else if (horizontal && !vertical)
                        {
                            hardY.Add(0.5 * (ay + by));
                            if (grade && Crowds(Math.Abs(bx - ax), extX, cap))
                                attY.Add(0.5 * (ay + by));
                        }
                        else if (!horizontal && !vertical)
                        {
                            anyOblique  = true;
                            oblique[i]  = true;
                        }
                    }

                    // Asked of THIS ring's own oblique flags, not of the polygon's `anyOblique` —
                    // a hole ring must not inherit the outer ring's classification.
                    if (grade && rimGrading != PlanarRimGrading.None)
                        AddRunAttractors(ring, oblique, extX, extY, rimGrading, attX, attY);
                }

                if (anyOblique && grade) staircased++;
            }

        return (hardX, hardY, attX, attY, staircased);
    }

    /// <summary>
    /// <b>"LONG ENOUGH TO CROWD" — and it is asked TWO ways, because one of them was measured wrong
    /// on a part whose two ends differ in scale.</b>
    ///
    /// <para>The original test is the first clause: an axis-parallel edge earns a graded fan when it
    /// is at least a fifth of the POLYGON's own extent across it. Its purpose is to stop a drawn
    /// staircase from demanding a fan per tread, and for that it works. <b>But the polygon's extent is
    /// the wrong yardstick for a part whose features differ in size, and a shipping PCell shows it:</b>
    /// on the reported MKlopf (Z1 = 50 → Z2 = 12, offset), the 12 Ω end cap is 20.292 mm and the 50 Ω
    /// end cap is 2.998 mm against a threshold of 0.2 × 21.989 = 4.398 mm — so the wide end got a
    /// graded fan (357 → 237 → 142 → 94 µm) and the narrow end, where the crowding is STRONGEST and
    /// where the user's port sits, got none. Same taper, same physics, opposite treatment, decided by
    /// the bounding box of the other end.</para>
    ///
    /// <para><b>The second clause is the geometric statement the first one was reaching for: an edge
    /// that terminates the conductor has BOTH its corners convex, and an edge that is part of a longer
    /// boundary chain does not.</b> A staircase alternates convex and reflex, so every tread and every
    /// riser has one of each and is still excluded — which is the property the first clause exists to
    /// protect, now held for a reason rather than by a proxy. It is O(1) per edge and purely local.</para>
    ///
    /// <para><b>The floor on the second clause is DERIVED rather than picked.</b> R17 caps this kernel
    /// at ~5,000 unknowns, i.e. ~2,500 cells, i.e. roughly a 50 × 50 grid — so one cell is about 2% of
    /// the extent per axis at the finest mesh this kernel can afford, and an edge shorter than that is
    /// sub-cell however the mesh is refined. Grading it would spend gridlines across the WHOLE tensor
    /// grid on a feature the mesh cannot represent. It also bounds the one pathological case: artwork
    /// with hundreds of small Manhattan features (a meshed pour, a via farm drawn as conductor) would
    /// otherwise ask for four fans each.</para>
    ///
    /// <para><b>This can only ADD attractors, never remove one</b> — it is an OR — so no mesh loses
    /// grading it had, and §10.7's hero is untouched because all four of its edges already pass the
    /// first clause (2.9 mm ≥ 0.58 mm and 20 mm ≥ 4 mm). N = 552 is asserted, not assumed.</para>
    /// </summary>
    private static bool Crowds(double edgeLength, double extentAcross, bool cap)
        => edgeLength >= 0.2 * extentAcross
        || (cap && edgeLength >= CapMinFractionOfExtent * extentAcross);

    /// <summary>See <see cref="Crowds"/> — one cell at R17's own ceiling, as a fraction of the part.</summary>
    private const double CapMinFractionOfExtent = 0.02;

    /// <summary>
    /// Which of the ring's corners turn the same way the ring itself winds. <b>Collinear counts as NOT
    /// a corner</b>, deliberately: a vertex sitting mid-edge means the edge either side of it is a
    /// sub-segment of a longer straight boundary, which is exactly not a cap.
    /// </summary>
    private static bool[] ConvexCorners(IReadOnlyList<EmPoint> ring)
    {
        int n = ring.Count;
        var convex = new bool[n];
        if (n < 3) return convex;

        double signed = 0, scale = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            signed += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
            scale   = Math.Max(scale, Math.Abs(ring[i].X) + Math.Abs(ring[i].Y));
        }
        double sign = Math.Sign(signed);
        double tol  = 1e-12 * scale * scale;

        for (int i = 0; i < n; i++)
        {
            var p0 = ring[(i + n - 1) % n];
            var p1 = ring[i];
            var p2 = ring[(i + 1) % n];
            double cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            convex[i] = cross * sign > tol;
        }
        return convex;
    }

    private static IEnumerable<IReadOnlyList<EmPoint>> Rings(PlanarPolygon poly)
    {
        yield return poly.Outer;
        foreach (var h in poly.HoleRings) yield return h;
    }

    // ── Rim grading: attractors decimated by RUN rather than by VERTEX ────────────────────────

    /// <summary>
    /// Emits edge attractors for the ring's oblique RUNS — a run being a maximal chain of
    /// consecutive oblique edges, so a 96-point disc is ONE run and a taper is one per flank.
    ///
    /// <para><b>This is what makes D9 hold numerically rather than by exclusion.</b> The rule it
    /// replaces threw the physics out with the cost: a curved metal rim has the same 1/√d current
    /// crowding a straight one has (R-msh-5), and an attractor per VERTEX would put 96 graded fans on
    /// one part. An attractor per RUN is O(1) however finely the artwork was tessellated.</para>
    ///
    /// <para>The "long enough to crowd" filter is the SAME test the axis-parallel path applies, asked
    /// of a run: an x-attractor is qualified by the run's own y-extent (that being the length of the
    /// rim the fan has to serve) and a y-attractor by its x-extent, each against a fifth of the
    /// polygon's own extent. A staircase tread cannot reach that, exactly as before.</para>
    /// </summary>
    private static void AddRunAttractors(
        IReadOnlyList<EmPoint> ring, bool[] oblique, double extX, double extY,
        PlanarRimGrading mode, List<double> attX, List<double> attY)
    {
        int n = ring.Count;
        int count = 0;
        for (int i = 0; i < n; i++) if (oblique[i]) count++;
        if (count == 0) return;

        // Edge i runs from ring[i-1] to ring[i]. A run is a maximal cyclic chain of oblique ones;
        // when EVERY edge is oblique the ring is a single closed run (the 96-point disc's case).
        if (count == n)
        {
            EmitRun(BuildRun(ring, 0, n), extX, extY, mode, attX, attY);
            return;
        }

        for (int i = 0; i < n; i++)
        {
            if (!oblique[i] || oblique[(i + n - 1) % n]) continue;   // not the START of a run
            int len = 0;
            while (len < n && oblique[(i + len) % n]) len++;
            EmitRun(BuildRun(ring, i, len), extX, extY, mode, attX, attY);
        }
    }

    /// <summary>The run's polyline: the first edge's start vertex, then every edge's end vertex.</summary>
    private static List<EmPoint> BuildRun(IReadOnlyList<EmPoint> ring, int firstEdge, int edgeCount)
    {
        int n = ring.Count;
        var pts = new List<EmPoint>(edgeCount + 1) { ring[(firstEdge + n - 1) % n] };
        for (int k = 0; k < edgeCount; k++) pts.Add(ring[(firstEdge + k) % n]);
        return pts;
    }

    private static void EmitRun(List<EmPoint> run, double extX, double extY,
                                PlanarRimGrading mode, List<double> attX, List<double> attY)
    {
        double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        foreach (var p in run)
        {
            xMin = Math.Min(xMin, p.X); xMax = Math.Max(xMax, p.X);
            yMin = Math.Min(yMin, p.Y); yMax = Math.Max(yMax, p.Y);
        }

        bool gradeX = (yMax - yMin) >= 0.2 * extY;   // an x-attractor serves a rim of this y-extent
        bool gradeY = (xMax - xMin) >= 0.2 * extX;

        if (gradeX) { attX.Add(xMin); if (xMax > xMin) attX.Add(xMax); }
        if (gradeY) { attY.Add(yMin); if (yMax > yMin) attY.Add(yMax); }

        if (mode != PlanarRimGrading.PerRunSampled || (!gradeX && !gradeY)) return;

        // Three interior samples spread along the run's own ARC LENGTH, each contributing one
        // attractor in the axis TRANSVERSE to the local tangent — which is the direction the
        // crowding has to be resolved in. Spreading by COORDINATE instead is wrong for a closed
        // curve: the midpoint of a disc's y-range is the disc's centre, and not on the rim at all.
        double total = 0;
        for (int i = 1; i < run.Count; i++) total += Length(run[i - 1], run[i]);
        if (!(total > 0)) return;

        foreach (double f in new[] { 0.25, 0.5, 0.75 })
        {
            double want = f * total, walked = 0;
            for (int i = 1; i < run.Count; i++)
            {
                double seg = Length(run[i - 1], run[i]);
                if (walked + seg < want) { walked += seg; continue; }

                double t  = seg > 0 ? (want - walked) / seg : 0;
                double px = run[i - 1].X + t * (run[i].X - run[i - 1].X);
                double py = run[i - 1].Y + t * (run[i].Y - run[i - 1].Y);
                double tx = Math.Abs(run[i].X - run[i - 1].X), ty = Math.Abs(run[i].Y - run[i - 1].Y);

                if (tx >= ty) { if (gradeY) attY.Add(py); }
                else          { if (gradeX) attX.Add(px); }
                break;
            }
        }

        static double Length(EmPoint a, EmPoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
    }

    private static long EstimateLineCount(double lo, double hi, List<double> hard, double h)
    {
        double span = hi - lo;
        if (!(h > 0) || double.IsInfinity(h)) return Math.Max(2, hard.Count);
        return (long)Math.Min(MaxGridCells, Math.Ceiling(span / h) + hard.Count + 2);
    }

    /// <summary>
    /// One axis's gridlines: the hard lines, with each interval between them subdivided by the
    /// cell-size field. <b>Kernel A's own marcher is reused</b>
    /// (<see cref="BoundaryMesher.PartitionFractions"/>) rather than re-written — R-mom-8 says the
    /// field formulation was deliberately written against geometry rather than against the microstrip
    /// case so that B and C could reuse it, and this is B doing so.
    ///
    /// <para>A final enforcement pass splits anything still longer than <paramref name="hMax"/>, so
    /// R-msh-3's cap holds by construction and not by the marcher's rescale step happening to be
    /// well-behaved.</para>
    /// </summary>
    internal static double[] BuildGridLines(
        double lo, double hi,
        List<double> hard, List<double> attractors,
        double hMax, double c0, double growth)
    {
        double span = hi - lo;
        double tol  = Math.Max(span * 1e-12, 1e-18);

        hard.Sort();
        var anchors = new List<double>();
        foreach (double v in hard)
        {
            if (v < lo - tol || v > hi + tol) continue;
            double c = Math.Clamp(v, lo, hi);
            if (anchors.Count == 0 || c - anchors[^1] > tol) anchors.Add(c);
        }
        if (anchors.Count == 0 || anchors[0] > lo + tol) anchors.Insert(0, lo);
        if (anchors[^1] < hi - tol) anchors.Add(hi);

        bool graded = c0 > 0 && growth > 0 && attractors.Count > 0;
        var atts = graded ? attractors : [];

        var lines = new List<double> { anchors[0] };
        for (int k = 0; k + 1 < anchors.Count; k++)
        {
            double a = anchors[k], b = anchors[k + 1];
            double len = b - a;
            if (len <= tol) continue;

            int minCells = double.IsInfinity(hMax) || !(hMax > 0)
                ? 1
                : Math.Max(1, (int)Math.Ceiling(len / hMax - 1e-9));

            if (!graded)
            {
                for (int i = 1; i <= minCells; i++) lines.Add(a + len * i / minCells);
                continue;
            }

            var fr = BoundaryMesher.PartitionFractions(len, u => SizeAt(a + u, atts, c0, growth, hMax), minCells);
            foreach (double f in fr) lines.Add(a + len * f);
        }

        // Enforcement: nothing longer than hMax survives, whatever the marcher's rescale did.
        if (hMax > 0 && !double.IsInfinity(hMax))
        {
            var final = new List<double> { lines[0] };
            for (int i = 1; i < lines.Count; i++)
            {
                double a = final[^1], b = lines[i];
                double len = b - a;
                if (len <= tol) continue;
                int n = Math.Max(1, (int)Math.Ceiling(len / hMax - 1e-9));
                for (int k = 1; k <= n; k++) final.Add(a + len * k / n);
            }
            lines = final;
        }

        // Guarantee monotone, strictly increasing, and exactly on the ends.
        var outLines = new List<double> { lo };
        foreach (double v in lines)
            if (v - outLines[^1] > tol && v < hi - tol) outLines.Add(v);
        outLines.Add(hi);
        return [.. outLines];
    }

    /// <summary>
    /// R-mom-8's field, verbatim: h(x) = min over attractors of [c₀ + (r−1)|x − a|], clamped to
    /// h_max. That linear form <i>is</i> the geometric progression — cell k starts at
    /// d_k = c₀(rᵏ−1)/(r−1) and has size c₀rᵏ = c₀ + (r−1)d_k — and stating it as a field rather than
    /// as a per-end loop is what makes it compose over any number of attractors and both-ended
    /// intervals. <b>It is CONTINUOUS, and that matters</b>: see the growth-ratio derivation in
    /// <see cref="Mesh"/> for the knife edge a hard cutoff introduced instead.
    /// </summary>
    private static double SizeAt(double x, IReadOnlyList<double> attractors,
                                 double c0, double growth, double hMax)
    {
        double best = double.IsInfinity(hMax) ? double.MaxValue : hMax;
        for (int i = 0; i < attractors.Count; i++)
        {
            double h = c0 + growth * Math.Abs(x - attractors[i]);
            if (h < best) best = h;
        }
        return Math.Max(best, 1e-18);
    }

    /// <summary>
    /// The growth ratio r for which c₀·r^<paramref name="edgeCells"/> = <paramref name="hMax"/>, so
    /// the graded run reaches the bulk cell size in exactly the requested number of cells. Clamped to
    /// keep it near §10.5's own "~1.5–2": below 1.2 the grading barely grades, above 3 it jumps.
    /// Returns 0 when there is no grading to do, which is what switches the field off.
    /// </summary>
    /// <summary>Bounds on the derived grading ratio. Below the floor the ramp is so gentle it is not
    /// grading at all; above the ceiling it jumps from the edge cell to the bulk in one step, which is
    /// the discontinuity grading exists to remove.</summary>
    public const double MinGrowthRatio = 1.2;
    public const double MaxGrowthRatio = 3.0;

    private static double GrowthRatioFor(double c0, double hMax, int edgeCells)
    {
        if (!(c0 > 0) || edgeCells <= 0 || !(hMax > c0)) return 0.0;
        return Math.Clamp(Math.Pow(hMax / c0, 1.0 / edgeCells), MinGrowthRatio, MaxGrowthRatio);
    }

    /// <summary>
    /// How many cells the ramp from <paramref name="c0"/> actually takes to reach the bulk size at
    /// the REALISED <paramref name="ratio"/> — which is the requested count only while the clamp in
    /// <see cref="GrowthRatioFor"/> is not binding. Zero when nothing is being graded.
    /// </summary>
    public static int EffectiveEdgeCells(double c0, double hMax, double ratio)
    {
        if (!(c0 > 0) || !(hMax > c0) || !(ratio > 1.0)) return 0;
        return Math.Max(1, (int)Math.Round(Math.Log(hMax / c0) / Math.Log(ratio)));
    }

    // ── Metrics ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fewest cells in any contiguous run along a grid row or column — R-msh-4 measured on the
    /// mesh that was actually produced rather than on the geometry it came from.
    /// </summary>
    private static int MinCellsAcrossRun(PlanarMesh mesh, int nx, int ny, int layerCount)
    {
        if (mesh.Cells.Count == 0) return 0;

        int best = int.MaxValue;
        for (int li = 0; li < layerCount; li++)
        {
            var present = new bool[nx * ny];
            foreach (var c in mesh.Cells)
                if (c.LayerIndex == li) present[c.IY * nx + c.IX] = true;

            for (int iy = 0; iy < ny; iy++)
            {
                int run = 0;
                for (int ix = 0; ix <= nx; ix++)
                {
                    bool on = ix < nx && present[iy * nx + ix];
                    if (on) run++;
                    else { if (run > 0) best = Math.Min(best, run); run = 0; }
                }
            }
            for (int ix = 0; ix < nx; ix++)
            {
                int run = 0;
                for (int iy = 0; iy <= ny; iy++)
                {
                    bool on = iy < ny && present[iy * nx + ix];
                    if (on) run++;
                    else { if (run > 0) best = Math.Min(best, run); run = 0; }
                }
            }
        }
        return best == int.MaxValue ? 0 : best;
    }

    // ── Degenerate reports ────────────────────────────────────────────────────────────────────

    private static PlanarMeshReport Empty(
        List<string> layerNames, double meshFreqHz, double lambdaG, double hWave, List<string> notes)
        => new(new PlanarMesh([], [], layerNames, [], []),
               0, 0, new int[layerNames.Count], new int[layerNames.Count],
               0, 0, 0, 0, meshFreqHz, lambdaG, hWave, 0, 0, 0,
               PlanarBudgetVerdict.Ok, null, notes);

    private static PlanarMeshReport Refused(
        List<string> layerNames, double meshFreqHz, double lambdaG, double hWave,
        double narrowest, double edgeRef, int staircased, List<string> notes, string refusal)
        => new(new PlanarMesh([], [], layerNames, [], []),
               0, 0, new int[layerNames.Count], new int[layerNames.Count],
               0, 0, 0, narrowest, meshFreqHz, lambdaG, hWave, edgeRef, staircased, 0,
               PlanarBudgetVerdict.Refused, refusal, notes);

    // ── Formatting ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a DISTANCE for a message a user reads. <c>null</c> everywhere below means SI
    /// engineering notation (<see cref="Eng"/> plus a bare "m") — the shipped behaviour before this
    /// existed, and what a headless run (no <c>.clay</c>, no display unit) still gets. A UI caller
    /// with a layout open supplies one that reads in the layout's own display unit instead (owner
    /// request, 2026-08-15). <b>A plain delegate over a double, never a UI type</b> — <c>src/Ui</c>'s
    /// <c>LayoutUnits</c> cannot be referenced from here, so the conversion is built on the other
    /// side of the firewall and handed down as this.
    /// </summary>
    public delegate string PlanarLengthFormat(double metres);

    /// <summary>The default <see cref="PlanarLengthFormat"/> — every call site below falls back to
    /// this when handed <c>null</c>, so passing no formatter reproduces the exact pre-2026-08-15
    /// text.</summary>
    internal static string DefaultLengthFormat(double metres) => Eng(metres) + "m";

    /// <summary>Engineering notation for a note. The report carries raw doubles; this is only ever
    /// used inside a note string.</summary>
    internal static string Eng(double v)
    {
        if (double.IsInfinity(v) || double.IsNaN(v) || v == 0) return v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
        double a = Math.Abs(v);
        (double scale, string suffix) =
            a >= 1e9  ? (1e9,  "G") :
            a >= 1e6  ? (1e6,  "M") :
            a >= 1e3  ? (1e3,  "k") :
            a >= 1    ? (1.0,  "")  :
            a >= 1e-3 ? (1e-3, "m") :
            a >= 1e-6 ? (1e-6, "µ") :
                        (1e-9, "n");
        return (v / scale).ToString("G4", System.Globalization.CultureInfo.InvariantCulture) + " " + suffix;
    }
}
