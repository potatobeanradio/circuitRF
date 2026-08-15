// brief-em-aim-ceiling.md — A1/A2/A3: does R17's 5,000-unknown ceiling move for the accelerated
// solver, and to what? M5 (AimAccuracyTests.cs) measured only up to N = 3,731; every claim about AIM
// above that point was an extrapolation until this file. See the brief for the full framing; this
// file only builds the ladder and reports what it measures. The decision itself, and the wiring if
// one is needed, are recorded in `src/Engine/Mom/CLAUDE.md` and `HISTORY.md`, not here.
//
// Two ladder CONSTRUCTIONS, per the brief's own trap warning — a ladder built by refining one
// geometry changes the mesh's CHARACTER as it grows, not only its size:
//   A1 — grow the LENGTH at fixed resolution (shipping mesh, cells/λ = 20). More of the same cell.
//   A2 — refine the RESOLUTION at fixed geometry (fixed 64 mm length, cells/λ swept). Same footprint,
//        finer cells throughout, including inside the edge grading fan.
// A3 asks a different question: does the accelerator work at all on a CONFORMALLY CUT mesh, which M5
// never measured (it ships off, and every M5 number above is staircased).
//
// 'MB' figures are the same APPROXIMATE-BYTES accounting R-emp-12 and the existing M5 table already
// use (PlanarSystem.MatrixBytes / PlanarAimReport.ApproximateBytes) — a counted, not profiled,
// working set, for the same reason R-emp-12's own projection was: it is what a user's machine will
// actually hold, in the terms the code itself already tracks, and it is reproducible without a
// profiler attached.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class AimCeilingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private sealed record Built(PlanarMesh Mesh, PlanarFillCores Geom,
                                PlanarKernelPair K, double Omega,
                                IReadOnlyList<PlanarPortResolution> Ports);

    // Geometry-only cores ONLY — the dense N×N cores are past R17's ceiling on most of this file's
    // rungs by construction, and PlanarFill.BuildCores refuses above it exactly like PlanarSystem.Build
    // does. Dense reference rungs build their own cores separately, in Rung, only when withDense asks.
    private static Built Build(PlanarProblem problem, PlanarMeshSettings mesh, double fHz, double z0 = 50.0)
    {
        var report = SurfaceMesher.Mesh(problem, mesh);
        var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem, z0));
        return new Built(report.Mesh,
                         PlanarFill.BuildGeometryOnlyCores(report.Mesh),
                         PlanarLineFixtures.Kernel(problem.Slab, fHz),
                         2.0 * Math.PI * fHz, ports);
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

    /// <summary>One rung: builds the AIM operator always, the dense system only when
    /// <paramref name="withDense"/> — the top rungs of each ladder are past where a dense reference is
    /// affordable, per the brief's own note that "past ~N = 8,000 there may not be one".</summary>
    private void Rung(string label, PlanarProblem problem, PlanarMeshSettings mesh, double fHz,
                      bool withDense)
    {
        var b = Build(problem, mesh, fHz);
        int n = b.Mesh.Bases.Count;

        var swB = Stopwatch.StartNew();
        var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega);
        swB.Stop();
        var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);

        string current = "—", denseS = "—", denseMB = "—";
        var swS = Stopwatch.StartNew();
        Vec<Complex>? got = null;
        int iters = -1;
        double resid = -1;
        try { got = aim.Solve(rhs); iters = aim.LastIterations; resid = aim.LastResidual; }
        catch (InvalidOperationException) { iters = aim.LastIterations; resid = aim.LastResidual; }
        swS.Stop();

        if (withDense)
        {
            // BuildCores (the O(N²) cached-triangle build) is UNTIMED here, matching the existing
            // A3 ladder convention in AimAccuracyTests — "dense s" is one fill + one factorisation +
            // one back-substitution, the three things the accelerator's build+solve have to beat.
            var denseCores = PlanarFill.BuildCores(b.Mesh);
            var swD = Stopwatch.StartNew();
            var system = PlanarSystem.Build(denseCores, b.K.VectorPotential, b.K.Scalar, b.Omega);
            var exact = system.Solve(rhs);
            swD.Stop();
            denseS  = swD.Elapsed.TotalSeconds.ToString("F2");
            denseMB = (PlanarSystem.MatrixBytes(n) / 1048576.0).ToString("F1");
            current = got is null ? "no conv" : RelNorm(exact, got.Value, n).ToString("E2");
        }

        double buildS = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                       + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
        _out.WriteLine($"  {label,10} {n,7}  {aim.Report.NearEntriesPerRow,8:F0}  " +
                       $"{aim.Report.NearFillFraction * 100,5:F1}%  {buildS,7:F2}  {iters,6}  " +
                       $"{resid,8:E1}  {swS.Elapsed.TotalSeconds,7:F2}  {denseS,7}  {current,10}  " +
                       $"{aim.Report.ApproximateBytes / 1048576.0,6:F1}  {denseMB,8}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A1 — grow the LENGTH at fixed resolution (shipping mesh)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A1_LadderByLength_ShippingMeshToAndPastTheCeiling()
    {
        _out.WriteLine("");
        _out.WriteLine("A1a — LENGTH ladder, FR-4 hero cross-section at 6 GHz, shipping mesh " +
                       "(cells/λ = 20, edge grading ON, staircase)");
        _out.WriteLine("");
        _out.WriteLine("      label       N  near/row  near %   build s   iters   resid    solve s  " +
                       "dense s     |Δcurrent|      MB   dense MB");

        // Probed once, mesh-only, before committing this file: 256->3,731 (M5's own top rung) through
        // 896->12,894 mm. R17's OWN ceiling is a hard refusal in PlanarFill.GuardCeiling/
        // PlanarSystem.GuardCeiling, not merely a slow one — dense CANNOT be asked to run past
        // N = 5,000 through the shipped API at all, so "the top of A1's ladder where a dense reference
        // exists" (the brief's own §A2) is exactly the ceiling itself, not the ~8,000 the brief guessed
        // at. Dense runs at 256/320 mm (N = 3,731 / 4,649); everything past it is AIM-only.
        (double LenMm, bool Dense)[] rungs =
        [
            (256, true), (320, true), (384, false), (448, false), (512, false), (640, false),
            (768, false), (896, false),
        ];

        foreach (var (lenMm, dense) in rungs)
        {
            var problem = PlanarLineFixtures.Fr4Line(lenMm * 1e-3, 6e9);
            Rung($"{lenMm:F0} mm", problem, PlanarLineFixtures.Shipping, 6e9, dense);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A2 — refine the RESOLUTION at fixed geometry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A1_LadderByResolution_FixedGeometryToAndPastTheCeiling()
    {
        _out.WriteLine("");
        _out.WriteLine("A1b — RESOLUTION ladder, FR-4 hero cross-section at 6 GHz, FIXED 64 mm length " +
                       "(edge grading ON, staircase) — the trap check: does the length ladder's story " +
                       "hold when the mesh's CHARACTER changes instead of its footprint?");
        _out.WriteLine("");
        _out.WriteLine("      label       N  near/row  near %   build s   iters   resid    solve s  " +
                       "dense s     |Δcurrent|      MB   dense MB");

        // Probed once, mesh-only: cells/λ 20->994 through 140->13,967 at L = 64 mm fixed. Same hard
        // limit as the length ladder: dense refuses above N = 5,000 by construction, so it runs at
        // cells/λ = 20/40/60 (N = 994/1,895/3,454) and every rung past that is AIM-only.
        (int Cpl, bool Dense)[] rungs =
        [
            (20, true), (40, true), (60, true), (80, false), (100, false), (120, false), (140, false),
        ];

        foreach (var (cpl, dense) in rungs)
        {
            var problem = PlanarLineFixtures.Fr4Line(64e-3, 6e9);
            var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpl, EdgeMesh: true,
                                              EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Staircase);
            Rung($"c/λ={cpl}", problem, mesh, 6e9, dense);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A3 — the CONFORMAL (cut) single-level mesh, which M5 never measured
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A3_ConformalCutMesh_AimAgainstDense()
    {
        _out.WriteLine("");
        _out.WriteLine("A3 — a CONFORMALLY CUT single-level mesh (BoundaryCells.Conformal), which " +
                       "M5's own ladder never built. A straight-flanked taper, 1.0 mm -> 6.71 mm, " +
                       "so every cell the flank crosses is genuinely cut rather than staircased.");
        _out.WriteLine("");
        _out.WriteLine("      label       N  cut cells  near/row  near %   build s   iters   resid   " +
                       " solve s   dense s     |Δcurrent|");

        foreach (double lenMm in new[] { 16.0, 32, 64, 96 })
        {
            var problem = PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 1.0e-3, 6.71e-3,
                                                    lenMm * 1e-3, 6e9);
            var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: true,
                                              EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Conformal);
            var report = SurfaceMesher.Mesh(problem, mesh);
            var b = Build(problem, mesh, 6e9);
            int n = b.Mesh.Bases.Count;

            var aim = PlanarAimOperator.Build(b.Geom, b.K.VectorPotential, b.K.Scalar, b.Omega);
            var rhs = PlanarExcitation.RightHandSide(n, b.Ports[0]);

            var denseCores = PlanarFill.BuildCores(b.Mesh);
            var swD = Stopwatch.StartNew();
            var system = PlanarSystem.Build(denseCores, b.K.VectorPotential, b.K.Scalar, b.Omega);
            var exact = system.Solve(rhs);
            swD.Stop();

            string current;
            var swS = Stopwatch.StartNew();
            try { current = RelNorm(exact, aim.Solve(rhs), n).ToString("E2"); }
            catch (InvalidOperationException) { current = "no conv"; }
            swS.Stop();

            double buildS = (aim.Report.ProjectionMs + aim.Report.GridKernelMs
                           + aim.Report.NearFillMs + aim.Report.PreconditionerMs) / 1000.0;
            _out.WriteLine($"  {lenMm,7:F0} mm {n,7}  {report.CutCellCount,9}  " +
                           $"{aim.Report.NearEntriesPerRow,8:F0}  {aim.Report.NearFillFraction * 100,5:F1}%  " +
                           $"{buildS,7:F2}  {aim.LastIterations,6}  {aim.LastResidual,8:E1}  " +
                           $"{swS.Elapsed.TotalSeconds,7:F2}  {swD.Elapsed.TotalSeconds,7:F2}   {current,10}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A2 (accuracy) — the de-embedded S at the top of the ladder where a dense reference exists
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void A2_DeEmbeddedS_AtTheTopOfTheReachableLadder()
    {
        // R-emp-16 gate 2's own construction, at a line long/fine enough to sit well above M5's
        // N = 94 gate — a calibrated point near the ceiling itself rather than a token line, which is
        // the scale this brief exists to answer for.
        var problem = PlanarLineFixtures.Fr4Line(30e-3, 10e9);
        var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 60, EdgeMesh: true,
                                          EdgeCells: 3, BoundaryCells: PlanarBoundaryCells.Staircase);
        var (builtMesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, mesh);
        double[] freqs = [2e9, 5e9, 10e9];

        var swD = Stopwatch.StartNew();
        var denseRun = PlanarSolve.Run(builtMesh, ports, problem.Slab, freqs);
        swD.Stop();

        var aimSettings = PlanarSolveSettings.Default with
        {
            Fill = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default },
        };
        var swA = Stopwatch.StartNew();
        var aimRun = PlanarSolve.Run(builtMesh, ports, problem.Slab, freqs, aimSettings);
        swA.Stop();

        _out.WriteLine("");
        _out.WriteLine($"A2 — 30 mm FR-4 line, N = {denseRun.UnknownCount} (near R17's ceiling), " +
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
                       "FR-4 at 10 GHz.");

        Assert.True(worstDe < 6.0e-3,
            $"the accelerated de-embedded S is {worstDe:E2} from the dense one, at or past L8d's own " +
            "measured de-embedding residual — the accelerator would be the error budget");
    }

    private static double MaxAbsDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double w = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++) w = Math.Max(w, (a[i, j] - b[i, j]).Magnitude);
        return w;
    }
}
