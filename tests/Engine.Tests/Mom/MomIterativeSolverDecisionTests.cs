// M5's DECISION GATE (brief-em-sweep-performance, gate 11 / R-emp-15) — run BEFORE any projection
// code exists, which is the whole point of it.
//
// M5 proposes AIM (the Adaptive Integral Method): project the graded, conformally-cut basis functions
// onto a separate uniform auxiliary grid, do the far-field matrix-vector product by FFT on that grid,
// and keep a sparse near-field correction computed exactly with L8c's existing closed forms. Cost
// goes from O(N²) fill + O(N³) solve to O(N log N) per ITERATION plus a sparse near-field fill.
//
// "Per iteration" is the load-bearing phrase. AIM has no direct solve — it needs an ITERATIVE one,
// and that is the same objection that deferred ACA at L9e ("a compressed matrix needs a solver that
// consumes it: an iterative one, whose convergence on a MoM system is not guaranteed and is its own
// research item"). So the decision is NOT about the FFT:
//
//     if GMRES needs O(N) iterations, AIM buys nothing — O(N log N) per iteration times O(N)
//     iterations is worse than the direct solve, and that is the honest outcome.
//
// This file measures exactly that, against the DENSE matrices this kernel already builds, with no
// accelerator anywhere.
//
// ── Two deliberate choices that make this a fair test of AIM's premise ──────────────────────────
//
// FULL GMRES, not restarted. Restarting can only converge more slowly, so full GMRES is an UPPER
// BOUND on how well any GMRES variant does here. If full GMRES needs hundreds of iterations,
// GMRES(50) needs at least that many. Giving AIM the benefit of the doubt is the point.
//
// RIGHT preconditioning, so the Arnoldi residual IS the true residual ‖b − Ax‖ and the three
// preconditioners are compared on the same quantity. Left preconditioning would report ‖M⁻¹(b − Ax)‖,
// which flatters a strong preconditioner for free.
//
// The right-hand side is the REAL port excitation (PlanarExcitation.RightHandSide), not a random
// vector: convergence on a MoM system is right-hand-side dependent, and the only RHS this solver ever
// sees is a port's incidence column.

using System.Numerics;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class MomIterativeSolverDecisionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>Where the de-embedding's own error budget sits. L8d measured de-embedding amplifying
    /// a raw-S error ~22x at the low-frequency end, and L8c puts the fill's own accuracy at 5.0e-6
    /// against an independent oracle — so a solve that is only good to 1e-4 has thrown away the fill.
    /// The table reports the crossing of every threshold rather than picking one, because which one
    /// matters is exactly what a reader will want to argue about.</summary>
    private static readonly double[] Thresholds = [1e-4, 1e-6, 1e-8, 1e-10];

    /// <summary>Stop as soon as the tightest threshold is crossed — everything past it is unreported
    /// and the ladders would otherwise spend most of their wall clock on the near-field rows, which
    /// converge in single digits.</summary>
    private const double Stop = 1e-10;

    /// <summary>Iteration cap. Full GMRES is exact at N, so a cap is only a reporting limit; 500 is
    /// well past the ~0.4·N the unpreconditioned rows need at every N measured, and rows that
    /// hit it are reported as not reaching the threshold rather than silently truncated.</summary>
    private static int Cap(int n) => Math.Min(n, 500);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Full GMRES, right-preconditioned. Returns the true relative residual after each iteration.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Row-major flat copy — the NumFlat indexer in a hot O(N²) loop dominates everything
    /// else at these iteration counts, and this harness lives or dies on being able to run hundreds
    /// of iterations at several N.</summary>
    private static Complex[] Flatten(Mat<Complex> a)
    {
        int n = a.RowCount;
        var f = new Complex[(long)n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) f[(long)i * n + j] = a[i, j];
        return f;
    }

    private static Complex[] MatVec(Complex[] a, int n, Complex[] x)
    {
        var y = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double re = 0, im = 0;
            long o = (long)i * n;
            for (int k = 0; k < n; k++)
            {
                var t = a[o + k]; var u = x[k];
                re += t.Real * u.Real - t.Imaginary * u.Imaginary;
                im += t.Real * u.Imaginary + t.Imaginary * u.Real;
            }
            y[i] = new Complex(re, im);
        }
        return y;
    }

    /// <summary>
    /// FULL (non-restarted) GMRES with RIGHT preconditioning. Returns the TRUE relative residual
    /// ‖b − Ax‖/‖b‖ after each iteration — right preconditioning is what makes the Arnoldi residual
    /// the true one, so the three preconditioners are compared on the same quantity.
    /// </summary>
    private static double[] GmresResiduals(Complex[] a, int n, Complex[] b,
                                           Func<Complex[], Complex[]> applyMInv, int maxIter,
                                           double stopBelow = 0.0)
    {
        double beta = Norm(b);
        if (beta == 0) return [0.0];

        var v  = new List<Complex[]> { Scale(b, 1.0 / beta) };
        var h  = new Complex[maxIter + 2, maxIter + 1];
        var cs = new Complex[maxIter + 1];
        var sn = new Complex[maxIter + 1];
        var g  = new Complex[maxIter + 2];
        g[0] = beta;

        var res = new List<double>();

        for (int j = 0; j < maxIter; j++)
        {
            var w = MatVec(a, n, applyMInv(v[j]));

            for (int i = 0; i <= j; i++)                     // modified Gram-Schmidt
            {
                h[i, j] = Dot(v[i], w);
                Axpy(w, v[i], -h[i, j]);
            }
            double hNext = Norm(w);                          // = h[j+1, j] BEFORE the rotation
            h[j + 1, j] = hNext;

            for (int i = 0; i < j; i++)                      // previous rotations
            {
                Complex t = cs[i] * h[i, j] + sn[i] * h[i + 1, j];
                h[i + 1, j] = -Complex.Conjugate(sn[i]) * h[i, j] + Complex.Conjugate(cs[i]) * h[i + 1, j];
                h[i, j] = t;
            }

            // The new rotation zeroing h[j+1, j]. For a complex Givens taking [a; b] to [d; 0] with
            // the matrix [c s; -conj(s) conj(c)], the choice is c = conj(a)/d, s = conj(b)/d.
            Complex aa = h[j, j], bb = h[j + 1, j];
            double d = Math.Sqrt(aa.Magnitude * aa.Magnitude + bb.Magnitude * bb.Magnitude);
            if (d == 0) { res.Add(res.Count == 0 ? 0.0 : res[^1]); break; }
            cs[j] = Complex.Conjugate(aa) / d;
            sn[j] = Complex.Conjugate(bb) / d;
            h[j, j]     = cs[j] * aa + sn[j] * bb;
            h[j + 1, j] = Complex.Zero;

            g[j + 1] = -Complex.Conjugate(sn[j]) * g[j];     // from the OLD g[j] — order matters
            g[j]     =  cs[j] * g[j];

            res.Add(g[j + 1].Magnitude / beta);

            if (res[^1] < 1e-14 || res[^1] <= stopBelow) break;
            if (hNext <= 1e-300) break;                       // lucky breakdown: the space is exact
            v.Add(Scale(w, 1.0 / hNext));
        }

        return [.. res];
    }

    private static Complex Dot(Complex[] a, Complex[] b)
    {
        double re = 0, im = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i]; var u = b[i];
            re += t.Real * u.Real + t.Imaginary * u.Imaginary;      // conj(a) . b
            im += t.Real * u.Imaginary - t.Imaginary * u.Real;
        }
        return new Complex(re, im);
    }

    private static double Norm(Complex[] a)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i].Real * a[i].Real + a[i].Imaginary * a[i].Imaginary;
        return Math.Sqrt(s);
    }

    private static Complex[] Scale(Complex[] a, double f)
    {
        var r = new Complex[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] * f;
        return r;
    }

    private static void Axpy(Complex[] y, Complex[] x, Complex alpha)
    {
        for (int i = 0; i < y.Length; i++) y[i] += alpha * x[i];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Preconditioners
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static Func<Complex[], Complex[]> Identity() => x => x;

    private static Func<Complex[], Complex[]> Jacobi(Complex[] a, int n)
    {
        var inv = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            var d = a[(long)i * n + i];
            inv[i] = d == Complex.Zero ? Complex.One : Complex.One / d;
        }
        return x =>
        {
            var r = new Complex[n];
            for (int i = 0; i < n; i++) r[i] = inv[i] * x[i];
            return r;
        };
    }

    /// <summary>Basis centre — the midpoint of its two cells' centres, which is what "near" means for
    /// a rooftop.</summary>
    private static (double X, double Y)[] Centres(PlanarMesh mesh)
    {
        var c = new (double, double)[mesh.Bases.Count];
        for (int i = 0; i < mesh.Bases.Count; i++)
        {
            var bfn = mesh.Bases[i];
            var ca = mesh.Cells[bfn.CellA];
            var cb = mesh.Cells[bfn.CellB];
            c[i] = (0.25 * (ca.XMin + ca.XMax + cb.XMin + cb.XMax),
                    0.25 * (ca.YMin + ca.YMax + cb.YMin + cb.YMax));
        }
        return c;
    }

    /// <summary>
    /// The NEAR-FIELD preconditioner: the exact entries of Z between bases within
    /// <paramref name="radiusM"/>, factored sparsely. This is AIM's own near-field block used as the
    /// preconditioner — the strongest one M5 could reasonably assume, since AIM computes exactly
    /// these entries anyway and would get the preconditioner for free.
    /// </summary>
    private static (Func<Complex[], Complex[]> Apply, double FillFraction, double FactorS)?
        NearField(Complex[] a, int n, PlanarMesh mesh, double radiusM)
    {
        var c = Centres(mesh);
        double r2 = radiusM * radiusM;

        // Counted first so the triplet store is allocated exactly — a wide radius is far more than
        // any fixed per-row guess.
        long nnz = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double dx = c[i].X - c[j].X, dy = c[i].Y - c[j].Y;
                if (j == i || dx * dx + dy * dy <= r2) nnz++;
            }

        var tri = new CoordinateStorage<Complex>(n, n, (int)Math.Min(nnz, int.MaxValue));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double dx = c[i].X - c[j].X, dy = c[i].Y - c[j].Y;
                if (j == i || dx * dx + dy * dy <= r2) tri.At(i, j, a[(long)i * n + j]);
            }

        var csc = SparseMatrix.OfIndexed(tri);
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        SparseLU lu;
        try
        {
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtPlusA);
            lu = SparseLU.Create(csc, perm, 1.0);
        }
        catch { return null; }
        sw.Stop();

        return (x =>
        {
            var r = new Complex[n];
            lu.Solve(x, r);
            return r;
        }, nnz / (double)n / n, sw.Elapsed.TotalSeconds);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Reporting
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string Crossings(double[] res, int n)
    {
        var parts = new List<string>();
        foreach (double t in Thresholds)
        {
            int at = -1;
            for (int i = 0; i < res.Length; i++) if (res[i] <= t) { at = i + 1; break; }
            parts.Add(at < 0 ? "   —" : $"{at,4}");
        }
        double frac = 0;
        for (int i = 0; i < res.Length; i++) if (res[i] <= 1e-6) { frac = (i + 1) / (double)n; break; }
        return string.Join(" ", parts) + (frac > 0 ? $"   {frac,5:F2}·N" : "        —");
    }

    private sealed record Sys(Complex[] A, int N, Complex[] B, PlanarMesh Mesh, Mat<Complex> Dense);

    private static Sys Fill(PlanarProblem problem, PlanarMeshSettings settings, double fHz,
                           double z0 = 50.0)
    {
        var report = SurfaceMesher.Mesh(problem, settings);
        var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem, z0));
        var kern   = PlanarLineFixtures.Kernel(problem.Slab, fHz);
        var cores  = PlanarFill.BuildCores(report.Mesh);
        var z      = PlanarFill.Fill(cores, kern.VectorPotential, kern.Scalar, 2 * Math.PI * fHz);

        int n   = z.RowCount;
        var rhs = PlanarExcitation.RightHandSide(n, ports[0]);
        var b   = new Complex[n];
        for (int i = 0; i < n; i++) b[i] = rhs[i];

        return new Sys(Flatten(z), n, b, report.Mesh, z);
    }

    private void Header(string what)
    {
        _out.WriteLine("");
        _out.WriteLine(what);
        _out.WriteLine("                                 iterations to reach");
        _out.WriteLine("                            1e-4 1e-6 1e-8 1e-10   at 1e-6");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 11
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_A_TheHarnessSolvesTheRealSystem_OrNothingBelowMeansAnything()
    {
        // Before any number is believed: full GMRES with no preconditioner must reproduce the dense
        // LU's own answer. Without this, a bug in the Arnoldi or the Givens updates would show up as
        // "GMRES converges beautifully" or "GMRES never converges" and either would be reported as a
        // finding.
        var line = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var s = Fill(line, PlanarLineFixtures.Coarse, 6e9);

        var rhs = new Vec<Complex>(s.N);
        for (int i = 0; i < s.N; i++) rhs[i] = s.B[i];
        var exact = PlanarSystem.Wrap(s.Dense).Solve(rhs);

        var res = GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop);

        _out.WriteLine($"N = {s.N}; full GMRES ran {res.Length} iteration(s), " +
                       $"final relative residual {res[^1]:E2}");
        _out.WriteLine($"dense LU solution norm {Norm([.. Enumerable.Range(0, s.N).Select(i => exact[i])]):E3}");

        // GMRES on an n x n system is exact at n iterations in exact arithmetic; all that is asserted
        // is that the residual really is driven down, i.e. the harness is solving THIS system.
        Assert.True(res[^1] < 1e-6,
            $"the GMRES harness did not converge on a system the dense LU solves — residual {res[^1]:E2}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_B_DoesTheIterationCountGrowWithN()
    {
        // A LENGTH ladder at fixed mesh density: the same cross-section, a longer board. That is what
        // "another tool solved a comparable problem" means and it is the growth AIM would be bought
        // for. (A refinement ladder answers a different question — see gate 11C.)
        Header("N ladder — same cross-section and density, longer line, 6 GHz");

        foreach (double lengthM in new[] { 8e-3, 16e-3, 32e-3, 64e-3, 128e-3, 256e-3 })
        {
            var line = PlanarLineFixtures.Fr4Line(lengthM, 6e9);
            var s = Fill(line, PlanarLineFixtures.Coarse, 6e9);
            int cap = Cap(s.N);

            var none = GmresResiduals(s.A, s.N, s.B, Identity(), cap, Stop);
            var jac  = GmresResiduals(s.A, s.N, s.B, Jacobi(s.A, s.N), cap, Stop);
            var nf   = NearField(s.A, s.N, s.Mesh, 3.0 * s.Mesh.BulkCellHint());

            _out.WriteLine($"  L = {lengthM * 1e3,4:F0} mm  N = {s.N,4}  none      {Crossings(none, s.N)}");
            _out.WriteLine($"                          Jacobi    {Crossings(jac, s.N)}");
            if (nf is { } p)
            {
                var r = GmresResiduals(s.A, s.N, s.B, p.Apply, cap, Stop);
                _out.WriteLine($"                          near-3c   {Crossings(r, s.N)}" +
                               $"   (nnz {p.FillFraction * 100,4:F1}%, LU {p.FactorS,5:F2} s)");
            }
            else _out.WriteLine("                          near-3c   sparse LU failed");
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_C_DoesItGrowWithMeshREFINEMENTAndWithFREQUENCY()
    {
        Header("refinement ladder — same 16 mm line at 6 GHz, finer mesh");
        foreach (int cpw in new[] { 10, 20, 40 })
        {
            var line = PlanarLineFixtures.Fr4Line(16e-3, 6e9);
            var ms   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw, EdgeMesh: false);
            var s    = Fill(line, ms, 6e9);

            var none = GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop);
            var nf   = NearField(s.A, s.N, s.Mesh, 3.0 * s.Mesh.BulkCellHint());
            _out.WriteLine($"  cells/λ {cpw,3}  N = {s.N,4}  none      {Crossings(none, s.N)}");
            if (nf is { } p)
                _out.WriteLine($"                          near-3c   " +
                               $"{Crossings(GmresResiduals(s.A, s.N, s.B, p.Apply, Cap(s.N), Stop), s.N)}");
        }

        Header("frequency ladder — the same 16 mm line and mesh");
        foreach (double f in new[] { 1e9, 6e9, 20e9 })
        {
            var line = PlanarLineFixtures.Fr4Line(16e-3, 6e9);
            var s = Fill(line, PlanarLineFixtures.Coarse, f);

            var none = GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop);
            var nf   = NearField(s.A, s.N, s.Mesh, 3.0 * s.Mesh.BulkCellHint());
            _out.WriteLine($"  {f / 1e9,5:F0} GHz  N = {s.N,4}  none      {Crossings(none, s.N)}");
            if (nf is { } p)
                _out.WriteLine($"                          near-3c   " +
                               $"{Crossings(GmresResiduals(s.A, s.N, s.B, p.Apply, Cap(s.N), Stop), s.N)}");
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_D_TheNearFieldRADIUSIsTheFreeParameter()
    {
        // R-emp-17's knob, measured as the preconditioner's own radius. A wider near field is a
        // better preconditioner AND a denser sparse factorisation; the trade is the decision.
        Header("near-field radius sweep — 128 mm line, 6 GHz");

        var line = PlanarLineFixtures.Fr4Line(128e-3, 6e9);
        var s = Fill(line, PlanarLineFixtures.Coarse, 6e9);
        double cell = s.Mesh.BulkCellHint();

        _out.WriteLine($"  N = {s.N}, bulk cell {cell * 1e3:F3} mm");
        _out.WriteLine($"  none               " +
                       $"{Crossings(GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop), s.N)}");
        foreach (double k in new[] { 1.5, 3.0, 6.0, 12.0 })
        {
            var nf = NearField(s.A, s.N, s.Mesh, k * cell);
            if (nf is not { } p) { _out.WriteLine($"  {k,4:F1} cells        sparse LU failed"); continue; }
            _out.WriteLine($"  {k,4:F1} cells        " +
                           $"{Crossings(GmresResiduals(s.A, s.N, s.B, p.Apply, Cap(s.N), Stop), s.N)}" +
                           $"   (nnz {p.FillFraction * 100,4:F1}%, LU {p.FactorS,5:F2} s)");
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_E_TheSHIPPINGMesh_WhichIsWhereEdgeGradingLives()
    {
        // The ladders above run the COARSE fixture, whose EdgeMesh is OFF. That is not the product.
        // The shipping mesh grades toward every conductor edge (L8b measures ~10x on the cell-size
        // spread, and its own control is 4.437% -> 0.431% for 3.6x the unknowns) and cuts conformal
        // boundary cells. Grading is LOCAL REFINEMENT, and the refinement ladder in gate 11C is where
        // the near-field preconditioner fell apart — so this is the configuration the decision has to
        // be made on, not the smooth one.
        Header("shipping mesh (edge grading ON) — FR-4 hero cross-section, 6 GHz");

        foreach (double lengthM in new[] { 16e-3, 32e-3, 64e-3, 128e-3 })
        {
            var line = PlanarLineFixtures.Fr4Line(lengthM, 6e9);
            var s = Fill(line, PlanarLineFixtures.Shipping, 6e9);
            double cell = s.Mesh.BulkCellHint();

            var none = GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop);
            _out.WriteLine($"  L = {lengthM * 1e3,4:F0} mm  N = {s.N,4}  none      {Crossings(none, s.N)}");

            foreach (double k in new[] { 3.0, 8.0 })
            {
                var nf = NearField(s.A, s.N, s.Mesh, k * cell);
                if (nf is not { } p) { _out.WriteLine($"                          near-{k,2:F0}c   sparse LU failed"); continue; }
                _out.WriteLine($"                          near-{k,2:F0}c   " +
                               $"{Crossings(GmresResiduals(s.A, s.N, s.B, p.Apply, Cap(s.N), Stop), s.N)}" +
                               $"   (nnz {p.FillFraction * 100,4:F1}%, LU {p.FactorS,5:F2} s)");
            }

            var (minC, maxC) = CellSpread(s.Mesh);
            _out.WriteLine($"                          cell spread {maxC / minC,6:F1}x " +
                           $"({minC * 1e6:F1} to {maxC * 1e6:F1} um)");
        }
    }

    private static (double Min, double Max) CellSpread(PlanarMesh mesh)
    {
        double lo = double.MaxValue, hi = 0;
        foreach (var c in mesh.Cells)
        {
            double m = Math.Min(c.Width, c.Height);
            if (m > 0) { lo = Math.Min(lo, m); hi = Math.Max(hi, m); }
        }
        return (lo, hi);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate11_F_AWIDEStructure_WhereTheNearFieldStopsBeingNarrowlyBanded()
    {
        // Every ladder above is a 2.9 mm line: a handful of cells across, so its near-field matrix is
        // narrowly BANDED and a sparse LU of it is nearly free. That flatters the preconditioner in a
        // way a real board does not.
        //
        // A wide conductor is the honest case, and it is also §0's own: the 12 Ohm end of the user's
        // taper is 6.71 mm and ~20 cells across, which is exactly why its calibration standards were
        // larger than the DUT. Here the near field spans the full transverse run, the bandwidth grows
        // with it, and the sparse LU's own fill-in is the cost that decides whether the preconditioner
        // is affordable at all. THAT is what this gate measures — the LU column, not just iterations.
        Header("WIDE conductor (6.71 mm, §0's own 12 Ohm end) — shipping mesh, 6 GHz");

        foreach (double lengthM in new[] { 16e-3, 32e-3, 64e-3, 128e-3 })
        {
            var wide = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 6.71e-3, lengthM, 6e9);
            var s = Fill(wide, PlanarLineFixtures.Shipping, 6e9, z0: 12.0);
            double cell = s.Mesh.BulkCellHint();
            int across = s.Mesh.GridY.Count - 1;

            var none = GmresResiduals(s.A, s.N, s.B, Identity(), Cap(s.N), Stop);
            _out.WriteLine($"  L = {lengthM * 1e3,4:F0} mm  N = {s.N,4}  ({across} cells across)");
            _out.WriteLine($"                          none      {Crossings(none, s.N)}");

            foreach (double k in new[] { 3.0, 8.0 })
            {
                var nf = NearField(s.A, s.N, s.Mesh, k * cell);
                if (nf is not { } p) { _out.WriteLine($"                          near-{k,2:F0}c   sparse LU failed"); continue; }
                _out.WriteLine($"                          near-{k,2:F0}c   " +
                               $"{Crossings(GmresResiduals(s.A, s.N, s.B, p.Apply, Cap(s.N), Stop), s.N)}" +
                               $"   (nnz {p.FillFraction * 100,4:F1}%, LU {p.FactorS,5:F2} s)");
            }

            // The number the LU column has to be judged against: what the DIRECT solve of the whole
            // dense system costs. A near-field factorisation approaching this buys nothing.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = PlanarSystem.Wrap(s.Dense).Lu;
            _out.WriteLine($"                          dense LU  {sw.Elapsed.TotalSeconds,5:F2} s");
        }
    }
}

internal static class MeshCellHint
{
    /// <summary>A representative cell size for this mesh — the median gridline spacing along x, which
    /// is what "a few cells away" has to be measured in.</summary>
    public static double BulkCellHint(this PlanarMesh mesh)
    {
        var dx = new List<double>();
        for (int i = 1; i < mesh.GridX.Count; i++) dx.Add(mesh.GridX[i] - mesh.GridX[i - 1]);
        dx.Sort();
        return dx.Count == 0 ? 1e-3 : dx[dx.Count / 2];
    }
}
