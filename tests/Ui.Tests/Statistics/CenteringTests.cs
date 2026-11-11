using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-11's bench: the 1 V divider with R1 designable and both resistors ±2 % (1σ). The spec window
/// Vout ∈ [0.49, 0.51] V is centred at R1 = R2 = 1 kΩ, where the exact yield is 84.3 % (YieldDividerTests); R1 starts
/// at 1.2 kΩ, Vout = 0.4545 V, five σ below the window, so the start's yield is zero to many places.</summary>
internal static class CenteringCircuits
{
    public static string Divider(string center, string statistics = "") => $"""
        Vdc:V1 in  0   Vdc=1
        R:R1   in  out R=1200 Ohm
        R:R2   out 0   R=1000 Ohm
        analysis DC1 type=dc
        tune R1.R min=500 Ohm max=2000 Ohm opt=1 dist=gauss sd=2%
        tune R2.R dist=gauss sd=2%
        goal Vout = DC1.V("out") analysis=DC1 in 0.49 0.51 use=yield
        {(statistics.Length == 0 ? "" : "statistics " + statistics)}
        center {center}
        """;

    public static CenteringRun Create(string cnl)
    {
        var run = CenteringRun.Create(PreparedCircuit.FromText(cnl, null, null));
        Assert.Null(run.Refusal);
        return run;
    }

    /// <summary>R1's value in ohms, from its value text.</summary>
    public static double Ohms(string text) => double.Parse(text.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>R-ya11-1/3/4/5: centering moves the nominal toward the window's centre, and the verified gain is resolved.</summary>
public sealed class CenteringDividerTests
{
    [Fact]
    public void TheOffCentreDivider_MovesTowardTheWindowsCentre_AndItsVerifiedYieldRises()
    {
        var r = CenteringCircuits.Create(CenteringCircuits.Divider("trials=40 verify=400 maxiter=15")).Run();
        Assert.Equal(CenteringOutcome.Finished, r.Outcome);

        double centred = CenteringCircuits.Ohms(r.BestValues["R1.R"]);
        Assert.InRange(centred, 960, 1040);                      // 1200 Ω → the window's centre, 1 kΩ (±2 % of it)

        var v = r.Verification!;
        double halfWidths = (v.Start.Upper - v.Start.Lower) / 2 + (v.Best.Upper - v.Best.Lower) / 2;
        Assert.True(v.Best.Yield - v.Start.Yield > halfWidths, v.Sentence);
        Assert.False(v.WithinOverlap);
        Assert.Equal(400, v.Trials);
        Assert.NotNull(r.Verified!.Data);                         // the verification's full yield DataSet
        Assert.Equal(r.History.Count, r.Iterations);
    }
}

/// <summary>An E-series nominal is searched continuously and ends on the series; the trials around it stay continuous.</summary>
public sealed class CenteringSnapTests
{
    [Fact]
    public void AnE24Nominal_EndsOnTheSeries_AndItsTrialsVaryContinuously()
    {
        var cnl = CenteringCircuits.Divider("trials=40 verify=100 maxiter=15")
            .Replace("opt=1 dist=gauss", "opt=1 discrete=e24 dist=gauss");
        var r = CenteringCircuits.Create(cnl).Run();
        Assert.Equal(CenteringOutcome.Finished, r.Outcome);

        Assert.Contains(CenteringCircuits.Ohms(r.BestValues["R1.R"]), new[] { 910.0, 1000.0, 1100.0 });
        Assert.Contains(r.Notes, n => n.Id == "yield.center.snapped");
        Assert.True(r.Best!.Records.Select(t => t.Values["R1.R"]).Distinct().Count() > 1);
    }
}

/// <summary>R-ya11-2: one fixed set of trials — a candidate scores the same twice, and every candidate sees the same draws.</summary>
public sealed class CommonRandomNumbersTests
{
    [Fact]
    public void TheSameCandidateScoresIdentically_AndTheTrialSetDoesNotMove()
    {
        var run = CenteringCircuits.Create(CenteringCircuits.Divider("trials=20"));
        var a = run.Score([0.3]);
        var b = run.Score([0.4]);
        var again = run.Score([0.3]);

        Assert.Equal(a.Objective, again.Objective);
        Assert.Equal(a.Yield, again.Yield);
        Assert.Equal(run.TrialNumbers, a.Records.Select(t => t.Trial));
        Assert.Equal(run.TrialNumbers, b.Records.Select(t => t.Trial));
        for (int i = 0; i < a.Records.Count; i++)
        {
            Assert.Equal(a.Records[i].Z, b.Records[i].Z);         // the same standard normals …
            Assert.NotEqual(a.Records[i].Values["R1.R"], b.Records[i].Values["R1.R"]);   // … around a moved nominal
        }
    }
}

/// <summary>R-ya11-6: the center line round-trips byte-stable through .cnl and .csch, defaults never written.</summary>
public sealed class CenteringSettingsRoundTripTests
{
    private const string Full = "center algorithm=simplex trials=300 verify=2000 maxiter=40 maxevals=50000 timelimit=60 s width=0.1 parallel=4 seed=7 surrogate=quadratic";

    private static string CenterLine(string cnl) => cnl.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("center", StringComparison.Ordinal));

    [Fact]
    public void TheCenterLine_RoundTripsByteStable_AndDefaultsAreNotWritten()
    {
        var (lib, tb) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\n" + Full);
        Assert.Empty(tb.ReadWarnings);
        Assert.Equal(Full, CenterLine(CnlWriter.Write(tb, lib)));

        var model = new SchematicEditModel();
        model.Components.Add(TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "50", "Ohm")));
        model.Tuning = tb.Tuning;
        string csch = SchematicPersistence.Serialize(model, "Flat");
        var (back, _, _) = SchematicPersistence.Deserialize(csch);
        Assert.Equal(csch, SchematicPersistence.Serialize(back, "Flat"));
        Assert.Equal(Full, CenterLine(CnlWriter.Write(NetExtractor.Extract(back, "tb").TestBench)));

        var (lib0, tb0) = new CnlReader().Read("R:R1 a 0 R=50 Ohm\ncenter algorithm=cmaes trials=200 verify=1000 width=0.05 seed=1");
        Assert.Equal("center", CenterLine(CnlWriter.Write(tb0, lib0)));
    }
}

/// <summary>R-ya11-8: the verb as a process — exit 0/3 by target on the verified yield, and --save-preset adds one preset.</summary>
public sealed class CenterCliVerbTests
{
    private static string Bench => CenteringCircuits.Divider("trials=30 verify=200 maxiter=10");

    [Fact]
    public void ExitsThreeBelowAnUnreachableTarget()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "div.cnl", Bench);
        var (exit, stdout, stderr) = OptCli.Run("yield", "center", cnl, "--target", "99%", "--json");
        Assert.True(exit == 3, stderr);
        var r = JsonNode.Parse(stdout)!["result"]!["center"]!;
        Assert.Equal("belowTarget", r["outcome"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(cnl)!, "div.yield.npy")));
    }

    [Fact]
    public void SavePreset_OnASchematic_AddsExactlyOnePreset_AtAReachableTarget()
    {
        string dir = OptCli.Dir();
        string cnl = OptCli.Write(dir, "div.cnl", Bench);
        string csch = Path.Combine(dir, "div.csch");
        Assert.Equal(0, OptCli.Run("netlist", cnl, "--to-schematic", "-o", csch).Exit);
        string before = File.ReadAllText(csch);

        var (exit, stdout, stderr) = OptCli.Run("yield", "center", csch, "--target", "50%", "--save-preset", "centred", "--json");
        Assert.True(exit == 0, stderr);

        var (model, view, cell) = SchematicPersistence.LoadFromFile(csch);
        var preset = Assert.Single(model.Tuning!.Presets);
        Assert.Equal("centred", preset.Name);
        var best = JsonNode.Parse(stdout)!["result"]!["center"]!["bestValues"]!.AsObject();
        Assert.Equal(best["R1.R"]!.GetValue<string>(), preset.Values["R1.R"]);

        model.Tuning.Presets.Clear();
        Assert.Equal(before, SchematicPersistence.Serialize(model, cell, view.PanX, view.PanY, view.Zoom));
    }
}
