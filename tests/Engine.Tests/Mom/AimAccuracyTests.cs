// M5 (brief-em-sweep-performance) — R-emp-16's TWO ACCURACY GATES and R-emp-17's TRADE TABLE.
//
// Gate 11 answered the question that could have stopped M5 (does an iterative solve converge on this
// operator — yes, flat in N with an 8-cell near field). It said nothing about AIM's own accuracy, and
// the brief is explicit that the accelerator "is a new formulation of the same matrix and it inherits
// the whole existing oracle ladder rather than a new one":
//
//   R-emp-16 gate 1 — AGAINST THE DENSE FILL ON THE SAME MESH, entry by entry, on a fixture small
//     enough to fill both ways. The target is THE FILL'S OWN accuracy (L8c reached 5.0e-6 against an
//     independent oracle), not the kernel's — an accelerator measured against the kernel's 5.4e-3
//     would be graded on a curve.
//
//   R-emp-16 gate 2 — THE DE-EMBEDDED S of §10.7's hero, ACROSS THE BAND rather than at one point,
//     because the de-embedding divides by a₂₁² and L8d measured that amplifying a raw-S error ~22× at
//     the low-frequency end.
//
//   R-emp-17 — THE NEAR-FIELD RADIUS AND THE PROJECTION ORDER are the free parameters, and the brief
//     asks for them "in the shape L8c's own extraction-order table and L9e's ViaZNodes table already
//     use: sweep it, tabulate error against cost, and pick the default from the table rather than
//     from a reference."
//
// Every method here is Category=Benchmark. The routine tier's contribution is AimAcceleratorTests'
// structural gates, which are milliseconds.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class AimAccuracyTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private sealed record Built(PlanarMesh Mesh, PlanarFillCores Dense, PlanarFillCores Geom,
                                PlanarKernelPair K, double Omega, double SlabH,
                                IReadOnlyList<PlanarPortResolution> Ports);

    private static Built Build(PlanarProblem problem, PlanarMeshSettings mesh, double fHz,
                               double z0 = 50.0, bool dense = true)
    {
        var report = SurfaceMesher.Mesh(problem, mesh);
        var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem, z0));
        return new Built(report.Mesh,
                         dense ? PlanarFill.BuildCores(report.Mesh) : PlanarFill.BuildGeometryOnlyCores(report.Mesh),
                         PlanarFill.BuildGeometryOnlyCores(report.Mesh),
                         PlanarLineFixtures.Kernel(problem.Slab, fHz),
                         2.0 * Math.PI * fHz, problem.Slab.HeightM, ports);
    }

    /// <summary>Worst entry-wise deviation, relative to the LARGEST entry of the dense matrix rather
    /// than to each entry's own magnitude. A far-field entry can be many decades below the diagonal,
    /// and grading a far entry against itself measures the dynamic range of the Green's function
    /// instead of the accelerator — the quantity a solve actually experiences is the one scaled by the
    /// matrix's own norm, which is what L8c's own fill accuracy is reported against.</summary>
    private static (double Worst, double Rms) EntryError(Mat<Complex> z, PlanarAimOperator aim,
                                                         Complex scalarScale, Complex vectorScale)
    {
        int n = z.RowCount;
        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) norm = Math.Max(norm, z[i, j].Magnitude);

        // The accelerator never forms a matrix, so the entries are recovered a column at a time by
        // probing it with unit vectors. That IS the accelerated operator, which is the thing under
        // test — not a second path into it.
        double worst = 0, sum = 0;
        var e = new Complex[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(e);
            e[j] = Complex.One;
            var col = aim.Multiply(e);
            for (int i = 0; i < n; i++)
            {
                double d = (col[i] - z[i, j]).Magnitude;
                worst = Math.Max(worst, d);
                sum += d * d;
            }
        }
        _ = scalarScale; _ = vectorScale;
        return (worst / norm, Math.Sqrt(sum / ((double)n * n)) / norm);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-16 gate 1 + R-emp-17 — the matrix itself, swept over both free parameters
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A1_AgainstTheDenseFill_TheOrderAndRadiusTrade()
    {
        // §10.7's own FR-4 cross-section, long enough that a far field EXISTS. That last clause is
        // load-bearing and is the first thing this reports: on a structure only a few grid pitches
        // across, every pair is near, the accelerator reproduces the dense matrix to round-off, and
        // the table would be measuring nothing at all.
        // THE SHIPPING MESH, not the coarse one. Gate 11E's argument transfers verbatim: edge grading
        // is LOCAL REFINEMENT and it is what the product actually meshes with, and §11's refinement
        // ladder is precisely where a fixed near-field stencil fell apart.
        var problem = PlanarLineFixtures.Fr4Line(32e-3, 6e9);
        var b = Build(problem, PlanarLineFixtures.Shipping, 6e9);
        var z = PlanarFill.Fill(b.Dense, b.K.VectorPotential, b.K.Scalar, b.Omega);
        // P7 — the factorisation consumes its matrix in place, and `z` is the dense reference every
        // row below is measured against. The copy is the test's to take.
        var system = PlanarSystem.Wrap(z.Copy());
        int n = b.Mesh.Bases.Count;

        var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);
        var exact = system.Solve(rhs);

        Complex ss = 1.0 / (Complex.ImaginaryOne * b.Omega * EmConstants.Eps0);
        Complex vs = Complex.ImaginaryOne * b.Omega * EmConstants.Mu0;

        _out.WriteLine("");
        _out.WriteLine($"R-emp-16 gate 1 / R-emp-17 — FR-4 hero cross-section, 32 mm, 6 GHz, " +
                       $"shipping mesh, N = {n}");
        _out.WriteLine("  |ΔZ| is against the DENSE matrix, scaled by its largest entry.");
        _out.WriteLine("  |ΔI| is the SOLVED current vector against the dense LU's — the quantity an");
        _out.WriteLine("  s-parameter is read from, and the one the matrix error is amplified into.");
        _out.WriteLine("");
        _out.WriteLine("  radius is in units of the mesh's LARGEST basis support; pitch held at 0.5 of it");
        _out.WriteLine("  order  radius   near/row  near %   build s   worst |ΔZ|   rms |ΔZ|   iters   |ΔI|");

        foreach (int order in new[] { 1, 2, 3, 4 })
            foreach (double radius in new[] { 2.0, 3.0, 4.0, 6.0, 8.0 })
            {
                // P8's floor is off here (NearRadiusMinM: 0) so this sweeps the FACTOR and nothing
                // else. It does not bind on this fixture at any radius in the list — the shipping
                // mesh's smallest rung is 2 supports = 2.98 h — but a radius sweep whose low end is
                // silently clamped measures nothing, and the next fixture might be finer.
                var st = new PlanarAimSettings(ProjectionOrder: order, NearRadiusFactor: radius,
                                               GridSpacingFactor: 0.5, NearRadiusMinM: 0);
                var sw = Stopwatch.StartNew();
                var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH, st);
                sw.Stop();
                var (worst, rms) = EntryError(z, aim, ss, vs);

                string current;
                int iters;
                try
                {
                    var got = aim.Solve(rhs);
                    iters = aim.LastIterations;
                    current = $"{RelNorm(exact, got, n):E2}";
                }
                catch (InvalidOperationException)
                {
                    iters = aim.LastIterations;
                    current = "no conv";
                }

                _out.WriteLine($"  {order,5}  {radius,5:F1}s  {aim.Report.NearEntriesPerRow,9:F0}  " +
                               $"{aim.Report.NearFillFraction * 100,5:F1}%  {sw.Elapsed.TotalSeconds,7:F2}  " +
                               $"{worst,11:E2}  {rms,9:E2}  {iters,6}   {current}");
            }

        var reference = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH);
        _out.WriteLine("");
        _out.WriteLine($"  grid {reference.Report.GridNodesX} × {reference.Report.GridNodesY} nodes at " +
                       $"a pitch of {reference.Report.GridPitchM * 1e3:F3} mm; " +
                       $"dense point {PlanarSystem.ResidentBytes(n, b.Mesh.Cells.Count) / 1048576.0:F1} MB " +
                       $"resident (of which matrix {PlanarSystem.MatrixBytes(n) / 1048576.0:F1} MB), " +
                       $"accelerator {reference.Report.ResidentBytes / 1048576.0:F1} MB");
        var r = reference.Report;
        _out.WriteLine($"  build breakdown at the default: projection {r.ProjectionMs:F0} ms, grid kernel " +
                       $"{r.GridKernelMs:F0} ms, near fill {r.NearFillMs:F0} ms, preconditioner " +
                       $"{r.PreconditionerMs:F0} ms ({r.PreconditionerNonZeros:N0} near nnz -> " +
                       $"{r.FactorNonZeros:N0} in L+U) — plus " +
                       $"{r.RemainderTableMs:F0} ms of radial remainder table, WHICH THE DENSE PATH " +
                       "BUILDS TOO and is therefore excluded from every comparison below.");

        // The one thing asserted rather than reported: the table has to be measuring a far field.
        Assert.True(reference.Report.NearFillFraction < 0.95,
            "every pair on this fixture is in the near field, so the table above compares the dense " +
            "matrix with itself and says nothing about the projection");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-16 gate 2 — the de-embedded S, across the band
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A2_TheDeEmbeddedSAcrossTheBand_AgainstTheDenseSolve()
    {
        // "The gate must be taken across the band, not at one point" — the de-embedding divides by
        // a₂₁² and L8d measured ~22× amplification at the low end, so a single high-frequency point
        // is the flattering one.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        double[] freqs = [2e9, 5e9, 10e9, 15e9, 20e9];

        var swD = Stopwatch.StartNew();
        var denseRun = PlanarSolve.Run(mesh, ports, problem.Slab, freqs);
        swD.Stop();

        var aimSettings = PlanarSolveSettings.Default with
        {
            Fill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default },
        };
        var swA = Stopwatch.StartNew();
        var aimRun = PlanarSolve.Run(mesh, ports, problem.Slab, freqs, aimSettings);
        swA.Stop();

        _out.WriteLine("");
        _out.WriteLine($"R-emp-16 gate 2 — 20 mm FR-4 hero line, N = {denseRun.UnknownCount}, " +
                       $"{denseRun.StandardCount} calibration standard(s)");
        _out.WriteLine($"  dense {swD.Elapsed.TotalSeconds:F1} s, accelerated {swA.Elapsed.TotalSeconds:F1} s");
        _out.WriteLine("");
        _out.WriteLine("     f      worst |ΔS| de-embedded   worst |ΔS| raw");

        double worstDe = 0, worstRaw = 0;
        for (int p = 0; p < freqs.Length; p++)
        {
            double de = MaxAbsDiff(denseRun.Points[p].S,    aimRun.Points[p].S);
            double rw = MaxAbsDiff(denseRun.Points[p].RawS, aimRun.Points[p].RawS);
            worstDe = Math.Max(worstDe, de);
            worstRaw = Math.Max(worstRaw, rw);
            _out.WriteLine($"  {freqs[p] / 1e9,4:F0} GHz            {de,10:E2}        {rw,10:E2}");
        }

        _out.WriteLine("");
        _out.WriteLine($"  worst over the band: de-embedded {worstDe:E2}, raw {worstRaw:E2}");
        _out.WriteLine("  for scale — L8d measured its own de-embedding residual at 6.0e-3 on 1.6 mm " +
                       "FR-4 at 10 GHz, and L9d ~1e-2 on a two-level structure.");

        // The brief's own yardstick: the accelerator has to be well inside the residual the
        // de-embedding already carries, or it is the thing setting the accuracy.
        Assert.True(worstDe < 6.0e-3,
            $"the accelerated de-embedded S is {worstDe:E2} from the dense one, which is at or past " +
            "L8d's own measured de-embedding residual — the accelerator would be the error budget");
    }

    private static double RelNorm(Vec<Complex> a, Vec<Complex> b, int n)
    {
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            num += (a[i] - b[i]).Magnitude * (a[i] - b[i]).Magnitude;
            den += a[i].Magnitude * a[i].Magnitude;
        }
        return Math.Sqrt(num / den);
    }

    private static double MaxAbsDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double w = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++) w = Math.Max(w, (a[i, j] - b[i, j]).Magnitude);
        return w;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The N ladder — the cost claim, and gate 11's own iteration count on the real operator
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A3_TheNLadder_CostAndIterations()
    {
        // §11 measured iteration counts against a DENSE product with a near-field preconditioner. This
        // is the same measurement on the thing that ships: the accelerated product, the same
        // preconditioner, and the real port excitation. If the iteration count moves, the projection
        // has changed the operator's conditioning and §11's numbers do not transfer.
        _out.WriteLine("");
        _out.WriteLine("N ladder — FR-4 hero cross-section at 6 GHz, shipping mesh (edge grading ON)");
        _out.WriteLine("");
        _out.WriteLine("     L      N   near/row  near %   build s   iters  resid     solve s   dense s   |Δcurrent|    MB   dense MB");

        foreach (double lengthM in new[] { 16e-3, 32e-3, 64e-3, 128e-3, 256e-3 })
        {
            var problem = PlanarLineFixtures.Fr4Line(lengthM, 6e9);
            var b = Build(problem, PlanarLineFixtures.Shipping, 6e9);
            int n = b.Mesh.Bases.Count;

            var swD = Stopwatch.StartNew();
            var system = PlanarSystem.Build(b.Dense, b.K.VectorPotential, b.K.Scalar, b.Omega);
            var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);
            var exact = system.Solve(rhs);
            swD.Stop();

            var swB = Stopwatch.StartNew();
            var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH);
            swB.Stop();

            var swS = Stopwatch.StartNew();
            var got = aim.Solve(rhs);
            swS.Stop();

            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                num += (exact[i] - got[i]).Magnitude * (exact[i] - got[i]).Magnitude;
                den += exact[i].Magnitude * exact[i].Magnitude;
            }

            double buildS = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                           + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
            _out.WriteLine($"  {lengthM * 1e3,4:F0} mm {n,6}  {aim.Report.NearEntriesPerRow,9:F0}  " +
                           $"{aim.Report.NearFillFraction * 100,5:F1}%  {buildS,7:F2}  " +
                           $"{aim.LastIterations,6}  {aim.LastResidual,8:E1}  " +
                           $"{swS.Elapsed.TotalSeconds,7:F2}  {swD.Elapsed.TotalSeconds,7:F2}   " +
                           $"{Math.Sqrt(num / den),10:E2}  {aim.Report.ResidentBytes / 1048576.0,6:F1}  " +
                           $"{PlanarSystem.MatrixBytes(n) / 1048576.0,8:F1}");
            _ = swB;
        }

        _out.WriteLine("");
        _out.WriteLine("  'MB' is the accelerator's whole working set — sparse near field, grid " +
                       "kernels, padded FFT arrays and per-basis stencils — against the dense matrix " +
                       "alone. R17 is a MEMORY ceiling, so this pair of columns is what M5 is " +
                       "actually won on.");
        _out.WriteLine("");
        _out.WriteLine("  'dense s' is ONE fill plus ONE factorisation plus ONE back-substitution — " +
                       "i.e. what the accelerator's build + solve columns have to beat together. " +
                       "Neither column carries the radial remainder table, which both paths build.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §0's own wide conductor — where the near field stops being narrowly banded
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A4_TheWIDEConductor_SecondsOwnTwelveOhmEnd()
    {
        // Gate 11F's own argument, and it applies to the near-field FILL as much as to the
        // preconditioner: every narrow line flatters a banded near matrix in a way a real board does
        // not. §0's 12 Ω end is 6.71 mm and ~20 cells across, which is exactly why its calibration
        // standards came out larger than the DUT.
        _out.WriteLine("");
        _out.WriteLine("WIDE conductor (6.71 mm, §0's own 12 Ω end) — shipping mesh, 6 GHz");
        _out.WriteLine("");
        _out.WriteLine("     L      N  across   near/row  near %   build s  iters   solve s   dense s   |Δcurrent|");

        foreach (double lengthM in new[] { 16e-3, 32e-3 })
        {
            var problem = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 6.71e-3, lengthM, 6e9);
            var b = Build(problem, PlanarLineFixtures.Shipping, 6e9, z0: 12.0);
            int n = b.Mesh.Bases.Count;
            int across = b.Mesh.GridY.Count - 1;

            var swD = Stopwatch.StartNew();
            var system = PlanarSystem.Build(b.Dense, b.K.VectorPotential, b.K.Scalar, b.Omega);
            var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);
            var exact = system.Solve(rhs);
            swD.Stop();

            var swB = Stopwatch.StartNew();
            var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH);
            swB.Stop();

            var swS = Stopwatch.StartNew();
            var got = aim.Solve(rhs);
            swS.Stop();

            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                num += (exact[i] - got[i]).Magnitude * (exact[i] - got[i]).Magnitude;
                den += exact[i].Magnitude * exact[i].Magnitude;
            }

            double buildS = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                           + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
            _out.WriteLine($"  {lengthM * 1e3,4:F0} mm {n,6}  {across,6}   {aim.Report.NearEntriesPerRow,8:F0}  " +
                           $"{aim.Report.NearFillFraction * 100,5:F1}%  {buildS,7:F2}  " +
                           $"{aim.LastIterations,5}  {swS.Elapsed.TotalSeconds,7:F2}  " +
                           $"{swD.Elapsed.TotalSeconds,7:F2}   {Math.Sqrt(num / den),10:E2}");
            _ = swB;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The GRID PITCH — the knob R-emp-17 does not name, and the measurement is what says it matters
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A5_TheGridPitchIsTheTHIRDKnob()
    {
        // R-emp-17 names the projection order and the near-field radius. The N ladder says there is a
        // third: the auxiliary PITCH. The stencil has to resolve the KERNEL over its own width, not
        // only enclose the basis — and at a pitch of one basis support the stencil spans a
        // non-negligible fraction of a guided wavelength, across which e^{−jk₀ρ} and every surface
        // wave turn.
        //
        // The near radius is held constant IN METRES across the sweep, so this isolates the pitch
        // rather than re-measuring the radius.
        var problem = PlanarLineFixtures.Fr4Line(64e-3, 6e9);
        var b = Build(problem, PlanarLineFixtures.Shipping, 6e9);
        var z = PlanarFill.Fill(b.Dense, b.K.VectorPotential, b.K.Scalar, b.Omega);
        // P7 — the factorisation consumes its matrix in place, and `z` is the dense reference every
        // row below is measured against. The copy is the test's to take.
        var system = PlanarSystem.Wrap(z.Copy());
        int n = b.Mesh.Bases.Count;
        var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);
        var exact = system.Solve(rhs);

        double lambdaG = EmConstants.C0 / (6e9 * Math.Sqrt(0.5 * (problem.Slab.Material.EpsR + 1)));

        _out.WriteLine("");
        _out.WriteLine($"grid pitch at a FIXED near radius in metres — 64 mm FR-4 hero, N = {n}, " +
                       $"λ_g ≈ {lambdaG * 1e3:F1} mm");
        _out.WriteLine("");
        _out.WriteLine("  pitch   h (mm)  h/λ_g  stencil/λ_g  radius  near/row  near %  build s  iters   |ΔI|");

        foreach (double pitch in new[] { 1.0, 0.5, 0.25, 0.125 })
        {
            const int order = 3;
            var st = new PlanarAimSettings(ProjectionOrder: order, GridSpacingFactor: pitch,
                                           NearRadiusFactor: 6.0);
            var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH, st);
            double h = aim.Report.GridPitchM;

            string current;
            try { current = $"{RelNorm(exact, aim.Solve(rhs), n):E2}"; }
            catch (InvalidOperationException) { current = "no conv"; }

            double buildS = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                           + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
            _out.WriteLine($"  {pitch,5:F2}  {h * 1e3,7:F3}  {h / lambdaG,5:F3}  " +
                           $"{order * h / lambdaG,11:F3}  {aim.Report.NearRadiusM * 1e3,5:F1}mm " +
                           $"{aim.Report.NearEntriesPerRow,8:F0}  {aim.Report.NearFillFraction * 100,5:F1}%  " +
                           $"{buildS,7:F2}  {aim.LastIterations,5}   {current}");
        }

        _out.WriteLine("");
        _out.WriteLine("  A finer pitch costs grid nodes (and one FFT over them) but NOT near-field " +
                       "entries — the radius is held in metres, so the near column is the control.");

        // And the question that follows from it: once the pitch is resolving the kernel, how far can
        // the RADIUS — the knob that actually costs — be pulled back? This is the cheapest
        // configuration search, and it is where the shipped default comes from.
        _out.WriteLine("");
        _out.WriteLine("  pitch × radius at order 3 — the cost/accuracy frontier");
        _out.WriteLine("");
        _out.WriteLine("  pitch  radius  near/row  near %  build s  iters   |ΔI|");
        foreach (double pitch in new[] { 0.5, 0.25 })
            foreach (double radius in new[] { 2.0, 3.0, 4.0, 6.0 })
            {
                var st = new PlanarAimSettings(ProjectionOrder: 3, GridSpacingFactor: pitch,
                                               NearRadiusFactor: radius, NearRadiusMinM: 0);
                var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega, b.SlabH, st);
                string cur;
                try { cur = $"{RelNorm(exact, aim.Solve(rhs), n):E2}"; }
                catch (InvalidOperationException) { cur = "no conv"; }
                double bs = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                           + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
                _out.WriteLine($"  {pitch,5:F2}  {radius,5:F1}s  {aim.Report.NearEntriesPerRow,8:F0}  " +
                               $"{aim.Report.NearFillFraction * 100,5:F1}%  {bs,7:F2}  " +
                               $"{aim.LastIterations,5}   {cur}");
            }
    }
}
