// ================================================================
//  ChainBudgetExampleTests.cs — the "RF Chain Budget" example workspace
//
//  The four chains (a 2.45 GHz superhet, a four-element steered array, a front end with a canceller and a dual-conversion
//  receiver) and their transferred benches, run as Simulate runs them (SchematicRunService.RunNetlist on the schematic's
//  own netlist), in a temporary copy of the workspace:
//
//    - the shipped benches and displays are what Transfer to Schematic writes from the shipped .cchain today, to the
//      last bit of a computed number (which the platform's maths library decides);
//    - the workspace checks clean, and every display column is a measurement its bench computes;
//    - each bench's simulated gain, NF, intercepts, compression and SNR sit on the budget within the tolerances the
//      README quotes, stage by stage;
//    - the array's Steering bench draws the closed-form array factor at every steer angle, its NF and array gain staying
//      put at each beam's peak, and its Combiners bench sets the four combiners each on its own budget;
//    - the dual-conversion receiver's benches, each conversion's on its own budget, and the two conversions' simulated
//      gain and NF cascading to the whole chain's budget.
//
//  The 2-Tone and Envelope runs take 7 to 110 s each, so those are Category=Benchmark.
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

    private static readonly string[] Chains = ["Superhet", "Array", "Front End", "Dual Conversion", "26 GHz Link"];

    /// <summary>The benches Transfer writes for a shipped chain: a dual-conversion chain's mostly once per conversion.</summary>
    private static IReadOnlyList<ChainBenchText> Benches(string root, string chain)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(root, chain + ".cchain"));
        return ChainBenches.Build(d, ChainBudget.Evaluate(d), chain);
    }

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chainex-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    [Fact]
    public void TheShippedBenches_AreWhatTransferWritesToday()
    {
        CopyDirectory(Workspace, _ws);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (string chain in Chains)
        {
            var d = ChainDesignIo.LoadFromFile(Path.Combine(_ws, chain + ".cchain"));
            var w = ChainBenches.Write(d, _ws, chain, replace: true);
            Assert.Empty(w.Refusals);
            ChainBudgetDisplay.WriteAll(_ws, chain, d, ChainBudget.Evaluate(d));
            written.UnionWith(Benches(_ws, chain).Select(b => b.CellName));
            foreach (var b in Benches(_ws, chain))
            {
                string cell = b.CellName;
                SameUpToTheLastBit(File.ReadAllText(Csch(cell, Workspace)), File.ReadAllText(Csch(cell, _ws)), cell + ".csch");
                SameUpToTheLastBit(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")), File.ReadAllText(Path.Combine(_ws, cell + ".cdd")), cell + ".cdd");
            }
        }
        // And nothing shipped that Transfer no longer writes: every bench folder and display is one of them.
        Assert.Equal(written.Order(), Directory.GetDirectories(Workspace).Select(x => Path.GetFileName(x)).Order());
        Assert.Equal(written.Order(), Directory.GetFiles(Workspace, "*.cdd").Select(x => Path.GetFileNameWithoutExtension(x)).Order());
    }

    /// <summary>
    /// The same document, but for the last bit of a computed number. Transfer writes the budget's numbers round-trip,
    /// and the budget is computed with the platform's maths library, whose log10 and pow can differ by an ulp between
    /// operating systems: the shipped superhet bench read "1.4972226032470457" where macOS writes "…455". So every
    /// character outside a number must match exactly, and every number to a relative 1e-12 — a thousand ulps, and a
    /// million times finer than any tolerance a bench reads its numbers to. (Rounding the written numbers instead does
    /// not cure it: 10·log10(4) = 6.020599913279625 sits on a rounding edge at 15 digits, and any digit count has such
    /// values.)
    /// </summary>
    private static void SameUpToTheLastBit(string expected, string actual, string what)
    {
        var number = new System.Text.RegularExpressions.Regex(@"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?");
        Assert.Equal(number.Replace(expected, "#"), number.Replace(actual, "#"));
        var e = number.Matches(expected);
        var a = number.Matches(actual);
        for (int i = 0; i < e.Count; i++)
        {
            if (e[i].Value == a[i].Value) continue;
            double x = double.Parse(e[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y = double.Parse(a[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(x - y) <= 1e-12 * Math.Max(Math.Abs(x), Math.Abs(y)),
                        $"{what}: {e[i].Value} written as {a[i].Value}");
        }
    }

    [Fact]
    public void TheWorkspaceChecksClean_AndEveryDisplayColumnIsAMeasurementOfItsBench()
    {
        Assert.Equal(0, CliEntry.Run(["check", Workspace]));
        foreach (string chain in Chains)
        {
            var benches = Benches(Workspace, chain);
            // Each bench's MEAS rows, by the result file a display trace names: a display reads its own bench, and the
            // Levels display also the 1-Tone and Blockers benches of its chain, or of its conversion.
            var rowsOf = benches.ToDictionary(b => b.CellName + ".npy", b =>
            {
                var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(b.CellName, Workspace));
                return model.Components.Where(c => c.Symbol == SymbolKind.Meas)
                            .SelectMany(c => c.Parameters.Select(p => p.Name)).ToHashSet(StringComparer.Ordinal);
            });
            foreach (var bench in benches)
            {
                var b = bench.Kind;
                string cell = bench.CellName;
                var cdd = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace, cell + ".cdd")))!;
                foreach (var trace in cdd["Tabs"]![0]!["Plots"]!.AsArray().SelectMany(p => p!["Traces"]!.AsArray()))
                {
                    string src = (string?)trace!["SourcePath"] ?? "";
                    Assert.True(rowsOf.ContainsKey(src), $"{cell}.cdd reads '{src}', which is not a bench of {chain}");
                    if (b != ChainBench.Levels) Assert.Equal(cell + ".npy", src);
                    else Assert.StartsWith(bench.Chain + " ", src);
                    if ((string?)trace["CubeName"] is { Length: > 0 } cube)
                        Assert.True(rowsOf[src].Contains(cube), $"{cell}.cdd shows '{cube}', which {src}'s MEAS block does not compute");
                }
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

    [Fact]
    public void Superhet_LevelsAndBlockers_EveryNodeOnTheBudget()
    {
        var lev = Simulate("Superhet Levels");
        var blk = Simulate("Superhet Blockers");
        for (int k = 0; k <= 6; k++)
        {
            Assert.InRange(M(lev, $"Psig_{k}_err"), -1e-4, 1e-4);
            if (k > 0) Assert.InRange(M(lev, $"IM3_{k}_err"), -1e-3, 1e-3);
            Assert.InRange(M(blk, $"Blk1_{k}_err"), -0.01, 1e-4);
            Assert.InRange(M(blk, $"Blk2_{k}_err"), -0.01, 1e-4);
        }
    }

    [Fact]
    public void FrontEnd_1Tone_TheCancellersNoiseOnTheBudget()
    {
        var ds = Simulate("Front End 1-Tone");
        Assert.InRange(M(ds, "G_err"), -1e-6, 1e-6);
        Assert.InRange(M(ds, "NF_err"), -1e-6, 1e-6);
        Assert.Equal(2.6685, M(ds, "NF_calc"), 4);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void FrontEnd_Canceller_DepthOnTheClosedForm_AndEvmWithAndWithout()
    {
        var ds = Simulate("Front End Canceller");
        double worst = 0;
        string at = "";
        foreach (string sw in new[] { "P", "A", "D" })
            for (int k = 1; k <= ChainBenches.CancInterference.Length; k++)
            {
                var sim = ds[$"measurements.Depth{sw}_I{k}_sim"].RealValues;
                var calc = ds[$"measurements.Depth{sw}_I{k}_calc"].RealValues;
                for (int i = 0; i < sim.Length; i++)
                    if (Math.Abs(sim[i] - calc[i]) / Math.Max(calc[i], 1) > worst) { worst = Math.Abs(sim[i] - calc[i]) / Math.Max(calc[i], 1); at = $"{sw}{k}[{i}]: {calc[i]:G4} dB deep, simulated {sim[i]:G4} dB"; }
            }
        output.WriteLine($"Front End Canceller: worst |depth - closed form| = {100 * worst:G3} % of the depth ({at}); EVM 64-QAM {M(ds, "EVM_QAM64_off"):G3} -> {M(ds, "EVM_QAM64_on"):G3} %, QPSK {M(ds, "EVM_QPSK_off"):G3} -> {M(ds, "EVM_QPSK_on"):G3} %");
        Assert.InRange(worst, 0, 0.02);
        Assert.True(M(ds, "EVM_QAM64_on") < M(ds, "EVM_QAM64_off") / 3);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Superhet_Demo_TheEvmIsTheBudgetsWhereNoiseDominates()
    {
        var ds = Simulate("Superhet Demo");
        var err = ds["measurements.SNR_QAM_err"].RealValues;
        var noise = ds["measurements.EVMnoise_QAM"].RealValues;
        var calc = ds["measurements.EVM_QAM_calc"].RealValues;
        output.WriteLine($"Superhet Demo: SNR err at the lowest power {err[0]:G3} dB; EVM noise {noise[0]:G3} % against {calc[0]:G3} %");
        Assert.InRange(err[0], -0.5, 0.5);
        Assert.InRange(noise[0] / calc[0], 0.9, 1.1);
    }

    // ── the 26 GHz link ──────────────────────────────────────────────────────

    /// <summary>The README's numbers for the link, worked out by hand there from its coefficients at 26 GHz.</summary>
    [Fact]
    public void Link_TheBudget_IsWhatTheReadmeWorksOut()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "26 GHz Link.cchain"));
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var ds = ChainBudget.ToDataSet(r);
        Assert.Equal(44.3806, M0(ds, "EIRP_dBm"), 4);
        Assert.Equal(106.7679, M0(ds, "Lfs_dB"), 4);
        Assert.Equal(-59.4053, M0(ds, "Pin_dBm"), 4);
        Assert.Equal(-93.4041, M0(ds, "Nir_dBm"), 4);
        Assert.Equal(34.00, M0(ds, "SNRtot_dB"), 2);
        Assert.Equal(9.0358, M0(ds, "Rate_Gbps"), 4);
        Assert.Equal(8.7523, M0(ds, "Pdctot_W"), 4);
        Assert.Equal(2, ds["Nstg"].RealValues[1]);
        Assert.Contains(r.Notes, x => x.StartsWith("MIX sets the receiver's compression", StringComparison.Ordinal));
        // Its three sweeps, each the whole chain again at every point, none refused.
        var notes = new List<string>();
        ChainBudget.AddSweeps(ds, d, notes);
        Assert.Empty(notes);
        Assert.All(ds["Frequency.Rate_Gbps"].RealValues, v => Assert.True(double.IsFinite(v)));
        Assert.Equal(12, ds["RxElements.SNR_dB"].RealValues.Length);
    }

    [Fact]
    public void Link_1Tone_TheReceiverOnTheBudget_AtThePowerTheLinkDelivers()
    {
        var ds = Simulate("26 GHz Link 1-Tone");
        Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
        Assert.InRange(M(ds, "NF_err"), -5e-4, 5e-4);
        foreach (string s in new[] { "RXSW", "LNA", "PS", "CMB", "DRV", "BPF", "MIX" })
            Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -5e-4, 5e-4);
        Assert.Equal(3.5814, M(ds, "NF_calc"), 4);
    }

    [Fact]
    public void Link_SteeringAndCombiners_EightElementsOnTheirBudgets()
    {
        var st = Simulate("26 GHz Link Steering");
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(M(st, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(M(st, $"NF_{a}_err"), -1e-9, 1e-9);
        }
        Assert.Equal(9.0309, M(st, "A_calc"), 4);
        var cmb = Simulate("26 GHz Link Combiners");
        foreach (var (_, _, code) in ChainBenches.CombinerVariants)
        {
            Assert.InRange(M(cmb, $"G_{code}_err"), -1e-9, 1e-9);
            Assert.InRange(M(cmb, $"NF_{code}_err"), -1e-9, 1e-9);
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Link_TX_WhatTheBackoffBuys()
    {
        var ds = Simulate("26 GHz Link TX");
        output.WriteLine($"26 GHz Link TX: at 8 dB, Pout {M(ds, "Pant_at_err"):0.###} dB from the budget, EVM {M(ds, "EVM_at"):0.##} %, " +
                         $"ACPR {M(ds, "ACPR_L_at"):0.#} / {M(ds, "ACPR_U_at"):0.#} dBc");
        Assert.InRange(M(ds, "Pant_at_err"), -1.0, -0.8);
        Assert.InRange(M(ds, "EVM_at"), 5.5, 7);
        Assert.InRange(M(ds, "Psat_err"), -1, 0);
    }

    private static double M0(DataSet ds, string name) => ds[name].RealValues[0];

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
            if (ds.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
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

    // ── the dual-conversion receiver: per conversion, and the conversions cascaded ──

    /// <summary>Each conversion's 1-Tone bench on its own budget, stage by stage; and the two together, their simulated
    /// gains added and their simulated noise factors cascaded by Friis, on the whole chain's budget.</summary>
    [Fact]
    public void DualConversion_1Tone_EachConversionOnItsBudget_AndTogetherTheWholeChains()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var t = ChainBudget.Evaluate(d).Totals!;
        var one = Simulate("Dual Conversion IF1 1-Tone");
        var two = Simulate("Dual Conversion IF2 1-Tone");
        foreach (var (ds, stages) in new[] { (one, new[] { "LNA", "RFBPF", "MIX1" }), (two, new[] { "IF1BPF", "IF1A", "IMGBPF", "MIX2", "IF2BPF", "IF2A" }) })
        {
            Assert.InRange(M(ds, "G_err"), -5e-4, 5e-4);
            Assert.InRange(M(ds, "NF_err"), -2e-4, 2e-4);
            foreach (string s in stages) Assert.InRange(M(ds, $"NF_{s}_sim") - M(ds, $"NF_{s}_calc"), -2e-4, 2e-4);
            Assert.InRange(M(ds, "IP1dB_err"), 0, 0.1);
        }
        static double Lin(double db) => Math.Pow(10, db / 10);
        double g = M(one, "G_sim") + M(two, "G_sim");
        double nf = 10 * Math.Log10(Lin(M(one, "NF_sim")) + (Lin(M(two, "NF_sim")) - 1) / Lin(M(one, "G_sim")));
        output.WriteLine($"Dual Conversion: the conversions cascaded, gain {g:F5} dB against {t.GainDb:F5}, NF {nf:F5} dB against {t.NfDb:F5}");
        Assert.InRange(g - t.GainDb, -1e-3, 1e-3);
        Assert.InRange(nf - t.NfDb, -2e-4, 2e-4);
    }

    /// <summary>Each conversion's levels and blockers on the budget node by node, the two image blockers among them. The
    /// second conversion's input reads the second blocker 0.002 dB high: the roofing filter there reflects a little at
    /// 308 MHz, and the input node is the one with no isolator in front of it.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void DualConversion_LevelsAndBlockers_EveryNodeOnTheBudget()
    {
        foreach (var (conv, nodes) in new[] { ("IF1", 3), ("IF2", 6) })
        {
            var lev = Simulate($"Dual Conversion {conv} Levels");
            var blk = Simulate($"Dual Conversion {conv} Blockers");
            for (int k = 0; k <= nodes; k++)
            {
                Assert.InRange(M(lev, $"Psig_{k}_err"), -1e-4, 1e-4);
                // A node before anything nonlinear has no IM3 to read.
                if (lev.CubesIn("measurements").ContainsKey($"IM3_{k}_err")) Assert.InRange(M(lev, $"IM3_{k}_err"), -1e-3, 1e-3);
                Assert.InRange(M(blk, $"Blk1_{k}_err"), -0.01, 5e-3);
                Assert.InRange(M(blk, $"Blk2_{k}_err"), -0.01, 5e-3);
            }
        }
    }

    /// <summary>The whole chain's two tones, on four tones (the two, and both LOs): the 600-product cap allows MaxMixOrder 5,
    /// the least that carries the output's IM3, and there the intercepts read 3×10⁻³ dB high, against 2×10⁻⁴ dB at the
    /// order a single mixer's converge at.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void DualConversion_2Tone_TheWholeChainsIntercept_AtTheOrderTheCapAllows()
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var ds = Simulate("Dual Conversion 2-Tone");
        Assert.InRange(M(ds, "G_err"), -1e-3, 1e-3);
        Assert.InRange(M(ds, "IIP3_err"), -5e-3, 5e-3);
        Assert.InRange(M(ds, "OIP3_err"), -5e-3, 5e-3);
        foreach (var s in d.Stages)
        {
            Assert.InRange(M(ds, $"G_{s.Name}_sim") - M(ds, $"G_{s.Name}_calc"), -1e-3, 1e-3);
            if (ds.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(M(ds, $"OIP3_{s.Name}_sim") - M(ds, $"OIP3_{s.Name}_calc"), -5e-3, 5e-3);
        }
    }

    /// <summary>
    /// Each conversion's envelope SNR on the budget; the second's is the whole chain's, its source carrying the noise the
    /// first delivers. The load's own noise is in the simulated SNR and not in the budget's: kT0 against the (Ts + Te)·G
    /// a conversion delivers into it, 0.52 dB at the first conversion's output (MIX1's, 7.5 dB of gain) and nothing to
    /// speak of after 32.5 dB.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("IF1")]
    [InlineData("IF2")]
    public void DualConversion_Envelope_EachConversionsSnrOnTheBudget(string conv)
    {
        double load = LoadNoiseDb(conv);
        var ds = Simulate($"Dual Conversion {conv} Envelope");
        output.WriteLine($"Dual Conversion {conv} Envelope: the load's own noise {load:G3} dB of SNR");
        Assert.InRange(M(ds, "SNR_err") + load, -0.35, 0.35);
        Assert.InRange(M(ds, "Pout_err"), -0.1, 0.1);
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("IF1")]
    [InlineData("IF2")]
    public void DualConversion_Demo_EachConversionsEvmIsItsBudgetsWhereNoiseDominates(string conv)
    {
        double load = LoadNoiseDb(conv);
        var ds = Simulate($"Dual Conversion {conv} Demo");
        var err = ds["measurements.SNR_QAM_err"].RealValues;
        output.WriteLine($"Dual Conversion {conv} Demo: SNR err at the lowest power {err[0]:G3} dB, the load's own noise {load:G3} dB");
        Assert.InRange(err[0] + load, -0.35, 0.35);
    }

    /// <summary>The SNR a conversion's load costs at its output, dB: 10·log10(1 + T0 / ((Ts + Te)·G)).</summary>
    private static double LoadNoiseDb(string conv)
    {
        var d = ChainDesignIo.LoadFromFile(Path.Combine(Workspace, "Dual Conversion.cchain"));
        var c = ChainBenches.Conversions(d, ChainBudget.Evaluate(d), "Dual Conversion")!.Single(x => x.Chain == $"Dual Conversion {conv}");
        var t = c.Budget.Totals!;
        return 10 * Math.Log10(1 + ChainBudget.T0 / ((c.Design.TemperatureK + t.TeK) * Math.Pow(10, t.GainDb / 10)));
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
