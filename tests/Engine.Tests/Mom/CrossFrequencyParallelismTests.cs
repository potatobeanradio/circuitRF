// M3 (brief-em-sweep-performance) — the measurement that was taken BEFORE building it, and the
// reason it was not built.
//
// M3 proposes solving whole frequency points concurrently. Its premise is that a de-embedded point
// leaves cores idle — that the fill's own row parallelism does not take the whole machine, so
// another frequency's work could fill the gaps. THAT PREMISE IS MEASURED FALSE HERE, and the two
// tests below are the evidence:
//
//   · one fill scales 5.3x on this 10-core box, and the WHOLE de-embedded point scales 5.4x
//     (src/Engine/Mom/CLAUDE.md §9). A point that scales as well as its own fill has essentially no
//     serial fraction left for another frequency to overlap.
//   · four independent frequency-shaped units run concurrently under one budget beat running them
//     one after another with each using the whole machine by 1.09x — which is M3's entire ceiling,
//     before any of its cost.
//
// The fall-off is HARDWARE, not scheduling: efficiency is 93% at 2 cores and 90% at 4, then 67% at
// 6 and 54% at 10. A fixed serial phase would show a CONSTANT Amdahl fraction; solving for it gives
// 5.9% / 3.3% / 9.6% at caps 2 / 4 / 10, which is not one number — so it is core heterogeneity or
// memory bandwidth, and running MORE fills at once cannot invent either.
//
// Both are Category=Benchmark and are measurements, not pass/fail gates. Keep them: they are what a
// future "why don't we just parallelise the sweep?" is answered with.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class CrossFrequencyParallelismTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double Freq = 10e9;

    private static (PlanarMesh Mesh, PlanarKernelPair Kernel) Fixture()
    {
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, Freq);
        return (SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh,
                PlanarLineFixtures.Kernel(slab, Freq));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void OneFillsOwnScaling_IsWhereTheCeilingComesFrom()
    {
        var (mesh, k) = Fixture();
        double w = 2.0 * Math.PI * Freq;

        double Time(int? cap)
        {
            var cores = PlanarFill.BuildCores(mesh,
                            PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var z  = PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, w);
            sw.Stop();
            Assert.Equal(mesh.Bases.Count, z.RowCount);
            return sw.Elapsed.TotalSeconds;
        }

        Time(2);                       // warm
        double t1 = Time(1);
        _out.WriteLine($"N = {mesh.Bases.Count}, {Environment.ProcessorCount} core(s) — ONE fill:");
        _out.WriteLine($"  cap  1 : {t1,6:F1} s    1.00x");
        foreach (int c in new[] { 2, 4, 6, 8, 10 })
        {
            double t = Time(c);
            _out.WriteLine($"  cap {c,2} : {t,6:F1} s   {t1 / t,5:F2}x   {100.0 * (t1 / t) / c,3:F0}% efficiency");
        }

        // Not a threshold on the ratio — the ceiling is the machine's, and a different box has a
        // different one. What is asserted is that capping BELOW the machine genuinely costs time, so
        // this is measuring the fill's parallelism rather than something that ignores the cap.
        Assert.True(Time(1) > Time(Environment.ProcessorCount) * 1.5,
            "a serial fill was not measurably slower than a parallel one — the cap is not being honoured");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M3sPremise_FourIndependentUnitsConcurrently_AgainstOneAfterAnother()
    {
        var (mesh, k) = Fixture();
        double w   = 2.0 * Math.PI * Freq;
        int    cap = Environment.ProcessorCount;

        var unbounded = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default);
        var budgeted  = PlanarFill.BuildCores(mesh, PlanarFillSettings.Default with
        {
            MaxDegreeOfParallelism = cap,
            Budget                 = new PlanarParallelBudget(cap),
        });

        PlanarFill.Fill(unbounded, k.VectorPotential, k.Scalar, w);   // warm

        double Sequential(int n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++) PlanarFill.Fill(unbounded, k.VectorPotential, k.Scalar, w);
            return sw.Elapsed.TotalSeconds;
        }

        double Concurrent(int n)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, n, _ => PlanarFill.Fill(budgeted, k.VectorPotential, k.Scalar, w));
            return sw.Elapsed.TotalSeconds;
        }

        // Alternate the two so a drifting machine cannot favour whichever ran first, and take the
        // best of each — the same methodology that reversed the sign of M2's own first measurement.
        double s1 = Sequential(4), c1 = Concurrent(4), c2 = Concurrent(4), s2 = Sequential(4);
        double seq = Math.Min(s1, s2), con = Math.Min(c1, c2);

        _out.WriteLine($"N = {mesh.Bases.Count}, {cap} core(s) — FOUR independent fills:");
        _out.WriteLine($"  one after another, each unbounded : {seq:F1} s  [{s1:F1}, {s2:F1}]");
        _out.WriteLine($"  all four at once, ONE budget      : {con:F1} s  [{c1:F1}, {c2:F1}]");
        _out.WriteLine($"  cross-unit parallelism is worth     {seq / con:F2}x   ← M3's whole ceiling");

        // Again no threshold: what a machine has left over is the machine's business. The recorded
        // number on the box this was written on is 1.09x, and it is why M3 is not built.
        Assert.True(con > 0 && seq > 0);
    }
}
