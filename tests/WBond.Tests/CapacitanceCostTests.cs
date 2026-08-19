using System.Diagnostics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// <b>Gate C4 of brief-wbond-capacitance §5 — capacitance must be cheap, and must not be in the drag
/// loop.</b>
///
/// <para><b>Stated as RATIOS against the inductance path, not as wall-clock seconds.</b> The brief's
/// budget is written in absolute milliseconds measured on one machine in Release; a routine
/// <c>dotnet test</c> runs Debug on whatever the machine is, and an absolute threshold there either
/// flakes or means nothing. What the phase actually claims is <i>relative</i> — "the electrostatic
/// pair loop is cheaper per pair than the inductance one" and "the reduction is one extra Cholesky" —
/// and both of those survive the change of configuration.</para>
///
/// <para><b>Measured, Release, Apple Silicon, the 600-wire / 12-array reference:</b> the inductance
/// fill 140–158 ms, the <b>P</b> fill 8–11 ms (<b>0.06–0.08 ×</b>, far cheaper than the brief's
/// predicted 0.25 × — most pairs in that design are far apart, and the far kernel is one reciprocal
/// square root against Grover's four <c>Atanh</c> and four <c>Atan2</c>), the array reduction 25 ms
/// and the capacitance reduction 26 ms. Cold build 170 ms → 206 ms, <b>+21 %</b>, inside the brief's
/// +25 %; the total reduction 51 ms, inside its ≤ 55 ms.</para>
///
/// <para><b>Tagged <c>Category=Benchmark</c> although it runs in well under a second</b> — the second
/// reason that tag exists (root <c>CLAUDE.md</c>): a test that is fast but <i>wall-clock-sensitive</i>
/// cannot survive the parallel-start burst of a full-solution run. Measured: it passes alone and every
/// time in isolation, and failed once in a 11,000-test full run with the P fill reading slower than the
/// L fill purely from core contention. <b>Do not untag it on the grounds that it runs quickly</b> — it
/// is tagged for the purpose the mechanism serves, not the letter of the ~5 s rule. Run it with
/// <c>dotnet test --settings circuitrf.benchmark.runsettings</c>.</para>
/// </summary>
public class CapacitanceCostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public CapacitanceCostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static double BestMs(Action action, int reps = 3)
    {
        action();   // warm up the JIT and the thread pool before anything is timed
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>
    /// <b>C4 — the capacitance fill is cheaper than the inductance fill, and the reduction is one
    /// extra Cholesky's worth.</b>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void C4_TheCapacitanceFillAndReductionAreCheaperThanTheInductanceOnes()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 200, arrayCount: 8, pointsPerWire: 7));

        double lFill = BestMs(() => InductanceMatrix.Fill(mesh, parallel: true));
        double pFill = BestMs(() => PotentialCoefficients.Fill(mesh, parallel: true));

        var l = InductanceMatrix.Fill(mesh, parallel: true);
        var p = PotentialCoefficients.Fill(mesh, parallel: true);

        double lReduce = BestMs(() => ArrayReduction.Reduce(l, mesh));
        double cReduce = BestMs(() => CapacitanceReduction.Compute(mesh, p));

        _out.WriteLine($"N = {mesh.WireCount}: L fill {lFill:F1} ms, P fill {pFill:F1} ms " +
                       $"(x{pFill / lFill:F3}); L reduce {lReduce:F1} ms, C reduce {cReduce:F1} ms " +
                       $"(x{cReduce / lReduce:F2})");
        _out.WriteLine($"cold build {lFill + lReduce:F1} ms -> {lFill + pFill + lReduce + cReduce:F1} ms " +
                       $"(+{100.0 * (pFill + cReduce) / (lFill + lReduce):F0} %)");

        Assert.True(pFill < lFill,
            $"The electrostatic pair loop must be cheaper per pair than the inductance one — no cos e, " +
            $"no four Atanh, no four Atan2. Measured P {pFill:F1} ms against L {lFill:F1} ms.");

        // The reduction is one Cholesky at N plus M solves plus one more solve; the inductance
        // reduction is one Cholesky at N plus M solves plus an M x M inverse. They are the same order,
        // and 3x is a wide enough band to survive a loaded machine while still catching an accidental
        // N-solve explicit inverse (which would be ~N/M = 25x here).
        Assert.True(cReduce < 3.0 * lReduce,
            $"The capacitance reduction must stay within a small multiple of the inductance one; " +
            $"{cReduce:F1} ms against {lReduce:F1} ms suggests P-inverse is being formed explicitly.");
    }
}
