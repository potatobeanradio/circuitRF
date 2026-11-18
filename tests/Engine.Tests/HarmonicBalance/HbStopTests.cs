// ================================================================
//  HbStopTests.cs — designer round 38: Stop reaches a harmonic-balance solve.
//
//  A two-tone bridge rectifier at MaxMixOrder 10 is one HB solve of about two minutes; the Newton loops read no token, so
//  Stop said "stopping" and the solve ran on, and the next Run was refused as already running. Each loop now checks the run's
//  control once per iteration (HbSolveWatch). A stage change clears the last stage's live figure, so the next analysis does
//  not inherit "Newton iteration 31".
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class HbStopTests
{
    // A bridge rectifier on a 50 Hz + 1 kHz source, with no other place that reads the token before the Newton loop.
    private const string Bridge =
        "Diode:D1 a out Is=3.2E-09 A N=1.85 Rs=1.6 Ohm\n" +
        "Diode:D2 0 a Is=3.2E-09 A N=1.85 Rs=1.6 Ohm\n" +
        "Diode:D3 b out Is=3.2E-09 A N=1.85 Rs=1.6 Ohm\n" +
        "Diode:D4 0 b Is=3.2E-09 A N=1.85 Rs=1.6 Ohm\n" +
        "C:C1 out 0 C=330 uF\n" +
        "PnTone:P1 a b Z=50 Ohm Freq[1]=50 Hz Pavl[1]=25 dBm Freq[2]=1 kHz Pavl[2]=12 dBm\n";

    [Theory]
    [InlineData("analysis HB1 type=hb Tone=50 ToneUnit=Hz MaxHarm=20\n")]
    [InlineData("analysis HB1 type=hb NumFreqs=2 Tone[1]=50 ToneUnit[1]=Hz Tone[2]=1 ToneUnit[2]=kHz MaxHarm=5 MaxMixOrder=5\n")]
    public void AStoppedRun_EndsTheNewtonSolve(string directive)
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var (lib, tb) = new CnlReader().Read(Bridge + directive);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        Assert.ThrowsAny<OperationCanceledException>(
            () => new HbEngine(nl, tb, control: new RunControl { Token = cts.Token }).Run(p));
    }

    [Fact]
    public void ANewStage_DoesNotInheritTheLastStagesDetail()
    {
        RunProgress? last = null;
        var control = new RunControl { Progress = new SyncProgress(p => last = p) };
        control.Stage = "HB1";
        control.SetStageDetail(HbSolveWatch.Describe(30, 1e-7));
        Assert.Equal("Newton iteration 31, residual 1.0E-07 A", last!.StageDetail);
        control.Stage = "ENV1";
        Assert.Equal("", last!.StageDetail);
    }

    private sealed class SyncProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
