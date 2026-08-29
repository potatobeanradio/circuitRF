// brief-em-p6-aim-frequency-independent-state.md — AIM must not rebuild its geometry at every
// frequency.
//
// Until P6 PlanarAimOperator was one object built per frequency, and its constructor rebuilt the
// stencils, the near set, the mirror index and — through a fresh PlanarEntryFill — every near
// pair's clustered-panel singular cores, which the dense path computes once per mesh (D6,
// CoreFillCount == 1). P6 splits it: PlanarAimGeometry is built once per mesh and holds all of that
// (the cores warmed over the near set at build, in PlanarEntryCores); PlanarAimOperator is built per
// frequency over it and holds only what carries ω.
//
// THE GATES ARE STRUCTURAL AND RUN ON THE COARSE FIXTURE. The split is meant to change no
// arithmetic, so the gate is bit-identity: a sweep through the shared geometry against a fresh
// per-frequency build of the pre-P6 shape (which the four-argument Build still is), on the
// accelerated product, the near exact entries and the solved current. The once-per-sweep claim is a
// COUNTER — PlanarCoreBuildCounter.AimGeometryTotal and PlanarEntryCores.CorePasses — not a
// stopwatch. Before any code changed, the whole accelerated matrix, the near entries and the solve
// at three frequencies were dumped from the committed tree and compared byte for byte against this
// tree: identical (the double-valued stencils included). The timings the brief asks for are the
// Benchmark method at the end and go to HISTORY.md §P6.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP6AimGeometryTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static readonly double[] Sweep = [2e9, 4e9, 6e9, 8e9, 10e9];

    /// <summary>Both basis directions, a real kernel, and small enough that the dense matrix exists.</summary>
    private static (PlanarMesh Mesh, PlanarFillCores Geom, IReadOnlyList<PlanarPortResolution> Ports, GroundedSlab Slab)
        Fixture(PlanarFillSettings? st = null)
    {
        var problem = PlanarLineFixtures.Fr4Line(16e-3, 10e9);
        var (m, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        return (m, PlanarFill.BuildGeometryOnlyCores(m, st), ports, problem.Slab);
    }

    private static void AssertSame(Complex a, Complex b, string what)
        => Assert.True(a.Real == b.Real && a.Imaginary == b.Imaginary, $"{what}: {a} vs {b}");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P6_1 — the shared geometry changes no bit: near entries, product, and solve, per frequency
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P6_1_ASharedGeometryReproducesThePerFrequencyBuildToTheBit()
    {
        var (mesh, geom, ports, slab) = Fixture();
        int n = mesh.Bases.Count;
        var shared = PlanarAimGeometry.Build(geom, PlanarAimSettings.Default with { KeepNearExact = true });
        long passesAfterBuild = shared.EntryCores.CorePasses;
        Assert.True(passesAfterBuild > 0, "the geometry warmed no cores, so the per-frequency gate below is vacuous");

        var rhs = PlanarExcitation.RightHandSide(n, ports[0]);
        var e   = new Complex[n];
        int compared = 0;

        foreach (double f in Sweep)
        {
            var k = PlanarLineFixtures.Kernel(slab, f);
            double w = 2 * Math.PI * f;

            // The pre-P6 shape: geometry AND operator, from scratch, at this one frequency.
            var fresh = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, w,
                                                PlanarAimSettings.Default with { KeepNearExact = true });
            // P6's shape: the operator alone, over the geometry built once above.
            var over  = PlanarAimOperator.Build(shared, k.VectorPotential, k.Scalar, w);
            Assert.Same(shared, over.Geometry);

            // ...and the per-entry fill on its own, as T1 gates it against the dense fill.
            var entry = new PlanarEntryFill(geom, k.VectorPotential, k.Scalar, w);

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (!shared.IsNear(i, j)) continue;
                    AssertSame(fresh.NearExactAt(i, j), over.NearExactAt(i, j), $"near exact [{i},{j}] at {f:E1}");
                    AssertSame(entry.At(i, j),          over.NearExactAt(i, j), $"entry.At [{i},{j}] at {f:E1}");
                    compared++;
                }

            for (int j = 0; j < n; j++)
            {
                Array.Clear(e); e[j] = Complex.One;
                var a = fresh.Multiply(e);
                var b = over.Multiply(e);
                for (int i = 0; i < n; i++) AssertSame(a[i], b[i], $"product column {j} row {i} at {f:E1}");
            }

            var xa = fresh.Solve(rhs);
            var xb = over.Solve(rhs);
            Assert.Equal(fresh.LastIterations, over.LastIterations);
            for (int i = 0; i < n; i++) AssertSame(xa[i], xb[i], $"solved current {i} at {f:E1}");
        }

        Assert.Equal(passesAfterBuild, shared.EntryCores.CorePasses);
        _out.WriteLine($"N = {n}: {Sweep.Length} frequencies, {compared:N0} near entries, {n * n * Sweep.Length:N0} " +
                       $"product entries and {n * Sweep.Length} solved currents bit-identical between the " +
                       $"per-frequency build and the shared geometry; {passesAfterBuild} core passes, all at " +
                       $"geometry build ({shared.EntryCores.ClassCount} classes).");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P6_2 — the counter: one geometry, one core pass, over a 5-point sweep through the solve context
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P6_2_OverAFivePointSweep_TheGeometryAndItsCoresAreBuiltExactlyOnce()
    {
        var counter = new PlanarCoreBuildCounter();
        var st = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default, CoreBuilds = counter };
        // The fixture's own cores carry no counter; the context's build is the one that is counted.
        var (mesh, geom, ports, slab) = Fixture();
        var ctx = new PlanarSolveContext(mesh, ports, st);

        // Lazy, like the cores: nothing is built until a frequency asks (P2/M4's own rule).
        Assert.False(ctx.AimGeometryBuilt);
        Assert.Equal(0, counter.AimGeometryTotal);

        long passesAfterFirst = -1;
        var ys = new List<Mat<Complex>>();
        foreach (double f in Sweep)
        {
            ys.Add(ctx.SolveAt(PlanarLineFixtures.Kernel(slab, f), f).Y);
            if (passesAfterFirst < 0) passesAfterFirst = ctx.AimGeometry!.EntryCores.CorePasses;
        }

        // The counter, as CoreFillCount is asserted: exactly one geometry for the mesh...
        Assert.True(ctx.AimGeometryBuilt);
        Assert.Equal(1, counter.AimGeometryTotal);
        Assert.Equal(1, counter.AimGeometryBuildsFor(mesh));
        // ...one geometry-only core build and no O(N²) pair-core build behind it...
        Assert.Equal(1, counter.Total);
        Assert.Equal(0, counter.PairCoreTotal);
        // ...and not one singular core computed after the first point. The stronger form: none after
        // the geometry itself, which the first point's count already reflects.
        Assert.True(passesAfterFirst > 0);
        Assert.Equal(passesAfterFirst, ctx.AimGeometry!.EntryCores.CorePasses);

        // The answers are the per-frequency build's, bit for bit — the same claim as P6_1, through
        // the shipped driver rather than the operator directly.
        for (int p = 0; p < Sweep.Length; p++)
        {
            var k = PlanarLineFixtures.Kernel(slab, Sweep[p]);
            var fresh = PlanarAimOperator.Build(geom, k.VectorPotential, k.Scalar, 2 * Math.PI * Sweep[p]);
            var yf = PlanarExcitation.Solve(fresh, ports).Y;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    AssertSame(yf[i, j], ys[p][i, j], $"Y[{i},{j}] at point {p}");
        }

        _out.WriteLine($"N = {mesh.Bases.Count}: {Sweep.Length} points, geometry built once " +
                       $"({ctx.AimGeometryBuildMs:F0} ms, {passesAfterFirst} core passes over " +
                       $"{ctx.AimGeometry.NearEntries:N0} near entries), core builds {counter.Total} " +
                       $"(pair-core builds {counter.PairCoreTotal}).");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P6_3 — the accounting: the report's geometry term IS the geometry, and the rest carries no stencil
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P6_3_TheReportSplitsGeometryFromPerFrequencyBytes_AndTheStencilsAreDoubles()
    {
        var (mesh, geom, _, slab) = Fixture();
        var shared = PlanarAimGeometry.Build(geom);
        var k = PlanarLineFixtures.Kernel(slab, 6e9);
        var a = PlanarAimOperator.Build(shared, k.VectorPotential, k.Scalar, 2 * Math.PI * 6e9);
        var b = PlanarAimOperator.Build(shared, k.VectorPotential, k.Scalar, 2 * Math.PI * 10e9);
        var r = a.Report;

        Assert.Equal(shared.Bytes, r.GeometryBytes);
        Assert.Equal(r.ResidentBytes - r.GeometryBytes, r.PerFrequencyBytes);
        Assert.Equal(a.Report.GeometryBytes, b.Report.GeometryBytes);

        // The geometry's own terms, reconstructed: stencils at 16 B per node (double, not Complex —
        // 32 was the pre-P6 figure), the CSR index at 4 B per near entry, the row pointer, and the
        // core store. No mirror index: the brief listed one, and it measured 18 MB at the ceiling
        // for a rebuild that costs tens of milliseconds (PlanarAimGeometry.Bytes).
        int side = r.ProjectionOrder + 1;
        long stencils = 16L * side * side * r.UnknownCount;
        long index    = 4L * r.NearEntries + 4L * (r.UnknownCount + 1);
        Assert.Equal(stencils + index + shared.EntryCores.Bytes, r.GeometryBytes);

        // The per-frequency part is exactly the ω-dependent arrays: correction, grid kernels, the
        // five padded FFT buffers, the sparse LU and its permutation. No stencil, no index.
        long perFreq = 16L * r.NearEntries
                     + 16L * r.GridNodesX * r.GridNodesY * 2
                     + 16L * r.PaddedGridNodes * 5
                     + 20L * r.FactorNonZeros + 8L * (r.UnknownCount + 1)
                     + 8L * r.UnknownCount;
        Assert.Equal(perFreq, r.PerFrequencyBytes);

        // And the report's timings say where the work went: the geometry's three phases are the
        // geometry's, and the per-frequency figure is the sum of exactly the four per-frequency phases.
        Assert.Equal(shared.TotalMs, r.GeometryMs);
        Assert.Equal(r.GridKernelMs + r.NearRemainderMs + r.CorrectionMs + r.LowerCopyMs + r.PreconditionerMs,
                     r.PerFrequencyMs);
        Assert.Equal(r.NearRemainderMs + r.CorrectionMs + r.LowerCopyMs, r.NearFillMs);

        _out.WriteLine($"N = {r.UnknownCount}: geometry {r.GeometryBytes / 1024.0:F0} KB (stencils " +
                       $"{stencils / 1024.0:F0} KB, index + mirror {index / 1024.0:F0} KB, cores " +
                       $"{shared.EntryCores.Bytes / 1024.0:F0} KB over {shared.EntryCores.ClassCount} classes), " +
                       $"per frequency {r.PerFrequencyBytes / 1024.0:F0} KB; geometry {r.GeometryMs:F0} ms " +
                       $"(projection {r.ProjectionMs:F0}, near set {r.NearSetMs:F0}, cores {r.NearCoreMs:F0}), " +
                       $"per frequency {r.PerFrequencyMs:F0} ms (grid {r.GridKernelMs:F0}, remainders " +
                       $"{r.NearRemainderMs:F0}, correction {r.CorrectionMs:F0}, lower copy {r.LowerCopyMs:F0}, " +
                       $"LU {r.PreconditionerMs:F0}).");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Benchmark — milestone 4/5: the per-frequency split at N = 552, 3,731 and 11,959, and a sweep
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P6_4_BuildSplitLadder_AndAFivePointSweep()
    {
        // The pre-P6 column of HISTORY.md §P6's table was measured on the committed tree the same
        // morning with the same fixtures (b373b2f); this prints the post-P6 column.
        _out.WriteLine("   label       N   near/row  classes | geometry ms: proj  nearset  cores | per-freq ms: grid  table  remainder  correction  copy  LU  TOTAL | MB: geometry  per-freq  resident");
        foreach (var (lenMm, fGHz, accel) in new[] { (20.0, 10.0, false), (256.0, 6.0, false), (832.0, 6.0, true) })
        {
            var problem = PlanarLineFixtures.Fr4Line(lenMm * 1e-3, fGHz * 1e9);
            var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping, accelerated: accel).Mesh;
            var geom = PlanarFill.BuildGeometryOnlyCores(mesh);
            var pair = PlanarLineFixtures.Kernel(problem.Slab, fGHz * 1e9).For(geom, PlanarFillSettings.Default.Order);
            double w = 2 * Math.PI * fGHz * 1e9;

            var g = PlanarAimGeometry.Build(geom);
            for (int rep = 0; rep < 2; rep++)
            {
                var aim = PlanarAimOperator.Build(g, pair.VectorPotential, pair.Scalar, w);
                var r = aim.Report;
                _out.WriteLine($"  {lenMm,5:F0} mm {r.UnknownCount,7} {r.NearEntriesPerRow,8:F0} {r.NearCoreClasses,8} | " +
                               $"{r.ProjectionMs,6:F0} {r.NearSetMs,8:F0} {r.NearCoreMs,6:F0} | " +
                               $"{r.GridKernelMs,6:F0} {r.RemainderTableMs,6:F0} {r.NearRemainderMs,10:F0} {r.CorrectionMs,11:F0} " +
                               $"{r.LowerCopyMs,5:F0} {r.PreconditionerMs,4:F0} {r.PerFrequencyMs,6:F0} | " +
                               $"{r.GeometryBytes / 1048576.0,8:F1} {r.PerFrequencyBytes / 1048576.0,9:F1} {r.ResidentBytes / 1048576.0,9:F1}" +
                               $"   (cores {g.EntryCores.Bytes / 1048576.0:F1} MB over {g.EntryCores.ClassCount} classes)");
            }
        }

        // The time crossover, re-stated with measured points rather than an N³ scaling: one dense
        // point (fill + LU + back-substitution, the P5 fill) against one accelerated point (build
        // over a built geometry + GMRES), at the three N a dense reference is cheap.
        _out.WriteLine("");
        _out.WriteLine("   label       N   dense fill s   dense LU+solve s   dense point s | AIM per-freq s   AIM solve s   AIM point s   iters");
        foreach (var (lenMm, fGHz) in new[] { (20.0, 10.0), (64.0, 6.0), (128.0, 6.0) })
        {
            var problem = PlanarLineFixtures.Fr4Line(lenMm * 1e-3, fGHz * 1e9);
            var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Shipping);
            int n = mesh.Bases.Count;
            var k = PlanarLineFixtures.Kernel(problem.Slab, fGHz * 1e9);
            double w = 2 * Math.PI * fGHz * 1e9;
            var rhs = PlanarExcitation.RightHandSide(n, ports[0]);

            var dense = PlanarFill.BuildCores(mesh);
            var swF = Stopwatch.StartNew();
            var system = PlanarSystem.Build(dense, k.VectorPotential, k.Scalar, w);
            double fillS = swF.Elapsed.TotalSeconds;
            swF.Restart();
            _ = system.Solve(rhs);
            double luS = swF.Elapsed.TotalSeconds;

            var geom = PlanarFill.BuildGeometryOnlyCores(mesh);
            var g = PlanarAimGeometry.Build(geom);
            var swA = Stopwatch.StartNew();
            var aim = PlanarAimOperator.Build(g, k.VectorPotential, k.Scalar, w);
            double buildS = swA.Elapsed.TotalSeconds;
            swA.Restart();
            _ = aim.Solve(rhs);
            double solveS = swA.Elapsed.TotalSeconds;

            _out.WriteLine($"  {lenMm,5:F0} mm {n,7} {fillS,13:F2} {luS,17:F2} {fillS + luS,13:F2} | " +
                           $"{buildS,14:F2} {solveS,12:F2} {buildS + solveS,12:F2} {aim.LastIterations,7}" +
                           $"   (geometry once: {g.TotalMs / 1000.0:F2} s)");
        }

        // A 5-point sweep through the shipped driver at N = 3,731: what a user's sweep pays now.
        {
            var problem = PlanarLineFixtures.Fr4Line(256e-3, 6e9);
            var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Shipping);
            var st = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default };
            var ctx = new PlanarSolveContext(mesh, ports, st);
            double[] freqs = [2e9, 3e9, 4e9, 5e9, 6e9];
            foreach (double f in freqs) _ = PlanarLineFixtures.Kernel(problem.Slab, f);   // fits out of the timing

            var sw = Stopwatch.StartNew();
            double first = 0;
            foreach (double f in freqs)
            {
                ctx.SolveAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
                if (first == 0) first = sw.Elapsed.TotalSeconds;
            }
            double total = sw.Elapsed.TotalSeconds;
            _out.WriteLine($"5-point sweep, N = {mesh.Bases.Count:N0}: {total:F2} s total — first point " +
                           $"{first:F2} s (geometry {ctx.AimGeometryBuildMs / 1000.0:F2} s of it), the other " +
                           $"four {(total - first) / 4:F2} s each; geometry built once " +
                           $"({ctx.AimGeometry!.EntryCores.CorePasses:N0} core passes).");
        }
    }
}
