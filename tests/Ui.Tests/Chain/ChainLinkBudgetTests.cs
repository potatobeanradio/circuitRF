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

        // The window keeps them through an edit.
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(d);
        vm.InputPowerDbm = "-65";
        var kept = vm.ToDesign();
        Assert.Equal((0.1, 2, 0.5, 8.0), (kept.FractionalBandwidth!.Value, kept.Streams, kept.NfAllowanceDb, kept.RxBackoffDb));
    }

    // ── the link ─────────────────────────────────────────────────────────────

    /// <summary>A 26 GHz link: 64 transmit elements, 150 m, and the 8-element receiver of <see cref="Array"/>.</summary>
    private static ChainDesign Link(string distance = "150 m")
    {
        var d = Array(8, 1, "Levels");
        d.FrequencyHz = 26e9;
        d.Stages[0].Parameters["Pdc"] = "10 mW";
        d.Stages[2].Parameters["Pdc"] = "20 mW";
        d.Link = new ChainLink
        {
            Tx = new() { ["Psat"] = "24", ["Backoff"] = "8", ["Loss"] = "0.8", ["AntennaGain"] = "5", ["Elements"] = "64", ["PaGain"] = "20", ["PAE"] = "15" },
            Path = new() { ["Distance"] = distance, ["Attenuation"] = "0.2", ["RxAntennaGain"] = "5" },
        };
        return d;
    }

    [Fact]
    public void TheLink_IsEirpLessThePath_AtEachElement_ByHand()
    {
        var r = ChainBudget.Evaluate(Link());
        Assert.Empty(r.Refusals);
        var l = r.Design!.Link!.Budget(26e9);
        double eirp = 24 - 8 - 0.8 + 5 + 20 * Math.Log10(64);
        double lfs = 20 * Math.Log10(4 * Math.PI * 150 * 26e9 / 299792458.0);
        double prx = eirp - lfs - 0.2 * 0.15 + 5;
        Assert.Equal(eirp, l.EirpDbm, 12);
        Assert.Equal(lfs, l.LfsDb, 12);
        Assert.Equal(prx, l.PrxDbm, 12);
        Assert.Equal(prx, r.Design.InputPowerDbm, 12);        // the chain's input is what each element receives
        Assert.Equal(prx + r.Totals!.GainDb, r.Totals.PoutDbm, 9);
        var ds = ChainBudget.ToDataSet(r);
        Assert.Equal(eirp, ds["EIRP_dBm"].RealValues[0], 12);
        Assert.Equal(prx, ds["Prx_dBm"].RealValues[0], 12);

        // A path written in kilometres is the same path.
        Assert.True(ChainLink.TryMetres("30 m", out double m1));
        Assert.True(ChainLink.TryMetres("0.03 km", out double m2));
        Assert.True(ChainLink.TryMetres("30", out double m3));
        Assert.Equal((30.0, 30.0, 30.0), (m1, m2, m3));
        Assert.Equal(prx, ChainBudget.Evaluate(Link("0.15 km")).Design!.InputPowerDbm, 12);
    }

    [Fact]
    public void ALinkThatCannotBeBudgeted_IsRefused_AndOneTooStrongIsHeldAtTheCeiling()
    {
        Assert.Contains(ChainBudget.Evaluate(Link("0 m")).Refusals, x => x.StartsWith("Path: Distance = '0 m' is not a distance above 0 m", StringComparison.Ordinal));
        Assert.Contains(ChainBudget.Evaluate(Link("far")).Refusals, x => x.StartsWith("Path: Distance = 'far'", StringComparison.Ordinal));
        var d = Link();
        d.Link!.Tx["Elements"] = "0";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.StartsWith("Transmitter: Elements = '0' is not a whole number", StringComparison.Ordinal));
        d.Link.Tx["Elements"] = "2.5";
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.StartsWith("Transmitter: Elements = '2.5'", StringComparison.Ordinal));

        // A metre away the link would deliver far more than the receiver takes: the input is its ceiling.
        var r = ChainBudget.Evaluate(Link("1 m"));
        Assert.Equal(r.Totals!.Ip1dbDbm - 10, r.Design!.InputPowerDbm, 12);
        Assert.Contains(r.Notes, x => x.StartsWith("The link delivers", StringComparison.Ordinal) && x.Contains("the budget takes", StringComparison.Ordinal));
        Assert.True(r.Design.Link!.Budget(26e9).PrxDbm > r.Design.InputPowerDbm);
    }

    [Fact]
    public void TheDcPower_IsEachPaAtItsBackoff_AndTheBranchOncePerElement()
    {
        var r = ChainBudget.Evaluate(Link());
        var p = ChainLink.Power(r)!;
        double pout = Math.Pow(10, (16 - 30) / 10.0);
        double pa = (pout - pout / 100) / 0.15;
        Assert.Equal(pa, p.PaW, 12);
        Assert.Equal(64 * pa, p.TxW, 12);
        Assert.Equal(8 * 0.010 + 0.020, p.RxW, 12);                  // the LNA per element; the driver once
        Assert.Equal(64 * pa + 0.1, p.TotalW, 12);
        Assert.Equal(1, p.EnergyPerBitJ * ChainCapacity.Read(r)!.RateBps / p.TotalW, 12);

        // Streams each have their hardware: twice the power and twice the rate, the same energy per bit.
        var d = Link();
        d.Streams = 2;
        var p2 = ChainLink.Power(ChainBudget.Evaluate(d))!;
        Assert.Equal(2 * p.TotalW, p2.TotalW, 12);
        Assert.Equal(1, p2.EnergyPerBitJ / p.EnergyPerBitJ, 12);

        // A stated Pdc is the PA's; a rate of nothing makes the energy per bit no number at all.
        d = Link("1e15 km");
        d.RxBackoffDb = 10;
        d.Link!.Tx["Pdc"] = "0.5";
        var far = ChainBudget.Evaluate(d);
        Assert.Equal(0.5, ChainLink.Power(far)!.PaW, 12);
        Assert.Equal(0, ChainCapacity.Read(far)!.RateBps);
        Assert.True(double.IsNaN(ChainLink.Power(far)!.EnergyPerBitJ));
        Assert.True(double.IsNaN(ChainBudget.ToDataSet(far)["Ebit_J"].RealValues[0]));
    }

    [Fact]
    public void ACombinerOfLevels_TakesThatTreesLossOnOneCombiner_AndSimulatesToIt()
    {
        var d = Array(6, 1, "Levels");      // ceil(log2 6) = 3 levels of 1 dB
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        Assert.Equal(10 * Math.Log10(6) - 3, r.Stages[1].Stage.GainDb, 12);
        Assert.Equal(-3, r.Stages[1].Stage.NoiseGainDb, 12);
        Assert.Equal(Db(6), r.Totals!.ArrayGainDb, 12);
        // At a power of 2 it is the tree's own budget.
        Assert.Equal(ChainBudget.Evaluate(Array(8, 1, "Tree")).Stages[1].Stage.GainDb, ChainBudget.Evaluate(Array(8, 1, "Levels")).Stages[1].Stage.GainDb, 12);
        Assert.Contains(ChainBudget.Evaluate(Array(6, 1, "Tree")).Refusals, x => x.Contains("or to Levels", StringComparison.Ordinal));
        var one = Bench(d, "Lv", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
    }

    [Fact]
    public void TheWindow_KeepsTheLinkAndItsRows_ThroughAnEdit_AndUndoesIt()
    {
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(Link());
        Assert.True(vm.HasLink);
        Assert.Equal("150 m", vm.LinkPath.Single(p => p.Name == "Distance").Value);
        vm.LinkPath.Single(p => p.Name == "Distance").Value = "200 m";
        var d = vm.ToDesign();
        Assert.Equal("200 m", d.Link!.Path["Distance"]);
        Assert.Equal("64", d.Link.Tx["Elements"]);
        Assert.DoesNotContain("Pdc", d.Link.Tx.Keys);                 // blank: not written
        Assert.Contains("Link: EIRP", vm.Summary, StringComparison.Ordinal);
        vm.HasLink = false;
        Assert.Null(vm.ToDesign().Link);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.HasLink);
        vm.UndoCommand.Execute(null);
        Assert.Equal("150 m", vm.LinkPath.Single(p => p.Name == "Distance").Value);
        Assert.NotNull(vm.ReceiverPlot);
        Assert.Contains(vm.PlotHost.Plots, c => ReferenceEquals(c.PlotVM.Plot, vm.LinkPlot));
    }

    // ── the transmitter bench ────────────────────────────────────────────────

    /// <summary>The TX bench's PA is an ideal Amp of output intercept Psat + 10·log10(π²/4): driven 30 dB past it, its
    /// fundamental is Psat, less the describing function's 0.0022 dB that far up.</summary>
    [Fact]
    public void TheTxBenchsPa_SaturatesAtPsat()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        string cnl = Path.Combine(_ws, "psat.cnl");
        File.WriteAllText(cnl, string.Join("\n",
            "temp = 16.85", "Frf = 26e9",
            "P1Tone:SRC  in  0  Num=1  Freq=Frf  Pavl=34 dBm  Z=50 Ohm",
            $"Amp:PA  in  0  out  0  Gain=20 dB  IP3={(24 + ChainBenches.TanhPsatBelowOip3Db).ToString("R", System.Globalization.CultureInfo.InvariantCulture)} dBm  IP3Ref=Output",
            "R:RL  out  0  R=50 Ohm",
            "analysis HB1 type=hb Tone=\"Frf\" ToneUnit=Hz MaxHarm=31 Tol=1e-12",
            "measure Psat_sim = dB(HB1.V(\"out\",1)) + 10", ""));
        var run = SchematicRunService.RunNetlist(cnl, _ws);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.InRange(Measure(run.GroupedResults!, "Psat_sim") - 24, -0.0022 - 1e-3, -0.0022 + 1e-3);
    }

    /// <summary>The TX bench, run: saturation near Psat at 10 dB of overdrive, the EVM falling as the back-off grows,
    /// and what the budget's Pout = Psat − Backoff leaves out at the link's 8 dB: an OFDM carrier through the tanh PA
    /// comes out 0.92 dB lower, at 6.3 % EVM (measured; 0.41 dB and 2.9 % at 12 dB).</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheTxBench_ShowsWhatTheBackoffBuys()
    {
        var d = Link();
        Assert.Contains(ChainBench.Transmitter, ChainBenches.BenchesFor(d));
        var tx = Bench(d, "Ln", ChainBench.Transmitter);
        double[] evm = tx["measurements.EVM_sim"].RealValues;
        double[] bo = tx["measurements.EVM_sim"].Axes[0].Values;
        foreach (var (b, e) in bo.Zip(evm)) output.WriteLine($"BO {b,5:0.#} dB: EVM {e:0.###} %, comp {tx["measurements.Comp"].RealValues[System.Array.IndexOf(bo, b)]:0.###} dB");
        for (int i = 1; i < evm.Length; i++) Assert.True(evm[i] < evm[i - 1], $"the EVM rose from {evm[i - 1]} % to {evm[i]} % at {bo[i]} dB of back-off");
        Assert.InRange(Measure(tx, "Psat_err"), -1, 0);
        Assert.InRange(Measure(tx, "Pant_at_err"), -1.0, -0.8);
        Assert.InRange(Measure(tx, "EVM_at"), 5.5, 7);
        output.WriteLine($"ACPR at the link's back-off: {Measure(tx, "ACPR_L_at"):0.##} / {Measure(tx, "ACPR_U_at"):0.##} dBc");
        Assert.True(Measure(tx, "ACPR_L_at") < -20 && Measure(tx, "ACPR_U_at") < -20);
    }

    // ── the sweeps ───────────────────────────────────────────────────────────

    [Fact]
    public void ASweep_IsTheWholeChainBudgetedAgainAtEachPoint()
    {
        Assert.Equal([20e9, 25e9, 30e9], new ChainSweep(ChainSweep.Frequency, 20e9, 30e9, 3).Values());
        Assert.Equal([4, 8, 16, 32, 64], new ChainSweep(ChainSweep.TxElements, 4, 64, 5).Values());
        Assert.Equal([1, 2, 3, 4, 6, 8, 11, 16, 23, 32, 45, 64], new ChainSweep(ChainSweep.RxElements, 1, 64, 13).Values());

        var d = Link();
        d.Stages[0].Parameters["NF"] = "0.06*f_GHz + 0.9";
        d.Sweeps = [new(ChainSweep.Frequency, 20e9, 40e9, 5), new(ChainSweep.TxElements, 4, 1024, 9), new(ChainSweep.RxElements, 1, 64, 13)];
        Assert.Empty(ChainBudget.Evaluate(d).Refusals);
        var notes = new List<string>();
        var f = ChainBudget.Sweep(d, d.Sweeps[0], notes);
        Assert.Empty(notes);
        Assert.Equal([20e9, 25e9, 30e9, 35e9, 40e9], f["SNR_dB"].Axes[0].Values);
        for (int i = 0; i < 5; i++)
        {
            var at = Link();
            at.Stages[0].Parameters["NF"] = "0.06*f_GHz + 0.9";
            at.FrequencyHz = 20e9 + 5e9 * i;
            var r = ChainBudget.Evaluate(at);
            Assert.Equal(r.Totals!.SnrDb, f["SNR_dB"].RealValues[i], 12);
            Assert.Equal(r.Totals.NfDb, f["NFtot_dB"].RealValues[i], 12);
            Assert.Equal(ChainCapacity.Read(r)!.RateBps / 1e9, f["Rate_Gbps"].RealValues[i], 9);
            Assert.Equal(r.Design!.Link!.Budget(at.FrequencyHz).EirpDbm, f["EIRP_dBm"].RealValues[i], 12);
        }
        // Over Mtx the EIRP climbs 20·log10 of the ratio; over Mrx the array gain 10·log10 of it, one element with no combiner.
        var tx = ChainBudget.Sweep(d, d.Sweeps[1]);
        Assert.Equal(20 * Math.Log10(1024.0 / 4), tx["EIRP_dBm"].RealValues[^1] - tx["EIRP_dBm"].RealValues[0], 9);
        var rx = ChainBudget.Sweep(d, d.Sweeps[2]);
        var one = Link();
        one.Stages[0].Parameters["NF"] = "0.06*f_GHz + 0.9";
        one.Stages.RemoveAt(1);
        Assert.Equal(ChainBudget.Evaluate(one).Totals!.SnrDb, rx["SNR_dB"].RealValues[0], 12);
        var six = Link();
        six.Stages[0].Parameters["NF"] = "0.06*f_GHz + 0.9";
        six.Stages[1].Parameters["N"] = "6";
        Assert.Equal(ChainBudget.Evaluate(six).Totals!.SnrDb, rx["SNR_dB"].RealValues[4], 12);

        // The groups of the DataSet the CLI writes, a sweep each.
        var ds = ChainBudget.ToDataSet(ChainBudget.Evaluate(d));
        ChainBudget.AddSweeps(ds, d);
        Assert.Equal(5, ds["Frequency.Pdc_W"].RealValues.Length);
        Assert.Equal(9, ds["TxElements.Ebit_pJ"].RealValues.Length);
        Assert.Equal(12, ds["RxElements.NFarray_dB"].RealValues.Length);
    }

    [Fact]
    public void ASweepPointTheBudgetRefuses_IsNoNumber_AndSaidOnce_AsIsAFilterLeftBehind()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, "atm.csv"), "freq,dB/km\n20 GHz, 0.1\n30 GHz, 0.12\n");
        var d = Link();
        d.BaseDirectory = _ws;
        d.Link!.Path["Attenuation"] = "atm.csv";
        d.Stages.Insert(2, S("BPF", "Filter", ("Response", "Butterworth"), ("F1", "25 GHz"), ("F2", "27 GHz"), ("IL", "1")));
        var notes = new List<string>();
        var f = ChainBudget.Sweep(d, new ChainSweep(ChainSweep.Frequency, 20e9, 40e9, 5), notes);
        Assert.Equal([false, false, false, true, true], f["SNR_dB"].RealValues.Select(double.IsNaN));
        Assert.Contains(notes, x => x.StartsWith("The Frequency sweep leaves out 2 of its 5 points", StringComparison.Ordinal)
                                    && x.Contains("covers 20 GHz to 30 GHz", StringComparison.Ordinal));
        Assert.Contains(notes, x => x.StartsWith("At 2 of the Frequency sweep's carriers a Filter's passband", StringComparison.Ordinal));
        d.Sweeps = [new(ChainSweep.Frequency, 30e9, 20e9, 5)];
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("runs from a value above 0 to a larger one", StringComparison.Ordinal));
        d.Sweeps = [new(ChainSweep.TxElements, 2, 8, 3)];
        d.Link = null;
        Assert.Contains(ChainBudget.Evaluate(d).Refusals, x => x.Contains("the chain has no link", StringComparison.Ordinal));
    }

    /// <summary>A sweep has no bench of its own; two of its points, each transferred, sit on their budgets.</summary>
    [Fact]
    public void TwoSweepPoints_Transferred_SimulateToTheirBudgets()
    {
        double nf0 = double.NaN;
        foreach (double f in new[] { 22e9, 38e9 })
        {
            var d = Link();
            d.Stages[0].Parameters["NF"] = "0.06*f_GHz + 0.9";
            d.FrequencyHz = f;
            string chain = $"At{f / 1e9:0}";
            var one = Bench(d, chain, ChainBench.OneTone);
            Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
            Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
            Assert.NotEqual(nf0, Measure(one, "NF_calc"));
            nf0 = Measure(one, "NF_calc");
        }
    }

    [Fact]
    public void TheWindow_ReadsASweepLikeTheBlockers_AndShowsItsTab()
    {
        var d = Link();
        d.Sweeps = [new(ChainSweep.Frequency, 24.25e9, 27.5e9, 14)];
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(d);
        Assert.Equal("24.25 GHz, 27.5 GHz, 14", vm.SweepInputs.Single(p => p.Name == ChainSweep.Frequency).Value);
        Assert.True(vm.HasFrequencySweep);
        Assert.False(vm.HasTxSweep);
        Assert.All(vm.FrequencySweepPlots, p => Assert.Contains(vm.PlotHost.Plots, c => ReferenceEquals(c.PlotVM.Plot, p)));
        vm.SweepInputs.Single(p => p.Name == ChainSweep.TxElements).Value = "4, 256, 7";
        Assert.True(vm.HasTxSweep);
        Assert.Equal(new ChainSweep(ChainSweep.TxElements, 4, 256, 7), vm.ToDesign().Sweeps[1]);
        vm.SweepInputs.Single(p => p.Name == ChainSweep.TxElements).Value = "4, 256";
        Assert.Contains("from a value above 0", vm.Refusals, StringComparison.Ordinal);
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
