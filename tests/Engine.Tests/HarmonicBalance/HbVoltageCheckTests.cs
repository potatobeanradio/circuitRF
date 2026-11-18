// ================================================================
//  HbVoltageCheckTests.cs — brief R-hbv-1 and R-hbv-3, on an unloaded peak detector.
//
//  R-hbv-1: an HB solve is converged when its residual is under Tol AND its last update is small. A peak detector's DC level
//  is set by the nA its 1 GΩ leak draws, so a residual under 1e-6 A left it 90 mV short (9.671 V) while reporting converged;
//  a solve at Tol=1e-13 reads 9.7614 V. (The bridge rectifier that showed it, src/Engine/RESOLVED.md, is the same physics at
//  a minute per solve in a Debug build; one diode at K=10 is milliseconds.) VRelTol=0 is the solve as it was.
//  R-hbv-3: 50 Hz and 1 kHz are harmonics 1 and 20 of 50 Hz, and two-tone HB solves them as independent tones: said once.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class HbVoltageCheckTests(ITestOutputHelper output)
{
    /// <summary>The detector's DC level from a solve at Tol=1e-13, residual test alone.</summary>
    private const double SettledVout = 9.7614;

    private const string Detector =
        "V_1Tone:VS in 0 V=10 V Freq=50 Hz\n" +
        "R:RS in a R=50 Ohm\n" +
        "Diode:D1 a out Is=3.2E-09 A N=1.85 Rs=1.6 Ohm\n" +
        "C:C1 out 0 C=330 uF\n" +
        "R:RL out 0 R=1000000000 Ohm\n";

    private const string OneTone = "analysis HB1 type=hb Tone=50 ToneUnit=Hz MaxHarm=10";
    private const string TwoTones =
        "analysis HB1 type=hb NumFreqs=2 Tone[1]=50 ToneUnit[1]=Hz Tone[2]=1 ToneUnit[2]=kHz MaxHarm=3 MaxMixOrder=3";

    private sealed record Solved(double Vout, int Iterations, bool Converged, Complex[] V, IReadOnlyList<string> Warnings);

    private static Solved Run(string analysis, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(Detector + analysis + "\n");
        using var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var run = new HbEngine(nl, tb, settings).Run(p);
        var v = run.DataSet["V"];
        int node = Array.IndexOf(v.Axes[0].Labels!, "out");
        return new Solved(((Complex)v[node, 0]).Real, run.Trace!.TotalIterations, run.Converged, v.ComplexValues, [.. nl.Warnings]);
    }

    [Fact]
    public void TheDefault_ReachesTheSettledLevel_WhereTheResidualAloneStoppedShort()
    {
        var on  = Run(OneTone);
        var off = Run(OneTone + " VRelTol=0");
        output.WriteLine($"Vout {on.Vout:F4} V in {on.Iterations} iterations; residual alone {off.Vout:F4} V in {off.Iterations}");
        Assert.True(on.Converged);
        Assert.InRange(on.Vout, SettledVout - 1e-3, SettledVout + 1e-3);
        Assert.True(off.Vout < SettledVout - 0.05, $"the residual alone stopped at {off.Vout:F4} V; this test needs it short of the settled level");
    }

    [Fact]
    public void VRelTolZero_IsTheSolveWithTheTestOff()
    {
        var keyed    = Run(OneTone + " VRelTol=0");
        var settings = Run(OneTone, AnalysisSettings.Default.WithVoltageCheck(0, 0));
        Assert.Equal(settings.Iterations, keyed.Iterations);
        Assert.Equal(settings.V, keyed.V);
    }

    [Fact]
    public void ARunOutOfIterations_SaysTheUpdateHeldItBack()
    {
        // The residual alone converges in fewer iterations than the update test needs; stop between the two. No drive ramp,
        // which would otherwise rescue the run and hide the message under a converged one.
        int alone = Run(OneTone + " VRelTol=0").Iterations;
        var held  = Run(OneTone + $" MaxIter={alone + 1} DriveStepping=Never");
        output.WriteLine(string.Join("\n", held.Warnings));
        Assert.False(held.Converged);
        Assert.Contains(held.Warnings, w => w.Contains("the residual met Tol, but the last update at out (DC)"));
    }

    [Fact]
    public void HarmonicallyRelatedTones_AreFlaggedOnce()
    {
        var two = Run(TwoTones);
        var said = two.Warnings.Where(w => w.Contains("harmonics 1 and 20 of 50 Hz")).ToList();
        output.WriteLine(string.Join("\n", said));
        Assert.Single(said);
        Assert.Contains("run one tone at 50 Hz with MaxHarm at least 20", said[0]);
        Assert.DoesNotContain(Run(OneTone).Warnings, w => w.Contains("independent tones"));
    }

    [Theory]
    [InlineData(new[] { 50.0, 1000.0 }, new[] { 1, 20 })]
    [InlineData(new[] { 3e9, 1e9 },     new[] { 3, 1 })]
    [InlineData(new[] { 2.00e9, 2.01e9 }, null)]   // closely spaced: independent sources, nothing to say
    [InlineData(new[] { 1e9, 1.5e9 },     null)]   // commensurate, but 1.5 GHz is not a multiple of the lowest
    public void HarmonicMultiples_AreWholeMultiplesOfTheLowestTone(double[] tones, int[]? expected)
        => Assert.Equal(expected, HbEngine.HarmonicMultiples(tones));

    [Fact]
    public void ASetThatRunsOnItsCommonFundamental_IsNotAlsoFlagged()
    {
        // 100 and 300 MHz at MaxMixOrder 5: products coincide, so the run moves to 100 MHz and says so itself.
        var p = new HbAnalysisParams([100e6, 300e6], 5, 5, 1, 1e-6, CircuitRF.Engine.DcBiasSteppingMode.IfNecessary, 0, null, 0, 0, 1);
        Assert.Null(HbEngine.ToneNote(p));
        Assert.NotNull(HbEngine.ToneNote(p with { ToneFreqsHz = [50, 1000] }));
    }
}
