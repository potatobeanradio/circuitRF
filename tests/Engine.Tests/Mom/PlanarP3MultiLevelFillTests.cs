// brief-em-p3-multilevel-fill-scalability.md — the multi-level fill must scale like the single-level
// one.
//
// FillMultiLevel was written for correctness against the single-level reduction, and it did per-ENTRY
// work inside the parallel row loop that Fill deliberately hoists: a kernel-set lookup under a lock
// plus a fresh PlanarKernelTerms allocation per cell pair and per basis pair, a remainder-cache lock
// per pair, a four-element array and two RampHalves per horizontal entry, and a locked dictionary
// lookup per via entry. P3 hoists all of it: every (kernel, level, level) pairing and every (span,
// span) / (span, level) combination the mesh contains is resolved ONCE before ForRows, into small
// arrays indexed by layer, and the inner loops read them. The arithmetic per entry is unchanged —
// only WHEN it is looked up changes — so the gate is bit-identity, pinned as digests.
//
// WHY THE DIGESTS ARE LITERALS. "Bit-identical" compares this build to the build before the change,
// and no single tree holds both. The literals below were printed by these same tests against the
// pre-P3 working tree (P1 and P2 applied, P3 not) and pasted in; a test that recomputed its own
// expected value from the code under test would assert nothing. RESOLVED.md records the procedure.
//
// The strided-write half of the brief (milestone 4) is gated the same way on the SINGLE-level fill:
// P2_1's hero digest is reused as the literal there, since that fill's answer must not move either.
//
// P4 RE-PINNED EVERY LITERAL BELOW. The vector block's cores are now assembled from per-cell-pair
// primitives (PlanarFill's P4 header), which the multi-level fill reads through HorizontalVectorEntry,
// so every digest moved in its last bits. The bridge from the P3 values is PlanarP4MomentCacheTests'
// 1e-12 gate against the retained four-call reference; the literals here were re-printed by these
// same tests on the P4 tree and pin the hoist AND the primitive assembly from here on.
//
// The wall-clock scaling tables — the number the brief exists to move — are Category=Benchmark and
// go to HISTORY.md; the routine gate for "nothing is allocated per entry" is an allocation COUNTER
// on a serial fill, which is deterministic where a stopwatch is not.

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP3MultiLevelFillTests
{
    private readonly ITestOutputHelper _out;
    public PlanarP3MultiLevelFillTests(ITestOutputHelper output) => _out = output;

    // =========================================================================================
    // Fixtures — the two the brief names, plus the FR-4 hero with a via for a second stack
    // =========================================================================================

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        PlanarLineFixtures.Rect(x0, y0, x1, y1);

    /// <summary>ViaPhysicsTests' own two-level MMIC line with one via — the brief's first fixture.</summary>
    private static PlanarProblem TwoLevel(double fHz, bool withVia, double lengthM = 400e-6)
    {
        var stack = LayerStacks.MmicTwoLevel;
        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 2e-6,
                                             stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 3e-6,
                                             stack.TopZ);
        var vias = withVia
            ? new[] { new PlanarVia(0, 1, [Rect(0.45 * lengthM, 30e-6, 0.55 * lengthM, 70e-6)], 4.1e7) }
            : [];
        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    /// <summary>MultiLevelPortTests' FR-4 hero on two levels with vias — a second stack, a second
    /// mesh character (a 20 mm board at 10 GHz), and several vias sharing one span.</summary>
    private static PlanarProblem Fr4HeroWithVias(double fHz, params double[] viaCentresM)
    {
        var fr4   = new EmMaterial(4.4, 0.02);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(1.5e-3, fr4), new MediumLayer(0.1e-3, fr4)], Termination.Air);
        const double w = 2.9e-3, len = 20e-3;
        var lower = new PlanarConductorLayer("L1", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("L2", [Rect(0, 0, len, w)], 5.8e7, 35e-6, stack.TopZ);
        var vias = viaCentresM
            .Select(cx => new PlanarVia(0, 1,
                [Rect(cx - 0.25e-3, 0.5 * w - 0.25e-3, cx + 0.25e-3, 0.5 * w + 0.25e-3)], 5.8e7))
            .ToArray();
        return new PlanarProblem([lower, upper], new GroundedSlab(1.6e-3, fr4), fHz, null, stack, vias);
    }

    private static (PlanarMesh Mesh, PlanarFillCores Cores, PlanarKernelSet Set, PlanarLevels Levels)
        Prepare(PlanarProblem problem, PlanarFillSettings? st = null)
    {
        var mesh   = SurfaceMesher.Mesh(problem).Mesh;
        var cores  = PlanarFill.BuildCores(mesh, st);
        var set    = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, problem.MaxFrequencyHz),
                                         (st ?? PlanarFillSettings.Default).Order).For(cores);
        return (mesh, cores, set, PlanarLevels.From(problem));
    }

    // =========================================================================================
    // Digests
    // =========================================================================================

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

    private static string Digest(Mat<Complex> z)
    {
        var buf = new byte[16];
        using var sha = SHA256.Create();
        Feed(sha, z, buf);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>The pre-P3 digests. The ROUTINE pair is the two-level MMIC line at 100 µm — with
    /// and without its via — which fills in about a second; the four larger fixtures (the brief's
    /// own 400 µm line, the FR-4 hero on two levels with three vias, and Order = Linear, which reaches
    /// the ∫∫r cores and the Linear coefficient on every arm) are ~80 s together and sit in the
    /// Benchmark tier. Every literal was printed by this same test against the pre-P3 tree.
    /// P5 (2026-08-29) re-pinned all six (the P4 values were C2A6E966…, 2D4D1583…, 082E71E0…,
    /// 270D605C…, A605A4A6…, 6026DB2D…): the multi-level fill reads every scalar and horizontal
    /// vector core through the translation-class table now, and a class value is the integral on the
    /// class's representative rather than on the pair itself, so the last bits move. The bridge is
    /// PlanarP5TranslationClassTests' diagonal-scale gate against the retained P4 reference.</summary>
    private static readonly (string Label, Func<PlanarProblem> Problem, PlanarExtractionOrder Order, string Expected)[]
        RoutineDigests =
    [
        ("two-level MMIC line + via, 100 µm, 10 GHz",  () => TwoLevel(10e9, withVia: true,  lengthM: 100e-6), PlanarExtractionOrder.Constant, "3FAA0CCCF7F5189BCD9AA18BA312ADD8F7F997B3716D488F06DA193E241129FE"),
        ("two-level MMIC line, no via, 100 µm, 10 GHz", () => TwoLevel(10e9, withVia: false, lengthM: 100e-6), PlanarExtractionOrder.Constant, "22C9EDB770DDC8EFBE979C21E6BD2A2529B9797C2FB1DBD51DF506ECB706CCBF"),
    ];

    private static readonly (string Label, Func<PlanarProblem> Problem, PlanarExtractionOrder Order, string Expected)[]
        BenchmarkDigests =
    [
        ("two-level MMIC line + via, 10 GHz",  () => TwoLevel(10e9, withVia: true),  PlanarExtractionOrder.Constant, "629148648F15C9522F4AF00410AF312E1D517072BDB1EC6F7ECE0C32D2EBA7DE"),
        ("two-level MMIC line, no via, 10 GHz", () => TwoLevel(10e9, withVia: false), PlanarExtractionOrder.Constant, "0283243589CEDBF8EF0799F270D4D19887926B31824546A54618D153CD4A1B4D"),
        ("FR-4 hero on two levels, 3 vias",     () => Fr4HeroWithVias(10e9, 5e-3, 10e-3, 15e-3), PlanarExtractionOrder.Constant, "821616096A0FC7D128648A49FC120777FB28469DCCE7201A457C01828E29CFA7"),
        ("two-level MMIC line + via, Linear",   () => TwoLevel(10e9, withVia: true),  PlanarExtractionOrder.Linear,   "DDF78C41FED1264EECEFC2F62213BF3A27D3EC808AE6E962EE27597FDCA94776"),
    ];

    private void AssertDigests((string Label, Func<PlanarProblem> Problem, PlanarExtractionOrder Order, string Expected)[] table)
    {
        // Every digest is printed BEFORE any is asserted, so a failure reports all of them.
        var got = new List<(string Expected, string Digest)>();
        foreach (var (label, make, order, expected) in table)
        {
            var problem = make();
            var st      = PlanarFillSettings.Default with { Order = order };
            var (mesh, cores, set, levels) = Prepare(problem, st);
            var z = PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * problem.MaxFrequencyHz);

            string digest = Digest(z);
            int vertical  = mesh.Bases.Count(b => b.Direction == PlanarBasisDirection.Z);
            _out.WriteLine($"{label}: N = {mesh.Bases.Count} ({vertical} vertical), cells = " +
                           $"{mesh.Cells.Count}, {set.FitCount} fits. SHA-256 = {digest}");
            got.Add((expected, digest));
        }
        foreach (var (expected, digest) in got) Assert.Equal(expected, digest);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // 9 s / 48 s / 24 s under full-suite load (P4, 2026-08-29): the via fixture's mixed block
    public void P3_1_TheMultiLevelFillIsBitIdenticalWithEveryPairingHoisted() => AssertDigests(RoutineDigests);

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P3_1b_TheMultiLevelFillIsBitIdentical_OnTheBriefsOwnFixtures() => AssertDigests(BenchmarkDigests);

    [Fact]
    public void P3_2_TheSingleLevelFillIsBitIdenticalWithTheCacheFriendlyWrites()
    {
        // P2_1's own fixture and P2_1's own literal: the hero at the shipping mesh. Milestone 4
        // changes which triangle is written first and in what order the mirror copies; the bits of
        // every entry must not move, and this is the same claim P2 pinned, re-asserted here so that
        // a reader of THIS file sees both halves of the brief gated in one place.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        var pair    = PlanarLineFixtures.Kernel(problem.Slab, 10e9)
                                        .For(cores, PlanarFillSettings.Default.Order);
        var z = PlanarFill.Fill(cores, pair.VectorPotential, pair.Scalar, 2.0 * Math.PI * 10e9);

        string digest = Digest(z);
        _out.WriteLine($"N = {mesh.Bases.Count}: SHA-256 of the assembled Z = {digest}");
        // P5 re-pinned this literal (was C30C787B…F90263, the P4 value) — see P2_1's P5 note.
        Assert.Equal("BF177C91149D1505076628785C09C4918F31EE795CAAC7FD18520A23D07EC34B", digest);

        // And the matrix is exactly symmetric — the mirror is a copy, whichever direction it runs.
        for (int i = 0; i < z.RowCount; i++)
            for (int j = 0; j < i; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // 9 s / 48 s / 24 s under full-suite load (P4, 2026-08-29): the via fixture's mixed block
    public void P3_3_TheMultiLevelFillIsBitIdenticalAtEveryCoreCap()
    {
        // R-emp-8's own claim, on the multi-level path: the cap changes no bit. The hoisted tables
        // are built before ForRows and are read-only inside it, so there is nothing for a thread
        // count to race on — asserted rather than argued.
        var problem = TwoLevel(10e9, withVia: true, lengthM: 100e-6);
        string? reference = null;
        foreach (int? cap in new int?[] { 1, 2, null })
        {
            var st = PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap };
            var (_, cores, set, levels) = Prepare(problem, st);
            var z = PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * 10e9);
            string d = Digest(z);
            reference ??= d;
            Assert.Equal(reference, d);
        }
        Assert.Equal(RoutineDigests[0].Expected, reference);
    }

    // =========================================================================================
    // The structural gate — nothing per entry
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // 9 s / 48 s / 24 s under full-suite load (P4, 2026-08-29): the via fixture's mixed block
    public void P3_4_TheMultiLevelFillAllocatesNothingPerEntry()
    {
        // The pre-P3 loop allocated a PlanarKernelTerms per CELL pair and per BASIS pair (set.Get
        // ... .With) and a four-element tuple array per horizontal entry: O(pairs) objects per fill,
        // ~207 bytes a pair on this fixture. What a fill may legitimately allocate is the two
        // matrices, ONE radial remainder table per (kernel, level, level) pairing — each capped at
        // MaxTableSamples complex samples however large the mesh — and O(N) arrays. The allowance
        // below is exactly that, computed from the settings rather than guessed, and the pre-P3 code
        // fails it by a factor of ~3 on this fixture. Measured on a SERIAL fill, where
        // GC.GetAllocatedBytesForCurrentThread is exact and other test classes cannot pollute it.
        //
        // The no-via line is the fixture on purpose: a ẑẑ entry's closed-form prism integral
        // (ViaZIntegral.PrismCore) allocates its own node arrays — ~5 MB per entry, O(N_z²) entries,
        // out of P3's scope — and would swamp the O(N²) question this test asks.
        var st = PlanarFillSettings.Default with { Parallel = false };
        var problem = TwoLevel(10e9, withVia: false, lengthM: 100e-6);
        var (mesh, cores, set, levels) = Prepare(problem, st);
        double omega = 2 * Math.PI * 10e9;

        PlanarFill.FillMultiLevel(cores, set, levels, omega);        // warm: fits, Legendre cache

        long before = GC.GetAllocatedBytesForCurrentThread();
        var z = PlanarFill.FillMultiLevel(cores, set, levels, omega);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(mesh.Bases.Count, z.RowCount);

        int n = mesh.Bases.Count, m = mesh.Cells.Count;
        long matrices = 16L * ((long)n * n + (long)m * m);
        long pairs    = (long)n * (n + 1) / 2 + (long)m * (m + 1) / 2;
        long extra    = allocated - matrices;

        int layers    = levels.Z.Count;
        int pairings  = 2 * layers * (layers + 1) / 2;                   // G_q and G_A, per (level, level)
        long tables   = 16L * (st.MaxTableSamples + 8) * pairings;       // one capped table each
        long allowance = tables + 64L * (n + m) + (1 << 20);

        _out.WriteLine($"N = {n}, cells = {m}, {pairs:N0} pairs, {layers} levels: a serial multi-level " +
                       $"fill allocated {allocated:N0} bytes — the two matrices are {matrices:N0}, leaving " +
                       $"{extra:N0} ({(double)extra / pairs:F1} bytes per pair) against an allowance of " +
                       $"{allowance:N0} for {pairings} radial tables plus O(N). Pre-P3: 207 bytes per pair on this fixture.");

        Assert.True(extra <= allowance,
            $"the multi-level fill allocated {extra:N0} bytes beyond its two matrices, over the " +
            $"{allowance:N0} its per-pairing tables and O(N) arrays can account for — something is " +
            "being allocated per entry again");
    }

    // =========================================================================================
    // Scaling — Category=Benchmark, tables in HISTORY.md
    // =========================================================================================

    private double TimeMultiLevel(PlanarProblem problem, int? cap, out int n, out int vertical)
    {
        var st = PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap };
        var (mesh, cores, set, levels) = Prepare(problem, st);
        n = mesh.Bases.Count;
        vertical = mesh.Bases.Count(b => b.Direction == PlanarBasisDirection.Z);
        PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * problem.MaxFrequencyHz);   // fits + tables
        var sw = Stopwatch.StartNew();
        var z = PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * problem.MaxFrequencyHz);
        sw.Stop();
        Assert.Equal(n, z.RowCount);
        return sw.Elapsed.TotalSeconds;
    }

    private void ScalingTable(string label, Func<int?, double> time, int n, string extra)
    {
        double t1 = time(1);
        _out.WriteLine($"{label}: N = {n}{extra}, {Environment.ProcessorCount} core(s)");
        _out.WriteLine($"  cap  1 : {t1,7:F2} s    1.00x");
        foreach (int c in new[] { 2, 4, 10 })
        {
            double t = time(c);
            _out.WriteLine($"  cap {c,2} : {t,7:F2} s   {t1 / t,5:F2}x   {100.0 * (t1 / t) / c,3:F0}% efficiency");
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P3_5_MultiLevelFillScaling_AtCaps1_2_4_10()
    {
        foreach (var (label, problem) in new (string, PlanarProblem)[]
        {
            ("two-level MMIC line + via, 10 GHz",     TwoLevel(10e9, withVia: true)),
            ("two-level MMIC line, no via, 10 GHz",   TwoLevel(10e9, withVia: false)),
            ("FR-4 hero on two levels, 3 vias, 10 GHz", Fr4HeroWithVias(10e9, 5e-3, 10e-3, 15e-3)),
        })
        {
            int n = 0, vertical = 0;
            TimeMultiLevel(problem, 2, out n, out vertical);       // warm
            ScalingTable(label, cap => TimeMultiLevel(problem, cap, out n, out vertical), n,
                         $" ({vertical} vertical)");
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P3_6_SingleLevelFillScaling_256mmLine_AtCaps1_2_4_10()
    {
        // HISTORY §12's top rung: the 256 mm FR-4 line at 6 GHz, N = 3,731 — where CLAUDE.md §6's
        // 98% / 81% / 53% fall-off was measured, and where milestone 4's strided writes would show.
        var problem = PlanarLineFixtures.Fr4Line(256e-3, 6e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var pair0   = PlanarLineFixtures.Kernel(problem.Slab, 6e9);
        double w    = 2 * Math.PI * 6e9;

        double Time(int? cap)
        {
            var cores = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap });
            var pair  = pair0.For(cores, PlanarFillSettings.Default.Order);
            var sw = Stopwatch.StartNew();
            var z = PlanarFill.Fill(cores, pair.VectorPotential, pair.Scalar, w);
            sw.Stop();
            Assert.Equal(mesh.Bases.Count, z.RowCount);
            return sw.Elapsed.TotalSeconds;
        }

        Time(2);   // warm
        ScalingTable("256 mm FR-4 line, 6 GHz, single-level Fill", Time, mesh.Bases.Count, "");
    }
}
