// ================================================================
//  ChainBudgetExampleTests.cs — the "RF Chain Budget" example workspace
//
//  The two chains (a 2.45 GHz superhet and a four-element steered array) and their transferred benches, run as Simulate
//  runs them (SchematicRunService.RunNetlist on the schematic's own netlist), in a temporary copy of the workspace:
//
//    - the shipped benches and displays are exactly what Transfer to Schematic writes from the shipped .cchain today;
//    - the workspace checks clean, and every display column is a measurement its bench computes;
//    - each bench's simulated gain, NF, intercepts, compression and SNR sit on the budget within the tolerances the
//      README quotes, stage by stage;
//    - the array's Steering bench draws the closed-form array factor at every steer angle, its NF and array gain staying
//      put at each beam's peak, and its Combiners bench sets the four combiners each on its own budget.
//
//  The 2-Tone and Envelope runs take 7 to 50 s each, so those are Category=Benchmark.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainBudgetExampleTests(ITestOutputHelper output) : IDisposable
{
    public const string Folder = "RF Chain Budget";
    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    private static readonly string[] Chains = ["Superhet", "Array"];

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainex-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    [Fact]
    public void TheShippedBenches_AreWhatTransferWritesToday()
    {
        CopyDirectory(Workspace, _ws);
        foreach (string chain in Chains)
        {
            var d = ChainDesignIo.LoadFromFile(Path.Combine(_ws, chain + ".cchain"));
            var w = ChainBenches.Write(d, _ws, chain, replace: true);
            Assert.Empty(w.Refusals);
            ChainBudgetDisplay.WriteAll(_ws, chain, d, ChainBudget.Evaluate(d));
            foreach (var b in ChainBenches.BenchesFor(d))
            {
                string cell = ChainBenches.CellName(chain, b);
                Assert.Equal(File.ReadAllText(Csch(cell, Workspace)), File.ReadAllText(Csch(cell, _ws)));
                Assert.Equal(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")), File.ReadAllText(Path.Combine(_ws, cell + ".cdd")));
            }
        }
    }

    [Fact]
    public void TheWorkspaceChecksClean_AndEveryDisplayColumnIsAMeasurementOfItsBench()
    {
        Assert.Equal(0, CliEntry.Run(["check", Workspace]));
        foreach (string chain in Chains)
            foreach (var b in ChainBenches.BenchesFor(ChainDesignIo.LoadFromFile(Path.Combine(Workspace, chain + ".cchain"))))
            {
                string cell = ChainBenches.CellName(chain, b);
                var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell, Workspace));
                var rows = model.Components.Where(c => c.Symbol == SymbolKind.Meas)
                                .SelectMany(c => c.Parameters.Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
                var cdd = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")))!;
                foreach (var trace in cdd["Tabs"]![0]!["Plots"]!.AsArray().SelectMany(p => p!["Traces"]!.AsArray()))
                {
                    Assert.Equal(cell + ".npy", (string?)trace!["SourcePath"]);
                    if ((string?)trace["CubeName"] is { Length: > 0 } cube)
                        Assert.True(rows.Contains(cube), $"{cell}.cdd shows '{cube}', which {cell}'s MEAS block does not compute");
                }
            }
    }

    // ── 1-Tone: noise and compression, seconds ───────────────────────────────

    [Fact]
    public void Superhet_1Tone_NfAndGainOnTheBudget_WithTheLoRunning()
    {
        var ds = Simulate("Superhet 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
        foreach (string s in new[] { "LNA", "RFBPF", "MIX", "IFBPF", "IFA", "ATT" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
        Assert.Equal(2.5488, M(ds, "NF_calc"), 4);
        Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
    }

    [Fact]
    public void Array_1Tone_OneElementsNf_AndTheCoherentGain()
    {
        var ds = Simulate("Array 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
        foreach (string s in new[] { "LNA", "PS", "CMB", "RFBPF", "MIX", "IFBPF", "IFA", "ATT" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
        Assert.Equal(26.0206, M(ds, "G_calc"), 4);
        Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
    }

    // ── the array's Steering and Combiners benches: an S-parameter run each, seconds ──

    [Fact]
    public void Array_Steering_ThePatternIsTheArrayFactor_AndTheNfStaysPut()
    {
        var ds = Simulate("Array Steering");
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(ds, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"NF_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"A_{a}_err"), -1e-9, 1e-9);
        }
        Assert.Equal(6.0206, M(ds, "A_calc"), 4);
        var p = ds["measurements.Pattern_sim"].RealValues;
        var e = ds["measurements.Pattern_err"].RealValues;
        double worst = Enumerable.Range(0, p.Length).Where(i => p[i] > -40).Max(i => Math.Abs(e[i]));
        output.WriteLine($"Array Steering: worst |pattern - array factor| above -40 dB = {worst:G3} dB");
        Assert.InRange(worst, 0, 1e-6);
    }

    [Fact]
    public void Array_Combiners_TheFourVariantsEachOnItsBudget()
    {
        var ds = Simulate("Array Combiners");
        foreach (var (_, _, code) in ChainBenches.CombinerVariants)
        {
            Assert.InRange(M(ds, $"G_{code}_err"), -1e-9, 1e-9);
            Assert.InRange(M(ds, $"NF_{code}_err"), -1e-9, 1e-9);
        }
        // The README's table: a tree's loss accumulates per level, an active combiner's amplifiers cascade.
        Assert.Equal(18.5206, M(ds, "G_DP_calc"), 4);
        Assert.Equal(18.0206, M(ds, "G_TP_calc"), 4);
        Assert.Equal(28.5206, M(ds, "G_DA_calc"), 4);
        Assert.Equal(38.0206, M(ds, "G_TA_calc"), 4);
    }

    // ── 2-Tone and Envelope ──────────────────────────────────────────────────

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("Superhet")]
    [InlineData("Array")]
    public void TwoTone_TheInterceptsOnTheBudget_StageByStage(string chain)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, chain + ".cchain"));
        var ds = Simulate(chain + " 2-Tone");
        Assert.InRange(M(ds, "IIP3_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "OIP3_err"), -5e-4, 5e-4);
        foreach (var s in d.Stages)
        {
            Assert.InRange(M(ds, $"G_{s.Name}_sim") - M(ds, $"G_{s.Name}_calc"), -5e-4, 5e-4);
            if (double.IsFinite(M(ds, $"OIP3_{s.Name}_calc")))
                Assert.InRange(M(ds, $"OIP3_{s.Name}_sim") - M(ds, $"OIP3_{s.Name}_calc"), -5e-4, 5e-4);
        }
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("Superhet")]
    [InlineData("Array")]
    public void Envelope_TheSnrOnTheBudget_WithinOneRecordsStatistics(string chain)
    {
        var ds = Simulate(chain + " Envelope");
        Assert.InRange(M(ds, "SNR_err"), -0.5, 0.5);
        Assert.InRange(M(ds, "Pout_err"), -0.1, 0.1);
        Assert.InRange(M(ds, "EVM_sim") / M(ds, "EVM_calc"), 0.9, 1.1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Csch(string cell, string root) => Path.Combine(root, cell, "schematic", cell + ".csch");

    /// <summary>Simulate: the bench's netlist at the workspace root, run, every measurement row evaluated.</summary>
    private DataSet Simulate(string cell)
    {
        if (!Directory.Exists(_ws)) CopyDirectory(Workspace, _ws);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell, _ws));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("failed to evaluate", StringComparison.Ordinal));
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        return ds;
    }

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
