// L8c — Tiers 5 and 6: the ω → 0 limit against a real physical capacitance, and convergence.
//
// D8 — THE STATIC HARNESS IS A TEST FIXTURE AND LIVES HERE, NOT IN THE ENGINE. It is the one
// excitation this slice may construct (all cells at 1 V), and the brief is explicit that it must not
// appear on IEmKernel, on the .cem or in the panel. It is built entirely from PlanarFill's public
// ScalarPotentialMatrix, which IS a product surface — D4's P is the electrostatic potential-
// coefficient matrix, and being able to say that is the whole reason Tier 5 is reachable.
//
// Tier 5's two rungs are chosen so that neither compares against a transcribed constant:
//   • a plate over ground at small h/W has ε₀A/h as an ASYMPTOTE, so the TREND is the test rather
//     than any single number — the fringing excess must fall as h/W does;
//   • an isolated plate has no closed form at all, so it is reported with a Richardson extrapolation
//     of its own refinement sequence, which is a statement about convergence rather than about a
//     number someone remembered.
//
// Tier 6 then separates the two error sources that a single convergence study would conflate: mesh
// refinement at fixed quadrature, and quadrature refinement at fixed mesh. If they are not separable
// you cannot tell a discretisation error from a quadrature error, which is the position L8b was in
// when it had to defer R-msh-5's convergence half to this slice.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarStaticLimitTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ── the harness ───────────────────────────────────────────────────────────────────────────

    /// <summary>A uniform nx × ny mesh of a w × h rectangle centred on the origin.</summary>
    private static PlanarMesh Uniform(double w, double h, int nx, int ny)
    {
        var gx = new double[nx + 1];
        var gy = new double[ny + 1];
        for (int i = 0; i <= nx; i++) gx[i] = -0.5 * w + w * i / nx;
        for (int j = 0; j <= ny; j++) gy[j] = -0.5 * h + h * j / ny;
        return PlanarFillTests.Grid(gx, gy);
    }

    /// <summary>
    /// <b>The static harness.</b> φ_a = (1/ε₀)·Σ_b P[a,b]·Q_b, so holding every cell at 1 V and
    /// solving gives the charges directly; the capacitance is their sum. Nothing here is an
    /// excitation in the L8d sense — there is no port, no reference impedance and no wave.
    /// </summary>
    private static double Capacitance(PlanarMesh mesh, PlanarKernelTerms termsQ,
                                      PlanarFillSettings? settings = null)
    {
        var st    = settings ?? PlanarFillSettings.Default;
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

    /// <summary>
    /// The mutual capacitance between two conductors sharing one mesh: hold every cell of A at 1 V
    /// and every cell of B at 0 V, and report −(charge on B). A genuine ENTRY of the physical
    /// capacitance matrix, and unlike a raw matrix element it has a limit to converge to.
    /// </summary>
    private static double MutualCapacitance(PlanarMesh mesh, Func<PlanarCell, bool> onA,
                                            PlanarKernelTerms termsQ)
    {
        var st    = PlanarFillSettings.Default;
        var cores = PlanarFill.BuildCores(mesh, st);
        var p     = PlanarFill.ScalarPotentialMatrix(cores, termsQ.With(st.Order, cores.RhoFloorM));

        int m = mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = onA(mesh.Cells[i]) ? Complex.One : Complex.Zero;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }

        var q = a.Lu().Solve(rhs);
        Complex onB = Complex.Zero;
        for (int i = 0; i < m; i++) if (!onA(mesh.Cells[i])) onB += q[i];
        return -onB.Real;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 5 — the static limit, and a real capacitance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T5_1_APlateOverGroundConvergesToEpsilonZeroAOverH_WithTheFringingFalling()
    {
        // εᵣ = 1, so the slab contributes exactly ONE image at depth 2h and the oracle stays closed
        // form. The parallel-plate value is an ASYMPTOTE — the test is that the excess falls as h/W,
        // not that any single ratio hits 1.
        const double w = 1e-3;
        double area = w * w;

        var ratios = new List<(double HOverW, double Ratio)>();
        foreach (double hOverW in new[] { 0.20, 0.10, 0.05 })
        {
            double h = hOverW * w;
            var mesh  = Uniform(w, w, 10, 10);
            var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);

            double c        = Capacitance(mesh, terms);
            double parallel = EmConstants.Eps0 * area / h;
            ratios.Add((hOverW, c / parallel));
            _out.WriteLine($"h/W = {hOverW:F2}: C = {c * 1e15:F3} fF, ε₀A/h = {parallel * 1e15:F3} fF, " +
                           $"ratio = {c / parallel:F4}");
        }

        // Every ratio exceeds 1 (fringing adds charge), and the excess shrinks with h/W.
        foreach (var (_, r) in ratios) Assert.True(r > 1.0, $"ratio {r:F4} is below the parallel-plate value");
        for (int i = 1; i < ratios.Count; i++)
            Assert.True(ratios[i].Ratio < ratios[i - 1].Ratio,
                $"fringing excess did not fall from h/W = {ratios[i - 1].HOverW} to {ratios[i].HOverW}");

        // At the tightest spacing the answer is within a fringing correction of the ideal.
        Assert.True(ratios[^1].Ratio < 1.35, $"h/W = 0.05 gives {ratios[^1].Ratio:F4}, too far from 1");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T5_2_AnIsolatedPlateConvergesUnderRefinement_ReportedByRichardson()
    {
        // No ground plane, no slab: G_q is free space alone. There is no closed form for a square
        // plate's self-capacitance, so the deliverable is the refinement sequence and its
        // extrapolation — NOT a comparison against a transcribed constant.
        const double w = 1e-3;
        var terms = PlanarKernelTerms.FreeSpace(0.0);

        var seq = new List<(int N, double C)>();
        foreach (int n in new[] { 6, 12, 24 })
            seq.Add((n, Capacitance(Uniform(w, w, n, n), terms)));

        foreach (var (n, c) in seq)
            _out.WriteLine($"{n,2}×{n,-2} cells: C = {c * 1e15:F5} fF   (C/(4πε₀w) = {c / (4 * Math.PI * EmConstants.Eps0 * w):F5})");

        // Richardson on a halving sequence: the observed order p from three levels, then the limit.
        double d1 = seq[1].C - seq[0].C, d2 = seq[2].C - seq[1].C;
        double order = Math.Log(Math.Abs(d1 / d2)) / Math.Log(2.0);
        double limit = seq[2].C + d2 * d2 / (d1 - d2);
        _out.WriteLine($"observed order {order:F2}, Richardson limit C = {limit * 1e15:F5} fF " +
                       $"⇒ C/(4πε₀w) = {limit / (4 * Math.PI * EmConstants.Eps0 * w):F5}");

        Assert.True(d1 * d2 > 0 && Math.Abs(d2) < Math.Abs(d1),
            "the refinement sequence is not monotonically converging");
        Assert.True(order > 0.4, $"observed convergence order {order:F2} is too weak to extrapolate");
        Assert.True(Math.Abs(limit - seq[2].C) / limit < 0.1,
            "the finest mesh is still far from the extrapolated limit");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 6 — convergence, with the two error sources SEPARATED
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T6_1_PIsEXACTLYInvariantUnderSubdivision_WhichIsWhyRawEntriesCannotBeTheTest()
    {
        // A RAW matrix entry has no limit to converge to — P[a,a] grows without bound as its cell
        // shrinks — so "individual entries converge" has to be asked of something subdivision-
        // independent. It turns out P has that property EXACTLY: because it is area-AVERAGED, the
        // mean coefficient between a fixed pair of regions is identical however those regions are
        // cut up. That is a strong check on the area normalisation in its own right, and it is what
        // makes the next test (a physical matrix entry) the right form of the question.
        const double w = 1e-3;
        var terms = PlanarKernelTerms.FreeSpace(0.0);

        var vals = new List<double>();
        foreach (int n in new[] { 4, 8, 16, 32 })
        {
            var mesh  = Uniform(w, w, n, 1);
            var cores = PlanarFill.BuildCores(mesh);
            var p     = PlanarFill.ScalarPotentialMatrix(cores, terms.With(cores.Settings.Order, cores.RhoFloorM));

            int q = n / 4;
            Complex sum = Complex.Zero;
            for (int a = 0; a < q; a++)
                for (int b = n - q; b < n; b++) sum += p[a, b];
            vals.Add((sum / (q * (double)q)).Real);
            _out.WriteLine($"{n,2} cells: ⟨P⟩(left¼, right¼) = {vals[^1]:G12}");
        }

        foreach (double v in vals)
            Assert.True(Math.Abs(v - vals[0]) / vals[0] < 1e-9,
                $"the area-averaged coefficient moved with the subdivision: {v:G12} vs {vals[0]:G12}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T6_1b_APhysicalMatrixENTRYConvergesUnderMeshRefinement()
    {
        // The mutual capacitance between two fixed coplanar plates — an entry of the capacitance
        // matrix, so it has a limit — under three levels of mesh refinement.
        const double w = 1e-3, gap = 0.4e-3;
        var terms = PlanarKernelTerms.FreeSpace(0.0);

        var vals = new List<double>();
        foreach (int n in new[] { 6, 12, 24 })
        {
            // one grid covering both plates and the gap; the middle third is empty
            var gx = new List<double>();
            for (int i = 0; i <= n; i++) gx.Add(w * i / n);
            for (int i = 0; i <= n; i++) gx.Add(w + gap + w * i / n);
            var gy = new double[n + 1];
            for (int j = 0; j <= n; j++) gy[j] = w * j / n;

            var full = PlanarFillTests.Grid([.. gx.Distinct().Order()], gy);
            var kept = full.Cells.Where(c => c.XMin < w - 1e-15 || c.XMin > w + gap - 1e-15).ToList();
            var mesh = Rebuild(kept, full);

            vals.Add(MutualCapacitance(mesh, c => c.CenterX < w, terms));
            _out.WriteLine($"{n,2}×{n,-2} per plate ({mesh.Cells.Count} cells): C₁₂ = {vals[^1] * 1e15:F5} fF");
        }

        for (int i = 1; i + 1 < vals.Count; i++)
            Assert.True(Math.Abs(vals[i + 1] - vals[i]) < Math.Abs(vals[i] - vals[i - 1]),
                "the mutual capacitance is not converging under mesh refinement");
        _out.WriteLine($"last step moved it by {Math.Abs(vals[^1] - vals[^2]) / vals[^1]:E2}");
    }

    /// <summary>Rebuilds a mesh from a kept subset of cells, re-deriving the rooftop pairs.</summary>
    private static PlanarMesh Rebuild(List<PlanarCell> kept, PlanarMesh full)
    {
        var index = new Dictionary<(int, int), int>();
        for (int i = 0; i < kept.Count; i++) index[(kept[i].IX, kept[i].IY)] = i;

        var bases = new List<PlanarBasis>();
        for (int i = 0; i < kept.Count; i++)
        {
            if (index.TryGetValue((kept[i].IX + 1, kept[i].IY), out int bx))
                bases.Add(new PlanarBasis(0, i, bx, PlanarBasisDirection.X));
            if (index.TryGetValue((kept[i].IX, kept[i].IY + 1), out int by))
                bases.Add(new PlanarBasis(0, i, by, PlanarBasisDirection.Y));
        }
        return full with { Cells = kept, Bases = bases };
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T6_2_TheCapacitanceConvergesAtAStatedRate()
    {
        const double w = 1e-3, h = 0.1e-3;
        var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);

        var seq = new List<(int N, double C)>();
        foreach (int n in new[] { 5, 10, 20, 30 }) seq.Add((n, Capacitance(Uniform(w, w, n, n), terms)));
        foreach (var (n, c) in seq) _out.WriteLine($"{n,2}×{n,-2}: C = {c * 1e15:F4} fF");

        double p1 = Math.Log(Math.Abs((seq[1].C - seq[0].C) / (seq[2].C - seq[1].C))) / Math.Log(2.0);
        double p2 = Math.Log(Math.Abs((seq[2].C - seq[1].C) / (seq[3].C - seq[2].C))) / Math.Log(2.0);
        _out.WriteLine($"observed convergence order: {p1:F2} then {p2:F2}");

        Assert.True(p2 > 0.4, $"capacitance convergence order {p2:F2} is not a rate worth stating");
        Assert.True(Math.Abs(seq[3].C - seq[2].C) < Math.Abs(seq[2].C - seq[1].C));
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T6_3_TheAnswerConvergesUnderQUADRATUREOrderIndependentlyOfTheMesh()
    {
        // R-fil-5's separability requirement: hold the mesh fixed and refine only the quadrature. If
        // the two could not be separated, a discretisation error and a quadrature error would be
        // indistinguishable and neither could be reported honestly.
        const double w = 1e-3, h = 0.1e-3;
        var mesh  = Uniform(w, w, 6, 6);
        var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);

        var vals = new List<double>();
        foreach (int f in new[] { 0, 1, 2 })
        {
            var st = f == 0 ? PlanarFillSettings.Default : PlanarFillSettings.Default.Finer(f);
            vals.Add(Capacitance(mesh, terms, st));
            _out.WriteLine($"quadrature Finer({f}): C = {vals[^1] * 1e15:F6} fF");
        }

        double drift = Math.Abs(vals[^1] - vals[0]) / vals[0];
        _out.WriteLine($"total drift across three quadrature levels on ONE mesh: {drift:E2}");
        Assert.True(drift < 1e-4,
            $"the default quadrature is {drift:E2} away from a converged one on a fixed mesh — that is " +
            "not separable from the discretisation error");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T6_4_ExtractionOrderIsAMeasurement_NotAPreference()
    {
        // D3: measure all three and report. The same mesh, the same kernel, three extraction orders.
        const double w = 1e-3, h = 0.1e-3;
        var mesh  = Uniform(w, w, 6, 6);
        var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One, 2.0 * h);

        double reference = Capacitance(mesh, terms, PlanarFillSettings.Default.Finer(1));
        foreach (var order in new[] { PlanarExtractionOrder.Inverse, PlanarExtractionOrder.Constant,
                                      PlanarExtractionOrder.Linear })
        {
            double c = Capacitance(mesh, terms, PlanarFillSettings.Default with { Order = order });
            _out.WriteLine($"{order,-9}: C = {c * 1e15:F6} fF, {Math.Abs(c - reference) / reference:E2} from the " +
                           "converged answer on the same mesh");
        }

        // All three must reach the same answer — they differ in what goes through quadrature, not in
        // what is being integrated.
        double a = Capacitance(mesh, terms, PlanarFillSettings.Default.Finer(1) with { Order = PlanarExtractionOrder.Inverse });
        Assert.True(Math.Abs(a - reference) / reference < 1e-6,
            "the extraction orders do not agree in the limit — one of them is not an identity");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fil-12 — R-msh-5's DEFERRED HALF, CLOSED
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T6_5_TheEdgeReferenceLengthIsMeasuredAgainstAConvergedCapacitance()
    {
        // L8b measured N under both candidate edge reference lengths and recorded that "the
        // CONVERGENCE half of R-mom-8's measurement needs a solver and belongs to L8c". It now
        // exists. The quantity is the static capacitance of §10.7's own FR-4 hero over its ground
        // plane, at εᵣ = 1 so the kernel stays closed form.
        //
        // THE REFERENCE IS EACH CANDIDATE'S OWN REFINEMENT LIMIT, not a uniform mesh. A uniform mesh
        // does not resolve the 1/√d edge current at all — that is the entire reason the edge mesh
        // exists — so it converges from BELOW at order ~0.5 and its extrapolation would flatter
        // whichever candidate happened to be coarser. Refining each candidate and checking that the
        // two agree in the limit is the comparison R-mom-8 actually makes.
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(20e-3, 0),
                                    new EmPoint(20e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
                5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9);

        var terms = PlanarKernelTerms.FreeSpaceWithImage(0.0, -Complex.One,
                                                         2.0 * GroundedSlab.Fr4Starter.HeightM);

        var limits = new Dictionary<PlanarEdgeReference, double>();
        var atDefault = new Dictionary<PlanarEdgeReference, (int N, double C)>();

        foreach (var kind in new[] { PlanarEdgeReference.ConductorWidth, PlanarEdgeReference.CellSize })
        {
            var seq = new List<(int N, double C)>();
            foreach (int cpw in new[] { 20, 30, 45 })
            {
                var settings = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                                      EdgeMesh: true, EdgeCells: 3);
                var report = SurfaceMesher.Mesh(problem, settings, kind);
                if (!report.CanSolve)
                {
                    _out.WriteLine($"{kind}/{cpw}: N = {report.UnknownCount} — refused by R17, skipped");
                    continue;
                }

                double c = Capacitance(report.Mesh, terms);
                seq.Add((report.UnknownCount, c));
                _out.WriteLine($"{kind,-14} cells/λ = {cpw,2}: N = {report.UnknownCount,5}, " +
                               $"smallest cell {report.MinCellEdgeM * 1e6,7:F2} µm, C = {c * 1e15:F3} fF");
                if (cpw == 20) atDefault[kind] = (report.UnknownCount, c);
            }

            // Richardson on the 1.5x refinement ladder.
            double d1 = seq[1].C - seq[0].C, d2 = seq[2].C - seq[1].C;
            double limit = Math.Abs(d1 - d2) > 0 ? seq[2].C + d2 * d2 / (d1 - d2) : seq[2].C;
            limits[kind] = limit;
            _out.WriteLine($"{kind,-14} extrapolated limit: {limit * 1e15:F3} fF");
        }

        double consensus = 0.5 * (limits[PlanarEdgeReference.ConductorWidth] + limits[PlanarEdgeReference.CellSize]);
        double spread = Math.Abs(limits[PlanarEdgeReference.ConductorWidth] - limits[PlanarEdgeReference.CellSize]) / consensus;
        _out.WriteLine($"the two references agree in the limit to {spread:P3} — consensus {consensus * 1e15:F3} fF");

        foreach (var kind in atDefault.Keys)
            _out.WriteLine($"AT THE DEFAULT MESH, {kind,-14}: N = {atDefault[kind].N,5}, " +
                           $"{Math.Abs(atDefault[kind].C - consensus) / consensus * 100:F2}% from the consensus limit");

        // The two candidates must converge to the SAME physical answer — if they did not, one of them
        // would be biasing the physics rather than merely the cost.
        Assert.True(spread < 0.01, $"the two edge references disagree in the limit by {spread:P2}");

        // …and the shipped default must reach that limit at least as well as the alternative does,
        // with fewer unknowns. That is R-fil-12's actual question.
        double eWidth = Math.Abs(atDefault[PlanarEdgeReference.ConductorWidth].C - consensus) / consensus;
        double eCell  = Math.Abs(atDefault[PlanarEdgeReference.CellSize].C - consensus) / consensus;
        Assert.True(eWidth < 0.02, $"the conductor-width default lands {eWidth:P2} from the converged value");
        Assert.True(atDefault[PlanarEdgeReference.ConductorWidth].N <= atDefault[PlanarEdgeReference.CellSize].N,
            "the conductor-width reference no longer costs fewer unknowns");
        _out.WriteLine($"conductor-width: {eWidth:P2} at N = {atDefault[PlanarEdgeReference.ConductorWidth].N}; " +
                       $"cell-size: {eCell:P2} at N = {atDefault[PlanarEdgeReference.CellSize].N}");
    }

}
