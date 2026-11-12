// ================================================================
//  ChainLinkBudgetTests.cs — frequency-scalable stages, stage counts and DC power, the capacity readouts, the link in
//  front of the chain, the transmitter bench and the sweeps.
//
//  As in ChainBudgetTests, every expected number is written out here by hand from its closed form, not read back from
//  the code under test; the simulated side is circuitRF's own analyses of the transferred benches.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainLinkBudgetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-link-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    // ── frequency-scalable parameters ────────────────────────────────────────

    /// <summary>A D-band receiver whose LNA's NF is an expression in the frequency, whose preselector follows the carrier
    /// (it keeps the LNA's noise out of the mixer's image, as the budget assumes), and whose mixer's LO follows it too, so
    /// the IF stays put.</summary>
    private static ChainDesign Scaled() => new()
    {
        FrequencyHz = 140e9, BandwidthHz = 1e9, InputPowerDbm = -60,
        Stages =
        [
            S("LNA", "Amp", ("Gain", "20"), ("NF", "0.05*f_GHz + 0.5"), ("IP3", "10")),
            S("BPF", "Filter", ("Response", "Butterworth"), ("F1", "freq - 4GHz"), ("F2", "freq + 4GHz"), ("IL", "1")),
            S("MIX", "Mixer", ("ConvGain", "-6"), ("NF", "10"), ("IIP3", "5"), ("Plo", "5"), ("Flo", "freq - 12GHz")),
            S("IFA", "Amp", ("Gain", "15"), ("NF", "freq < 20e9 ? 3 : 9"), ("IP3", "20")),
        ],
    };

    [Fact]
    public void AnExpression_IsReadAtItsStagesOwnFrequency_AndAMixersLoMovesIt()
    {
        var r = ChainBudget.Evaluate(Scaled());
        Assert.Empty(r.Refusals);
        Assert.Equal(7.5, r.Stages[0].Stage.NfDb, 12);              // 0.05·140 + 0.5
        Assert.Equal(12e9, r.Stages[2].FrequencyHz, 3);              // |140 GHz − (140 − 12) GHz|
        Assert.Equal(3, r.Stages[3].Stage.NfDb, 12);                 // the IF amplifier reads 12 GHz, not 140
        Assert.Equal("128000000000", r.Design!.Stages[2].Parameters["Flo"]);
        Assert.Equal("144000000000", r.Design.Stages[1].Parameters["F2"]);

        // The same chain at 150 GHz: the NF follows, the IF does not move.
        var d = Scaled();
        d.FrequencyHz = 150e9;
        var r2 = ChainBudget.Evaluate(d);
        Assert.Equal(8, r2.Stages[0].Stage.NfDb, 12);
        Assert.Equal(12e9, r2.Stages[2].FrequencyHz, 3);
    }

    [Fact]
    public void ATable_IsLinearBetweenItsRows_AndNeverExtrapolated()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, "nf.csv"), "freq_Hz,NF_dB\n100 GHz, 5\n# a comment\n200e9, 9\n150 GHz, 6\n");
        var d = Scaled();
        d.BaseDirectory = _ws;
        d.Stages[0].Parameters["NF"] = "nf.csv";
        d.FrequencyHz = 125e9;
        Assert.Equal(5.5, ChainBudget.Evaluate(d).Stages[0].Stage.NfDb, 12);   // halfway from 100 to 150 GHz
        d.FrequencyHz = 175e9;
        Assert.Equal(7.5, ChainBudget.Evaluate(d).Stages[0].Stage.NfDb, 12);
        d.FrequencyHz = 200e9;
        Assert.Equal(9, ChainBudget.Evaluate(d).Stages[0].Stage.NfDb, 12);     // the end row itself
        d.FrequencyHz = 210e9;
        var r = ChainBudget.Evaluate(d);
        Assert.Null(r.Totals);
        Assert.Contains(r.Refusals, x => x.Contains("Stage 'LNA': NF = 'nf.csv' covers 100 GHz to 200 GHz and is read at 210 GHz", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatCannotBeRead_IsRefusedNamingTheStage()
    {
        var d = Scaled();
        d.Stages[0].Parameters["NF"] = "0.05*fghz + 0.5";      // an unknown name: not an expression of the frequency
        d.Stages[3].Parameters["Gain"] = "j*f_GHz";             // complex
        var r = ChainBudget.Evaluate(d);
        Assert.Contains(r.Refusals, x => x.Contains("Stage 'IFA': Gain = 'j*f_GHz' is a complex value at 12 GHz", StringComparison.Ordinal));
        d.Stages[3].Parameters["Gain"] = "15";
        r = ChainBudget.Evaluate(d);
        Assert.Contains(r.Refusals, x => x.Contains("Stage 'LNA': NF = '0.05*fghz + 0.5' is not a number", StringComparison.Ordinal));
        d.Stages[0].Parameters["NF"] = "log10(f_GHz - 200)";   // a domain error at 140 GHz
        r = ChainBudget.Evaluate(d);
        Assert.Contains(r.Refusals, x => x.StartsWith("Stage 'LNA': NF = 'log10(f_GHz - 200)' cannot be evaluated at 140 GHz", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBenches_CarryTheResolvedNumbers_NotTheExpressions()
    {
        var d = Scaled();
        var r = ChainBudget.Evaluate(d);
        foreach (var b in ChainBenches.Build(d, r, "Dband"))
        {
            Assert.DoesNotContain("f_GHz", b.Netlist, StringComparison.Ordinal);
            Assert.DoesNotContain("freq - ", b.Netlist, StringComparison.Ordinal);
            if (b.Kind == ChainBench.OneTone)
            {
                Assert.Contains("Amp:LNA  in  0  n_LNA  0  Gain=20 dB  NF=7.5 dB", b.Netlist, StringComparison.Ordinal);
                Assert.Contains("Freq=128000000000", b.Netlist, StringComparison.Ordinal);
            }
        }
        // And the 1-Tone bench, run, sits on the budget at the resolved values.
        var one = Bench(d, "Dband", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(one, "NF_err"), -1e-3, 1e-3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string Csch(string cell) => Path.Combine(_ws, cell, "schematic", cell + ".csch");

    /// <summary>Transfers <paramref name="d"/> and runs one of its benches as Simulate runs it.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which)
    {
        Directory.CreateDirectory(_ws);
        if (!File.Exists(Path.Combine(_ws, ".cws")))
            File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        if (!Directory.Exists(Path.Combine(_ws, ChainBenches.CellName(chain, which))))
        {
            var w = ChainBenches.Write(d, _ws, chain);
            Assert.True(w.SchematicPaths.Count == ChainBenches.BenchesFor(d).Count, string.Join("\n", w.Refusals));
        }
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        return ds;
    }

    private static double Measure(DataSet ds, string name) => ds["measurements." + name].RealValues[0];
}
