// brief-em-p7-symmetric-inplace-factorisation.md milestone 5 — what the factorisation costs.
//
// Category=Benchmark, and it earns the tag twice over: the largest rung factors a 4,800-unknown
// matrix five times (four core caps plus NumFlat's general LU, each on its own copy, because the
// new one CONSUMES its input) and holds well over a gigabyte while it does.
//
// TWO NUMBERS, AND ONLY THE FIRST IS A SPEED CLAIM.
//
//   * TIME vs the parallel cap. P1 measured NumFlat's LU at CPU/wall = 1.00 on ten cores: it does
//     not thread, so near R17's ceiling a sweep was about two thirds one-core factorisation while
//     the fill beside it scaled 5.4x. The question this answers is whether the blocked right-looking
//     trailing update recovers that, and by how much on THIS box — whose ten cores are four
//     performance and six efficiency ones (P3 established that, and it is why no speed-up here will
//     read as 10x).
//
// AND ONE TRAP THAT IS SPECIFIC TO THIS COMPARISON: **`dotnet test` builds Debug.** Every other
// timing in this area compares managed code against managed code, so the configuration cancels and
// nobody has had to think about it. This one does not: NumFlat's LU is native and its time barely
// moves between configurations, while the new factorisation is ordinary C# and runs about 9x slower
// unoptimised. Measured on the same box at N = 1,980: Debug 9.65 s / 2.10 s (cap 1 / cap 10) against
// Release 1.05 s / 0.27 s, with NumFlat at 1.71 s and 1.88 s respectively. So the Debug column below
// UNDERSTATES the shipped factorisation by roughly an order of magnitude and the LU not at all, and
// the "LU / best" ratio it prints is meaningless unless this test was run in Release. The
// configuration is printed with the table, the assertions below are the ones true in both, and
// HISTORY.md §P7 records the Release numbers as the shipped ones.
//   * MEMORY. That one is arithmetic, not a stopwatch: the factors overwrite the matrix, so the
//     32*N^2 of separate L and U simply is not allocated. It is MEASURED here anyway, because P1's
//     own lesson was that a counted figure and a live heap can disagree for structural reasons
//     (the large object heap does not hand released space back), and the claim being made is about
//     what a machine sees.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarP7FactorCostTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static string Mb(long b) => $"{b / (1024.0 * 1024.0):N1}";
    private static long Live()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return GC.GetTotalMemory(true);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // three fills at the shipping mesh, fifteen factorisations
    public void P7_M1_FactorTimeAcrossCaps_AndTheResidentPeak_AtThreeSizes()
    {
#if DEBUG
        const string config = "DEBUG — see this file's header: the LU column is native and the LDLᵀ " +
                              "column is managed, so this build UNDERSTATES the latter ~9x";
#else
        const string config = "RELEASE — the shipped configuration, and the only one in which the " +
                              "'LU / best' column means anything";
#endif
        _out.WriteLine($"Build configuration: {config}.");
        _out.WriteLine("");
        _out.WriteLine("FR-4 hero cross-section at 10 GHz, shipping mesh — the same three rungs P1's");
        _out.WriteLine("own memory table used, so the two are directly comparable.");
        _out.WriteLine("");
        _out.WriteLine("      N   cells    cap 1    cap 2    cap 4   cap 10   NumFlat LU   " +
                       "LU / best   scaling 1->10");

        var rows = new List<(int N, double C1, double C10, double Lu)>();
        var mem  = new List<(int N, long LdlAlloc, long LuAlloc, long LdlLive, long LuLive)>();

        foreach (double lengthM in new[] { 20e-3, 80e-3, 200e-3 })
        {
            var report = SurfaceMesher.Mesh(PlanarLineFixtures.Fr4Line(lengthM, 10e9),
                                            PlanarLineFixtures.Shipping);
            Assert.Null(report.Refusal);
            var mesh = report.Mesh;
            int n = mesh.Bases.Count, cells = mesh.Cells.Count;

            // ONE fill per rung. Every factorisation below runs on its own copy of it, because the
            // shipped one CONSUMES its input — which is the whole point, and is why the copies are
            // outside the timers.
            var cores = PlanarFill.BuildCores(mesh);
            var k     = PlanarLineFixtures.Kernel(GroundedSlab.Fr4Starter, 10e9)
                            .For(cores, PlanarFillSettings.Default.Order);
            var z     = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, 2.0 * Math.PI * 10e9);

            var times = new double[4];
            int[] caps = [1, 2, 4, 10];
            for (int c = 0; c < caps.Length; c++)
            {
                var a  = z.Copy();
                var st = PlanarFillSettings.Default with { MaxDegreeOfParallelism = caps[c] };
                var sw = Stopwatch.StartNew();
                _ = SymmetricFactorization.Factor(a, st);
                times[c] = sw.Elapsed.TotalSeconds;
            }

            double luS;
            {
                var a  = z.Copy();
                var sw = Stopwatch.StartNew();
                _ = a.Lu();
                luS = sw.Elapsed.TotalSeconds;
            }

            double best = times.Min();
            _out.WriteLine($"  {n,5}   {cells,5}  {times[0],7:F2}s {times[1],7:F2}s {times[2],7:F2}s " +
                           $"{times[3],7:F2}s   {luS,9:F2}s   {luS / best,9:F1}x   " +
                           $"{times[0] / times[3],13:F1}x");
            rows.Add((n, times[0], times[3], luS));

            // ── memory, in the one unit that cannot be polluted ──────────────────────────────
            //
            // BOTH counters, around the factorisation ALONE — not around a whole frequency point.
            // P1's lesson was that a live-heap delta spanning several phases is untrustworthy on the
            // large object heap, and an earlier version of this table proved it again by reading a
            // NEGATIVE 188 MB at the largest rung. Scoped to one factorisation on a fresh copy, with
            // the baseline taken immediately before that copy, it is exact — and it is the number
            // that carries the assertion below. The allocation counter is reported beside it and is
            // the more striking of the two for the LU, but it is process-wide and picks up the
            // parallel loop's own task objects on the LDLᵀ side.
            mem.Add((n,
                     Measure(z, PlanarFillSettings.Default, out long ldlLive),
                     Measure(z, PlanarFillSettings.Default with { UseSymmetricFactorization = false },
                             out long luLive),
                     ldlLive, luLive));

            GC.KeepAlive(cores);
        }

        _out.WriteLine("");
        _out.WriteLine("MEMORY. Around the FACTORISATION alone, on an identical copy of one matrix.");
        _out.WriteLine("'allocated' is exact; 'live' is the heap it left behind, and carries the");
        _out.WriteLine("factorisation's own released scratch still committed on the large object heap.");
        _out.WriteLine("");
        _out.WriteLine("      N   alloc LDLᵀ    alloc LU   ratio     live LDLᵀ     live LU   " +
                       "16·N² for scale");
        foreach (var m in mem)
            _out.WriteLine($"  {m.N,5}   {Mb(m.LdlAlloc),10}  {Mb(m.LuAlloc),10}   " +
                           $"{(double)m.LuAlloc / Math.Max(1, m.LdlAlloc),5:F0}x   {Mb(m.LdlLive),10}  " +
                           $"{Mb(m.LuLive),10}   {Mb(PlanarSystem.MatrixBytes(m.N)),15}");

        _out.WriteLine("");
        _out.WriteLine("WHAT THIS SETTLES. The factorisation was the one part of a dense frequency");
        _out.WriteLine("point that ran on one core, and at the ceiling it was the majority of the");
        _out.WriteLine("point. It now scales over the same budget the fill spends, and it holds no");
        _out.WriteLine("matrix of its own. Neither number is a mesh property: this is arithmetic on");
        _out.WriteLine("an N x N array, and the fixtures are only a way of getting three realistic Ns.");

        // The claim, as an assertion rather than a table — and ONLY the part that is true in both
        // build configurations. "Faster than NumFlat's LU" is a Release statement (Release: 1.05 s
        // and 0.27 s at N = 1,980 against the LU's 1.88 s) and asserting it here would make this
        // test fail under the `dotnet test` everyone actually runs.
        foreach (var r in rows)
            Assert.True(r.C10 < r.C1,
                $"N = {r.N}: cap 10 took {r.C10:F2}s against cap 1's {r.C1:F2}s — the trailing " +
                "update is supposed to parallelise, and this is the claim P7 exists to make");

        // The memory claim, which is configuration-independent because it is arithmetic. It is
        // asserted on the LIVE figures rather than on the allocation counter, and that is a
        // measured decision: GC.GetTotalAllocatedBytes is process-wide and counts every thread, so
        // the LDLᵀ column picks up the Parallel.For's own task objects and whatever the test host
        // allocated in the same window — 0.2 to 0.9 MB, independent of N, and therefore noise
        // rather than a number to gate on. The live heap around the factorisation alone is exact
        // to the last megabyte here (measured 357.0 MB against 356.9 MB of matrix at N = 4,836, and
        // 1,070.6 against 3 x 356.9 for the LU), so that is what carries the assertion.
        foreach (var m in mem)
        {
            long matrix = PlanarSystem.MatrixBytes(m.N);
            Assert.True(m.LdlLive < 1.5 * matrix,
                $"N = {m.N}: the in-place factorisation left {Mb(m.LdlLive)} MB live against a " +
                $"matrix of {Mb(matrix)} MB — it is supposed to leave the matrix it consumed and a " +
                "length-N diagonal");
            Assert.True(m.LuLive > 2.5 * matrix,
                $"N = {m.N}: the general LU left {Mb(m.LuLive)} MB live against a matrix of " +
                $"{Mb(matrix)} MB — that is not the matrix plus two further full factors P1 " +
                "measured, so this comparison is measuring the wrong thing");
        }
    }

    /// <summary>Factor a COPY of <paramref name="z"/> and report what that cost: the exact allocated
    /// bytes as the return, the live heap it left behind through <paramref name="live"/>.</summary>
    private static long Measure(Mat<Complex> z, PlanarFillSettings st, out long live)
    {
        long b0 = Live();
        var a = z.Copy();
        var sys = PlanarSystem.Wrap(a, st);

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        sys.Factor();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

        live = Live() - b0;
        GC.KeepAlive(sys);
        return alloc;
    }

}
