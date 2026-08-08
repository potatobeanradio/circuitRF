using System.Diagnostics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// M1 — the measurement that decides whether WB-C is possible (brief-wbond-wba §3).
///
/// <para>The design note's kernel costs were taken on <b>flat scalar arguments in a tight loop</b> —
/// no object graph, no polyline indirection, no block-structured matrix. These measurements are the
/// same quantities taken through the real <see cref="WireMesh"/> / <see cref="InductanceMatrix"/>
/// path, which is the only version that predicts anything about the editor.</para>
///
/// <para><b>Reference figures to beat</b> (design note §4.1): 41.7 ns/pair skew, 28.4 ns/pair
/// parallel, ~0.54 s cold fill at 600 wires, ~3.6 ms for a one-wire refresh.</para>
///
/// <para>Tagged <c>Benchmark</c>: the 600-wire cold fill alone is ~0.5 s and this class runs it
/// several times, and a timing measurement sharing a run with the rest of the suite reads more than
/// twice as slow (the L8d/L9d lesson). <b>Take these alone:</b>
/// <c>dotnet test tests/WBond.Tests --settings circuitrf.benchmark.runsettings</c>.</para>
/// </summary>
[Trait("Category", "Benchmark")]
public class M1CostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public M1CostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>M1 measurement 1 — ns per filament pair through the real accessors.</summary>
    [Fact]
    public void M1_1_KernelThroughput_ThroughRealFilaments()
    {
        var design = TestDesigns.PowerAmplifier();
        var mesh = WireMesh.Build(design);

        // Two filaments from different wires, far enough apart to be genuinely skew, and two that
        // are parallel — the same shapes the fill actually meets.
        var skewA = mesh.Filaments[1];
        var skewB = mesh.Filaments[mesh.WireStart[400] + 3];
        var parA = mesh.Filaments[0];
        var parB = mesh.Filaments[mesh.WireStart[1]];

        _out.WriteLine($"mesh: {mesh.WireCount} wires, {mesh.FilamentCount} filaments, images={mesh.HasImages}");

        double skewNs = TimeKernel(in skewA, in skewB);
        double parNs = TimeKernel(in parA, in parB);

        _out.WriteLine($"skew kernel:     {skewNs,7:F1} ns/pair   (reference 41.7)");
        _out.WriteLine($"parallel kernel: {parNs,7:F1} ns/pair   (reference 28.4)");

        // WHERE THE GAP AGAINST THE REFERENCE COMES FROM — measured, not asserted.
        //
        // The design note's 41.7 ns was taken on a function that received d, eps, mu and nu as
        // ARGUMENTS. Grover.Mutual must derive all of them: a dot product, a 2x2 solve for the
        // common perpendicular, the closest-approach distance, and the GMD clamp. Timing
        // Grover.Skew directly with those quantities precomputed isolates that setup cost, so the
        // comparison is like-for-like instead of an apples-to-oranges regression.
        double cosEps = skewA.Ux * skewB.Ux + skewA.Uy * skewB.Uy + skewA.Uz * skewB.Uz;
        double sinEps = Math.Sqrt(1.0 - cosEps * cosEps);
        double bodyNs = TimeSkewBody(in skewA, in skewB, cosEps, sinEps);

        _out.WriteLine($"  of which formula body: {bodyNs,6:F1} ns   (comparable to the 41.7 reference)");
        _out.WriteLine($"  geometry setup:        {skewNs - bodyNs,6:F1} ns   (work the reference did not do)");

        Assert.True(skewNs > 0 && parNs > 0 && bodyNs > 0);
    }

    private static double TimeSkewBody(in Filament a, in Filament b, double cosEps, double sinEps)
    {
        const int warm = 200_000;
        const int n = 4_000_000;

        double sink = 0;
        for (int i = 0; i < warm; i++) sink += Grover.Skew(in a, in b, cosEps, sinEps);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) sink += Grover.Skew(in a, in b, cosEps, sinEps);
        sw.Stop();

        GC.KeepAlive(sink);
        return sw.Elapsed.TotalNanoseconds / n;
    }

    private static double TimeKernel(in Filament a, in Filament b)
    {
        const int warm = 200_000;
        const int n = 4_000_000;

        double sink = 0;
        for (int i = 0; i < warm; i++) sink += Grover.Mutual(in a, in b);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) sink += Grover.Mutual(in a, in b);
        sw.Stop();

        GC.KeepAlive(sink);
        return sw.Elapsed.TotalNanoseconds / n;
    }

    /// <summary>
    /// M1 measurements 2 and 3 — the cold fill and the one-wire incremental refresh, at the stated
    /// worst case of 600 wires.
    /// </summary>
    [Fact]
    public void M1_2_ColdFillAndIncrementalRefresh_At600Wires()
    {
        var design = TestDesigns.PowerAmplifier();
        var mesh = WireMesh.Build(design);

        _out.WriteLine($"600-wire design: {mesh.FilamentCount} filaments, " +
                       $"{(long)mesh.FilamentCount * mesh.FilamentCount:N0} ordered pairs (x2 for images)");

        // Cold fill, single-threaded — the figure directly comparable to the design note's 0.54 s.
        var sw = Stopwatch.StartNew();
        var l = InductanceMatrix.Fill(mesh);
        sw.Stop();
        double coldSerial = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var lPar = InductanceMatrix.Fill(mesh, parallel: true);
        sw.Stop();
        double coldParallel = sw.Elapsed.TotalSeconds;

        // One-wire refresh — the drag path's fill half.
        const int reps = 20;
        sw.Restart();
        for (int i = 0; i < reps; i++) l.RefreshWire(mesh, 300 + i);
        sw.Stop();
        double refreshMs = sw.Elapsed.TotalMilliseconds / reps;

        _out.WriteLine($"cold fill, serial:    {coldSerial,7:F3} s     (reference 0.54)");
        _out.WriteLine($"cold fill, parallel:  {coldParallel,7:F3} s     ({coldSerial / coldParallel:F1}x on " +
                       $"{Environment.ProcessorCount} logical cores)");
        _out.WriteLine($"one-wire refresh:     {refreshMs,7:F2} ms    (reference 3.6)");

        // The parallel fill must agree with the serial one BIT-FOR-BIT across every entry — it is the
        // same arithmetic in a different order over independent blocks, so any difference at all is a
        // data race, not a rounding difference.
        for (int i = 0; i < mesh.WireCount; i++)
            for (int j = 0; j < mesh.WireCount; j++)
                Assert.Equal(l[i, j], lPar[i, j], 0.0);

        Assert.True(coldSerial > 0);
    }

    /// <summary>
    /// The scaling check the design note's incremental argument rests on: refreshing one wire is
    /// O(N) blocks against the cold fill's O(N²), so the ratio must grow roughly linearly with N.
    /// </summary>
    [Fact]
    public void M1_3_IncrementalRefresh_ScalesLinearlyAgainstQuadraticFill()
    {
        foreach (int n in new[] { 120, 300, 600 })
        {
            var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: n, arrayCount: 6));

            var sw = Stopwatch.StartNew();
            var l = InductanceMatrix.Fill(mesh);
            sw.Stop();
            double fillMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (int i = 0; i < 20; i++) l.RefreshWire(mesh, n / 2);
            sw.Stop();
            double refreshMs = sw.Elapsed.TotalMilliseconds / 20;

            _out.WriteLine($"N={n,4}  cold fill {fillMs,9:F1} ms   one-wire refresh {refreshMs,7:F3} ms   " +
                           $"ratio {fillMs / refreshMs,7:F0}x");
        }
    }

    /// <summary>
    /// M6 / TIER 10 — <b>the headline gate: a single-wire drag update at 600 wires, under 10 ms.</b>
    ///
    /// <para>Reports the split the design note predicts (WB13: the fill dominates, not the solve), so
    /// a future regression can be attributed rather than guessed at.</para>
    /// </summary>
    [Fact]
    public void M6_1_SingleWireDragUpdate_StaysInsideTheTenMillisecondBudget()
    {
        var design = TestDesigns.PowerAmplifier();
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: true);

        const int reps = 20;
        var sw = new Stopwatch();

        // Warm the paths so the first JIT pass is not in the measurement.
        incremental.MoveWires([1], SelectionMotion.General);
        incremental.Reduce();

        // (a) fill half alone — the block recompute, no factor work.
        var bare = InductanceMatrix.Fill(WireMesh.Build(design), parallel: true);
        var bareMesh = WireMesh.Build(design);
        sw.Restart();
        for (int i = 0; i < reps; i++) bare.RefreshWire(bareMesh, 300 + i);
        sw.Stop();
        double fillMs = sw.Elapsed.TotalMilliseconds / reps;

        // (b) the whole move: mesh refresh + blocks + rank-2 Cholesky update.
        sw.Restart();
        for (int i = 0; i < reps; i++) incremental.MoveWires([300 + i], SelectionMotion.General);
        sw.Stop();
        double moveMs = sw.Elapsed.TotalMilliseconds / reps;

        // (c) the array reduction the panel reads.
        sw.Restart();
        for (int i = 0; i < reps; i++) incremental.Reduce();
        sw.Stop();
        double reduceMs = sw.Elapsed.TotalMilliseconds / reps;

        double total = moveMs + reduceMs;

        _out.WriteLine($"single-wire drag at {mesh.WireCount} wires, {mesh.ArrayCount} arrays:");
        _out.WriteLine($"  block recompute (fill):   {fillMs,7:F2} ms");
        _out.WriteLine($"  move total (fill+factor): {moveMs,7:F2} ms   -> factor ~{moveMs - fillMs,5:F2} ms");
        _out.WriteLine($"  array reduction:          {reduceMs,7:F2} ms");
        _out.WriteLine($"  TOTAL PER DRAG FRAME:     {total,7:F2} ms   (budget 10.00, frame 16.67)");

        Assert.True(total < 10.0,
            $"A single-wire drag update at 600 wires must stay under 10 ms; measured {total:F2} ms " +
            $"(fill {fillMs:F2}, factor {moveMs - fillMs:F2}, reduce {reduceMs:F2}).");
    }

    /// <summary>
    /// M6 — what a multi-wire drag actually costs, and where the exact incremental path stops being
    /// viable. The design note predicts the crossover is around 5–10 simultaneously-moving wires.
    /// </summary>
    [Fact]
    public void M6_2_MultiWireDrag_LocatesTheCrossover()
    {
        var design = TestDesigns.PowerAmplifier();
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: true);

        _out.WriteLine("moving wires | ms/update | ms incl. reduce | fits 16.7 ms frame?");

        foreach (int k in new[] { 1, 2, 5, 10, 25, 50 })
        {
            int[] moved = [.. Enumerable.Range(100, k)];
            var sw = Stopwatch.StartNew();
            const int reps = 5;
            for (int i = 0; i < reps; i++) incremental.MoveWires(moved, SelectionMotion.General);
            sw.Stop();
            double moveMs = sw.Elapsed.TotalMilliseconds / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++) incremental.Reduce();
            sw.Stop();
            double total = moveMs + sw.Elapsed.TotalMilliseconds / reps;

            _out.WriteLine($"{k,12} | {moveMs,9:F2} | {total,15:F2} | {(total < 16.67 ? "yes" : "NO")}");
        }
    }

    /// <summary>
    /// M6 / R-wb-10 — how much horizontal rigid translation actually saves, measured rather than
    /// assumed. The design note predicts ~8 % for 50-of-600 and ~33 % when a whole 200-wire array
    /// moves.
    /// </summary>
    [Fact]
    public void M6_3_RigidMotionInvariance_SavingIsMeasured()
    {
        var design = TestDesigns.PowerAmplifier();
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: true);

        _out.WriteLine("selection | blocks recomputed | skipped | saving");

        foreach (int k in new[] { 50, 200 })
        {
            int[] moved = [.. Enumerable.Range(0, k)];
            incremental.MoveWires(moved, SelectionMotion.HorizontalRigidTranslation);

            int done = incremental.LastBlocksRecomputed;
            int skipped = incremental.LastBlocksSkipped;
            double saving = (double)skipped / (done + skipped);

            _out.WriteLine($"{k,9} | {done,17} | {skipped,7} | {saving,6:P1}");
        }
    }
}
