// ================================================================
//  AgentAuthoringToolsTests.cs — two capabilities an agent driving the MCP server lacked, and two netlist
//  defects it met on the way (found building an SMT LC filter board end to end over MCP):
//
//  - create what=lcFilter: the LC Filter Designer (`filter lc`) had no MCP mode and no --json document.
//  - create what=layout: Update Layout from Schematic had no headless spelling at all.
//  - `filter lc -o` wrote its header as a SPICE `*` comment (a warning in every file) and no analysis, and a snapped
//    value as 6.799999999999999E-12.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Cli;

namespace CircuitRF.Ui.Tests.Cli;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class AgentAuthoringToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-agent-auth-" + Guid.NewGuid().ToString("N")[..10]);

    public AgentAuthoringToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void LcFilter_ReturnsTheLadder_AndWritesANetlistThatChecksClean()
    {
        using var server = new InProcessServe(_root);
        string cnl = Path.Combine(_root, "lpf.cnl");
        var doc = JsonNode.Parse(server.Call("create", new JsonObject
        {
            ["what"] = "lcFilter", ["band"] = "lowpass", ["response"] = "Chebyshev", ["order"] = 5, ["fc"] = "1GHz",
            ["first"] = "parallel", ["series"] = "e24", ["output"] = cnl,
        }))!;
        Assert.Equal(0, doc["exitCode"]!.GetValue<int>());

        var elements = doc["result"]!["lcFilter"]!["elements"]!.AsArray();
        Assert.Equal(5, elements.Count);
        foreach (var e in elements)
        {
            double v = e!["value"]!.GetValue<double>();
            Assert.Equal(double.Parse(v.ToString("G3", System.Globalization.CultureInfo.InvariantCulture),
                                      System.Globalization.CultureInfo.InvariantCulture), v);   // a preferred value, exactly
        }

        string text = File.ReadAllText(cnl);
        Assert.StartsWith(";", text);
        Assert.Contains("analysis SP1 type=sparam", text);
        Assert.DoesNotContain("99999", text);

        var check = JsonNode.Parse(server.Call("check", new JsonObject { ["path"] = cnl }))!;
        Assert.Equal(0, check["result"]!["check"]!["errors"]!.GetValue<int>());
        Assert.Equal(0, check["result"]!["check"]!["warnings"]!.GetValue<int>());
    }

    [Fact]
    public void Layout_FromSchematic_PlacesEveryPartAndJoinsThem()
    {
        using var server = new InProcessServe(_root);
        Assert.Equal(0, ExitOf(server.Call("create", new JsonObject
        {
            ["what"] = "workspace", ["path"] = _root, ["name"] = "ws", ["tech"] = "pcb-2layer_RO4350B_20mil_1oz",
        })));
        string ws = Path.Combine(_root, "ws");
        string cnl = Path.Combine(ws, "Board.cnl");
        File.WriteAllText(cnl, """
            Term:P1  IN 0  Num=1 Z=50 Ohm
            MLIN:TL1 IN n1 W=44 mil L=100 mil
            C:C1     n1 0  C=1.5 pF Footprint=smt:0603
            MLIN:TL2 n1 OUT W=44 mil L=100 mil
            Term:P2  OUT 0 Num=2 Z=50 Ohm
            """);
        string cell = Path.Combine(ws, "Board");
        Assert.Equal(0, ExitOf(server.Call("netlist", new JsonObject
        {
            ["path"] = cnl, ["toSchematic"] = true, ["output"] = Path.Combine(cell, "schematic", "Board.csch"),
        })));

        var made = JsonNode.Parse(server.Call("create", new JsonObject
        {
            ["what"] = "layout", ["cell"] = cell, ["fromSchematic"] = true, ["emSetup"] = true,
        }))!;
        Assert.Equal(0, made["exitCode"]!.GetValue<int>());
        var kinds = made["outputs"]!.AsArray().Select(o => o!["kind"]!.GetValue<string>()).ToList();
        Assert.Contains("layout", kinds);
        Assert.Contains("em-setup", kinds);
        string clay = Path.Combine(cell, "layout", "Board.clay");
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(clay))!["Instances"]!.AsArray().Count);

        var lvs = JsonNode.Parse(server.Call("lvs", new JsonObject { ["path"] = cell }))!;
        Assert.True(lvs["result"]!["lvs"]!["clean"]!.GetValue<bool>());
    }

    /// <summary>
    /// A note the optimizer adds while it runs (here the snap to E24) reaches the caller. The verb reported the notes
    /// after Run() by skipping as many as the run held before it — but the run's list and the result's are one list,
    /// so the count was taken after Run() and every such note was skipped.
    /// </summary>
    [Fact]
    public void Optimize_ReportsTheNotesItsRunAdds()
    {
        string cnl = Path.Combine(_root, "pad.cnl");
        File.WriteAllText(cnl, """
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=60 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            tune R1.R min=20 Ohm max=200 Ohm discrete=e24 opt=1
            goal Match = dB(SP1.S(1,1)) analysis=SP1 le -40
            optimize algorithm=auto maxevals=200 seed=1
            """);
        using var server = new InProcessServe(_root);
        var doc = JsonNode.Parse(server.Call("run", new JsonObject { ["analysis"] = "optimize", ["path"] = cnl }))!;
        var ids = doc["diagnostics"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("opt.snap.report", ids);
    }

    /// <summary>sparam runs the analysis it is asked for: it took the first in the file whatever was named or enabled, so a
    /// second span (SP2 beside an optimizer's SP1) could not be run at all.</summary>
    [Fact]
    public void Sparam_RunsTheNamedAnalysis()
    {
        string cnl = Path.Combine(_root, "two.cnl");
        File.WriteAllText(cnl, """
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=60 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            analysis SP2 type=sparam start=3 stop=4 npts=5 Unit=GHz
            """);
        using var server = new InProcessServe(_root);
        var doc = JsonNode.Parse(server.Call("run", new JsonObject { ["analysis"] = "sparam", ["path"] = cnl, ["name"] = "SP2" }))!;
        var freq = doc["result"]!["groups"]![""]!["S"]!["axes"]![0]!;
        Assert.Equal(5, freq["length"]!.GetValue<int>());
        Assert.Equal(3e9, freq["values"]![0]!.GetValue<double>());
    }

    private static int ExitOf(string document) => JsonNode.Parse(document)!["exitCode"]!.GetValue<int>();
}
