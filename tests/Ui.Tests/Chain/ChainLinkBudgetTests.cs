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

    // ── stage counts and DC power ────────────────────────────────────────────

    [Fact]
    public void AGainPerStage_CountsTheAmplifierStages_AndTheirDcPower()
    {
        Assert.Equal(4, ChainParts.StageCount(20, 6));      // 3.33 stages: four
        Assert.Equal(4, ChainParts.StageCount(20, 5));      // exactly four, not five after rounding
        Assert.Equal(4, ChainParts.StageCount(20, 20.0 / 4 * (1 + 1e-15)));
        Assert.Null(ChainParts.StageCount(20, -0.5));
        Assert.Null(ChainParts.StageCount(0, 6));

        var d = Scaled();
        d.Stages[0].Parameters["GainPerStage"] = "29.5 - 11*log10(f_GHz)";   // 5.894 dB at 140 GHz: four stages
        d.Stages[0].Parameters["Pdc"] = "4 mW";
        d.Stages[2].Parameters["Pdc"] = "0.02 W";
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        Assert.Equal(20, r.Stages[0].GainDb, 12);                            // the gain stays the one stated
        var ds = ChainBudget.ToDataSet(r);
        Assert.Equal([4, double.NaN, double.NaN, double.NaN], ds["Nstg"].RealValues);
        Assert.Equal([0.016, 0, 0.02, 0], ds["Pdc_W"].RealValues);

        // Above the frequency where a stage still has gain, no count reaches it.
        d.FrequencyHz = 600e9;
        d.Stages[1].Parameters["F1"] = "100 GHz"; d.Stages[1].Parameters["F2"] = "800 GHz";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.StartsWith("Stage 'LNA': GainPerStage is -1.0", StringComparison.Ordinal)
                                                               && x.Contains("no number of stages reaches the gain", StringComparison.Ordinal));

        // A budget-only value never reaches the netlist.
        d.FrequencyHz = 140e9;
        foreach (var b in ChainBenches.Build(d, ChainBudget.Evaluate(d), "Dc"))
        {
            Assert.DoesNotContain("Pdc", b.Netlist, StringComparison.Ordinal);
            Assert.DoesNotContain("GainPerStage", b.Netlist, StringComparison.Ordinal);
        }
    }

    // ── the readouts ─────────────────────────────────────────────────────────

    /// <summary>A front end of gain g and noise factor F_LNA per element, N elements into a combiner of loss L, then one
    /// amplifier of noise factor F_B.</summary>
    private static ChainDesign Array(int n, double ilDb, string topology = "Direct") => new()
    {
        FrequencyHz = 28e9, BandwidthHz = 400e6, InputPowerDbm = -70,
        Stages =
        [
            S("LNA", "Amp", ("Gain", "18"), ("NF", "2.5"), ("IP3", "10")),
            S("CMB", "Combiner", ("N", n.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Topology", topology), ("IL", ilDb.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            S("DRV", "Amp", ("Gain", "10"), ("NF", "6"), ("IP3", "25")),
        ],
    };

    [Fact]
    public void TheArraysNoiseFactor_IsFaOverMPlusFbMinusOneOverGa()
    {
        // Lossless: F_A is the front end's, G_A = g·N.
        var r = ChainBudget.Evaluate(Array(8, 0));
        var o = ChainCapacity.Read(r)!;
        double fa = Lin(2.5), fb = Lin(6), g = Lin(18);
        Assert.Equal(Db(fa / 8 + (fb - 1) / (8 * g)), o.NfArrayDb, 12);

        // A combiner of loss L: F_A takes its loss after the front end, G_A = g·N/L, and the hand Friis per element agrees.
        r = ChainBudget.Evaluate(Array(8, 1.5));
        o = ChainCapacity.Read(r)!;
        double l = Lin(1.5);
        double faL = fa + (l - 1) / g, ga = g * 8 / l;
        Assert.Equal(Db(faL / 8 + (fb - 1) / ga), o.NfArrayDb, 12);
        Assert.Equal(Db(fa + (l - 1) / g + (fb - 1) * l / g), r.Totals!.NfDb, 12);
        // The input-referred noise is kT0·B at that factor; the SNR is the input over it.
        Assert.Equal(Db(1.380649e-23 * 290 * 400e6) + 30 + o.NfArrayDb, o.NirDbm, 9);
        Assert.Equal(-70 - o.NirDbm, o.SnrDb, 9);
    }

    [Fact]
    public void TheRate_IsShannonOverTheStatedOrFractionalBandwidth_TimesTheStreams()
    {
        var d = Array(8, 1);
        d.RequiredSnrDb = 12;
        var o = ChainCapacity.Read(ChainBudget.Evaluate(d))!;
        Assert.Equal(400e6 * Math.Log2(1 + Lin(o.SnrDb)), o.RateBps, 3);
        Assert.Equal(o.SnrDb - 12, o.MarginDb, 12);

        d.FractionalBandwidth = 0.1;
        d.Streams = 2;
        var r = ChainBudget.Evaluate(d);
        o = ChainCapacity.Read(r)!;
        Assert.Equal(2.8e9, o.BandwidthHz, 3);
        Assert.Equal(-70 - (Db(1.380649e-23 * 290 * 2.8e9) + 30 + o.NfArrayDb), o.SnrDb, 9);   // the noise is in 2.8 GHz too
        Assert.Equal(2 * 2.8e9 * Math.Log2(1 + Lin(o.SnrDb)), o.RateBps, 1);
        var ds = ChainBudget.ToDataSet(r);
        Assert.Equal(o.RateBps, ds["Rate_bps"].RealValues[0]);
        Assert.Equal(o.NfArrayDb, ds["NFarray_dB"].RealValues[0]);
    }

    [Fact]
    public void TheNfAllowedAfterAStage_PutThere_TakesTheWholeChainToItsNfPlusTheAllowance()
    {
        var d = Scaled();
        foreach (double x in new[] { 1.0, 0.3 })
        {
            d.NfAllowanceDb = x;
            var r = ChainBudget.Evaluate(d);
            var o = ChainCapacity.Read(r)!;
            for (int k = 0; k < d.Stages.Count - 1; k++)
            {
                // The rest replaced by one stage of exactly that NF (a noise gain of 1: Friis reads only its noise factor).
                var cut = d.Clone();
                cut.Stages.RemoveRange(k + 1, cut.Stages.Count - k - 1);
                cut.Stages.Add(S("REST", "Amp", ("Gain", "0"), ("NF", o.NfAllowedDb[k].ToString("R", System.Globalization.CultureInfo.InvariantCulture)), ("IP3", "200")));
                Assert.Equal(r.Stages[k].NfDb + x, ChainBudget.Evaluate(cut).Totals!.NfDb, 12);
            }
        }
    }

    [Fact]
    public void ACompressingStageAfterTheCombiner_IsNamed_AndTheSharesAddUp()
    {
        var d = Array(16, 1, "Tree");
        d.Stages.Add(S("MIX", "Mixer", ("ConvGain", "-6"), ("NF", "10"), ("IIP3", "-5"), ("Plo", "5"), ("Flo", "freq - 3GHz")));
        var r = ChainBudget.Evaluate(d);
        Assert.Contains(r.Notes, x => x.StartsWith("MIX sets the receiver's compression", StringComparison.Ordinal)
                                       && x.Contains("the 16 elements' signals arrive in phase", StringComparison.Ordinal));
        var o = ChainCapacity.Read(r)!;
        Assert.Equal(100, o.Ip1dbSharePct.Sum(), 9);
        Assert.True(o.Ip1dbSharePct[3] > 50);
        // The ceiling: the cascade's input P1dB less the back-off, said when the input is above it.
        Assert.Equal(r.Totals!.Ip1dbDbm - 10, o.CeilingDbm, 12);
        d.InputPowerDbm = r.Totals.Ip1dbDbm - 5;
        Assert.Contains(ChainBudget.Evaluate(d).Notes, x => x.Contains("is above the receiver's ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNewFields_RoundTrip_AsVersion2_AndAPlainChainStaysVersion1()
    {
        var plain = Array(8, 1);
        Assert.Contains("\"FormatVersion\": 1", ChainDesignIo.Serialize(plain));
        var d = Array(8, 1);
        d.FractionalBandwidth = 0.1; d.Streams = 2; d.NfAllowanceDb = 0.5; d.RxBackoffDb = 8;
        string json = ChainDesignIo.Serialize(d);
        Assert.Contains("\"FormatVersion\": 2", json);
        var back = ChainDesignIo.Deserialize(json);
        Assert.Equal((0.1, 2, 0.5, 8.0), (back.FractionalBandwidth!.Value, back.Streams, back.NfAllowanceDb, back.RxBackoffDb));
        // An expression or a budget-only value is version 2 as well: version 1 would put it on a netlist line.
        Assert.Contains("\"FormatVersion\": 2", ChainDesignIo.Serialize(Scaled()));
        Assert.Throws<InvalidDataException>(() => ChainDesignIo.Deserialize("{\"FormatVersion\": 3}"));

        // The window keeps them through an edit and a save.
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(d);
        vm.InputPowerDbm = "-65";
        var kept = vm.ToDesign();
        Assert.Equal((0.1, 2, 0.5, 8.0), (kept.FractionalBandwidth!.Value, kept.Streams, kept.NfAllowanceDb, kept.RxBackoffDb));
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
