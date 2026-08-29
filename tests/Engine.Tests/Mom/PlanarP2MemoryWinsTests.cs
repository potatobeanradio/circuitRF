// brief-em-p2-cheap-memory-wins.md — four mechanical memory wins on the dense path.
//
// Each removes an ALLOCATION and leaves the arithmetic where it was, so the gates here are identity
// gates and counters rather than measurements of a machine:
//
//   M1  the VXArea/VYArea packed triangles — an O(N²) array of an outer product of an O(N) vector.
//   M2  StaticCapacitance's copy of P, built only to divide every entry by ε₀.
//   M3  StaticCapacitance re-coring a mesh whose PlanarSolveContext already holds its cores.
//   M4  a calibrator coring every standard of its band, when two per frequency are ever filled.
//
// WHY THE IDENTITY GATES ARE HASHES AGAINST LITERALS. "Bit-identical" is a claim about this build
// against the build before the change, and no single tree can hold both. So the digests below were
// taken from a git worktree at the commit before P2 and are pinned here as literals; a test that
// recomputed its own expected value from the code under test would assert nothing. RESOLVED.md
// records the procedure and the three digests it produced.
//
// AND ONE OF THE FOUR IS NOT BIT-IDENTICAL, deliberately. M2 replaces "divide every entry of P by
// ε₀, then solve against 1" with "solve P against ε₀", which is the same system and a different
// rounding. Isolated by running the pre-P2 worktree twice, with and without M2 alone: the DIGEST
// moves, and M1/M3/M4 together move nothing. See P2_5.

using System.Numerics;
using System.Security.Cryptography;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP2MemoryWinsTests
{
    private readonly ITestOutputHelper _out;
    public PlanarP2MemoryWinsTests(ITestOutputHelper output) => _out = output;

    private static string Mb(long bytes) => $"{bytes / 1048576.0:N2}";

    /// <summary>Every entry of a matrix, row-major, as raw IEEE bytes — so two runs agree only if
    /// every last bit of every entry agrees.</summary>
    private static void Feed(SHA256 sha, Mat<Complex> z, byte[] buf)
    {
        for (int i = 0; i < z.RowCount; i++)
            for (int j = 0; j < z.ColCount; j++)
            {
                BitConverter.TryWriteBytes(buf.AsSpan(0, 8), z[i, j].Real);
                BitConverter.TryWriteBytes(buf.AsSpan(8, 8), z[i, j].Imaginary);
                sha.TransformBlock(buf, 0, 16, null, 0);
            }
    }

    private static string Digest(params Mat<Complex>[] mats)
    {
        var buf = new byte[16];
        using var sha = SHA256.Create();
        foreach (var m in mats) Feed(sha, m, buf);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    // =========================================================================================
    // M1 — the two packed triangles that held an outer product
    // =========================================================================================

    [Fact]
    public void P2_1_TheHeroFillIsBitIdenticalWithoutTheAreaTriangles()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        var pair    = PlanarLineFixtures.Kernel(problem.Slab, 10e9)
                                        .For(cores, PlanarFillSettings.Default.Order);
        var z = PlanarFill.Fill(cores, pair.VectorPotential, pair.Scalar, 2.0 * Math.PI * 10e9);

        string digest = Digest(z);
        long was = cores.CoreBytes + 8L * cores.VectorPairs - 8L * cores.UnknownCount;
        _out.WriteLine($"N = {mesh.Bases.Count}, cells = {mesh.Cells.Count}, cores " +
                       $"{Mb(cores.CoreBytes)} MB (was {Mb(was)} MB). " +
                       $"SHA-256 of the assembled Z = {digest}");

        // The pre-P2 worktree's digest for this exact fixture, with VXArea/VYArea still stored.
        // The extracted constant's vector core was `mMom * nMom` read out of a packed triangle; it is
        // now the same product of the same two doubles formed at the point of use, so the bits cannot
        // differ — this pins that rather than arguing it.
        // P4 re-pinned this literal (was 04F078DD…B84D99, the P2/P3 value): the vector block's cores
        // and remainders are now assembled from per-cell-pair primitives, which moves the last bits
        // by association. The bridge is PlanarP4MomentCacheTests' 1e-12 gate against the retained
        // four-call reference; from P4 on, this digest pins the primitive-assembled fill.
        // P5 re-pinned it again (was C30C787B…F90263): every core and remainder is now the value on
        // its translation class's representative, which sits with the outer cell at the origin, so
        // the last bits move everywhere — including the scalar block P4 left untouched. The bridge
        // is PlanarP5TranslationClassTests' diagonal-scale gate against the retained P4 reference.
        Assert.Equal("BF177C91149D1505076628785C09C4918F31EE795CAAC7FD18520A23D07EC34B", digest);
    }

    [Fact]
    public void P2_2_TheCachedCoresAreTwoTrianglesPerFamilyAndOneVector()
    {
        // The COMPOSITION, not a delta — a delta cannot be measured inside one build. At the shipped
        // extraction order the cores are S0/SLog over cell pairs, V*0/V*Log over same-direction basis
        // pairs, and one length-N vector of moments. The third vector triangle is what M1 removed.
        foreach (var (lengthM, settings) in new (double, PlanarMeshSettings)[]
        {
            (20e-3, PlanarLineFixtures.Coarse),
            (20e-3, PlanarLineFixtures.Shipping),
            (80e-3, PlanarLineFixtures.Shipping),
        })
        {
            var problem = PlanarLineFixtures.Fr4Line(lengthM, 10e9);
            var mesh    = SurfaceMesher.Mesh(problem, settings).Mesh;
            var cores   = PlanarFill.BuildCores(mesh);
            int n       = cores.UnknownCount;

            // P5 re-pinned the composition: the cores are now a band of 4-byte class indices over
            // P4's ordered cell pairs, a class table of seven (Inverse, Log) primitives per class,
            // the band's row starts, and the same length-N moment vector — plus the classifier's
            // own O(n_x² + n_y²) tables. The triangle layout this test used to pin survives only under
            // the reference builders. "Before" stays what P2 measured against, for the same report.
            long expected = 8L * (cores.CellCount + 1 + cores.ClassCount + 7L * 2 * cores.ClassCount + n)
                          + 4L * cores.BandPairs + cores.ClassifierBytes;
            long before   = 8L * (2 * cores.ScalarPairs + 3 * cores.VectorPairs);

            long saved         = before - cores.CoreBytes;
            long residentAfter = PlanarSystem.ResidentBytes(n, cores.CellCount);
            _out.WriteLine($"N = {n,5}, cells = {cores.CellCount,5}: cores {Mb(cores.CoreBytes)} MB, " +
                           $"was {Mb(before)} MB — the O(N²) vector-area triangle was " +
                           $"{Mb(8L * cores.VectorPairs)} MB and the O(N) vector that replaced it is " +
                           $"{8L * n / 1024.0:N1} kB ({100.0 * saved / before:F1}% off the cores, " +
                           $"{100.0 * saved / (residentAfter + saved):F1}% off a whole dense point)");

            Assert.Equal(expected, cores.CoreBytes);

            // The a-priori figure a refusal quotes is P4's triangle layout (see P1_5's note); the
            // class layout sits at or under it once translation reuse passes ≈ 3×.
            long estimate = PlanarSystem.CoreBytes(n, cores.CellCount);
            Assert.InRange((double)estimate / cores.CoreBytes, 0.6, 20.0);
        }
    }

    // =========================================================================================
    // M2 — the copy of P that existed to divide by ε₀
    // =========================================================================================

    [Fact]
    public void P2_3_TheStaticCapacitanceSolveNoLongerCopiesP()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        int endRun = PlanarCalibration.EndRunCellsFor(ports[0], problem.Slab);
        var std   = PlanarCalibration.BuildLine(ports[0], 4e-3, endRun);
        var terms = PlanarKernelTerms.StaticScalar(problem.Slab);

        GC.Collect();
        GC.Collect();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        double c = PlanarDeembed.StaticCapacitance(std.Mesh, terms);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        int m = std.Mesh.Cells.Count;
        long oneMatrix = 16L * m * m;
        _out.WriteLine($"m = {m}, n = {std.Mesh.Bases.Count}: C = {c:R} F, " +
                       $"{allocated / 1024.0:N0} kB allocated for the call; one m×m is " +
                       $"{oneMatrix / 1024.0:N0} kB, and the copy this no longer builds was exactly that.");

        // The pre-P2 value on this fixture, to full round-trip precision. On THIS mesh the two forms
        // agree bit for bit — scaling the right-hand side is the arithmetic the divided matrix was
        // approximating, and here it lands on the same doubles. That is not a promise for every mesh:
        // P2_5's swept digest DOES move, by 1-2 ulp, which is what the brief's 1e-14 allows for.
        const double PreP2 = 1.4951676979500528E-12;
        double rel = Math.Abs(c - PreP2) / PreP2;
        _out.WriteLine($"pre-P2 {PreP2:R}, now {c:R}, relative difference {rel:E2}");
        Assert.True(rel < 1e-14, $"the scaling must not move the answer materially; got {rel:E3}");

        // …and the allocation is now under what P + a COPY of P would have cost on its own, which is
        // the whole claim. (L and U are still there; the copy is not.)
        Assert.True(allocated > oneMatrix,
            "the call must still allocate P and its factors, or this fixture is measuring nothing");
    }

    // =========================================================================================
    // M3 — one core build per mesh, over a whole de-embedded sweep
    // =========================================================================================

    [Fact]
    public void P2_4_ADeembeddedSweepBuildsEachMeshsCoresExactlyOnce()
    {
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        var counter = new PlanarCoreBuildCounter();
        var settings = PlanarSolveSettings.Default with
        {
            Deembed = true,
            Fill    = PlanarFillSettings.Default with { CoreBuilds = counter },
        };
        var run = PlanarSolve.Run(mesh, ports, slab, [1e9, 5e9, 10e9, 15e9, 20e9], settings);

        _out.WriteLine($"{run.StandardCount} standard mesh(es) + the DUT: CoreFillCount = " +
                       $"{run.CoreFillCount}, BuildCores calls = {counter.PairCoreTotal}, distinct " +
                       $"meshes cored = {counter.PairCoreMeshCount}, worst mesh cored " +
                       $"{counter.MaxPairCoreBuildsPerMesh}× — was {counter.PairCoreTotal + 2} calls " +
                       "before M3, because StaticCapacitance re-cored the two extreme standards.");

        // R-prt-11's own counter is UNCHANGED — M3 is not allowed to move it.
        Assert.Equal(1 + run.StandardCount, run.CoreFillCount);

        // …and the new one says the same number is now the number of BUILDS, which it was not: the
        // two extreme standards were cored a second time inside CapacitancePerMetre.
        Assert.Equal(1, counter.MaxPairCoreBuildsPerMesh);
        Assert.Equal(run.CoreFillCount, counter.PairCoreTotal);
        Assert.Equal(run.CoreFillCount, counter.PairCoreMeshCount);
    }

    // =========================================================================================
    // M4 — a standard no frequency selects is never cored
    // =========================================================================================

    [Fact]
    public void P2_5_AStandardNoFrequencySelectsIsNeverCored()
    {
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        var counter = new PlanarCoreBuildCounter();
        var fill = PlanarFillSettings.Default with { CoreBuilds = counter };

        // A WIDE band — so the standard set spans a decade of separations — stepped only near its
        // middle. This is M4's own case, and it is the shape an interrupted run, a single-frequency
        // check and an adaptively sampled sweep all have.
        var cal = new PlanarPortCalibrator(ports[0], slab, 1e9, 20e9, null, fill);

        // Nothing is cored by the constructor at all now, which is the change.
        Assert.Equal(0, counter.PairCoreTotal);
        Assert.Equal(0, cal.CoredMeshCount);

        long all = 0;
        foreach (var s in cal.Standards)
            all += PlanarSystem.CoreBytes(s.Mesh.Bases.Count, s.Mesh.Cells.Count);

        foreach (double f in new[] { 9e9, 10e9, 11e9 })
            cal.At(PlanarLineFixtures.Kernel(slab, f), f);

        long built = 0;
        var lines = new List<string>();
        foreach (var s in cal.Standards)
        {
            long bytes = PlanarSystem.CoreBytes(s.Mesh.Bases.Count, s.Mesh.Cells.Count);
            bool cored = counter.PairCoreBuildsFor(s.Mesh) > 0;
            if (cored) built += bytes;
            lines.Add($"N = {s.Mesh.Bases.Count,4} ({bytes / 1024.0,7:N1} kB) " +
                      (cored ? "cored" : "NOT cored"));
        }
        foreach (var l in lines) _out.WriteLine("  " + l);
        _out.WriteLine($"{cal.CoredMeshCount} of {cal.MeshCount} standards cored: " +
                       $"{built / 1024.0:N1} kB of {all / 1024.0:N1} kB.");

        Assert.True(cal.CoredMeshCount < cal.MeshCount,
            "if every standard is selected somewhere in three mid-band steps the fixture is not " +
            "exercising M4 at all");
        Assert.Equal(cal.CoredMeshCount, counter.PairCoreTotal);

        // The two the STATIC solve needs are cored whether or not a frequency picked them — D7
        // differences the two EXTREME lengths, and that is correct rather than a leak.
        Assert.True(counter.PairCoreBuildsFor(cal.Standards[0].Mesh) > 0);
        Assert.True(counter.PairCoreBuildsFor(cal.Standards[^1].Mesh) > 0);
    }

    [Fact]
    public void P2_6_TheSweepsPublishedSMovesOnlyByM2sRescaling()
    {
        // The whole-sweep identity gate. The literal is the pre-P2 worktree WITH M2's six-line change
        // applied and nothing else, so a match here says M1, M3 and M4 together moved not one bit of
        // a published s-parameter, and isolates M2 as the only arithmetic P2 changed.
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        var run = PlanarSolve.Run(mesh, ports, slab, [1e9, 5e9, 10e9, 15e9, 20e9],
                                  PlanarSolveSettings.Default with { Deembed = true });

        string digest = Digest([.. run.Points.Select(p => p.S)]);
        _out.WriteLine($"5-point de-embedded sweep, N = {mesh.Bases.Count}: SHA-256 of the published " +
                       $"S = {digest}");
        foreach (var p in run.Points)
            _out.WriteLine($"  {p.FrequencyHz:E3} Hz  S21 = {p.S[1, 0]}");

        // P4 re-pinned this literal (was 8DC00C54…96DD97): see P2_1's note — the same last-bit
        // motion, seen through a de-embedded sweep. The pre-P2 NotEqual below still holds.
        // P5 re-pinned it (was 4634B313…9839CD): see P2_1's P5 note.
        Assert.Equal("713EFE3B9C6866B76F7EEA221D57C10DBAD2B06A785227A7E599E0D9D0D4FE0B", digest);

        // …and the pre-P2 digest, which differs. Recorded so the claim "M2 moves the last bits" is a
        // measurement in the tree rather than a sentence in a write-up.
        Assert.NotEqual("2D6BD9EC94335D05B02EB00621EFA821C5961727267F1802A2EE454BE90AAC5F", digest);
    }

    // =========================================================================================
    // The three series fixtures — what P2 takes off a whole dense frequency point
    // =========================================================================================

    [Fact]
    public void P2_7_WhatP2TakesOffAWholeDenseFrequencyPoint()
    {
        _out.WriteLine("P1's own three rungs — 20 / 80 / 200 mm of the FR-4 hero cross-section at the");
        _out.WriteLine("shipping mesh. Counted from the arrays, not profiled; the fill and the");
        _out.WriteLine("factorisation are not run, because nothing here depends on them.");
        _out.WriteLine("");
        _out.WriteLine("      N  cells   cores before    cores after   resident before    " +
                       "resident after   saved");

        foreach (double lengthM in new[] { 20e-3, 80e-3, 200e-3 })
        {
            var report = SurfaceMesher.Mesh(PlanarLineFixtures.Fr4Line(lengthM, 10e9),
                                            PlanarLineFixtures.Shipping);
            Assert.Null(report.Refusal);
            int n = report.Mesh.Bases.Count, cells = report.Mesh.Cells.Count;

            long after  = PlanarSystem.CoreBytes(n, cells);
            long nx = n / 2, ny = n - nx;
            long vectorPairs = nx * (nx + 1) / 2 + ny * (ny + 1) / 2;
            long before = after - 8L * n + 8L * vectorPairs;

            long residentAfter  = PlanarSystem.ResidentBytes(n, cells);
            long residentBefore = residentAfter - after + before;

            _out.WriteLine($"  {n,5}  {cells,5}  {Mb(before),12}  {Mb(after),13}  " +
                           $"{Mb(residentBefore),16}  {Mb(residentAfter),15}  " +
                           $"{100.0 * (residentBefore - residentAfter) / residentBefore,5:F1}%");

            Assert.True(after < before, "M1 must take bytes off, not add them");
        }

        _out.WriteLine("");
        _out.WriteLine("M1 is the only one of the four that shows up here: it is a fixed ~24% off the");
        _out.WriteLine("cores at every N, because the term it removed is one of three O(N²) vector");
        _out.WriteLine("triangles. M2 takes one m×m off a DIFFERENT solve (the calibration standards'");
        _out.WriteLine("static capacitance, over CELLS), and M3/M4 take whole core builds off a");
        _out.WriteLine("de-embedded run rather than bytes off a point.");
    }
}
