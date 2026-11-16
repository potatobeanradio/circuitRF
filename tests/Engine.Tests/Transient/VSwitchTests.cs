using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>brief-trn-3's gates for the voltage-controlled switch (docs/design/transient.md §7.1).</summary>
public sealed class VSwitchTests(ITestOutputHelper output)
{
    private static VSwitchModel Switch(double ron, double roff, double von, double voff) => new(new Dictionary<string, Value>
    {
        ["Ron"] = new(ron), ["Roff"] = new(roff), ["Von"] = new(von), ["Voff"] = new(voff),
    });

    /// <summary>Gate 1 — at Von and beyond the conductance is 1/Ron, at Voff and beyond 1/Roff; Von below Voff is inverse logic.</summary>
    [Fact]
    public void Gate1_Levels()
    {
        var sw = Switch(2, 5e6, 1.5, 0.5);
        foreach (double vc in new[] { 1.5, 1.6, 10.0 }) Assert.True(Math.Abs(sw.Conductance(vc).G * 2 - 1) <= 1e-12);
        foreach (double vc in new[] { 0.5, 0.4, -10.0 }) Assert.True(Math.Abs(sw.Conductance(vc).G * 5e6 - 1) <= 1e-12);
        var inverse = Switch(2, 5e6, 0.5, 1.5);
        Assert.True(Math.Abs(inverse.Conductance(0.0).G * 2 - 1) <= 1e-12);
        Assert.True(Math.Abs(inverse.Conductance(2.0).G * 5e6 - 1) <= 1e-12);
    }

    /// <summary>Gate 2 — the conductance and its first derivative are continuous across the transition, the derivative is the
    /// conductance's own slope, and it is zero at both ends.</summary>
    [Fact]
    public void Gate2_ConductanceAndItsDerivativeAreContinuous()
    {
        var sw = Switch(1, 1e9, 1, 0);
        const int n = 200_000;
        double lo = -0.2, hi = 1.2, dv = (hi - lo) / n;
        double worstJumpG = 0, worstJumpD = 0, worstFd = 0;
        var (gPrev, dPrev) = sw.Conductance(lo);
        for (int k = 1; k <= n; k++)
        {
            double v = lo + k * dv;
            var (g, d) = sw.Conductance(v);
            worstJumpG = Math.Max(worstJumpG, Math.Abs(g - gPrev));
            worstJumpD = Math.Max(worstJumpD, Math.Abs(d - dPrev));
            double fd = (g - gPrev) / dv, mid = sw.Conductance(v - dv / 2).DG;
            worstFd = Math.Max(worstFd, Math.Abs(fd - mid) / Math.Max(1, Math.Abs(mid)));
            (gPrev, dPrev) = (g, d);
        }
        double gMax = 1, dMax = Enumerable.Range(0, 1001).Max(i => Math.Abs(sw.Conductance(i / 1000.0).DG));
        output.WriteLine($"largest step in g {worstJumpG:E3} (of {gMax}), in dg/dv {worstJumpD:E3} (of {dMax:E3}); finite difference {worstFd:E3}");
        Assert.True(worstJumpG < 1e-3 * gMax, "g jumps");
        Assert.True(worstJumpD < 1e-3 * dMax, "dg/dv jumps");
        Assert.True(worstFd < 1e-4, "dg/dv is not the slope of g");
        Assert.Equal(0.0, sw.Conductance(0).DG);
        Assert.Equal(0.0, sw.Conductance(1).DG);
    }

    private const string ToneDriven = """
        Vdc:VDD vdd 0 Vdc=1
        R:RL vdd a R=100
        VSwitch:S1 a b ctrl 0 Ron=10 Roff=1e4 Von=0.8 Voff=-0.8
        IProbe:IP b 0
        V_1Tone:VC ctrl 0 V=1 Freq=1 MHz
        """;

    /// <summary>Gate 3 — a tone-driven switch converges in HB, and its current's first five harmonics match a transient run's
    /// settled spectrum within 0.05 dB.</summary>
    [Fact]
    public void Gate3_HbSpectrumMatchesTheTransient()
    {
        var (lib, tb) = new CnlReader().Read(ToneDriven + "\nanalysis HB1 type=hb Tone=1e6 MaxHarm=31 Tol=1e-12\n");
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals);
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.Equal(1.0, ds["Converged"].RealValues[0]);
        var icube = ds["I"];
        int branch = Array.IndexOf(icube.Axes[0].Labels!.ToArray(), "IP");
        int nh = icube.Axes[1].Length;
        Complex Hb(int k) => icube.ComplexValues[branch * nh + k];

        const int N = 1024;
        var (lib2, tb2) = new CnlReader().Read(ToneDriven + $"\nanalysis TR1 type=tran Stop=3u Step={1e-6 / N:R} MaxStep={1e-6 / N:R}\n");
        var nl2 = new Elaborator(lib2).Elaborate(tb2);
        var r = new TransientEngine(nl2).Run(TransientRunner.Resolve(tb2.Analyses.OfType<TransientAnalysis>().Single(), nl2.ResolvedGlobals));
        int pi = Array.IndexOf(r.ProbeNames, "IP");
        int start = r.Time.Length - 1 - N;          // the last period, its end point left out
        double worst = 0;
        for (int k = 1; k <= 5; k++)
        {
            Complex x = 0;
            for (int s = 0; s < N; s++) x += r.I[start + s][pi] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * s / N);
            double tranDb = 20 * Math.Log10(2 * x.Magnitude / N), hbDb = 20 * Math.Log10(Hb(k).Magnitude);
            output.WriteLine($"k={k}: HB {hbDb:F4} dB, transient {tranDb:F4} dB");
            worst = Math.Max(worst, Math.Abs(tranDb - hbDb));
        }
        Assert.True(worst < 0.05, $"worst harmonic differs by {worst:F4} dB");
    }

    /// <summary>R-trn3-3 — a slow crossing of the transition is taken in at least TransitionPoints accepted steps, though nothing else
    /// in the circuit would have limited the step.</summary>
    [Fact]
    public void SoftBreakpoints_LimitTheStepAcrossTheTransition()
    {
        var (lib, tb) = new CnlReader().Read("""
            Vdc:VDD vdd 0 Vdc=1
            R:RL vdd a R=100
            VSwitch:S1 a 0 ctrl 0 Ron=1 Roff=1e6 Von=1.5 Voff=0.5
            V_PWL:VC ctrl 0 Points="0 0, 100n 2"
            analysis TR1 type=tran Stop=200n Step=50n SaveSteps=yes
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var r = new TransientEngine(nl).Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals));
        int c = Array.IndexOf(r.NodeNames, "ctrl");
        int inside = r.StepV!.Count(v => v[c] > 0.5 && v[c] < 1.5);
        output.WriteLine($"accepted {r.AcceptedSteps}, inside the window {inside}");
        Assert.True(inside >= 7, $"{inside} accepted steps inside the window; 8 across it needs at least 7");
    }

    /// <summary>R-trn3-1 — Hyst in transient: off, the switch is not fully on until Hyst/2 past Von; on, it stays fully on until
    /// Hyst/2 past Voff. A DC run ignores it and says so; a transient run does not.</summary>
    [Fact]
    public void Hysteresis_InTransientOnly_AndNotedElsewhere()
    {
        const string bench = """
            Vdc:VDD vdd 0 Vdc=1
            R:RL vdd a R=100
            VSwitch:S1 a 0 ctrl 0 Ron=1 Roff=1e6 Von=1.5 Voff=0.5 Hyst=0.4
            V_PWL:VC ctrl 0 Points="0 0, 200n 2, 400n 0"
            """;
        var (lib, tb) = new CnlReader().Read(bench + "\nanalysis TR1 type=tran Stop=400n Step=1n\n");
        var nl = new Elaborator(lib).Elaborate(tb);
        var r = new TransientEngine(nl).Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals));
        int a = Array.IndexOf(r.NodeNames, "a");
        double On(double t) => r.V[(int)Math.Round(t / 1e-9)][a];
        double fullyOn = 1.0 / 101;      // 1 V across 100 + 1 ohm
        // rising through Von = 1.5 V (t = 150 ns), where a switch with no hysteresis is fully on: inside the off-state window
        // [0.7, 1.7], not yet
        Assert.True(On(150e-9) > 2 * fullyOn, $"rising at 1.5 V: {On(150e-9):G4} V");
        // falling through 1.4 V (t = 260 ns): the on-state window is [0.3, 1.3], so still fully on
        Assert.Equal(fullyOn, On(260e-9), 9);
        // at one control voltage, 0.8 V, the switch is further on falling (t = 320 ns, the on-state window) than rising (t = 80 ns)
        Assert.True(On(320e-9) < 0.95 * On(80e-9), $"0.8 V falling {On(320e-9):G4} V against rising {On(80e-9):G4} V");
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("Hyst", StringComparison.Ordinal));

        var (lib2, tb2) = new CnlReader().Read(bench);
        var nl2 = new Elaborator(lib2).Elaborate(tb2);
        NonlinearDcEngine.Run(nl2);
        Assert.Contains(nl2.Warnings, w => w.Contains("S1: Hyst=0.4 V applies in a transient analysis only", StringComparison.Ordinal));
    }

    /// <summary>R-trn3-2 — DC and S-parameter: a switch biased on is Ron in series; biased off, Roff.</summary>
    [Fact]
    public void SParameter_LinearisedAtItsBias()
    {
        double S21(double vc)
        {
            var (lib, tb) = new CnlReader().Read($"""
                Port:P1 in 0 Num=1 Z=50 Ohm
                Port:P2 out 0 Num=2 Z=50 Ohm
                VSwitch:S1 in out ctrl 0 Ron=10 Roff=1e6 Von=1 Voff=0
                Vdc:VC ctrl 0 Vdc={vc}
                """);
            var nl = new Elaborator(lib).Elaborate(tb);
            return SParameterEngine.Run(nl, [1e9])["S"].ComplexValues[2].Magnitude;   // S[f, i=2, j=1]
        }
        Assert.Equal(100.0 / 110.0, S21(2), 9);       // 2·50 / (2·50 + 10)
        Assert.Equal(100.0 / (100 + 1e6), S21(-1), 9);
    }

    /// <summary>
    /// Gate 4 and R-trn3-6 — a buck converter (switch, diode, LC, load) driven by a 1 MHz pulse over 200 cycles: the settled output
    /// matches the duty-cycle estimate corrected for Ron and the diode drop within 2 %. The counters go to src/Engine/RESOLVED.md.
    /// </summary>
    [Fact]
    public void Gate4_Buck_SettlesToTheDutyCycleEstimate()
    {
        var (lib, tb) = new CnlReader().Read("""
            Vdc:VIN vin 0 Vdc=12
            V_Pulse:VG gate 0 V1=0 V2=5 Delay=0 Rise=10n Fall=10n Width=490n Period=1u
            VSwitch:S1 vin sw gate 0 Ron=0.1 Roff=1e6 Von=3 Voff=2
            Diode:D1 0 sw Is=1e-14
            L:L1 sw out L=10 uH
            C:C1 out 0 C=1 uF
            R:RL out 0 R=5
            analysis TR1 type=tran Stop=200u Step=10n InitialState=zero
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new TransientEngine(nl).Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals));
        sw.Stop();
        int o = Array.IndexOf(r.NodeNames, "out");
        // the last 20 cycles
        var tail = Enumerable.Range(0, r.Time.Length).Where(k => r.Time[k] > 180e-6 && r.Time[k] <= 200e-6).Select(k => r.V[k][o]).ToArray();
        double vout = tail.Average(), iLoad = vout / 5;
        const double d = 0.5, vt = 0.025852;
        double vd = vt * Math.Log(iLoad / 1e-14 + 1);
        double estimate = d * (12 - iLoad * 0.1) - (1 - d) * vd;
        output.WriteLine($"Vout {vout:F4} V (ripple {tail.Max() - tail.Min():F4} V), estimate {estimate:F4} V (Vd {vd:F3} V), "
                         + $"error {100 * (vout / estimate - 1):F2} %");
        output.WriteLine($"accepted {r.AcceptedSteps}, rejected {r.RejectedSteps}, Newton {r.NewtonIterations}, factorizations {r.Factorizations}, "
                         + $"device evaluations {r.DeviceEvaluations}, breakpoints {r.BreakpointsLanded}, rungs {r.MethodRungsVisited}, "
                         + $"path {r.NewtonPathUsed}, {sw.Elapsed.TotalSeconds:F2} s");
        Assert.True(Math.Abs(vout / estimate - 1) < 0.02, $"Vout {vout:F4} V against {estimate:F4} V");
    }
}
