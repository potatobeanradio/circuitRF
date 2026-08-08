using System.Diagnostics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// WB-B M1 — the measurement that decides whether a swept simulation is affordable
/// (brief-wbond-wbb §2).
///
/// <para><c>wbond.md</c> §5.3 asserts that one complex N × N factorisation per frequency point is
/// "fine for a swept simulation". <b>That assertion was never measured.</b> A complex LU at N = 600
/// is roughly 4× the flops of a real one and 2× a symmetric factorisation's, so the honest estimate
/// was 100–200 ms per point — 20–40 s for a 201-point sweep and 100–200 s for a 1001-point one.</para>
///
/// <para>Tagged <c>Benchmark</c>: a single 600-wire cold fill is ~0.15 s and this class runs several
/// factorisations on top. <b>Take these alone.</b></para>
/// </summary>
[Trait("Category", "Benchmark")]
public class WbbCostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public WbbCostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>M1 measurements 1 and 2 — one point, and an extrapolated sweep.</summary>
    [Fact]
    public void M1_1_ArrayImpedance_PerFrequencyCostAt600Wires()
    {
        var design = TestDesigns.PowerAmplifier();

        var sw = Stopwatch.StartNew();
        var reduction = ImpedanceReduction.Create(design, parallel: true);
        sw.Stop();
        double buildMs = sw.Elapsed.TotalMilliseconds;

        _out.WriteLine($"N = {reduction.WireCount} wires, M = {reduction.ArrayCount} arrays");
        _out.WriteLine($"  Create (fill L once, parallel):   {buildMs,8:F1} ms   <- ONCE per structural change");

        // Warm the paths.
        reduction.ArrayImpedance(1e9);

        const int reps = 5;
        sw.Restart();
        for (int i = 0; i < reps; i++) reduction.ArrayImpedance(1e9 + i * 1e8);
        sw.Stop();
        double perPointMs = sw.Elapsed.TotalMilliseconds / reps;

        _out.WriteLine($"  Z_arr(w) per frequency point:     {perPointMs,8:F1} ms");
        _out.WriteLine($"    -> 201-point sweep:             {perPointMs * 201 / 1000.0,8:F1} s");
        _out.WriteLine($"    -> 1001-point sweep:            {perPointMs * 1001 / 1000.0,8:F1} s");
        _out.WriteLine($"  (refilling L per point would add  {buildMs * 201 / 1000.0,8:F1} s to a 201-point sweep)");

        Assert.True(perPointMs > 0);
    }

    /// <summary>
    /// M1 — how the per-point cost scales, so the N = 600 figure can be projected rather than
    /// guessed at for other designs. A dense LU is O(N^3).
    /// </summary>
    [Fact]
    public void M1_2_PerFrequencyCost_ScalesCubically()
    {
        _out.WriteLine("    N | per-point ms | ms/N^3 (x1e9)");

        foreach (int n in new[] { 150, 300, 600 })
        {
            var reduction = ImpedanceReduction.Create(
                TestDesigns.PowerAmplifier(wireCount: n, arrayCount: 6), parallel: true);

            reduction.ArrayImpedance(1e9);

            var sw = Stopwatch.StartNew();
            const int reps = 5;
            for (int i = 0; i < reps; i++) reduction.ArrayImpedance(1e9 + i * 1e8);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds / reps;
            _out.WriteLine($"{n,5} | {ms,12:F1} | {ms / ((double)n * n * n) * 1e9,14:F2}");
        }
    }
}
