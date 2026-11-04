using System.Text.Json.Nodes;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tests.Optimization;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>brief-yield-5 §3: the <c>yield</c> verb as a process, its parity with the in-process run, MCP
/// <c>run analysis=yield</c>, the generated reference page and the agent walk-through. Every circuit is the 1 V
/// divider of <see cref="YieldCircuits"/>, so a 200-trial run is a few hundred DC solves.</summary>
internal static class YieldCli
{
    /// <summary>The divider, 200 trials, yield ≈ 88 %.</summary>
    public static string Divider(string dir, string name = "div.cnl", string statistics = "trials=200")
        => OptCli.Write(dir, name, YieldCircuits.Divider(statistics));

    /// <summary>The <c>result.yield</c> object of a <c>--json</c> document.</summary>
    public static JsonObject Report(string json) => JsonNode.Parse(json)!["result"]!["yield"]!.AsObject();
}

/// <summary>R-ya5-1…3: the verb as a process.</summary>
public sealed class YieldCliVerbTests
{
    [Fact]
    public void Estimate_ExitsZeroAtAReachableTarget_AndThreeAtAnUnreachableOne_NamingTheGoal()
    {
        string cnl = YieldCli.Divider(OptCli.Dir());
        var (met, metOut, metErr) = OptCli.Run("yield", "estimate", cnl, "--target", "50%", "--json");
        Assert.True(met == 0, metErr);
        Assert.True(YieldCli.Report(metOut)["targetMet"]!.GetValue<bool>());

        var (missed, stdout, _) = OptCli.Run("yield", "estimate", cnl, "--target", "99%");
        Assert.Equal(3, missed);
        Assert.Contains("target 99.0 %: NOT MET", stdout);
        Assert.Matches(@"(?m)^  Vout\s+\d+\.\d %", stdout);       // the goal's own row, its yield a percent
    }

    [Fact]
    public void MonteCarlo_WithNoGoal_ExitsZero()
    {
        string cnl = OptCli.Write(OptCli.Dir(), "spread.cnl", """
            Vdc:V1 in  0   Vdc=1
            R:R1   in  out R=1000 Ohm
            R:R2   out 0   R=1000 Ohm
            analysis DC1 type=dc
            measure Vo = DC1.V("out")
            tune R1.R dist=gauss sd=2%
            statistics trials=50
            """);
        var (exit, stdout, stderr) = OptCli.Run("yield", "mc", cnl, "--json");
        Assert.True(exit == 0, stderr);
        var r = YieldCli.Report(stdout);
        Assert.Equal("montecarlo", r["mode"]!.GetValue<string>());
        Assert.Null(r["yield"]);
        Assert.Equal(50, r["statistics"]!.AsArray().Single(s => s!["of"]!.GetValue<string>() == "Vo")!["count"]!.GetValue<int>());
    }

    [Fact]
    public void Trial7_ReRunAlone_DrawsTheFullRunsValues()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir);
        Assert.Equal(0, OptCli.Run("yield", "estimate", cnl).Exit);
        var (full, _) = DataSetImporter.Import(Path.Combine(dir, "div.yield.npy"));

        var (exit, stdout, stderr) = OptCli.Run("yield", "trial", cnl, "--trial", "7", "--json");
        Assert.True(exit == 0, stderr);
        var draws = YieldCli.Report(stdout)["trial"]!["draws"]!.AsObject();
        foreach (var key in new[] { "R1.R", "R2.R" })
            Assert.Equal(full["trials.stat:" + key].RealValues[6], draws[key]!.GetValue<double>());
    }

    /// <summary>R-ya5-6: a statistics function in a trace expression plots — a histogram over its bin axis.</summary>
    [Fact]
    public void Plot_TakesAStatisticsFunction()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir);
        Assert.Equal(0, OptCli.Run("yield", "estimate", cnl).Exit);
        string svg = Path.Combine(dir, "h.svg");
        var (exit, _, stderr) = OptCli.Run("plot", Path.Combine(dir, "div.yield.npy"), "-o", svg,
                                           "--trace", "cube=histogram(trials.goal:Vout:worst, 20)");
        Assert.True(exit == 0, stderr);
        Assert.True(File.Exists(svg));
    }

    [Fact]
    public void SaveCorner_OnASchematic_AddsOneCornerLine_AndChangesNothingElse()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir);
        string csch = Path.Combine(dir, "div.csch");
        Assert.Equal(0, OptCli.Run("netlist", cnl, "--to-schematic", "-o", csch).Exit);
        string before = File.ReadAllText(csch);

        var (exit, _, stderr) = OptCli.Run("yield", "estimate", csch, "--trial", "3", "--save-corner", "Worst");
        Assert.True(exit == 0, stderr);

        var (model, view, cell) = SchematicPersistence.LoadFromFile(csch);
        var corner = Assert.Single(model.Tuning!.Corners);
        Assert.Equal(("Worst", 3, 1, 200), (corner.Name, corner.Trial, corner.Seed, corner.Trials));
        model.Tuning.Corners.Clear();
        Assert.Equal(before, SchematicPersistence.Serialize(model, cell, view.PanX, view.PanY, view.Zoom));

        var netlist = OptCli.Run("netlist", csch).StdOut;
        Assert.Single(netlist.Split('\n'), l => l.StartsWith("corner ", StringComparison.Ordinal));
    }
}

/// <summary>R-ya5-4: the verb's result file is byte for byte the in-process run's, same file and seed.</summary>
public sealed class YieldParityTests
{
    [Fact]
    public void TheVerbsDataSet_IsTheInProcessRunsBytes()
    {
        string dir = OptCli.Dir();
        string cnl = YieldCli.Divider(dir);
        string verb = Path.Combine(dir, "verb.npy"), mine = Path.Combine(dir, "mine.npy");
        Assert.Equal(0, OptCli.Run("yield", "estimate", cnl, "-o", verb).Exit);

        var run = StatisticalRun.Create(PreparedCircuit.FromFile(cnl, dir),
                                        new StatisticalOptions { Mode = StatisticalMode.Yield, ResultPath = mine });
        Assert.Equal(StatisticalOutcome.Finished, run.Run().Outcome);
        Assert.Equal(File.ReadAllBytes(mine), File.ReadAllBytes(verb));
    }
}

/// <summary>R-ya5-5: <c>run analysis=yield</c> returns the verb's <c>--json</c> object, sends a progress
/// notification per batch, and a cancelled call answers 130 and writes nothing.</summary>
public sealed class YieldMcpTests
{
    [Fact]
    public void RunYield_ReturnsTheJsonObject_ReportsProgress_AndCancels()
    {
        string root = OptCli.Dir();
        string cnl = YieldCli.Divider(root);
        var expected = YieldCli.Report(OptCli.Run("yield", "estimate", cnl, "--json").StdOut);

        using var server = new OptMcpTests.Serve(root);
        server.Request("initialize", new JsonObject());
        JsonObject Call(string token, JsonObject args) => new()
        {
            ["name"] = "run", ["arguments"] = args, ["_meta"] = new JsonObject { ["progressToken"] = token },
        };

        int first  = server.Send("tools/call", Call("y-1", new JsonObject { ["analysis"] = "yield", ["path"] = cnl }));
        string never = Path.Combine(root, "cancelled.yield.npy");
        int second = server.Send("tools/call", Call("y-2", new JsonObject
        {
            ["analysis"] = "yield", ["path"] = cnl, ["trials"] = 50000, ["output"] = never,
        }));
        server.Notify("notifications/cancelled", new JsonObject { ["requestId"] = second });

        var document = JsonNode.Parse(server.Await(first)["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(0, document["exitCode"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expected, document["result"]!["yield"]));

        var progress = server.Frames.Where(f => f["method"]?.GetValue<string>() == "notifications/progress"
                                             && f["params"]!["progressToken"]!.GetValue<string>() == "y-1").ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Contains("did not evaluate", p["params"]!["message"]!.GetValue<string>()));
        Assert.Equal(200, progress[^1]["params"]!["progress"]!.GetValue<int>());

        var stopped = JsonNode.Parse(server.Await(second)["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(130, stopped["exitCode"]!.GetValue<int>());
        Assert.Empty(stopped["outputs"]!.AsArray());
        Assert.False(File.Exists(never));
    }
}

/// <summary>R-ya5-7: the statistics page is generated — from the directive schema and the verb's own flag and noun
/// tables — so a key or a flag added there appears with nothing to edit; and every MCP field is a flag on it.</summary>
public sealed class YieldReferenceTests
{
    [Fact]
    public void TheStatisticsPage_ListsEverySchemaKey_EveryFlag_AndEveryNoun()
    {
        string page = CircuitRF.Cli.Reference.RenderStatistics();
        foreach (var d in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == AnalysisDirectiveSchema.StatisticsTopic))
            foreach (var k in d.Keys) Assert.Contains(k.Name, page);
        foreach (var (flag, takes, _, _) in CircuitRF.Cli.Yield.Flags) Assert.Contains($"{flag} {takes}".TrimEnd(), page);
        foreach (var (noun, _) in CircuitRF.Cli.Yield.Nouns) Assert.Contains($"  {noun} ", page);

        var run = CircuitRF.Cli.Serve.ToolCatalog.Tools.Single(t => t.Name == "run");
        // --result is JsonRun's, read for every verb before the verb's own parser sees the line, so the yield verb's
        // table does not list it.
        foreach (var mode in run.Modes.Where(m => m.Verb[0] == "yield"))
            foreach (var o in mode.Options.Where(o => o.Cli != "--result"))
                Assert.Contains(CircuitRF.Cli.Yield.Flags, f => f.Flag == o.Cli);
    }
}

/// <summary>R-ya5-7: the server instructions' yield walk-through, MCP only, on a fresh workspace — create, (the file
/// the agent writes), check, explain the analysis, run the yield, read the summary, re-run the worst trial — ends
/// with a yield and no error.</summary>
public sealed class YieldAgentWalkthroughTests
{
    [Fact]
    public void TheWalkthrough_EndsWithAYield_AndNoError()
    {
        string root = OptCli.Dir();
        using var server = new OptMcpTests.Serve(root);
        server.Request("initialize", new JsonObject());
        JsonNode Call(string tool, JsonObject args)
        {
            var frame = server.Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
            var doc = JsonNode.Parse(frame["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.True(doc["exitCode"]!.GetValue<int>() == 0, doc.ToJsonString());
            Assert.DoesNotContain(doc["diagnostics"]?.AsArray() ?? [], d => d!["severity"]!.GetValue<string>() == "error");
            return doc;
        }

        Call("create", new JsonObject { ["what"] = "workspace", ["path"] = root, ["name"] = "demo" });
        string cnl = YieldCli.Divider(Path.Combine(root, "demo"), "div.cnl", "trials=500");
        Call("check", new JsonObject { ["path"] = cnl });
        var explained = Call("explain", new JsonObject { ["path"] = cnl, ["analysis"] = "" });
        Assert.NotNull(explained["result"]!["explain"]!["statistics"]!["run"]);

        var ran = Call("run", new JsonObject { ["analysis"] = "yield", ["path"] = cnl, ["trials"] = 500 })["result"]!["yield"]!;
        double y = ran["yield"]!["yield"]!.GetValue<double>();
        Assert.InRange(y, 0.8, 0.9);
        int worst = ran["goals"]![0]!["worstTrial"]!.GetValue<int>();

        var read = Call("read", new JsonObject { ["path"] = ran["output"]!.GetValue<string>() });
        Assert.Equal("yield", read["result"]!["groups"]!.AsObject().First().Key);
        Assert.Equal(y, read["result"]!["groups"]!["yield"]!["yield"]!["values"]![0]!.GetValue<double>());

        var trial = Call("run", new JsonObject { ["analysis"] = "yield", ["path"] = cnl, ["trial"] = worst })["result"]!["yield"]!["trial"]!;
        Assert.False(trial["pass"]!.GetValue<bool>());
    }
}
