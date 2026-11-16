using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief-trn-2's gates: the waveform sources, tone sources with Delay/Ramp/Damping, IC= and <c>time</c>
/// (docs/design/transient.md §6, §7.2). Closed forms throughout.
/// </summary>
public sealed class TransientSourcesTests(ITestOutputHelper output)
{
    private static (TransientRunResult Result, ElaboratedNetlist Nl) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals);
        return (new TransientEngine(nl).Run(p), nl);
    }

    private static double[] Node(TransientRunResult r, string name)
    {
        int i = Array.IndexOf(r.NodeNames, name);
        Assert.True(i >= 0, $"node {name} is not recorded: {string.Join(", ", r.NodeNames)}");
        return [.. r.V.Select(v => v[i])];
    }

    /// <summary>V_Pulse V1=0 V2=1 Delay=10n Rise=2n Fall=3n Width=20n Period=50n, by its definition.</summary>
    private static double Pulse(double t)
    {
        double tau = t - 10e-9;
        if (tau <= 0) return 0;
        tau %= 50e-9;
        if (tau <= 2e-9) return tau / 2e-9;
        if (tau <= 22e-9) return 1;
        if (tau <= 25e-9) return 1 - (tau - 22e-9) / 3e-9;
        return 0;
    }

    /// <summary>Gate 1 — every corner of a pulse train is an accepted step, and the output equals the definition at every
    /// output point to round-off.</summary>
    [Fact]
    public void Gate1_Pulse_CornersAreSteps_AndTheWaveformIsTheDefinition()
    {
        var (r, _) = Run("""
            V_Pulse:V1 in 0 V1=0 V2=1 Delay=10n Rise=2n Fall=3n Width=20n Period=50n
            R:R1 in 0 R=1k
            analysis TR1 type=tran Stop=200n Step=0.5n SaveSteps=yes
            """);
        var steps = r.StepTime!;
        var corners = new List<double>();
        for (int k = 0; k < 4; k++)
            foreach (var c in new[] { 0, 2e-9, 22e-9, 25e-9 }) corners.Add(10e-9 + k * 50e-9 + c);
        // a corner is the step's time to the last bit the source's own sum of Delay, Period and the durations gives
        foreach (var c in corners) Assert.Contains(steps, st => Math.Abs(st - c) <= 1e-22);
        var v = Node(r, "in");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++) worst = Math.Max(worst, Math.Abs(v[k] - Pulse(r.Time[k])));
        output.WriteLine($"corners {corners.Count}, accepted {r.AcceptedSteps}, worst {worst:E3}");
        Assert.True(worst < 1e-12, $"worst departure from the definition {worst:E3}");
    }

    /// <summary>Gate 2 — V_PWL File= equals the same points given as Points=, bit for bit; a time that does not increase is
    /// refused naming the line.</summary>
    [Fact]
    public void Gate2_PwlFile_EqualsPoints_AndANonIncreasingTimeIsRefusedByLine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-trn2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "wave.csv");
            File.WriteAllText(file, "# time, value\n0, 0\n1e-8, 1  # rise\n\n3e-8 1\n4e-8,-0.5\n");
            const string tail = "\nR:R1 in 0 R=1k\nC:C1 in 0 C=1p\nanalysis TR1 type=tran Stop=60n Step=0.25n";
            var (fromPoints, _) = Run("V_PWL:V1 in 0 Points=\"0 0, 1e-8 1, 3e-8 1, 4e-8 -0.5\"" + tail);
            var (fromFile, _) = Run($"V_PWL:V1 in 0 File=\"{file}\"" + tail);
            Assert.Equal(Node(fromPoints, "in"), Node(fromFile, "in"));

            File.WriteAllText(file, "0 0\n1e-8 1\n1e-8 2\n");
            var ex = Assert.ThrowsAny<Exception>(() => Run($"V_PWL:V1 in 0 File=\"{file}\"" + tail));
            output.WriteLine(ex.Message);
            Assert.Contains("line 3", ex.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Gate 3 — a V_1Tone with Ramp=10/f rises along the raised cosine: the output is env(t)·cos(ωt) within 1e-3.</summary>
    [Fact]
    public void Gate3_Ramp_FollowsTheRaisedCosine()
    {
        var (r, _) = Run("""
            V_1Tone:V1 in 0 V=1 Freq=100 MHz Ramp=100n
            R:R1 in 0 R=50
            analysis TR1 type=tran Stop=200n Step=0.1n MaxStep=0.1n
            """);
        var v = Node(r, "in");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            double t = r.Time[k];
            double env = t >= 100e-9 ? 1 : 0.5 * (1 - Math.Cos(Math.PI * t / 100e-9));
            worst = Math.Max(worst, Math.Abs(v[k] - env * Math.Cos(2 * Math.PI * 1e8 * t)));
        }
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-3, $"worst departure from the raised cosine {worst:E3}");
    }

    /// <summary>Gate 8 — V_1Tone with Delay and Damping is A·e^(−θ(t−td))·sin(2πf(t−td)+φ) after Delay (a tone source's phase is
    /// a cosine's, so the sine's φ = 30° is Phase = −60°), and nothing before it.</summary>
    [Fact]
    public void Gate8_DampedSine_IsTheClosedFormAfterDelay()
    {
        var (r, _) = Run("""
            V_1Tone:V1 in 0 V=2 Freq=100 MHz Phase=-60 deg Delay=20n Damping=1e7
            R:R1 in 0 R=50
            analysis TR1 type=tran Stop=150n Step=0.1n MaxStep=0.1n SaveSteps=yes
            """);
        Assert.Contains(20e-9, r.StepTime!);
        var v = Node(r, "in");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            double t = r.Time[k], tau = t - 20e-9;
            // the closed form jumps at Delay (sin φ ≠ 0): the step that lands on Delay holds the level before it, so an output
            // point within the one step after Delay lies on the line across the jump, and is not a reading of the form
            if (tau > 0 && tau < 0.1e-9 * 1.001) continue;
            double want = tau <= 0 ? 0 : 2 * Math.Exp(-1e7 * tau) * Math.Sin(2 * Math.PI * 1e8 * tau + Math.PI / 6);
            worst = Math.Max(worst, Math.Abs(v[k] - want));
        }
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-4, $"worst departure from the damped sine {worst:E3}");
    }

    /// <summary>R-trn2-5 — Delay, Ramp and Damping are transient-only: a harmonic-balance result is unchanged by them, bit for bit.</summary>
    [Fact]
    public void ToneShapeKeys_LeaveHarmonicBalanceUnchanged()
    {
        const string bench = """
            V_1Tone:V1 in 0 V=0.3 Freq=1 GHz{0}
            R:R1 in a R=50
            Diode:D1 a 0 Is=1e-14
            analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-12
            """;
        var plain = Hb(string.Format(bench, ""));
        var shaped = Hb(string.Format(bench, " Delay=1n Ramp=2n Damping=1e6"));
        Assert.Equal(plain["V"].ComplexValues.ToArray(), shaped["V"].ComplexValues.ToArray());
    }

    /// <summary>Gate 4 — in HB and S-parameter runs a pulse source is its value at t = 0: both equal the same bench with Vdc at
    /// that value, bit for bit.</summary>
    [Fact]
    public void Gate4_SteadyState_APulseIsVdcAtItsFirstLevel()
    {
        const string hb = """
            {0}
            R:RB bias a R=1k
            V_1Tone:VS rf 0 V=0.1 Freq=1 GHz
            R:RS rf a R=50
            Diode:D1 a 0 Is=1e-14
            analysis HB1 type=hb Tone=1e9 MaxHarm=4 Tol=1e-12
            """;
        const string pulse = "V_Pulse:VB bias 0 V1=0.7 V2=1.5 Delay=1n Rise=1n Fall=1n Width=5n";
        const string vdc = "Vdc:VB bias 0 Vdc=0.7";
        Assert.Equal(Hb(string.Format(hb, vdc))["V"].ComplexValues.ToArray(), Hb(string.Format(hb, pulse))["V"].ComplexValues.ToArray());

        const string sp = """
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            R:R1 in mid R=20
            {0}
            C:C1 bias out C=1p
            R:R2 mid out R=30
            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            """;
        const string pulseSp = "V_Pulse:VB mid bias V1=0.7 V2=1.5 Delay=1n Rise=1n Fall=1n Width=5n";
        const string vdcSp = "Vdc:VB mid bias Vdc=0.7";
        Assert.Equal(Sp(string.Format(sp, vdcSp))["S"].ComplexValues.ToArray(), Sp(string.Format(sp, pulseSp))["S"].ComplexValues.ToArray());
    }

    /// <summary>Gate 5 — an RC with IC=1 on the capacitor and no source decays as e^(−t/τ) from 1 V within 1e-5.</summary>
    [Fact]
    public void Gate5_CapacitorIc_DecaysFromItsInitialVoltage()
    {
        var (r, _) = Run("""
            R:R1 out 0 R=1 kOhm
            C:C1 out 0 C=1 nF IC=1
            analysis TR1 type=tran Stop=5u Step=5n RelTol=1e-6
            """);
        var v = Node(r, "out");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++) worst = Math.Max(worst, Math.Abs(v[k] - Math.Exp(-r.Time[k] / 1e-6)));
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-5, $"worst {worst:E3}");
    }

    /// <summary>R-trn2-6 — an inductor's IC= is its starting current: an RL with IC=1m and no source decays as e^(−tR/L), the
    /// node at −R·i.</summary>
    [Fact]
    public void InductorIc_IsItsStartingCurrent()
    {
        var (r, _) = Run("""
            R:R1 a 0 R=1
            L:L1 a 0 L=1 uH IC=1m
            analysis TR1 type=tran Stop=5u Step=5n RelTol=1e-6
            """);
        var v = Node(r, "a");
        double worst = 0;
        for (int k = 1; k < r.Time.Length; k++) worst = Math.Max(worst, Math.Abs(v[k] + 1e-3 * Math.Exp(-r.Time[k] / 1e-6)));
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-8, $"worst {worst:E3}");
    }

    /// <summary>The TRN review: a variable whose value reads `time` is resolved once, before the run, so V_Expr reading it is
    /// refused by name — it used to play the steady-state value (5 V from t = 0) with the step lost.</summary>
    [Fact]
    public void VExpr_ReadingAVariableThatReadsTime_IsRefusedNamingIt()
    {
        var (lib, tb) = new CnlReader().Read("""
            gate = if(time>1u,1,0)
            V_Expr:V1 in 0 Expr="5*gate"
            R:R1 in 0 R=1k
            analysis TR1 type=tran Stop=2u
            """);
        var ex = Assert.ThrowsAny<Exception>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("'gate', whose value depends on time", ex.ToString());
    }

    /// <summary>Gate 6 — V_Expr Expr="1 - exp(-time/1u)" equals V_Exp with the same constants; `time` outside a condition in an
    /// HB bench is refused naming it, and inside one reads its steady state.</summary>
    [Fact]
    public void Gate6_Time_VExprEqualsVExp_AndOutsideAConditionIsRefused()
    {
        const string tail = "\nR:R1 in 0 R=1k\nanalysis TR1 type=tran Stop=5u Step=10n";
        var (expr, _) = Run("V_Expr:V1 in 0 Expr=\"1 - exp(-time/1u)\"" + tail);
        var (exp, _) = Run("V_Exp:V1 in 0 V1=0 V2=1 Delay1=0 Tau1=1u" + tail);
        var a = Node(expr, "in");
        var b = Node(exp, "in");
        double worst = a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-9, $"worst {worst:E3}");

        var ex = Assert.ThrowsAny<Exception>(() => Hb("""
            V_1Tone:V1 in 0 V=1 Freq=1 GHz
            R:R1 in 0 R=1000*(1+time)
            analysis HB1 type=hb Tone=1e9 MaxHarm=3
            """));
        output.WriteLine(ex.Message);
        Assert.Contains("'time'", ex.ToString());

        // inside a condition: steady state, time > 0 is true
        var (lib, tb) = new CnlReader().Read("""
            V_1Tone:V1 in 0 V=1 Freq=1 GHz
            R:R1 in 0 R=if(time>0,1000,2000)
            analysis HB1 type=hb Tone=1e9 MaxHarm=3
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(1000.0, nl.Components.Single(c => c.InstancePath == "R1").Parameters["R"].AsReal());
    }

    /// <summary>R-trn2-7 — an SDD's equation reads `time` in a transient run (a resistor to a ramp, loaded by an equal one, sits
    /// at half the ramp) and is refused naming it by a steady-state run.</summary>
    [Fact]
    public void Sdd_ReadsTimeInTransient_AndIsRefusedInSteadyState()
    {
        const string sdd = "SDD:S1 n 0 NumPorts=1 I[1,0]=(_v1-time/1e-6)/1000\nR:R2 n 0 R=1k\n";
        var (r, _) = Run(sdd + "analysis TR1 type=tran Stop=2u Step=10n");
        var v = Node(r, "n");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++) worst = Math.Max(worst, Math.Abs(v[k] - 0.5 * r.Time[k] / 1e-6));
        output.WriteLine($"worst {worst:E3}");
        Assert.True(worst < 1e-6, $"worst {worst:E3}");

        var ex = Assert.ThrowsAny<Exception>(() => Hb("V_1Tone:V1 a 0 V=1 Freq=1 GHz\nR:R1 a n R=50\n" + sdd
                                                       + "analysis HB1 type=hb Tone=1e9 MaxHarm=3"));
        output.WriteLine(ex.Message);
        Assert.Contains("'time'", ex.ToString());
    }

    /// <summary>R-trn2-2 — Idc is a constant current into its first node in transient, as in DC.</summary>
    [Fact]
    public void Idc_IsAConstantCurrentIntoItsFirstNode()
    {
        var (r, _) = Run("Idc:I1 n 0 Idc=2m\nR:R1 n 0 R=1k\nanalysis TR1 type=tran Stop=10n Step=1n");
        Assert.All(Node(r, "n"), v => Assert.Equal(2.0, v, 8));   // 2 mA into 1 kOhm, beside the 1e-12 S gmin
    }

    // R-trn2-5's refusal of a P1Tone with a harmonic termination of its own was lifted by brief TRN-4 R-trn4-6, which realizes
    // it (TransientLinesAndFitsTests.ToneSourceHarmonicTerminations_AreRealized).

    private static DataSet Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals);
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.Equal(1.0, ds["Converged"].RealValues[0]);
        return ds;
    }

    private static DataSet Sp(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, [1e9, 2e9, 3e9]);
    }
}
