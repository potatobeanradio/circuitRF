using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief-trn-1's gates for the native transient engine (docs/design/transient.md §4): closed forms where they exist, the
/// engine's own counters where the claim is about work, never a wall clock.
/// </summary>
public sealed class TransientEngineTests(ITestOutputHelper output)
{
    private static (TransientRunResult Result, ElaboratedNetlist Nl) Run(string cnl, Func<TransientParams, TransientParams>? edit = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals);
        if (edit is not null) p = edit(p);
        return (new TransientEngine(nl).Run(p), nl);
    }

    private static double[] Node(TransientRunResult r, string name)
    {
        int i = Array.IndexOf(r.NodeNames, name);
        Assert.True(i >= 0, $"node {name} is not recorded: {string.Join(", ", r.NodeNames)}");
        return [.. r.V.Select(v => v[i])];
    }

    /// <summary>Gate 1 — an RC charged from 0 by a 1 V source follows 1 − e^(−t/τ) within 1e-5 of the swing at every point.</summary>
    [Fact]
    public void RcStep_FollowsTheExponential()
    {
        var (r, _) = Run("""
            Vdc:V1 in 0 Vdc=1
            R:R1 in out R=1 kOhm
            C:C1 out 0 C=1 nF
            analysis TR1 type=tran Stop=5e-6 Step=5e-9 InitialState=zero RelTol=1e-6
            """);
        var v = Node(r, "out");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++) worst = Math.Max(worst, Math.Abs(v[k] - (1 - Math.Exp(-r.Time[k] / 1e-6))));
        output.WriteLine($"worst {worst:E3}; accepted {r.AcceptedSteps}, rejected {r.RejectedSteps}, factorizations {r.Factorizations}");
        Assert.True(worst < 1e-5, $"worst error {worst:E3} of a 1 V swing");
    }

    /// <summary>The TRN review: a unit standing apart from its number is the number's ("Stop=10 ns" is ten nanoseconds, not ten
    /// seconds), and a Step longer than the record, or a negative Start, is refused by name.</summary>
    [Fact]
    public void ASpacedUnit_IsTheValues_AndAStepLongerThanTheRecord_IsRefused()
    {
        const string circuit = "Vdc:V1 in 0 Vdc=1\nR:R1 in out R=1k\nC:C1 out 0 C=1p\n";
        var (_, tb) = new CnlReader().Read(circuit + "analysis TR1 type=tran Stop=10 ns Step=5 ps\n");
        var tr = tb.Analyses.OfType<TransientAnalysis>().Single();
        var p = TransientRunner.Resolve(tr, new Dictionary<string, CircuitRF.Core.Expressions.Value>());
        Assert.Equal(10e-9, p.StopS, 1e-21);
        Assert.Equal(5e-12, p.StepS, 1e-24);
        foreach (string bad in new[] { "Stop=1n Step=2n", "Stop=1n Start=-1n" })
        {
            var (_, tb2) = new CnlReader().Read(circuit + $"analysis TR1 type=tran {bad}\n");
            Assert.Throws<TransientRefusedException>(() =>
                TransientRunner.Resolve(tb2.Analyses.OfType<TransientAnalysis>().Single(), new Dictionary<string, CircuitRF.Core.Expressions.Value>()));
        }
    }

    private const double L = 1e-6, Cap = 1e-9, R = 5;
    private static string Rlc(string method, string step = "1e-9", string maxStep = "auto") => $"""
        Vdc:V1 in 0 Vdc=1
        R:R1 in a R={R}
        L:L1 a b L=1 uH
        C:C1 b 0 C=1 nF
        analysis TR1 type=tran Stop=4e-6 Step={step} MaxStep={maxStep} InitialState=zero Method={method}
        """;

    /// <summary>Crossings of the ring (v − 1) through zero, interpolated, and the peaks of |v − 1| between them.</summary>
    private static (double Freq, double Decay) Ring(TransientRunResult r)
    {
        var v = Node(r, "b");
        var cross = new List<double>();
        for (int k = 1; k < v.Length; k++)
            if ((v[k - 1] - 1) * (v[k] - 1) < 0)
                cross.Add(r.Time[k - 1] + (r.Time[k] - r.Time[k - 1]) * (1 - v[k - 1]) / (v[k] - v[k - 1]));
        // frequency: half-periods between the 2nd and the last crossing
        double f = (cross.Count - 2) / (2 * (cross[^1] - cross[1]));
        // decay: the log of successive peak heights over their spacing, through a least-squares line
        var pk = new List<(double t, double a)>();
        for (int k = 1; k < v.Length - 1; k++)
        {
            double a0 = Math.Abs(v[k - 1] - 1), a1 = Math.Abs(v[k] - 1), a2 = Math.Abs(v[k + 1] - 1);
            if (a1 >= a0 && a1 > a2 && a1 > 1e-3)
            {
                // parabola through the three points
                double den = a0 - 2 * a1 + a2, dt = r.Time[k] - r.Time[k - 1];
                double off = den != 0 ? 0.5 * (a0 - a2) / den : 0;
                pk.Add((r.Time[k] + off * dt, a1 - 0.25 * (a0 - a2) * off));
            }
        }
        double mt = pk.Average(x => x.t), ml = pk.Average(x => Math.Log(x.a));
        double slope = pk.Sum(x => (x.t - mt) * (Math.Log(x.a) - ml)) / pk.Sum(x => (x.t - mt) * (x.t - mt));
        return (f, -slope);
    }

    /// <summary>Gate 2 — an underdamped series RLC rings at ωd within 1e-4 and decays at R/2L within 1e-3 over 20 cycles with
    /// trap; gear2 damps more, and by how much is recorded.</summary>
    [Fact]
    public void RlcRing_TrapKeepsFrequencyAndDecay()
    {
        double w0 = 1 / Math.Sqrt(L * Cap), a = R / (2 * L), wd = Math.Sqrt(w0 * w0 - a * a);
        double fWant = wd / (2 * Math.PI);
        var (trap, _) = Run(Rlc("trap"));
        var (f, decay) = Ring(trap);
        output.WriteLine($"trap: f {f:G8} Hz (want {fWant:G8}), decay {decay:G6}/s (want {a:G6}); accepted {trap.AcceptedSteps}, " +
                         $"rejected {trap.RejectedSteps}, factorizations {trap.Factorizations}, rungs {trap.MethodRungsVisited}");
        Assert.True(Math.Abs(f / fWant - 1) < 1e-4, $"frequency off by {f / fWant - 1:E2}");
        Assert.True(Math.Abs(decay / a - 1) < 1e-3, $"decay off by {decay / a - 1:E2}");
        // gear2's extra damping, (ωh)⁴/4h, is ~1e-3 of this circuit's own at the steps the default tolerance picks — below
        // what the peak fit resolves on a ring that decays by itself. It is measured on an undamped LC instead (below).
    }

    /// <summary>Gate 2, second half — an undamped LC started at 1 V, at the default tolerance with steps up to 20 ns: trap keeps the
    /// amplitude (0.9999 after 20 cycles), gear2 damps it (0.900 when measured: about 0.5 % a cycle at the steps that tolerance picks).</summary>
    [Fact]
    public void LcRing_TrapKeepsAmplitude_Gear2DampsIt()
    {
        double Last(string method)
        {
            var (r, _) = Run($"""
                L:L1 a 0 L=1 uH
                C:C1 a 0 C=1 nF
                analysis TR1 type=tran Stop=4e-6 Step=1e-10 MaxStep=20e-9 InitialState=zero IC="a=1" Method={method}
                """);
            var v = Node(r, "a");
            double last = v.Skip(9 * v.Length / 10).Max(Math.Abs);
            output.WriteLine($"{method}: amplitude after 20 cycles {last:F6}; accepted {r.AcceptedSteps}, rejected {r.RejectedSteps}");
            return last;
        }
        double trap = Last("trap"), gear = Last("gear2");
        Assert.True(Math.Abs(trap - 1) < 1e-3, $"trap amplitude {trap:F6}");
        Assert.True(gear < 0.995 && gear > 0.85, $"gear2 amplitude {gear:F6}");
    }

    /// <summary>Gate 3 — on the RLC, a factorization per (method, rung) visited and per breakpoint landed, no more.</summary>
    [Fact]
    public void RlcRing_FactorsOncePerRung()
    {
        var (r, _) = Run(Rlc("trap"));
        output.WriteLine($"factorizations {r.Factorizations}, (method, rung) pairs {r.MethodRungsVisited}, breakpoints {r.BreakpointsLanded}, accepted {r.AcceptedSteps}");
        Assert.True(r.Factorizations <= r.MethodRungsVisited + r.BreakpointsLanded);
        Assert.True(r.AcceptedSteps > 100 * r.Factorizations, "each factorization should serve many steps");
    }

    private sealed class StepAt(double tStep, int row, double amps) : ITransientDrive
    {
        public IEnumerable<double> Breakpoints(double t0, double t1) => tStep > t0 && tStep <= t1 ? [tStep] : [];
        public void AddTo(double t, double[] rhs) { if (t > tStep) rhs[row] += amps; }
    }

    /// <summary>Gate 4 — a drive reporting a corner at 1 µs: a step lands on it exactly, and the two steps after it are backward
    /// Euler.</summary>
    [Fact]
    public void Breakpoint_IsLandedOnAndRestartsWithBackwardEuler()
    {
        const string cnl = """
            R:R1 out 0 R=1 kOhm
            C:C1 out 0 C=1 nF
            analysis TR1 type=tran Stop=3e-6 Step=1e-8 SaveSteps=yes
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.True(nl.Nodes.TryGetIndex("out", out int nd));
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals) with
        {
            Drives = [new StepAt(1e-6, nd - 1, 1e-3)],
        };
        var r = new TransientEngine(nl).Run(p);
        int at = Array.IndexOf(r.StepTime!, 1e-6);
        output.WriteLine($"landed at index {at}; methods around it: {new string(r.StepMethod![(at - 2)..(at + 4)])}");
        Assert.True(at > 0, "no accepted step at exactly 1 µs");
        Assert.Equal('E', r.StepMethod![at + 1]);
        Assert.Equal('E', r.StepMethod![at + 2]);
        Assert.Equal('T', r.StepMethod![at + 3]);
        // and the response is the RC's: 1 V (1 mA into 1 kΩ) after 2 µs, 1 − e^(−2)
        var v = Node(r, "out");
        Assert.True(Math.Abs(v[^1] - (1 - Math.Exp(-2))) < 2e-3, $"v(3 µs) = {v[^1]:G6}");
    }

    private const string Rectifier = """
        V_1Tone:Vs in 0 Freq=1e6 V=5 Phase=0 Vdc=0
        Diode:D1 in out Is=1e-14
        R:RL out 0 R=1 kOhm
        C:CL out 0 C=100 nF
        analysis TR1 type=tran Stop=20e-6 Step=1e-9
        """;

    /// <summary>Gate 5 — a half-wave rectifier into an RC: once settled, between the peak (where the diode turns off) and the
    /// trough the capacitor discharges through the load as e^(−t/RC); and both Newton paths give the same waveform.</summary>
    [Fact]
    public void Rectifier_DischargesThroughTheLoad_OnBothNewtonPaths()
    {
        var (r, _) = Run(Rectifier, p => p with { RelTol = 1e-6 });
        var v = Node(r, "out");
        output.WriteLine($"path {r.NewtonPathUsed} (N = {r.InterfaceCount}); accepted {r.AcceptedSteps}, rejected {r.RejectedSteps}, " +
                         $"Newton {r.NewtonIterations}, factorizations {r.Factorizations}");
        // the last whole cycle: 19-20 µs
        int i0 = Array.FindIndex(r.Time, t => t >= 19e-6);
        int iMax = i0, iMin = i0;
        for (int k = i0; k < v.Length; k++) { if (v[k] > v[iMax]) iMax = k; }
        for (int k = iMax; k < v.Length; k++) { if (v[k] < v[iMin] || iMin < iMax) iMin = k; }
        double want = v[iMax] * Math.Exp(-(r.Time[iMin] - r.Time[iMax]) / (1e3 * 100e-9));
        double ripple = v[iMax] - v[iMin];
        output.WriteLine($"peak {v[iMax]:G6} V at {r.Time[iMax]:G6} s, trough {v[iMin]:G6} V at {r.Time[iMin]:G6} s; discharge predicts {want:G6} V; ripple {ripple:G4} V");
        Assert.True(Math.Abs(v[iMin] - want) < 0.01 * ripple, $"trough {v[iMin]:G6} V against {want:G6} V");

        var (full, _) = Run(Rectifier, p => p with { RelTol = 1e-6, NewtonPath = TransientNewtonPath.Full });
        var vf = Node(full, "out");
        double diff = 0;
        for (int k = 0; k < v.Length; k++) diff = Math.Max(diff, Math.Abs(v[k] - vf[k]));
        output.WriteLine($"full path: accepted {full.AcceptedSteps}, Newton {full.NewtonIterations}, factorizations {full.Factorizations}; largest difference {diff:E3} V");
        Assert.True(diff < 1e-9 * 5, $"the two Newton paths differ by {diff:E3} V on a 5 V swing");
    }
}
