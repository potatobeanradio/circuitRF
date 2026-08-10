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
    /// lightweight.
    /// </summary>
    public const int UnknownCeiling = 5000;

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
    public static PlanarMeshReport Mesh(
        PlanarProblem       problem,
        PlanarMeshSettings? settings      = null,
        PlanarEdgeReference edgeReference = PlanarEdgeReference.ConductorWidth,
        RunControl?         control       = null,
        PlanarRimGrading    rimGrading    = PlanarRimGrading.None)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var s = (settings ?? PlanarMeshSettings.Default).Resolved;

        var notes = new List<string>();
        double lambdaG = problem.GuidedWavelengthM;
        double hWave   = double.IsInfinity(lambdaG) ? double.PositiveInfinity
                                                    : lambdaG / s.CellsPerWavelength;

        var layerNames = new List<string>(problem.Layers.Count);
        foreach (var l in problem.Layers) layerNames.Add(l.Name);

        if (problem.PolygonCount == 0)
        {
            notes.Add("This EM setup's layout holds no conductor artwork, so there is nothing to mesh.");
            return Empty(layerNames, problem, lambdaG, hWave, notes);
        }

        var (x0, y0, x1, y1) = problem.Bounds();
        if (!(x1 > x0) || !(y1 > y0))
        {
            notes.Add("The conductor artwork has zero extent in x or y, so it encloses no area to mesh.");
            return Empty(layerNames, problem, lambdaG, hWave, notes);
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
            return Refused(layerNames, problem, lambdaG, hWave, narrowest, edgeRef, staircased, notes,
                $"This geometry needs on the order of {estX * estY:N0} mesh cells at " +
                $"{s.CellsPerWavelength} cells per wavelength, which is far past the {UnknownCeiling:N0}-unknown " +
                "ceiling this kernel is built for. Lower Cells per wavelength, turn the edge mesh off, " +
                "or analyse a smaller region.");
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
            perLayerCells[li] = cells.Count - firstCell;

            // R-msh-6: N is the number of SHARED INTERNAL EDGES — one rooftop per adjacent pair.
            int firstBasis = bases.Count;
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int a = cellAt[iy * nx + ix];
                    if (a < 0) continue;
                    if (ix + 1 < nx && cellAt[iy * nx + ix + 1] is var bx && bx >= 0)
                        bases.Add(new PlanarBasis(li, a, bx, PlanarBasisDirection.X));
                    if (iy + 1 < ny && cellAt[(iy + 1) * nx + ix] is var by && by >= 0)
                        bases.Add(new PlanarBasis(li, a, by, PlanarBasisDirection.Y));
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

        double minEdge = double.PositiveInfinity, maxEdge = 0;
        foreach (var c in cells)
        {
            minEdge = Math.Min(minEdge, Math.Min(c.Width, c.Height));
            maxEdge = Math.Max(maxEdge, c.LongestEdge);
        }
        if (cells.Count == 0) { minEdge = 0; maxEdge = 0; }

        int across = MinCellsAcrossRun(mesh, nx, ny, problem.Layers.Count);

        // ── The notes a user would otherwise have to guess at ─────────────────────────────────
        if (double.IsInfinity(lambdaG))
            notes.Add("No sweep frequency was given, so the mesh is driven purely by geometry — " +
                      "the λ_g/N cell-size cap did not apply.");
        else
            notes.Add($"Cell size capped at λ_g/{s.CellsPerWavelength} = {Eng(hWave)}m — λ_g = {Eng(lambdaG)}m " +
                      $"in εᵣ = {problem.Slab.Material.EpsR:G4} at {Eng(problem.MaxFrequencyHz)}Hz, the " +
                      "highest frequency of the sweep. Widening the sweep upward will change this, and " +
                      "with it the unknown count.");

        notes.Add($"Narrowest conductor dimension {Eng(narrowest)}m, meshed {across} cell(s) across " +
                  $"(target {PlanarMeshSettings.MinCellsAcrossConductor}).");

        if (s.EdgeMesh && s.EdgeCells > 0)
        {
            notes.Add($"Edge mesh on: {s.EdgeCells} graded cell(s) at every axis-parallel conductor edge, " +
                      $"outermost {Eng(c0)}m ({PlanarMeshSettings.EdgeFractionOfReference:P0} of {Eng(edgeRef)}m, " +
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
                          "graded fan — it is approximated by a staircase instead" +
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
                          $"the ramp from {Eng(c0)}m to the bulk cell reaches it in about {used} cell(s), " +
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

        if (staircased > 0)
            notes.Add($"{staircased} polygon(s) are not axis-aligned and are approximated by a " +
                      "STAIRCASE — this mesher builds rectangular cells only (D2). A mitred bend, a " +
                      "taper or a curve therefore carries a quantisation error that scales with the " +
                      "cell size; conformal cells and triangles are not built in this phase.");

        foreach (var alt in problem.Alternatives)
            notes.Add($"{alt.Subject} already has a validated analytic model ({alt.ModelName}), which " +
                      $"is effectively free. {alt.Reason} Full-wave is still a legitimate choice here — " +
                      "this is a note about cost, not a refusal.");

        int n = bases.Count;
        var verdict = n > UnknownCeiling ? PlanarBudgetVerdict.Refused
                    : n >= (int)(UnknownCeiling * WarnFraction) ? PlanarBudgetVerdict.Warn
                    : PlanarBudgetVerdict.Ok;

        string? refusal = null;
        if (verdict == PlanarBudgetVerdict.Refused)
            refusal = BuildRefusal(n, s);
        else if (verdict == PlanarBudgetVerdict.Warn)
            notes.Add($"{n:N0} unknowns is within {1 - WarnFraction:P0} of the {UnknownCeiling:N0} " +
                      $"ceiling ({MatrixMegabytes(n):N0} MB of dense complex matrix). It will solve, " +
                      "but a coarser mesh will solve much faster.");

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
            FrequencyHz:                   problem.MaxFrequencyHz,
            GuidedWavelengthM:             lambdaG,
            MaxCellSizeM:                  hWave,
            EdgeReferenceLengthM:          edgeRef,
            StaircasedPolygons:            staircased,
            ViaUnknownCount:               viaUnknowns,
            Verdict:                       verdict,
            Refusal:                       refusal,
            Notes:                         notes);
    }

    /// <summary>
    /// R-msh-7's wording, in one place: <b>the predicted N, the ceiling, and WHAT TO CHANGE</b>. A
    /// refusal that names only the first two leaves the user with nothing to do about it.
    /// </summary>
    private static string BuildRefusal(int n, PlanarMeshSettings s)
        => $"This geometry needs {n:N0} unknowns at {s.CellsPerWavelength} cells per wavelength" +
           (s.EdgeMesh ? $" with a {s.EdgeCells}-cell edge mesh" : " with the edge mesh off") +
           $", which is past the {UnknownCeiling:N0}-unknown ceiling this kernel is built for " +
           $"({MatrixMegabytes(n):N0} MB of dense complex matrix, against {MatrixMegabytes(UnknownCeiling):N0} MB " +
           "at the ceiling). Lower Cells per wavelength, turn the edge mesh off, or analyse a smaller " +
           "region — full-wave analysis of a structure this size needs matrix compression, which is " +
           "not built.";

    private static double MatrixMegabytes(int n) => (double)n * n * 16.0 / (1024.0 * 1024.0);

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
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        double ax = ring[j].X, ay = ring[j].Y, bx = ring[i].X, by = ring[i].Y;
                        bool vertical   = Math.Abs(bx - ax) <= tolX;
                        bool horizontal = Math.Abs(by - ay) <= tolY;

                        if (vertical && !horizontal)
                        {
                            hardX.Add(0.5 * (ax + bx));
                            // "Long enough to crowd": at least a fifth of the region's own extent
                            // across the edge. A staircase tread never reaches that; a real conductor
                            // edge always does.
                            if (grade && Math.Abs(by - ay) >= 0.2 * extY) attX.Add(0.5 * (ax + bx));
                        }
                        else if (horizontal && !vertical)
                        {
                            hardY.Add(0.5 * (ay + by));
                            if (grade && Math.Abs(bx - ax) >= 0.2 * extX) attY.Add(0.5 * (ay + by));
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
        List<string> layerNames, PlanarProblem p, double lambdaG, double hWave, List<string> notes)
        => new(new PlanarMesh([], [], layerNames, [], []),
               0, 0, new int[layerNames.Count], new int[layerNames.Count],
               0, 0, 0, 0, p.MaxFrequencyHz, lambdaG, hWave, 0, 0, 0,
               PlanarBudgetVerdict.Ok, null, notes);

    private static PlanarMeshReport Refused(
        List<string> layerNames, PlanarProblem p, double lambdaG, double hWave,
        double narrowest, double edgeRef, int staircased, List<string> notes, string refusal)
        => new(new PlanarMesh([], [], layerNames, [], []),
               0, 0, new int[layerNames.Count], new int[layerNames.Count],
               0, 0, 0, narrowest, p.MaxFrequencyHz, lambdaG, hWave, edgeRef, staircased, 0,
               PlanarBudgetVerdict.Refused, refusal, notes);

    // ── Formatting ────────────────────────────────────────────────────────────────────────────

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
