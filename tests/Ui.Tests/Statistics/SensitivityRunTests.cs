using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>Benches for the SENS-1 gates.</summary>
internal static class SensCircuits
{
    /// <summary>R1 = 1100 Ω over R2 = 1000 Ω from 1 V, so Vout ≈ 0.476 V sits below the band [0.49, 0.51] and its
    /// margin is Vout − 0.49 across every step — linear in Vout, with no edge to switch to.</summary>
    public static string Divider(string tolerances, string sensitivity = "factors=stat") => $"""
        Vdc:V1 in  0   Vdc=1
        R:R1   in  out R=1100 Ohm
        R:R2   out 0   R=1000 Ohm
        analysis DC1 type=dc
        {tolerances}
        goal Vout = DC1.V("out") analysis=DC1 in 0.49 0.51 use=yield
        sensitivity {sensitivity}
        """;

    /// <summary>Vout of the divider at R1, R2.</summary>
    public static double V(double r1, double r2) => r2 / (r1 + r2);

    public static SensitivityRun Create(string cnl, SensitivityOptions? options = null)
    {
        var run = SensitivityRun.Create(PreparedCircuit.FromText(cnl, null, null), options);
        Assert.Null(run.Refusal);
        return run;
    }
}

/// <summary>R-sens1-3/4, D2–D6: the ranking, the cache, the estimate rule and the nonlinear flag.</summary>
public sealed class SensitivityRunTests
{
    [Fact]
    public void Divider_R2RanksFirst_DeltaIsTheClosedFormStep_AndSharesSumToOne()
    {
        var r = SensCircuits.Create(SensCircuits.Divider("tune R1.R dist=gauss sd=1%\ntune R2.R dist=gauss sd=5%")).Run();
        Assert.Equal(SensitivityOutcome.Finished, r.Outcome);
        var g = Assert.Single(r.Goals);
        Assert.Equal(["R2.R", "R1.R"], g.Contributors.Select(c => c.Factor.Key));
        Assert.All(g.Contributors, c => Assert.Equal("σ", c.Factor.BasisText));

        // margin = Vout − 0.49 and scale = the band's width 0.02, so Δ = (V(+σ) − V(−σ)) / 2 / 0.02 exactly.
        double d2 = (SensCircuits.V(1100, 1050) - SensCircuits.V(1100, 950)) / 2 / 0.02;
        double d1 = (SensCircuits.V(1111, 1000) - SensCircuits.V(1089, 1000)) / 2 / 0.02;
        Assert.Equal(d2, g.Contributors[0].Delta, 1e-6);
        Assert.Equal(d1, g.Contributors[1].Delta, 1e-6);
        Assert.Equal(1.0, g.Contributors.Sum(c => c.Share), 12);
        Assert.Equal((SensCircuits.V(1100, 1000) - 0.49) / 0.02, g.Nominal, 1e-6);
    }

    [Fact]
    public void TheRunSpends2kPlus1_AndASecondRunOnTheSameEvaluatorSpendsNone()
    {
        var run = SensCircuits.Create(SensCircuits.Divider("tune R1.R dist=gauss sd=1%\ntune R2.R dist=gauss sd=5%"));
        Assert.Equal(5, run.EvaluationsToSpend);
        Assert.Equal(5, run.Run().Evaluations);
        Assert.Equal(0, run.EvaluationsToSpend);
        Assert.Equal(0, run.Run().Evaluations);
    }

    [Fact]
    public void AnEstimate_OnlyWhenEveryBasisIsSigma()
    {
        // R1 toleranced, R2 (and V1.Vdc) stepped by 1 %: no estimate, and the note says why.
        var mixed = SensCircuits.Create(SensCircuits.Divider("tune R1.R dist=gauss sd=1%", "factors=all")).Run();
        var g = Assert.Single(mixed.Goals);
        Assert.Contains(g.Contributors, c => c.Factor.Key == "R2.R" && c.Factor.BasisText == "1 %");
        Assert.Null(g.Estimate);
        Assert.Contains(mixed.Notes, n => n.Id == "yield.sens.no-estimate");

        var both = SensCircuits.Create(SensCircuits.Divider("tune R1.R dist=gauss sd=1%\ntune R2.R dist=gauss sd=5%")).Run();
        var b = Assert.Single(both.Goals);
        Assert.Equal(NormalDistribution.Cdf(b.Nominal / b.Spread), b.Estimate!.Value, 12);
        Assert.DoesNotContain(both.Notes, n => n.Id == "yield.sens.no-estimate");
    }

    [Fact]
    public void AGoalWhoseTightestPointJumpsBetweenTheSteps_IsFlaggedNonlinear()
    {
        // A series LC resonant at 1.5 GHz, the geometric centre of the goal's band: + L makes the top edge the tightest
        // point, − L the bottom one, so both steps cost margin and the two halves of Δ disagree.
        const string cnl = """
            Port:P1 in  0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            L:L1 in  mid L=11.2579 nH
            C:C1 mid out C=1 pF
            analysis SP1 type=sparam start=1 stop=2 npts=101 Unit=GHz
            goal Pass = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1.25 GHz hi=1.8 GHz ge -3
            """;
        var r = SensCircuits.Create(cnl, new SensitivityOptions { Vars = ["L1.L"] }).Run();
        var c = Assert.Single(Assert.Single(r.Goals).Contributors);
        Assert.Equal(SensitivityStatus.Nonlinear, c.Status);
        Assert.True(c.Asymmetry > SensitivityRun.NonlinearThreshold, $"asymmetry {c.Asymmetry}");
    }
}

/// <summary>The ports' own values are not factors of factors=all unless ports=on — one note names them.</summary>
public sealed class SensitivityPortsTests
{
    private const string Lc = """
        Port:P1 in  0 Num=1 Z=50 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        L:L1 in  out L=10 nH
        analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
        goal Pass = dB(SP1.S(2,1)) analysis=SP1 ge -3
        sensitivity {0}
        """;

    [Fact]
    public void PortImpedances_AreLeftOutByDefault_AndSteppedWithPortsOn()
    {
        var off = SensCircuits.Create(Lc.Replace("{0}", "factors=all"));
        Assert.Equal(["L1.L"], off.Factors.Select(f => f.Key));
        Assert.Contains(off.Notes, n => n.Id == "yield.sens.left-out" && n.Render().Contains("P1.Z, P2.Z"));

        var on = SensCircuits.Create(Lc.Replace("{0}", "ports=on"));
        Assert.Equal(["L1.L", "P1.Z", "P2.Z"], on.Factors.Select(f => f.Key).Order(StringComparer.Ordinal));
    }
}

/// <summary>Harmonic balance: a driven nonlinear resistor's fundamental and third harmonic rank, and the source's tone
/// frequency — fixed by the analysis's tone grid — is left out of factors=all rather than failing.</summary>
public sealed class SensitivityHarmonicBalanceTests
{
    [Fact]
    public void AnHbGoal_Ranks_AndTheToneFrequencyIsLeftOut()
    {
        const string cnl = """
            V_1Tone:V1 in 0 Freq=1e9 V=1
            R:R1 in out R=50 Ohm
            SDD:D1 out 0 I[1,0]=_v1/100+0.02*_v1^3
            analysis HB1 type=hb Tone=1e9 MaxHarm=5
            tune R1.R dist=gauss sd=2%
            goal H3 = mag(HB1.V("out",3)) analysis=HB1 le 0.05 use=yield
            """;
        var run = SensCircuits.Create(cnl);
        Assert.DoesNotContain(run.Factors, f => f.Key == "V1.Freq");
        Assert.Contains(run.Notes, n => n.Id == "yield.sens.left-out" && n.Render().Contains("V1.Freq"));
        var g = Assert.Single(run.Run().Goals);
        Assert.Equal(["V1.V", "R1.R"], g.Contributors.Select(c => c.Factor.Key));
        Assert.All(g.Contributors, c => Assert.Equal(SensitivityStatus.Ok, c.Status));
    }
}

/// <summary>R-sens1-5: the sensitivity line round-trips byte-stable through .cnl and .csch, defaults unwritten.</summary>
public sealed class SensitivityLineTests
{
    [Fact]
    public void TheSensitivityLine_RoundTripsByteStable_ThroughCnlAndCsch_DefaultsUnwritten()
    {
        const string line = "sensitivity factors=stat step=2% sigma=3 ports=on snp=off snpexclude=S1,S2 snpdb=0.5 snpdeg=2 responses=all parallel=4";
        var (lib, tb) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\n" + line);
        Assert.Empty(tb.ReadWarnings);
        Assert.Contains(line, CnlWriter.Write(tb, lib).Split('\n').Select(l => l.TrimEnd('\r')));

        var (lib2, tb2) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\nsensitivity factors=all step=1% sigma=1 ports=off snp=on snpdb=0.1 snpdeg=1 responses=goals");
        Assert.Contains("sensitivity", CnlWriter.Write(tb2, lib2).Split('\n').Select(l => l.TrimEnd('\r')));

        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Tuning = tb.Tuning;
        string csch = SchematicPersistence.Serialize(model, "Flat");
        var (m1, _, _) = SchematicPersistence.Deserialize(csch);
        Assert.Equal(csch, SchematicPersistence.Serialize(m1, "Flat"));
        Assert.Contains(line, CnlWriter.Write(NetExtractor.Extract(m1, "tb").TestBench).Split('\n').Select(l => l.TrimEnd('\r')));
    }
}

/// <summary>R-sens1-7: <c>yield sens</c> as a process writes the in-process run's bytes, and its --json is the run.</summary>
public sealed class YieldSensCliTests
{
    [Fact]
    public void TheVerbsDataSet_IsTheInProcessRunsBytes_AndItsJsonIsTheRun()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "div.cnl", SensCircuits.Divider("tune R1.R dist=gauss sd=1%\ntune R2.R dist=gauss sd=5%", "factors=all"));
        string verb = Path.Combine(dir, "verb.npy"), mine = Path.Combine(dir, "mine.npy");
        var (exit, stdout, stderr) = OptCli.Run("yield", "sens", cnl, "-o", verb, "--json");
        Assert.True(exit == 0, stderr);

        var run = SensitivityRun.Create(PreparedCircuit.FromFile(cnl, dir), new SensitivityOptions { ResultPath = mine });
        var r = run.Run();
        Assert.Equal(File.ReadAllBytes(mine), File.ReadAllBytes(verb));

        var json = JsonNode.Parse(stdout)!["result"]!["sensitivity"]!;
        var goal = Assert.Single(json["goals"]!.AsArray())!;
        var mineGoal = Assert.Single(r.Goals);
        Assert.Equal(mineGoal.Contributors.Select(c => c.Factor.Key), goal["contributors"]!.AsArray().Select(c => c!["key"]!.GetValue<string>()));
        Assert.Equal(mineGoal.Contributors.Select(c => c.Delta), goal["contributors"]!.AsArray().Select(c => c!["delta"]!.GetValue<double>()));
        Assert.Equal(r.Evaluations, json["evaluations"]!.GetValue<long>());
    }
}
