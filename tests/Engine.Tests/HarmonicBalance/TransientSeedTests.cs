// ================================================================
//  TransientSeedTests.cs — brief TRN-8: transient-assisted harmonic balance (docs/design/transient.md §10).
//
//  Gate 1/2: a hard-driven class-E stage that harmonic balance does not converge from DC within its iteration budget with no
//  drive stepping converges from the transient seed, to the drive-stepped solution, in fewer Newton iterations than the drive
//  ladder takes. Gate 3: a parametric divide-by-two: the DC start finds the undivided solution, the seed the divided one, which
//  a long native transient confirms. Gate 4: a PA biased through 10 uH chokes and 1 uF blocks at 2 GHz seeds within the cap with
//  fast bias and does not settle without it; the harmonic balance is the same either way. R-trn8-3/4: incommensurate tones are
//  refused, and in a sweep only the first point is seeded. Gate 5 (the shipped oscillator) is in Ui.Tests, which reads it.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public sealed class TransientSeedTests(ITestOutputHelper output)
{
    private sealed record Hb(HbRunResult Run, DataSet Ds, IReadOnlyList<string> Notes, int Iterations);

    private static Hb RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        using var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var rr = new HbEngine(nl, tb).Run(p);
        return new Hb(rr, rr.DataSet, [.. nl.Notes, .. nl.Warnings], rr.Trace!.Steps.Sum(s => s.Iterations));
    }

    private static Complex V(DataSet ds, string node, int k)
    {
        var v = ds["V"];
        return (Complex)v[Array.IndexOf(v.Axes[0].Labels!, node), k];
    }

    /// <summary>The largest difference between two runs' V, each node's harmonics against that node's largest harmonic.</summary>
    private static double WorstDifference(DataSet a, DataSet b)
    {
        var va = a["V"]; var vb = b["V"];
        double worst = 0;
        for (int n = 0; n < va.Axes[0].Length; n++)
        {
            double big = 1e-12;
            for (int k = 0; k < va.Axes[1].Length; k++) big = Math.Max(big, ((Complex)va[n, k]).Magnitude);
            for (int k = 0; k < va.Axes[1].Length; k++)
                worst = Math.Max(worst, ((Complex)va[n, k] - (Complex)vb[n, k]).Magnitude / big);
        }
        return worst;
    }

    private static double Scalar(DataSet ds, string name) => ds[name].RealValues[0];

    // A class-E stage at 1 GHz on a built-in FET law with its gate diode and a drain body diode: 5 V through a 100 nH choke, the
    // shunt capacitor at the drain, a series L0-C0 with the class-E excess reactance into 10 ohm, the gate driven at 30 dBm
    // from -2.5 V. Harmonic balance's line search reaches it from DC in 29 Newton iterations; MaxIter=20 is the budget that
    // makes the DC start fail, as HbDriveSteppingTests caps one to make a cold start fail.
    private static string ClassE(string keys) => $@"
P1Tone:SRC in 0 Num=1 Pavl=30 dBm Z=50 Freq=1 GHz
C:CIN in g C=10p
L:LG gb g L=100n
Vdc:VG gb 0 Vdc=-2.5
FET_Angelov:Q1 g d 0 Ipk=0.08 Vpk=-0.8 P1=1.2 Alpha=2 Lambda=0.02 Cgs=0.4p Cgd=0.04p
Diode:DG g 0 Is=1e-12 N=1.2
Diode:DB 0 d Is=1e-12 N=1
L:LD vdd d L=100n
Vdc:VD vdd 0 Vdc=5
C:C1 d 0 C=2.92p
L:L0 d m L=7.96n
C:C0 m out C=4.13p
R:RL out 0 R=10
analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=15 Tol=1e-9 MaxIter=20 {keys}
";

    [Fact]
    public void Gate1And2_ClassE_ConvergesFromTheSeed_ToTheDriveSteppedSolution_InFewerIterations()
    {
        var dc = RunHb(ClassE("DriveStepping=Never"));
        var seeded = RunHb(ClassE("DriveStepping=Never InitialGuess=transient"));
        var stepped = RunHb(ClassE("DriveStepping=Always"));
        output.WriteLine($"DC start: converged {dc.Run.Converged} after {dc.Iterations}; drive-stepped: {stepped.Run.Converged}, " +
                         $"{stepped.Iterations} over {stepped.Run.Trace!.Steps.Count} rungs; seeded: {seeded.Run.Converged}, " +
                         $"Seed.HbIterations {Scalar(seeded.Ds, "Seed.HbIterations")}, Seed.Periods {Scalar(seeded.Ds, "Seed.Periods")}");
        foreach (var n in seeded.Notes) output.WriteLine("  " + n);

        Assert.False(dc.Run.Converged);
        Assert.True(seeded.Run.Converged);
        Assert.True(stepped.Run.Converged);
        double worst = WorstDifference(seeded.Ds, stepped.Ds);
        output.WriteLine($"worst interface difference {worst:E2} of each node's largest harmonic");
        Assert.True(worst < 1e-6, $"seeded and drive-stepped differ by {worst:E2}");
        Assert.Equal(1, Scalar(seeded.Ds, "Seed.Settled"));
        Assert.Contains(seeded.Notes, n => n.StartsWith(HbTransientSeed.Note, StringComparison.Ordinal));

        // Gate 2 — a counter, not a time.
        Assert.True(Scalar(seeded.Ds, "Seed.HbIterations") < stepped.Iterations,
            $"seeded {Scalar(seeded.Ds, "Seed.HbIterations")} against drive-stepped {stepped.Iterations}");

        // InitialGuess=fallback is the DC start, then the seed: the same solution.
        var fallback = RunHb(ClassE("DriveStepping=Never InitialGuess=fallback"));
        Assert.True(fallback.Run.Converged);
        Assert.True(WorstDifference(fallback.Ds, stepped.Ds) < 1e-6);
    }

    // A degenerate parametric divider: a 1 GHz tank (10 nH, 2.32 pF and 1 kohm) across a capacitance whose charge is
    // 0.3 pC/V^2 v^2 + 0.05 pC/V^3 v^3, pumped at 2 GHz by a 39 mA current source started at zero (Phase=-90). The pump's 1.68 V
    // modulates the capacitance by about 0.4 of the tank's, above the 2/Q threshold, and the cubic term detunes the tank as the
    // half-frequency grows until it holds. The capacitance stays positive at every voltage (c3 > c2^2/(3 CT)).
    private static string Divider(string analysis) => $@"
I_1Tone:IP 0 n Freq=2 GHz I=39 mA Phase=-90
L:LT n 0 L=10n
C:CT n 0 C=2.32p
R:RT n 0 R=1000
SDD:DV n 0 Ports=1 I[1,1]=0.3e-12*_v1^2+0.05e-12*_v1^3
{analysis}
";

    [Fact]
    public void Gate3_Divider_DcFindsTheUndividedSolution_TheSeedTheDivided()
    {
        const string hb = "analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=8 Tol=1e-12";
        var dc = RunHb(Divider(hb));
        var seeded = RunHb(Divider(hb + " InitialGuess=transient"));
        double dc1 = V(dc.Ds, "n", 1).Magnitude, dc2 = V(dc.Ds, "n", 2).Magnitude;
        double s1 = V(seeded.Ds, "n", 1).Magnitude;
        output.WriteLine($"DC start: |V1| {dc1:G4}, |V2| {dc2:G6}; seeded: |V1| {s1:G6}, |V2| {V(seeded.Ds, "n", 2).Magnitude:G6}");
        Assert.True(dc.Run.Converged && seeded.Run.Converged);
        Assert.True(dc1 < 1e-9 * dc2, "from DC the half-frequency stays at zero: the undivided solution");
        Assert.True(s1 > 1, "from the seed: the divided solution");

        // A long native transient, its last half-frequency period transformed.
        var (lib, tb) = new CnlReader().Read(Divider("analysis TR1 type=tran Stop=100n Step=10p"));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var tr = TransientRunner.Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals), nl);
        var v = tr["V"].RealValues;
        var t = tr["V"].Axes[0].Values;
        Complex a = 0;
        for (int j = v.Length - 100; j < v.Length; j++) a += v[j] * Complex.Exp(new Complex(0, -2 * Math.PI * 1e9 * t[j]));
        double tran1 = 2 * a.Magnitude / 100;
        output.WriteLine($"100 ns transient: |V1| {tran1:G6} ({100 * (tran1 / s1 - 1):F3} % from harmonic balance)");
        Assert.True(Math.Abs(tran1 / s1 - 1) < 1e-2);
    }

    // A 2 GHz PA biased through 10 uH chokes and coupled through 1 uF blocks.
    private static string BiasedPa(string keys) => $@"
P1Tone:SRC in 0 Num=1 Pavl=10 dBm Z=50 Freq=2 GHz
C:CIN in g C=1u
L:LG gb g L=10u
Vdc:VG gb 0 Vdc=-0.8
FET_Angelov:Q1 g d 0 Ipk=0.08 Vpk=-0.8 P1=1.2 Alpha=2 Lambda=0.02 Cgs=0.4p Cgd=0.04p
L:LD db d L=10u
Vdc:VD db 0 Vdc=5
C:COUT d m C=1u
L:LM m out L=3n
C:CM out 0 C=1p
R:RL out 0 R=50
analysis HB1 type=hb Tone=2 ToneUnit=GHz MaxHarm=7 Tol=1e-9 InitialGuess=transient {keys}
";

    [Fact]
    public void Gate4_FastBias_SettlesWithinTheCap_AndNotWithout_SameHarmonicBalance()
    {
        var fast = RunHb(BiasedPa(""));
        var slow = RunHb(BiasedPa("SeedFastBias=no"));
        foreach (var n in fast.Notes.Concat(slow.Notes)) output.WriteLine("  " + n);
        Assert.Equal(1, Scalar(fast.Ds, "Seed.Settled"));
        Assert.True(Scalar(fast.Ds, "Seed.Periods") <= HbSeedSettings.AutoCap);
        Assert.Equal(0, Scalar(slow.Ds, "Seed.Settled"));
        Assert.Equal(HbSeedSettings.AutoCap, Scalar(slow.Ds, "Seed.Periods"));
        var scaled = fast.Ds["Seed.ScaledElements"];
        Assert.Equal(["CIN", "LG", "LD", "COUT"], scaled.Axes[0].Labels!);
        Assert.Equal(0, slow.Ds["Seed.ScaledElements"].Axes[0].Length);
        Assert.True(fast.Run.Converged && slow.Run.Converged);
        double worst = WorstDifference(fast.Ds, slow.Ds);
        output.WriteLine($"worst difference {worst:E2}");
        Assert.True(worst < 1e-6);
    }

    // R-trn8-3 — two tones with no common fundamental within the retained products.
    [Fact]
    public void IncommensurateTones_AreRefusedWithTransient_AndStartFromDcWithFallback()
    {
        string Two(string keys) => $@"
V_1Tone:V1 a 0 Freq=1 GHz V=0.1
V_1Tone:V2 a2 a Freq=1.0001234567 GHz V=0.1
R:RS a2 b R=50
Diode:D1 b 0 Is=1e-14
analysis HB1 type=hb NumFreqs=2 Tone[1]=1e9 Tone[2]=1.0001234567e9 MaxHarm=3 MaxMixOrder=3 {keys}
";
        var ex = Assert.Throws<InvalidOperationException>(() => RunHb(Two("InitialGuess=transient")));
        output.WriteLine(ex.Message);
        Assert.Contains("no period to seed from", ex.Message);
        var fb = RunHb(Two("InitialGuess=fallback"));
        Assert.True(fb.Run.Converged);
        Assert.Contains(fb.Notes, n => n.Contains("InitialGuess=fallback starts harmonic balance from DC"));
    }

    // R-trn8-4 — in a parametric sweep only the first point is seeded; the rest warm-start from their neighbour as before.
    [Fact]
    public void Sweep_OnlyTheFirstPointIsSeeded()
    {
        string cnl = "ip = 0.039\n" + Divider("analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=8 Tol=1e-12 InitialGuess=transient\n" +
                                            "analysis SW1 type=parametric_sweep Var=ip Values=0.039,0.040,0.041 Inner=HB1")
                                    .Replace("I=39 mA", "I=ip");
        var (lib, tb) = new CnlReader().Read(cnl);
        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().Single();
        var ds = ParametricSweepEngine.Run(sweep, lib, tb);
        var periods = ds["Seed.Periods"].RealValues;
        output.WriteLine($"Seed.Periods {string.Join(", ", periods)}");
        Assert.True(periods[0] > 0);
        Assert.Equal([0.0, 0.0], periods[1..]);
        var v = ds["V"];
        foreach (int i in new[] { 0, 1, 2 })
            Assert.True(((Complex)v[i, Array.IndexOf(v.Axes[1].Labels!, "n"), 1]).Magnitude > 1, "every point on the divided branch");
    }
}
