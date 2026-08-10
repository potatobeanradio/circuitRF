// brief-edge-mesh-on-curved-geometry.md — edge grading on a CURVED rim.
//
// L8b's rule is stated once, in SurfaceMesher's own header: "a gridline comes from an AXIS-PARALLEL
// boundary edge, never from a vertex", and an oblique edge contributes NEITHER a gridline nor an
// attractor. That is D9's guarantee that a 96-point smooth outline cannot inflate the grid, and it
// is real and must be preserved. What it also does — measured in the Ui-side PlanarMeshPCellTests,
// because the shipping PCells live in src/Ui — is throw the PHYSICS out with the cost: the graded
// fan exists to resolve the 1/√d current crowding at a metal rim (R-msh-5), and a curved rim has
// that crowding just as a straight one does.
//
// §2 IS THE QUESTION THAT DECIDES WHETHER THE WORK IS WORTH DOING, and it has to be answered with a
// CONVERGED PHYSICAL QUANTITY rather than with a cell count. The 1/√d crowding is at the TRUE rim;
// a staircased rim is in the wrong place by up to a cell, so refining toward a tread's own edge may
// resolve the quantisation artifact rather than the physics — and would then buy unknowns for
// nothing, or worse, converge confidently onto the staircase.
//
// The quantity is L8c's own Tier 5 harness: the static capacitance from PlanarFill's
// ScalarPotentialMatrix at ω → 0, at εᵣ = 1 so the kernel is closed form and only the mesh can be
// wrong. It is the same quantity R-fil-12 used to close R-msh-5's deferred half, for the same
// reason — it is cheap, it has a refinement limit, and it genuinely feels the edge current.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarRimGradingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double DiscRadiusM = 1.45e-3;
    private const int    DiscPoints  = 96;

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §0's own fixture: a disc tessellated at <paramref name="points"/> vertices. <b>It has no
    /// axis-parallel edge anywhere</b> — which is exactly why it is the fixture: every other shipping
    /// part has end caps, and a cap's attractor refines whole grid columns whose fans then reach the
    /// nearby rim, so a taper reads as "responded" while its mid-rim gets nothing.
    /// </summary>
    private static PlanarProblem Disc(int points = DiscPoints, double radiusM = DiscRadiusM,
                                      double fHz = 10e9)
    {
        var ring = new EmPoint[points];
        for (int i = 0; i < points; i++)
        {
            // Vertices ON the axes, deliberately. The obvious alternative — offsetting by half a step
            // so no vertex lands on an axis — makes the four edges that STRADDLE each axis exactly
            // axis-parallel to the mesher's own 1e-12 tolerance, which splits the ring into four runs
            // and hands the fixture the very gridlines it exists not to have. (Measured: 16
            // attractors instead of 4.) A vertex is geometry to be covered, never a gridline, so
            // putting them on the axes costs nothing.
            double a = 2.0 * Math.PI * i / points;
            ring[i] = new EmPoint(radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, fHz);
    }

    /// <summary>§10.7's own FR-4 hero — the Manhattan bit-identity fixture.</summary>
    private static PlanarProblem Hero() => new(
        [new PlanarConductorLayer("Metal",
            [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(20e-3, 0),
                                new EmPoint(20e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
            5.8e7, 35e-6)],
        GroundedSlab.Fr4Starter, 10e9);

    private static PlanarMeshSettings Settings(int cellsPerWavelength, int edgeCells, bool edgeMesh = true)
        => new(Auto: false, CellsPerWavelength: cellsPerWavelength, EdgeMesh: edgeMesh, EdgeCells: edgeCells);

    /// <summary>
    /// L8c's D8 static harness, reproduced here rather than reached for: φ_a = (1/ε₀)·Σ_b P[a,b]·Q_b,
    /// so holding every cell at 1 V and solving gives the charges and their sum is the capacitance.
    /// Built entirely from <see cref="PlanarFill.ScalarPotentialMatrix"/>, which IS a product surface.
    /// </summary>
    private static double Capacitance(PlanarMesh mesh, PlanarKernelTerms termsQ)
    {
        var st    = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);
        var p     = PlanarFill.ScalarPotentialMatrix(cores, termsQ.With(st.Order, cores.RhoFloorM));

        int m = mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = Complex.One;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }

        var q = a.Lu().Solve(rhs);
        Complex total = Complex.Zero;
        for (int i = 0; i < m; i++) total += q[i];
        return total.Real;
    }

    private static PlanarKernelTerms Eps1OverGround()
        => PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * GroundedSlab.Fr4Starter.HeightM);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §0's finding, as a regression, and D9 asserted on the COUNT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void E1_ADiscsMeshDoesNotRespondToEdgeCellsAtAll_AndTheRimSeamIsWhatChangesThat()
    {
        var disc = Disc();

        var shipped = new List<int>();
        var withRim = new List<int>();
        foreach (int ec in new[] { 0, 3, 10, 20 })
        {
            var a = SurfaceMesher.Mesh(disc, Settings(20, ec));
            var b = SurfaceMesher.Mesh(disc, Settings(20, ec), PlanarEdgeReference.ConductorWidth,
                                       null, PlanarRimGrading.PerRun);
            shipped.Add(a.UnknownCount);
            withRim.Add(b.UnknownCount);
            _out.WriteLine($"EdgeCells = {ec,2}: N = {a.UnknownCount,5} (shipped, no rim attractors), " +
                           $"{b.UnknownCount,5} (PerRun), min cell = {a.MinCellEdgeM * 1e6:F1} / " +
                           $"{b.MinCellEdgeM * 1e6:F1} µm");
        }

        // §0's measured table: N is IDENTICAL at every EdgeCells, and the min cell is flat. The mesh
        // is the plain λ_g-driven marcher and nothing else.
        Assert.Single(shipped.Distinct());
        Assert.True(withRim.Distinct().Count() > 1,
            "the rim seam did not make a 96-point disc respond to EdgeCells — it is not doing the one " +
            "thing it exists to do");
    }

    [Fact]
    public void E1b_TheAllCurvedPartSAYSTheEdgeMeshDidNothing_AndAManhattanOneDoesNot()
    {
        // §5, worth doing whatever §2 decides. "N graded cell(s) at every axis-parallel conductor
        // edge" is accurate and nobody reads the qualifier.
        var curved = SurfaceMesher.Mesh(Disc(), Settings(20, 3));
        var square = SurfaceMesher.Mesh(Hero(), Settings(20, 3));

        foreach (var n in curved.Notes) _out.WriteLine($"[disc]  {n}");
        foreach (var n in square.Notes) _out.WriteLine($"[hero]  {n}");

        Assert.Contains(curved.Notes, n => n.Contains("NO edge grading was actually applied", StringComparison.Ordinal));
        Assert.DoesNotContain(square.Notes, n => n.Contains("NO edge grading was actually applied", StringComparison.Ordinal));
    }

    [Fact]
    public void E1c_D9IsPreservedNUMERICALLY_AssertedOnTheATTRACTORCountRatherThanOnN()
    {
        // The rule the rim seam replaces preserved D9 BY EXCLUSION; this preserves it by decimating
        // to the RUN. A disc is one run however finely it was tessellated, so the attractor count is
        // O(1) — and must not move when the artwork's own tessellation is quadrupled.
        foreach (int points in new[] { 24, 96, 384 })
        {
            var d = Disc(points);
            var none    = SurfaceMesher.EdgeAttractors(d);
            var perRun  = SurfaceMesher.EdgeAttractors(d, PlanarRimGrading.PerRun);
            var sampled = SurfaceMesher.EdgeAttractors(d, PlanarRimGrading.PerRunSampled);

            _out.WriteLine($"{points,3}-point disc: attractors (x + y) = " +
                           $"{none.X.Count + none.Y.Count} shipped, " +
                           $"{perRun.X.Count + perRun.Y.Count} PerRun, " +
                           $"{sampled.X.Count + sampled.Y.Count} PerRunSampled");

            Assert.Empty(none.X);
            Assert.Empty(none.Y);
            Assert.Equal(4, perRun.X.Count + perRun.Y.Count);
            Assert.Equal(7, sampled.X.Count + sampled.Y.Count);
        }
    }

    [Fact]
    public void E2_AManhattanMeshIsBITIDENTICAL_WithTheRimSeamOnOrOff()
    {
        // R-msh-1's tiling guarantee, and every L8b/L8c/L8d/L9 number in the repository, rest on a
        // Manhattan mesh not moving. It cannot move — a Manhattan polygon has no oblique edge and
        // therefore no run — and that is asserted rather than argued.
        foreach (var mode in new[] { PlanarRimGrading.PerRun, PlanarRimGrading.PerRunSampled })
            foreach (int ec in new[] { 0, 3, 10 })
            {
                var a = SurfaceMesher.Mesh(Hero(), Settings(20, ec));
                var b = SurfaceMesher.Mesh(Hero(), Settings(20, ec), PlanarEdgeReference.ConductorWidth,
                                           null, mode);

                Assert.Equal(a.UnknownCount, b.UnknownCount);
                Assert.Equal(a.CellCount, b.CellCount);
                Assert.Equal(a.Mesh.GridX, b.Mesh.GridX);      // bit-identical gridlines, not a tolerance
                Assert.Equal(a.Mesh.GridY, b.Mesh.GridY);
                Assert.Equal(a.Mesh.Cells, b.Mesh.Cells);
                Assert.Equal(a.Mesh.Bases, b.Mesh.Bases);
            }

        // §10.7's own FR-4 hero, still exactly 552.
        Assert.Equal(552, SurfaceMesher.Mesh(Hero(), Settings(20, 3)).UnknownCount);
        Assert.Equal(552, SurfaceMesher.Mesh(Hero(), Settings(20, 3), PlanarEdgeReference.ConductorWidth,
                                             null, PlanarRimGrading.PerRunSampled).UnknownCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M1 — DOES A GRADED FAN ON A STAIRCASED RIM ACTUALLY HELP?
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void E3a_TheCONTROL_GradingATRUERimDoesHelp_OnTheSameQuantityAndTheSameHarness()
    {
        // Run FIRST, and read FIRST. Without it, "the rim ladders did not improve anything" is
        // indistinguishable from "this harness cannot see edge grading at all" — and this codebase
        // has been burned by concluding from a measurement whose oracle was the broken part, on
        // record, nine times in this area.
        //
        // A Manhattan square: every rim is an exact conductor edge, in exactly the right place, and
        // the graded fan lands ON it. R-fil-12 already measured that the shipped edge mesh reaches a
        // converged capacitance at fewer unknowns than the alternative reference; this asks the
        // cruder question the disc is about to be asked — graded against NOT graded.
        var square = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(2.9e-3, 0),
                                    new EmPoint(2.9e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
                5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);
        var terms = Eps1OverGround();

        var ladders = new Dictionary<string, List<(int N, double C)>>();
        foreach (var (label, edgeMesh) in new[] { ("uniform", false), ("edge-graded", true) })
        {
            var rows = new List<(int, double)>();
            foreach (int cpw in new[] { 20, 45, 95, 141, 200 })
            {
                var r = SurfaceMesher.Mesh(square, Settings(cpw, edgeMesh ? 3 : 0, edgeMesh));
                if (!r.CanSolve) continue;
                double c = Capacitance(r.Mesh, terms);
                rows.Add((r.UnknownCount, c));
                _out.WriteLine($"[control {label,-11}] cells/λ = {cpw,3}: N = {r.UnknownCount,5}, " +
                               $"cells = {r.Mesh.Cells.Count,5}, min cell = {r.MinCellEdgeM * 1e6,7:F2} µm, " +
                               $"C = {c * 1e15:F4} fF");
            }
            ladders[label] = rows;
        }

        double consensus = 0.5 * (Richardson(ladders["uniform"]) + Richardson(ladders["edge-graded"]));
        _out.WriteLine($"[control] the two ladders' extrapolated limits: " +
                       $"{Richardson(ladders["uniform"]) * 1e15:F4} / " +
                       $"{Richardson(ladders["edge-graded"]) * 1e15:F4} fF, consensus " +
                       $"{consensus * 1e15:F4} fF");

        foreach (var (label, rows) in ladders)
            foreach (var (n, c) in rows)
                _out.WriteLine($"[control {label,-11}] N = {n,5}: {Math.Abs(c - consensus) / consensus:P3} " +
                               "from the consensus limit");

        // At its COARSEST rung — the shipping mesh — the graded ladder must already be closer than
        // the uniform one. That is what "the fan buys something" means when the rim is in the right
        // place, and it is the statement the disc is about to fail to reproduce.
        double eUniform = Math.Abs(ladders["uniform"][0].C - consensus) / consensus;
        double eGraded  = Math.Abs(ladders["edge-graded"][0].C - consensus) / consensus;
        _out.WriteLine($"[control] at the shipping mesh: uniform {eUniform:P3} (N = {ladders["uniform"][0].N}), " +
                       $"graded {eGraded:P3} (N = {ladders["edge-graded"][0].N})");
        Assert.True(eGraded < eUniform,
            $"edge grading does not improve the answer even on a TRUE, axis-parallel rim " +
            $"({eGraded:P3} against {eUniform:P3}) — the harness, not the rim, is what this test would " +
            "then be measuring, and nothing below it can be believed");
    }

    /// <summary>Richardson on the last three rungs of a ladder, falling back to the finest value when
    /// the sequence is not converging monotonically.</summary>
    private static double Richardson(List<(int N, double C)> rows)
    {
        if (rows.Count < 3) return rows[^1].C;
        double d1 = rows[^2].C - rows[^3].C, d2 = rows[^1].C - rows[^2].C;
        if (!(d1 * d2 > 0) || !(Math.Abs(d2) < Math.Abs(d1))) return rows[^1].C;
        return rows[^1].C + d2 * d2 / (d1 - d2);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void E3_TheConvergenceComparison_ThreeLaddersOnAGenuinelyCurvedRim()
    {
        var disc  = Disc();
        var terms = Eps1OverGround();

        // Ladder 3 first: the reference limit. A UNIFORM mesh (edge mesh off) refined along its own
        // axis, extrapolated. It is the reference and NOT a competitor — R-fil-12 records why a
        // uniform mesh flatters nobody: it does not resolve the 1/√d edge current at all.
        // The ladder starts at 70 rather than 20 deliberately: below ~65 the disc's cell size is set
        // by R-msh-4's narrowness rule (945 µm across / 4) and not by λ_g at all, so cells/λ = 20 and
        // 45 mesh IDENTICALLY and would put two copies of one point in a Richardson extrapolation.
        var uniform = new List<(int N, int Cells, double C)>();
        foreach (int cpw in new[] { 70, 100, 141, 200, 250 })
        {
            var r = SurfaceMesher.Mesh(disc, Settings(cpw, 0, edgeMesh: false));
            if (!r.CanSolve) { _out.WriteLine($"[uniform ref] cells/λ = {cpw,3}: N = {r.UnknownCount} — refused by R17, skipped"); continue; }
            double c = Capacitance(r.Mesh, terms);
            uniform.Add((r.UnknownCount, r.Mesh.Cells.Count, c));
            _out.WriteLine($"[uniform ref] cells/λ = {cpw,3}: N = {r.UnknownCount,5}, " +
                           $"cells = {r.Mesh.Cells.Count,5}, cell = {r.MaxCellEdgeM * 1e6,7:F1} µm, " +
                           $"C = {c * 1e15:F4} fF");
        }

        // THE REFERENCE LADDER DOES NOT CONVERGE MONOTONICALLY, and that is the finding rather than a
        // fixture problem — so it is measured rather than smoothed by an extrapolation that assumes
        // what is not true. L8b already recorded the same shape for the mitre: "the error is not
        // monotone in cell size, and that is the staircase's own signature — the error depends on how
        // the grid happens to ALIGN with an oblique edge, not only on how fine it is."
        //
        // The limit is therefore the finest mesh's own value, carrying a BAND: the spread of the last
        // three rungs. Nothing below that band can be resolved by any ladder here.
        double limit = uniform[^1].C;
        double lo = uniform.TakeLast(3).Min(u => u.C), hi = uniform.TakeLast(3).Max(u => u.C);
        double band = (hi - lo) / limit;
        _out.WriteLine($"[uniform ref] limit taken as the finest rung: C = {limit * 1e15:F4} fF, " +
                       $"with a NON-MONOTONE band of {band:P3} across the last three rungs " +
                       $"({lo * 1e15:F4} … {hi * 1e15:F4} fF)");

        // Ladders 1 and 2: the SHIPPING mesh, and the shipping mesh plus rim attractors, each refined
        // along cells/λ at the shipping EdgeCells = 3. (Below ~65, cells/λ does not move this mesh at
        // all — R-msh-4's narrowness rule binds — so 30 and 45 are omitted as exact duplicates of 20.)
        var ladders = new Dictionary<PlanarRimGrading, List<(int N, double C, double Err)>>();
        foreach (var mode in new[] { PlanarRimGrading.None, PlanarRimGrading.PerRun,
                                     PlanarRimGrading.PerRunSampled })
        {
            var rows = new List<(int, double, double)>();
            foreach (int cpw in new[] { 20, 70, 100, 141 })
            {
                var r = SurfaceMesher.Mesh(disc, Settings(cpw, 3), PlanarEdgeReference.ConductorWidth,
                                           null, mode);
                if (!r.CanSolve) { _out.WriteLine($"[{mode,-14}] cells/λ = {cpw,3}: N = {r.UnknownCount} — refused by R17, skipped"); continue; }
                double c = Capacitance(r.Mesh, terms);
                double e = Math.Abs(c - limit) / limit;
                rows.Add((r.UnknownCount, c, e));

                double meshArea = r.Mesh.Cells.Sum(x => x.Area);
                double trueArea = Math.PI * DiscRadiusM * DiscRadiusM;
                _out.WriteLine($"[{mode,-14}] cells/λ = {cpw,3}: N = {r.UnknownCount,5}, " +
                               $"cells = {r.Mesh.Cells.Count,5}, min cell = {r.MinCellEdgeM * 1e6,7:F2} µm, " +
                               $"C = {c * 1e15:F4} fF, {e:P3} from the limit; " +
                               $"STAIRCASE area error = {Math.Abs(meshArea - trueArea) / trueArea:P3}");
            }
            ladders[mode] = rows;
        }

        _out.WriteLine("[VERDICT] error from the limit, ladder by ladder, at comparable N:");
        foreach (var (mode, rows) in ladders)
            _out.WriteLine($"[VERDICT]   {mode,-14}: " +
                           string.Join(", ", rows.Select(r => $"N={r.N} → {r.Err:P3}")));

        double bestShipped = ladders[PlanarRimGrading.None].Min(r => r.Err);
        foreach (var mode in new[] { PlanarRimGrading.PerRun, PlanarRimGrading.PerRunSampled })
            _out.WriteLine($"[VERDICT] {mode} best {ladders[mode].Min(r => r.Err):P3} against the shipped " +
                           $"mesh's own best {bestShipped:P3}, with the reference band at {band:P3}");

        // §2's own instruction: "If (2) reaches the limit at materially fewer unknowns than (1), build
        // it. If it does not, stop and report that." THIS IS THE NEGATIVE RESULT, ASSERTED — so that a
        // later change which made rim grading genuinely pay would turn this red and be noticed, rather
        // than quietly contradicting the note in CLAUDE.md.
        //
        // "Materially" is 2x here: the rim ladders would have to halve the shipped mesh's own error
        // to be worth the unknowns, and the reference itself is only good to `band`.
        foreach (var mode in new[] { PlanarRimGrading.PerRun, PlanarRimGrading.PerRunSampled })
            Assert.False(ladders[mode].Min(r => r.Err) < 0.5 * bestShipped,
                $"{mode} now reaches the limit materially better than the shipped mesh does — the " +
                "negative result recorded in src/Engine/Mom/CLAUDE.md no longer holds and PlanarRimGrading " +
                "should be reconsidered as a default rather than left as a measurement seam");

        // And the reason it cannot: the staircase's own quantisation error is the dominant term, and
        // it is what makes the reference band as wide as the differences being compared.
        Assert.True(band > 0.5 * bestShipped,
            $"the reference ladder's non-monotone band ({band:P3}) is no longer comparable to the " +
            $"shipped mesh's own error ({bestShipped:P3}) — the staircase is no longer what dominates, " +
            "so this comparison should be re-taken");
    }
}
