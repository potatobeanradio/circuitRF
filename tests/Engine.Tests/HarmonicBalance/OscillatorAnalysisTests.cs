using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Brief NZ-9 — the oscillator analysis. Each test is one of the brief's gates: a cubic negative conductance across an LC
/// tank against its closed form, a transistor Colpitts, and a circuit that does not oscillate.
/// </summary>
public class OscillatorAnalysisTests(ITestOutputHelper output)
{
    private static (ElaboratedNetlist Nl, TestBench Tb) Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return (new Elaborator(lib).Elaborate(tb), tb);
    }

    private static OscResult Run(string cnl, OscillatorAnalysis a)
    {
        var (nl, tb) = Elaborate(cnl);
        var p = OscEngine.Resolve(a, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return OscEngine.Run(nl, tb, p);
    }

    private static Complex V(DataCube cube, string node, int k)
    {
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        return (Complex)cube[i, k];
    }

    // i(v) = −g1·v + g3·v³ across a parallel LC tank loaded by GL. At one harmonic the describing function of the cubic is
    // (3/4)·g3·A², so the tank sustains A = √(4(g1 − GL)/(3·g3)) at f = 1/(2π√(LC)).
    private const double L = 10e-9, C = 10e-12, GL = 1e-3, G1 = 2e-3, G3 = 1e-3;
    private static string Tank(string extra = "") => $@"
L:L1   n 0  L={L:R}
C:C1   n 0  C={C:R}
R:RL   n 0  R={1 / GL:R}
SDD:D1 n 0  Ports=1  I[1,0]=-{G1:R}*_v1+{G3:R}*_v1^3
OscProbe:X1 n 0 Mode=shunt
{extra}";

    [Fact]
    public void Gate1_CubicConductanceAcrossTank_MatchesClosedForm()
    {
        double f0 = 1 / (2 * Math.PI * Math.Sqrt(L * C));
        double a0 = Math.Sqrt(4 * (G1 - GL) / (3 * G3));
        var r = Run(Tank(), new OscillatorAnalysis("OSC1")
        {
            FguessExpr = "0.45", Unit = "GHz", MaxHarmonicExpr = "1", FFTOverSampleExpr = "4", TolExpr = "1e-12",
        });
        output.WriteLine($"Fosc {r.FoscHz:R} (closed form {f0:R}), Aosc {r.Aosc:R} (closed form {a0:R}), {r.Iterations} Newton steps");
        Assert.True(Math.Abs(r.FoscHz / f0 - 1) < 1e-6, $"Fosc {r.FoscHz} vs {f0}");
        Assert.True(Math.Abs(r.Aosc / a0 - 1) < 1e-6, $"Aosc {r.Aosc} vs {a0}");
        Assert.True(Math.Abs(V(r.Data["V"], "n", 1).Magnitude / a0 - 1) < 1e-6, "the HB cubes carry the oscillation");
        Assert.Equal(r.FoscHz, r.Data["Fosc"].RealValues[0]);
        Assert.Equal(401, r.Data["Startup"].Axes[0].Length);
    }

    // The cubic makes a third harmonic and nothing else at first order; the tank shorts it, so V3/V1 is small, and V5 (which
    // appears only through the third's own distortion) smaller again. More harmonics leave the fundamental almost where it was.
    [Fact]
    public void Gate1_HarmonicContentFallsWithMaxHarm()
    {
        var a = new OscillatorAnalysis("OSC1") { FguessExpr = "0.5", Unit = "GHz", MaxHarmonicExpr = "5", FFTOverSampleExpr = "2", TolExpr = "1e-12" };
        var r = Run(Tank(), a);
        var v = r.Data["V"];
        double v1 = V(v, "n", 1).Magnitude, v3 = V(v, "n", 3).Magnitude, v5 = V(v, "n", 5).Magnitude;
        double v2 = V(v, "n", 2).Magnitude;
        output.WriteLine($"Fosc {r.FoscHz:R}, |V1| {v1:G6}, |V2| {v2:G3}, |V3| {v3:G6}, |V5| {v5:G6}");
        Assert.True(v3 / v1 < 0.05 && v3 / v1 > 1e-4, $"V3/V1 = {v3 / v1}");
        Assert.True(v5 < 0.1 * v3, $"V5 {v5} vs V3 {v3}");
        Assert.True(v2 < 1e-9 * v1, $"an odd nonlinearity makes no even harmonic, V2 = {v2}");
        double f0 = 1 / (2 * Math.PI * Math.Sqrt(L * C));
        Assert.True(Math.Abs(r.FoscHz / f0 - 1) < 1e-2, "the harmonics pull the frequency only slightly");
    }

    // A tank whose load outweighs the negative conductance never starts: refused with the start-up sentence, never the DC
    // solution returned as an oscillation.
    [Fact]
    public void Gate3_NoNegativeConductance_IsRefused()
    {
        var cnl = Tank().Replace($"R={1 / GL:R}", "R=200");
        var ex = Assert.Throws<InvalidOperationException>(() => Run(cnl, new OscillatorAnalysis("OSC1") { FguessExpr = "0.5", Unit = "GHz" }));
        output.WriteLine(ex.Message);
        Assert.Contains("does not start to oscillate", ex.Message);
        Assert.Contains("between 0.25 and 0.75 GHz", ex.Message);
        Assert.Contains("WSProbe", ex.Message);
    }
}

/// <summary>Brief NZ-9 gate 2 — a common-base Colpitts on the built-in BJT.</summary>
public class OscillatorColpittsTests(ITestOutputHelper output)
{
    // Base held at AC ground by Cb and biased by the divider (about 0.85 mA); the tank is L1 from the collector to the supply,
    // resonated by C1 (collector–emitter) in series with C2 (emitter–ground) and loaded by RL. The loop gain is about three.
    // The OscProbe sits across the tank, collector to ground (the supply is AC ground).
    private const double L = 100e-9, C1 = 20e-12, C2 = 60e-12, Cjc = 0.2e-12;
    private static readonly string Colpitts = $@"
Vdc:VCC   vcc 0   Vdc=5 V
R:R1      vcc b   R=10000 Ohm
R:R2      b   0   R=4700 Ohm
C:Cb      b   0   C=10 nF
R:RE      e   0   R=1000 Ohm
L:L1      vcc c   L={L:R} R=0.5 Ohm
R:RL      vcc c   R=500 Ohm
C:C1      c   e   C={C1:R}
C:C2      e   0   C={C2:R}
BJT_NPN:Q1 c b e  Is=1e-15 Bf=100 Cje=0.5e-12 Cjc={Cjc:R} Tf=20e-12
OscProbe:X1 c 0 Mode=shunt
";

    // The expected pull. At start-up the transistor conducts all cycle, and its emitter's low impedance across C2 lifts the
    // tank above the passive resonance (140.4 MHz here). As the swing grows the transistor cuts off for part of each cycle,
    // the emitter stops loading C2, and the frequency falls toward the tank's resonance with the transistor off: L with C1 in
    // series with C2, and Cjc across it (129.1 MHz). The oscillation lies between those two.
    [Fact]
    public void Gate2_Colpitts_ConvergesFromFguessWithinTwentyPercent()
    {
        double fOff = 1 / (2 * Math.PI * Math.Sqrt(L * (C1 * C2 / (C1 + C2) + Cjc)));
        var (lib, tb) = new CnlReader().Read(Colpitts);
        double? fosc = null;
        foreach (double factor in new[] { 0.8, 1.2 })
        {
            var nl = new Elaborator(lib).Elaborate(tb);
            var a = new OscillatorAnalysis("OSC1") { FguessExpr = (0.14 * factor).ToString("R"), Unit = "GHz", MaxHarmonicExpr = "9" };
            var p = OscEngine.Resolve(a, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            var r = OscEngine.Run(nl, tb, p);
            double fs = r.Startup.StartHz!.Value;
            output.WriteLine($"Fguess {p.FguessHz / 1e6:F1} MHz: start-up {fs / 1e6:F4} MHz, transistor-off {fOff / 1e6:F4} MHz, " +
                             $"Fosc {r.FoscHz / 1e6:F4} MHz ({100 * (r.FoscHz / fs - 1):F3} %), Aosc {r.Aosc:G6} V, {r.Iterations} Newton steps");
            Assert.InRange(r.FoscHz, fOff, fs);
            Assert.True(r.Aosc > 0.05 && r.Aosc < 5, $"Aosc {r.Aosc}");
            if (fosc is { } f0) Assert.True(Math.Abs(r.FoscHz / f0 - 1) < 1e-6, $"{r.FoscHz} vs {f0} from the other guess");
            fosc = r.FoscHz;
        }
    }
}
