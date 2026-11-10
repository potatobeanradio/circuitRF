// ================================================================
//  ChainBudgetTests.cs — the RF chain budget: the closed-form cascade, the .cchain format, the `chain` verb, and the
//  transfer to test benches checked and run as Simulate runs them, every simulated number beside the budget's.
//
//  The expected budgets are written out here by hand from the textbook formulas (Friis; the cascaded IIP3 and P1dB),
//  not read back from ChainBudget: a second copy of the same arithmetic agreeing with itself proves nothing. The
//  simulated side is circuitRF's own S-parameter, harmonic-balance and envelope analyses of the System blocks.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainBudgetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-chain-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    /// <summary>The 2.45 GHz superhet the example ships.</summary>
    internal static ChainDesign Superhet() => new()
    {
        FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -70, TemperatureK = 290, RequiredSnrDb = 10,
        Stages =
        [
            S("LNA", "Amp", ("Gain", "16"), ("NF", "1"), ("IP3", "15"), ("IP3Ref", "Output")),
            S("RFBPF", "Filter", ("Response", "Butterworth"), ("Form", "Bandpass"), ("Order", "3"), ("F1", "2.35 GHz"), ("F2", "2.55 GHz"), ("IL", "1.5")),
            S("MIX", "Mixer", ("ConvGain", "-7"), ("NF", "7"), ("NFType", "ssb"), ("IIP3", "10"), ("Plo", "7"), ("Flo", "2.1 GHz")),
            S("IFBPF", "Filter", ("Response", "Butterworth"), ("Form", "Bandpass"), ("Order", "3"), ("F1", "320 MHz"), ("F2", "380 MHz"), ("IL", "1")),
            S("IFA", "Amp", ("Gain", "20"), ("NF", "4"), ("IP3", "25"), ("IP3Ref", "Output")),
            S("ATT", "Atten", ("Loss", "3")),
        ],
    };

    /// <summary>A 4-element array at RF: LNA and phase shifter per element, the combiner steered 45°, a filter and a
    /// second amplifier. No mixer, so its benches are an S-parameter run and two-tone harmonic balance: seconds.</summary>
    internal static ChainDesign RfArray() => new()
    {
        FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -80, TemperatureK = 290,
        Stages =
        [
            S("LNA", "Amp", ("Gain", "18"), ("NF", "1.2"), ("IP3", "10")),
            S("PS", "PhaseShifter", ("Phase", "30"), ("IL", "5"), ("NF", "5"), ("IIP3", "15")),
            S("CMB", "Combiner", ("N", "4"), ("IL", "0.5"), ("Steer", "45")),
            S("BPF", "Filter", ("Response", "Butterworth"), ("F1", "2.35 GHz"), ("F2", "2.55 GHz"), ("IL", "1.5")),
            S("A2", "Amp", ("Gain", "14"), ("NF", "2"), ("IP3", "20")),
        ],
    };

    // ── the closed form ──────────────────────────────────────────────────────

    [Fact]
    public void Superhet_IsFriisAndTheCascadedInterceptByHand()
    {
        var r = ChainBudget.Evaluate(Superhet());
        Assert.Empty(r.Refusals);
        var t = r.Totals!;

        double[] g = [16, -1.5, -7, -1, 20, -3], nf = [1, 1.5, 7, 1, 4, 3];
        double[] iip3 = [15 - 16, double.PositiveInfinity, 10, double.PositiveInfinity, 25 - 20, double.PositiveInfinity];
        double f = 1, gain = 1, inv = 0;
        for (int i = 0; i < g.Length; i++)
        {
            f += (Lin(nf[i]) - 1) / gain;                       // Friis: each stage's excess noise over the gain before it
            if (double.IsFinite(iip3[i])) inv += gain / Lin(iip3[i]);
            gain *= Lin(g[i]);
        }
        Assert.Equal(23.5, t.GainDb, 12);
        Assert.Equal(Db(f), t.NfDb, 12);
        Assert.Equal(-Db(inv), t.Iip3Dbm, 12);
        Assert.Equal(-Db(inv) + 23.5, t.Oip3Dbm, 12);
        Assert.Equal(-Db(inv) - ChainParts.TanhP1dbBelowIip3, t.Ip1dbDbm, 12);   // every stage is a tanh: one law
        Assert.Equal(t.Ip1dbDbm + 23.5 - 1, t.Op1dbDbm, 12);

        // The system numbers: kTB, the MDS, sensitivity, SNR and SFDR, for a 290 K source.
        double kt0b = Db(1.380649e-23 * 290 * 4.5e6) + 30;
        Assert.Equal(kt0b, t.ThermalFloorDbm, 12);
        Assert.Equal(kt0b + Db(f), t.MdsDbm, 12);
        Assert.Equal(t.MdsDbm + 10, t.SensitivityDbm, 12);
        Assert.Equal(-70 - t.MdsDbm, t.SnrDb, 12);
        Assert.Equal(2.0 / 3.0 * (t.Iip3Dbm - t.MdsDbm), t.SfdrDb, 12);
        Assert.Equal(t.MdsDbm + 23.5, t.NoiseOutDbm, 12);
        Assert.Equal(350e6, t.OutputFrequencyHz, 6);

        // The shares of F − 1 sum to all of it, and the per-stage rows end at the totals.
        Assert.Equal(100, r.Stages.Sum(s => s.NoiseSharePct), 9);
        Assert.Equal(t.NfDb, r.Stages[^1].NfDb, 12);
        Assert.Equal(1, r.Stages[0].NfDb, 12);
    }

    [Fact]
    public void Array_GainsItsCoherentSum_NotItsNoise_AndTheSnrRisesBy10LogN()
    {
        var d = RfArray();
        var r = ChainBudget.Evaluate(d);
        var t = r.Totals!;

        // One element's branch, the combiner's excess loss as a pad, then the rest: the array's noise temperature.
        double gb = Lin(18 - 5), te = 290 * (Lin(1.2) - 1) + 290 * (Lin(5) - 1) / Lin(18);
        te += 290 * (Lin(0.5) - 1) / gb + 290 * (Lin(1.5) - 1) / (gb / Lin(0.5)) + 290 * (Lin(2) - 1) / (gb / Lin(0.5 + 1.5));
        Assert.Equal(te, t.TeK, 9);
        Assert.Equal(Db(1 + te / 290), t.NfDb, 12);
        // The signal adds in amplitude: +10·log10(4) of gain at the combiner.
        Assert.Equal(18 - 5 + Db(4) - 0.5 - 1.5 + 14, t.GainDb, 12);
        Assert.Equal(Db(4), t.ArrayGainDb, 12);
        // The SNR: N elements' power against one branch's noise.
        double mds1 = Db(1.380649e-23 * (290 + te) * 4.5e6) + 30;
        Assert.Equal(-80 + Db(4) - mds1, t.SnrDb, 12);
        Assert.Equal(mds1 - Db(4), t.MdsDbm, 12);
        // The intercept, by the signal gains: the stages after the combiner see N times one element's power.
        double inv = 1 / Lin(10 - 18) + Lin(18) / Lin(15) + Lin(18 - 5 + Db(4) - 0.5 - 1.5) / Lin(20 - 14);
        Assert.Equal(-Db(inv), t.Iip3Dbm, 12);
        Assert.Equal(Db(4), r.Stages[2].ArrayGainDb, 12);
        Assert.Equal(0, r.Stages[1].ArrayGainDb, 12);
    }

    [Fact]
    public void TheComposedAndPassiveParts_HaveTheirClosedForms()
    {
        StageBudget B(ChainStage s) => ChainParts.Find(s.Part)!.Budget(s);
        var cpl = B(S("C", "Coupler", ("Coupling", "10"), ("IL", "0.2")));
        Assert.Equal(-(-Db(1 - 0.1) + 0.2), cpl.GainDb, 12);
        Assert.Equal(-cpl.GainDb, cpl.NfDb, 12);
        var spl = B(S("P", "Splitter", ("N", "4"), ("IL", "0.3")));
        Assert.Equal(-(Db(4) + 0.3), spl.GainDb, 12);
        var lim = B(S("L", "Limiter", ("IL", "0.5"), ("P1dB", "12")));
        Assert.Equal(12, lim.Ip1dbDbm, 12);
        Assert.Equal(12 + ChainParts.TanhP1dbBelowIip3, lim.Iip3Dbm, 12);
        var ps = B(S("PS", "PhaseShifter", ("IL", "4")));
        Assert.Equal(4, ps.NfDb, 12);                                     // a blank NF is a passive shifter's: its loss
        Assert.True(double.IsPositiveInfinity(ps.Iip3Dbm));
        var dsb = B(S("M", "Mixer", ("ConvGain", "-6"), ("NF", "5"), ("NFType", "dsb")));
        Assert.Equal(5 + Db(2), dsb.NfDb, 12);
        var amp = B(S("A", "Amp", ("Gain", "12"), ("IP3", "200")));
        Assert.True(double.IsPositiveInfinity(amp.Iip3Dbm));               // the Amp's own "exactly linear" sentinel
        var cmb = B(S("C", "Combiner", ("N", "8"), ("IL", "1")));
        Assert.Equal(Db(8) - 1, cmb.GainDb, 12);
        Assert.Equal(-1, cmb.NoiseGainDb, 12);
        Assert.Equal(8, cmb.ArrayN);
    }

    [Fact]
    public void ANoiselessStage_AndASteerWithNoPhaseShifter_AreSaid()
    {
        var d = new ChainDesign { Stages = [S("A", "Amp", ("Gain", "10")), S("M", "Mixer", ("NF", "2")), S("C", "Combiner", ("Steer", "30"))] };
        // A combiner last with every stage before it a branch is a legal chain.
        var r = ChainBudget.Evaluate(d);
        Assert.Contains(r.Notes, n => n.Contains("A: NF is blank", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("below a noiseless mixer", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("no PhaseShifter precedes it", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatCannotBeBudgeted_IsRefusedWithEveryCause()
    {
        var d = new ChainDesign
        {
            Stages = [S("A", "Amp", ("Gain", "ten")), S("A", "Nope"), S("C1", "Combiner"), S("C2", "Combiner"), S("9x", "Atten")],
        };
        var r = ChainBudget.Evaluate(d);
        Assert.Null(r.Totals);
        Assert.Contains(r.Refusals, x => x.Contains("Gain = 'ten' is not a number", StringComparison.Ordinal));
        Assert.Contains(r.Refusals, x => x.Contains("'Nope' is not a chain part", StringComparison.Ordinal));
        Assert.Contains(r.Refusals, x => x.Contains("Two stages are named 'A'", StringComparison.Ordinal));
        Assert.Contains(r.Refusals, x => x.Contains("at most one Combiner", StringComparison.Ordinal));
        Assert.Contains(r.Refusals, x => x.Contains("starts with a letter", StringComparison.Ordinal));
        Assert.NotEmpty(ChainBudget.Evaluate(new ChainDesign()).Refusals);
    }

    // ── the file ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheFile_RoundTrips_AndANewerOneIsRefused()
    {
        var d = Superhet();
        var back = ChainDesignIo.Deserialize(ChainDesignIo.Serialize(d));
        Assert.Equal(ChainDesignIo.Serialize(d), ChainDesignIo.Serialize(back));
        Assert.Equal("2.35 GHz", back.Stages[1].Parameters["F1"]);
        Assert.Equal(ChainBudget.Evaluate(d).Totals, ChainBudget.Evaluate(back).Totals);
        Assert.Throws<InvalidDataException>(() => ChainDesignIo.Deserialize("{\"FormatVersion\": 99}"));
        Assert.Throws<InvalidDataException>(() => ChainDesignIo.Deserialize("not json"));
        // An absent key takes the default; a stage's absent parameter takes its part's.
        var sparse = ChainDesignIo.Deserialize("{\"Stages\":[{\"Name\":\"A\",\"Part\":\"Atten\"}]}");
        Assert.Equal(10, ChainBudget.Evaluate(sparse).Totals!.NfDb, 12);    // the Attenuator's own default Loss
    }

    // ── the verb ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheVerb_WritesTheBudgetAsADataSet_AndRefusesWhatIsNotAChain()
    {
        Directory.CreateDirectory(_ws);
        string path = Path.Combine(_ws, "Rx.cchain"), npy = Path.Combine(_ws, "budget.npy");
        ChainDesignIo.SaveToFile(path, Superhet());
        Assert.Equal(0, CliEntry.Run(["chain", path, "-o", npy]));
        var ds = CircuitRF.Render.DataDisplay.PlotSourceFile.LoadWithNotes(npy).Item1!;
        Assert.Equal(["LNA", "RFBPF", "MIX", "IFBPF", "IFA", "ATT"], ds["G_dB"].Axes[0].Labels!);
        Assert.Equal(ChainBudget.Evaluate(Superhet()).Totals!.NfDb, ds["NFtot_dB"].RealValues[0], 12);
        Assert.Equal(1, CliEntry.Run(["chain", npy]));
        Assert.Equal(1, CliEntry.Run(["chain", path, "--nope"]));
        Assert.Equal(0, CliEntry.Run(["check", path]));
    }

    [Fact]
    public void Transfer_WritesThreeCheckedBenches_AndADisplay_AndNeverOverwritesUnasked()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        string path = Path.Combine(_ws, "Arr.cchain");
        ChainDesignIo.SaveToFile(path, RfArray());
        Assert.Equal(0, CliEntry.Run(["chain", path, "--to-schematic"]));
        foreach (var b in ChainBenches.BenchesFor(RfArray()))
        {
            Assert.True(File.Exists(Csch(ChainBenches.CellName("Arr", b))));
            // Each bench's display beside it, where Simulate looks for one first.
            Assert.True(File.Exists(Path.Combine(_ws, ChainBenches.CellName("Arr", b) + ".cdd")));
        }
        Assert.Equal(0, CliEntry.Run(["check", _ws]));
        // A second transfer refuses rather than overwriting; --replace writes them again.
        Assert.Equal(1, CliEntry.Run(["chain", path, "--to-schematic"]));
        Assert.Equal(0, CliEntry.Run(["chain", path, "--to-schematic", "--replace"]));

        // The drawing is the netlist: each bench extracts to the instances the transfer wrote, the array's four
        // branches and the combiner included.
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch("Arr 1-Tone"));
        var names = model.Components.Select(c => c.InstanceName).ToHashSet();
        foreach (string n in new[] { "SRC1", "SRC4", "LNA_1", "LNA_4", "PS_1", "PATH2", "CMB", "BPF", "A2", "PO" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void TheDiagram_IsTheTransferredChainAlone_EveryStageMapped_AndNoted()
    {
        var d = RfArray();
        var r = ChainBudget.Evaluate(d);
        var dia = ChainBenches.Diagram(d, r);
        Assert.NotNull(dia);
        var names = dia!.Model.Components.ToDictionary(c => c.Id, c => c.InstanceName);

        // Every element's branch, the library's N-way Combiner, the receiver after it; none of a bench's sources or rows.
        foreach (string n in new[] { "LNA_1", "LNA_4", "PS_1", "PS_4", "PATH2", "CMB", "BPF", "A2" })
            Assert.Contains(n, names.Values);
        Assert.DoesNotContain(dia.Model.Components, c => c.Symbol == SymbolKind.Meas || c.InstanceName.StartsWith("SRC", StringComparison.Ordinal));

        // A click on any of a stage's parts selects that stage: every stage has parts, and each part one stage.
        Assert.Equal(Enumerable.Range(0, d.Stages.Count), dia.StageOfComponent.Values.Distinct().Order());
        Assert.Equal(0, dia.StageOfComponent[names.Single(kv => kv.Value == "LNA_3").Key]);
        Assert.Equal(2, dia.StageOfComponent[names.Single(kv => kv.Value == "CMB").Key]);

        // Four lines over each stage; the cascade's line carries the budget's own numbers.
        var notes = dia.Model.CanvasObjects.OfType<EditableText>().Select(t => t.Text).ToList();
        Assert.Equal(4 * d.Stages.Count, notes.Count);
        Assert.Contains($"Σ G {r.Totals!.GainDb.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}  "
                        + $"NF {r.Totals.NfDb.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}", notes);

        // A chain the budget refuses has no drawing.
        var bad = RfArray();
        bad.Stages.Add(S("C2", ChainParts.Combiner, ("N", "2")));
        Assert.Null(ChainBenches.Diagram(bad, ChainBudget.Evaluate(bad)));
    }

    // ── the benches, run ─────────────────────────────────────────────────────

    /// <summary>
    /// A chain with every part but the mixer, its 1-Tone and 2-Tone benches run: S-parameter gain and NF, each stage's
    /// cumulative NF from the run's own shares, the two-tone intercepts, each stage's gain and OIP3, and the compression
    /// point. Worst deviations measured: 3e-10 dB in gain and NF (S-parameters), 3e-4 dB at the two tones' 50 dB
    /// back-off, and the cascaded-P1dB formula's own approximation, 0.05 dB.
    /// </summary>
    [Fact]
    public void EveryPart_SimulatesToItsBudget()
    {
        var d = new ChainDesign
        {
            FrequencyHz = 2.45e9, BandwidthHz = 1e6, InputPowerDbm = -60,
            Stages =
            [
                S("LIM", "Limiter", ("IL", "0.5"), ("P1dB", "20")),
                S("SW", "Switch", ("IL", "0.4")),
                S("ISO", "Circulator", ("IL", "0.3")),
                S("LNA", "Amp", ("Gain", "15"), ("NF", "1.5"), ("IP3", "18")),
                S("CPL", "Coupler", ("Coupling", "20"), ("IL", "0.1")),
                S("BPF", "Filter", ("Response", "Butterworth"), ("Form", "Bandpass"), ("Order", "3"), ("F1", "2.3 GHz"), ("F2", "2.6 GHz"), ("IL", "1")),
                S("PS", "PhaseShifter", ("Phase", "60"), ("IL", "4"), ("IIP3", "25")),
                S("SPL", "Splitter", ("N", "2"), ("IL", "0.2")),
                S("A2", "Amp", ("Gain", "20"), ("NF", "3"), ("IP3", "30")),
                S("ATT", "Atten", ("Loss", "2")),
            ],
        };
        var one = Bench(d, "All", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "IP1dB_err"), -0.1, 0.1);
        var two = Bench(d, "All", ChainBench.TwoTone);
        Assert.InRange(Measure(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(two, "OIP3_err"), -1e-3, 1e-3);
        foreach (var s in d.Stages)
        {
            Assert.InRange(Measure(two, $"G_{s.Name}_sim") - Measure(two, $"G_{s.Name}_calc"), -1e-3, 1e-3);
            if (two.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(Measure(two, $"OIP3_{s.Name}_sim") - Measure(two, $"OIP3_{s.Name}_calc"), -1e-3, 1e-3);
        }
    }

    /// <summary>
    /// The array at RF, steered: the S-parameter run's coherent gain |Σ S(out,k)|² is the budget's only if every phase
    /// shifter removes its element's steering; its NF is one element's, (F1 − 1)/N above 1; and the intercept scales
    /// with N after the combiner.
    /// </summary>
    [Fact]
    public void TheSteeredArray_SimulatesToItsBudget()
    {
        var d = RfArray();
        var one = Bench(d, "Arr", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "IP1dB_err"), -0.1, 0.1);
        var two = Bench(d, "Arr", ChainBench.TwoTone);
        Assert.InRange(Measure(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(two, "G_CMB_sim") - Measure(two, "G_CMB_calc"), -1e-3, 1e-3);

        // Without the phase shifters' compensation the same array loses its coherent sum: steering is what they undo.
        var off = RfArray();
        off.Stages.RemoveAt(1);
        var mis = Bench(off, "Mis", ChainBench.OneTone);
        Assert.True(Measure(mis, "G_err") < -1, $"an unsteered array read {Measure(mis, "G_err")} dB from its coherent gain");
    }

    /// <summary>The superhet's mixer benches: the harmonic-balance noise run's SSB NF and conversion gain, and its
    /// compression, with the LO running.</summary>
    [Fact]
    public void TheSuperhet_SimulatesToItsBudget_WithItsLoRunning()
    {
        var d = Superhet();
        var one = Bench(d, "Rx", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(one, "NF_err"), -1e-3, 1e-3);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-3, 1e-3);
        Assert.InRange(Measure(one, "IP1dB_err"), -0.1, 0.1);
    }

    // ── the combiner's four variants ─────────────────────────────────────────

    /// <summary>The RF array with the combiner as given; a tree's two levels set level by level.</summary>
    private static ChainDesign Variant(string topology, string kind, string pathIl = "")
    {
        bool tree = topology == "Tree";
        return new()
        {
            FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -80, TemperatureK = 290,
            Stages =
            [
                S("LNA", "Amp", ("Gain", "18"), ("NF", "1.2"), ("IP3", "10")),
                S("PS", "PhaseShifter", ("Phase", "30"), ("IL", "5"), ("NF", "5"), ("IIP3", "15")),
                S("CMB", "Combiner", ("N", "4"), ("Topology", topology), ("Kind", kind), ("IL", tree ? "0.5, 0.3" : "0.5"),
                  ("PathIL", pathIl), ("Gain", tree ? "10, 8" : "10"), ("NF", tree ? "2, 3" : "2"), ("IIP3", tree ? "10, 16" : "10"),
                  ("Steer", "45")),
                S("A2", "Amp", ("Gain", "14"), ("NF", "2"), ("IP3", "20")),
            ],
        };
    }

    /// <summary>
    /// Each variant's stage budget, by hand from the cascade of its levels: signal gain n·g and noise gain g for a
    /// passive n-way level of loss 1/g, T0·(1/g − 1) of noise; an active level's amplifier after it. Referred to one
    /// element's input. The tree's two levels are set one by one: IL 0.5 and 0.3 dB, and so on.
    /// </summary>
    [Fact]
    public void EveryCombinerVariant_IsTheCascadeOfItsLevels()
    {
        const double t0 = 290;
        StageBudget B(string topo, string kind, string path = "")
        {
            var r = ChainBudget.Evaluate(Variant(topo, kind, path));
            Assert.Empty(r.Refusals);
            return r.Stages[2].Stage;
        }
        double g1 = Lin(-0.5), g2 = Lin(-0.3);

        var dp = B("Direct", "Passive");
        Assert.Equal(Db(4 * g1), dp.GainDb, 12);
        Assert.Equal(-0.5, dp.NoiseGainDb, 12);
        Assert.Equal(0.5, dp.NfDb, 12);

        // Two 2:1 levels: their losses cascade, and their noise as two pads at T0.
        var tp = B("Tree", "Passive");
        Assert.Equal(Db(2 * g1 * 2 * g2), tp.GainDb, 12);
        Assert.Equal(0.8, tp.NfDb, 12);
        Assert.Equal(Db(4), tp.GainDb - tp.NoiseGainDb, 12);

        // Direct, active: the N:1 then one amplifier (10 dB, NF 2 dB, IIP3 10 dBm) seeing four elements' power.
        var da = B("Direct", "Active");
        double te = t0 * (1 / g1 - 1) + t0 * (Lin(2) - 1) / g1;
        Assert.Equal(Db(4 * g1 * Lin(10)), da.GainDb, 12);
        Assert.Equal(Db(1 + te / t0), da.NfDb, 12);
        Assert.Equal(10 - Db(4 * g1), da.Iip3Dbm, 12);

        // A tree, active: level 1 (2:1 at 0.5 dB, amplifier 10 dB / 2 dB / 10 dBm), level 2 (0.3 dB, 8 dB / 3 dB / 16 dBm).
        var ta = B("Tree", "Active");
        double gs = 1, gn = 1, inv3 = 0;
        te = 0;
        foreach (var (g, ga, nf, ip) in new[] { (g1, 10.0, 2.0, 10.0), (g2, 8.0, 3.0, 16.0) })
        {
            te += t0 * (1 / g - 1) / gn; gs *= 2 * g; gn *= g;
            te += t0 * (Lin(nf) - 1) / gn; inv3 += gs / Lin(ip); gs *= Lin(ga); gn *= Lin(ga);
        }
        Assert.Equal(Db(gs), ta.GainDb, 12);
        Assert.Equal(Db(gn), ta.NoiseGainDb, 12);
        Assert.Equal(Db(1 + te / t0), ta.NfDb, 12);
        Assert.Equal(-Db(inv3), ta.Iip3Dbm, 12);
        Assert.Equal(-Db(inv3) - ChainParts.TanhP1dbBelowIip3, ta.Ip1dbDbm, 9);

        // Unequal paths: signal (Σa)²/N, noise Σa²/N, array gain (Σa)²/Σa².
        var w = B("Direct", "Passive", "1, 0, 0, 1");
        double a = Math.Pow(10, -1 / 20.0), sa = 2 + 2 * a, sa2 = 2 + 2 * a * a;
        Assert.Equal(Db(sa * sa / 4 * g1), w.GainDb, 12);
        Assert.Equal(Db(sa2 / 4 * g1), w.NoiseGainDb, 12);
        Assert.Equal(Db(sa * sa / sa2), ChainBudget.Evaluate(Variant("Direct", "Passive", "1, 0, 0, 1")).Totals!.ArrayGainDb, 12);
    }

    [Fact]
    public void ACombinerThatCannotBeBuilt_IsRefusedWithItsReason()
    {
        string[] Why(params (string K, string V)[] p)
        {
            var d = Variant("Direct", "Passive");
            foreach (var (k, v) in p) d.Stages[2].Parameters[k] = v;
            return [.. ChainBudget.Evaluate(d).Refusals];
        }
        Assert.Contains(Why(("Topology", "Tree"), ("N", "6")), x => x.Contains("power of 2", StringComparison.Ordinal));
        Assert.Contains(Why(("Topology", "Tree"), ("N", "8"), ("IL", "0.1, 0.2")), x => x.Contains("3 levels", StringComparison.Ordinal));
        Assert.Contains(Why(("Kind", "Active"), ("PathIL", "1, 0, 0, 1")), x => x.Contains("passive combiner's", StringComparison.Ordinal));
        Assert.Contains(Why(("PathIL", "1, 0")), x => x.Contains("one per element", StringComparison.Ordinal));
        Assert.Contains(Why(("Topology", "Star")), x => x.Contains("neither Direct nor Tree", StringComparison.Ordinal));
        Assert.Empty(Why(("Topology", "Tree"), ("N", "8"), ("IL", "0.1, 0.2, 0.3")));
    }

    /// <summary>
    /// Every variant transferred and run: the S-parameter gain, NF and each stage's NF, the compression point, and the
    /// two-tone intercepts and each stage's, against the budget. A tree places levels of 2:1 Combiners, an active
    /// combiner an Amp after each.
    /// </summary>
    [Theory]
    [InlineData("Direct", "Passive", "")]
    [InlineData("Tree", "Passive", "")]
    [InlineData("Direct", "Active", "")]
    [InlineData("Tree", "Active", "")]
    [InlineData("Tree", "Passive", "1, 0, 0, 1")]
    public void EveryCombinerVariant_SimulatesToItsBudget(string topology, string kind, string pathIl)
    {
        var d = Variant(topology, kind, pathIl);
        string chain = topology + kind + (pathIl.Length > 0 ? "W" : "");
        var one = Bench(d, chain, ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "IP1dB_err"), -0.1, 0.1);
        var two = Bench(d, chain, ChainBench.TwoTone);
        Assert.InRange(Measure(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.InRange(Measure(two, "OIP3_err"), -1e-3, 1e-3);
        foreach (var s in d.Stages)
        {
            Assert.InRange(Measure(two, $"G_{s.Name}_sim") - Measure(two, $"G_{s.Name}_calc"), -1e-3, 1e-3);
            if (two.CubesIn("measurements").ContainsKey($"OIP3_{s.Name}_calc"))
                Assert.InRange(Measure(two, $"OIP3_{s.Name}_sim") - Measure(two, $"OIP3_{s.Name}_calc"), -1e-3, 1e-3);
        }
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(ChainBenches.CellName(chain, ChainBench.OneTone)));
        var combiners = model.Components.Count(c => c.Symbol == SymbolKind.Combiner);
        Assert.Equal(topology == "Tree" ? 3 : 1, combiners);
        Assert.Equal(kind == "Active" ? (topology == "Tree" ? 3 : 1) : 0, model.Components.Count(c => c.InstanceName.StartsWith("CMB_A", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The Steering bench: a plane wave swept from −90° to 90° for three steer angles. The simulated pattern is the
    /// closed-form array factor, with and without a taper set in its VAR block; at each beam's own peak the gain is the
    /// budget's, the NF from the wave's port stays put, and the array gain is the budget's 10·log10 N.
    /// </summary>
    [Fact]
    public void TheSteeringBench_PatternIsTheArrayFactor_AndTheNfStaysPut()
    {
        var d = Variant("Tree", "Active");
        var st = Bench(d, "Beam", ChainBench.Steering);
        foreach (string a in new[] { "m30", "0", "p30" })
        {
            Assert.InRange(Measure(st, $"G_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(Measure(st, $"NF_{a}_err"), -1e-9, 1e-9);
            Assert.InRange(Measure(st, $"A_{a}_err"), -1e-9, 1e-9);
        }
        Assert.Equal(Db(4), Measure(st, "A_calc"), 12);
        AssertPatternIsTheArrayFactor(st);

        // A 6 dB taper set in the bench's own VAR block: the pattern and the closed form move together.
        var tapered = Bench(d, "Beam", ChainBench.Steering, cnl => cnl.Replace("taper = 0", "taper = 6", StringComparison.Ordinal));
        AssertPatternIsTheArrayFactor(tapered);
        Assert.True(Measure(tapered, "G_0_err") < -0.1, "the taper's own coherent loss is in the peak");
    }

    private static void AssertPatternIsTheArrayFactor(DataSet ds)
    {
        var pat = ds["measurements.Pattern_sim"];
        var err = ds["measurements.Pattern_err"].RealValues;
        var p = pat.RealValues;
        double worst = 0;
        for (int i = 0; i < p.Length; i++)
            if (p[i] > -40) worst = Math.Max(worst, Math.Abs(err[i]));
        Assert.InRange(worst, 0, 1e-6);
        Assert.Equal(543, p.Length);
    }

    /// <summary>The Combiners bench: the four variants side by side in one S-parameter run, gain and NF each on its
    /// own budget.</summary>
    [Fact]
    public void TheCombinersBench_SetsTheFourVariantsSideBySide()
    {
        var d = Variant("Direct", "Passive");
        var cb = Bench(d, "Four", ChainBench.Combiners);
        foreach (var (_, _, code) in ChainBenches.CombinerVariants)
        {
            Assert.InRange(Measure(cb, $"G_{code}_err"), -1e-9, 1e-9);
            Assert.InRange(Measure(cb, $"NF_{code}_err"), -1e-9, 1e-9);
            Assert.Equal(ChainBudget.Evaluate(ChainBenches.Variant(d, code[0] == 'D' ? "Direct" : "Tree", code[1] == 'P' ? "Passive" : "Active"))
                             .Totals!.GainDb, Measure(cb, $"G_{code}_calc"), 12);
        }
    }

    // ── the levels node by node ──────────────────────────────────────────────

    /// <summary>
    /// The levels at every node, by hand: the signal Pin + G, the noise floor k·(Ts + Te)·B·Gn, the IM3 3·P − 2·OIP3,
    /// the margin OP1dB − P; and a blocker through a 3rd-order Butterworth bandpass written out here,
    /// |S21|² = IL/(1 + Ω⁶) with Ω = (f² − f0²)/(f·BW), then moved by the mixer to |f − Flo| and filtered again.
    /// </summary>
    [Fact]
    public void TheLevels_AreTheCascadeNodeByNode_AndABlockerIsCarriedThroughEveryFilter()
    {
        var d = Superhet();
        d.Blockers.Add(new ChainBlocker(150e6, -40));
        var r = ChainBudget.Evaluate(d);
        var lv = ChainLevels.Evaluate(d, r);
        Assert.Equal(d.Stages.Count + 1, lv.Count);
        Assert.Equal("IN", lv[0].Node);
        Assert.Equal(Db(ChainBudget.K * 290 * 4.5e6) + 30, lv[0].NoiseDbm, 12);
        for (int k = 1; k < lv.Count; k++)
        {
            var row = r.Stages[k - 1];
            Assert.Equal(d.InputPowerDbm + row.GainDb, lv[k].SignalDbm, 12);
            Assert.Equal(Db(ChainBudget.K * (290 + row.TeK) * 4.5e6) + 30 + row.GainDb, lv[k].NoiseDbm, 9);
            Assert.Equal(lv[k].SignalDbm - lv[k].NoiseDbm, lv[k].SnrDb, 12);
            if (double.IsFinite(row.Oip3Dbm)) Assert.Equal(3 * lv[k].SignalDbm - 2 * row.Oip3Dbm, lv[k].Im3Dbm, 12);
        }
        static double Butter(double f, double f1, double f2, double il)
        {
            double f0 = Math.Sqrt(f1 * f2), w = (f * f - f0 * f0) / (f * (f2 - f1));
            return -il - 10 * Math.Log10(1 + Math.Pow(w, 6));
        }
        double fb = 2.45e9 + 150e6, b = -40 + 16;                    // the LNA
        Assert.Equal(b, lv[1].BlockerDbm[0], 9);
        b += Butter(fb, 2.35e9, 2.55e9, 1.5);                         // the RF filter's rejection at 2.6 GHz
        Assert.Equal(b, lv[2].BlockerDbm[0], 6);
        b += -7; fb -= 2.1e9;                                         // the mixer: to 500 MHz
        Assert.Equal(b, lv[3].BlockerDbm[0], 6);
        b += Butter(fb, 320e6, 380e6, 1);                             // the IF filter at 500 MHz
        Assert.Equal(b, lv[4].BlockerDbm[0], 6);
        Assert.True(lv[4].BlockerDbm[0] < lv[4].SignalDbm - 15, "the IF filter takes the blocker under the signal");
        Assert.True(double.IsNaN(lv[0].Im3Dbm));
        Assert.Equal(r.Totals!.Ip1dbDbm - (-40), lv[0].MarginDb, 12);   // before anything compresses: the chain's, the blocker the strongest
        Assert.Equal(r.Stages[^1].Op1dbDbm - lv[^1].SignalDbm, lv[^1].MarginDb, 12);
    }

    /// <summary>The Levels bench (two tones at the input power) and the Blockers bench (the blockers alone, an isolator
    /// after every stage so each node is a forward wave), run: every node's signal, IM3 and blocker on the budget.</summary>
    [Fact]
    public void TheLevelsAndBlockersBenches_SimulateToTheBudgetNodeByNode()
    {
        var d = Superhet();
        d.Blockers.AddRange([new ChainBlocker(150e6, -40), new ChainBlocker(20e6, -50)]);
        var lev = Bench(d, "Lv", ChainBench.Levels);
        var blk = Bench(d, "Lv", ChainBench.Blockers);
        for (int k = 0; k <= d.Stages.Count; k++)
        {
            Assert.InRange(Measure(lev, $"Psig_{k}_err"), -1e-4, 1e-4);
            if (k > 0) Assert.InRange(Measure(lev, $"IM3_{k}_err"), -1e-3, 1e-3);
            // The blockers compress the LNA by a few thousandths of a dB; the budget carries them linearly.
            Assert.InRange(Measure(blk, $"Blk1_{k}_err"), -0.01, 1e-4);
            Assert.InRange(Measure(blk, $"Blk2_{k}_err"), -0.01, 1e-4);
        }
        var cube = lev["measurements.Psig_sim"];
        output.WriteLine($"Psig_sim axes: {string.Join(", ", cube.Axes.Select(a => $"{a.Name}[{a.Values.Length}]"))}");
        Assert.Equal(d.Stages.Count + 1, cube.RealValues.Length);
    }

    // ── the canceller ────────────────────────────────────────────────────────

    private static ChainDesign FrontEnd() => new()
    {
        FrequencyHz = 2.45e9, BandwidthHz = 4.5e6, InputPowerDbm = -60, TemperatureK = 290,
        Stages =
        [
            S("CANC", "Canceller", ("Coupling", "10"), ("IL", "0.2"), ("Pint", "-20"), ("Bint", "10 MHz"), ("Pref", "-10"),
              ("NFref", "6"), ("AmpErr", "0.1"), ("PhaseErr", "1"), ("DelayErr", "0.1 ns")),
            S("LNA", "Amp", ("Gain", "18"), ("NF", "1.2"), ("IP3", "20")),
            S("BPF", "Filter", ("Response", "Butterworth"), ("F1", "2.35 GHz"), ("F2", "2.55 GHz"), ("IL", "1")),
        ],
    };

    /// <summary>
    /// The canceller's closed forms, by hand. On the wanted signal: the through arm's loss IL − 10·log10(1 − c), and the
    /// reference path's noise, F = L_t + (F_ref·G_ref − 1)·c/(1 − c) with G_ref·P_ref·c = P_int·(1 − c). On the
    /// interference: R = 1 + a² − 2a·cos(φ − 2πντ)·sinc(πBτ), the band average of |1 − a·e^(j(φ − 2πfτ))|².
    /// </summary>
    [Fact]
    public void TheCanceller_HasItsClosedForms()
    {
        var c = CancellerSpec.Read(FrontEnd().Stages[0]);
        double cc = 0.1, lt = Lin(0.2) / (1 - cc);
        Assert.Equal(Db(lt), c.ThroughLossDb, 12);
        double gref = Lin(-20) * (1 - cc) / (cc * Lin(-10));
        Assert.Equal(Db(gref), c.RefGainDb, 12);
        Assert.Equal(Db(lt + (Lin(6) * gref - 1) * cc / (1 - cc)), c.Budget().NfDb, 12);
        // The depth: the band average done numerically here against the closed form.
        foreach (var (ae, pe, tau, bw, nu) in new[] { (0.1, 1.0, 0.0, 10e6, 0.0), (0.0, 0.0, 1e-9, 20e6, 0.0), (0.5, -3.0, 0.5e-9, 5e6, -15e6) })
        {
            double a = Math.Pow(10, ae / 20), sum = 0;
            const int m = 20000;
            for (int i = 0; i < m; i++)
            {
                double f = nu - bw / 2 + bw * (i + 0.5) / m, ph = pe * Math.PI / 180 - 2 * Math.PI * f * tau;
                sum += 1 + a * a - 2 * a * Math.Cos(ph);
            }
            Assert.Equal(sum / m, CancellerSpec.Residual(ae, pe, tau, bw, nu), 6);
        }
        Assert.Equal(33.5436, -10 * Math.Log10(CancellerSpec.Residual(0.1, 1, 0, 1)), 4);
        Assert.Equal(c.PintDbm - c.ThroughLossDb - c.DepthDb, c.ResidualDbm, 12);
        // The levels carry the interference from the canceller's input on, its residual after it.
        var lv = ChainLevels.Evaluate(FrontEnd(), ChainBudget.Evaluate(FrontEnd()));
        Assert.Equal(-20, lv[0].InterferenceDbm, 12);
        Assert.Equal(c.ResidualDbm, lv[1].InterferenceDbm, 12);
        Assert.Equal(c.ResidualDbm + 18, lv[2].InterferenceDbm, 12);
    }

    /// <summary>The canceller on the ordinary benches: its coupler's through arm, and the reference path's noise into the
    /// isolated port; gain and NF, each stage's NF, and the intercepts on the budget.</summary>
    [Fact]
    public void TheCanceller_OnTheOrdinaryBenches_IsItsThroughArmAndItsReferencePathsNoise()
    {
        var d = FrontEnd();
        var one = Bench(d, "Fe", ChainBench.OneTone);
        Assert.InRange(Measure(one, "G_err"), -1e-6, 1e-6);
        Assert.InRange(Measure(one, "NF_err"), -1e-6, 1e-6);
        foreach (var s in d.Stages) Assert.InRange(Measure(one, $"NF_{s.Name}_sim") - Measure(one, $"NF_{s.Name}_calc"), -1e-6, 1e-6);
        var two = Bench(d, "Fe", ChainBench.TwoTone);
        Assert.InRange(Measure(two, "IIP3_err"), -1e-3, 1e-3);
        Assert.Contains(ChainBench.Canceller, ChainBenches.BenchesFor(d));
    }

    /// <summary>
    /// The Canceller bench, with real signals: the interference's three carriers (10, 5 and 20 MHz) cancelled with each
    /// error swept. Every simulated depth is the closed form at that carrier's bandwidth and offset; the wanted carriers'
    /// EVM falls with cancellation.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheCancellerBench_DepthIsTheClosedForm_AtEveryErrorAndBandwidth()
    {
        var cb = Bench(FrontEnd(), "Fc", ChainBench.Canceller);
        double worst = 0;
        foreach (string sw in new[] { "P", "A", "D" })
            for (int k = 1; k <= ChainBenches.CancInterference.Length; k++)
            {
                var sim = cb[$"measurements.Depth{sw}_I{k}_sim"].RealValues;
                var calc = cb[$"measurements.Depth{sw}_I{k}_calc"].RealValues;
                Assert.Equal(calc.Length, sim.Length);
                // Relative to the depth: a deep null is a small residual, measured over one record.
                for (int i = 0; i < sim.Length; i++) worst = Math.Max(worst, Math.Abs(sim[i] - calc[i]) / Math.Max(calc[i], 1));
            }
        output.WriteLine($"Canceller bench: worst |depth sim - closed form| = {100 * worst:G3} % of the depth");
        Assert.InRange(worst, 0, 0.02);
        Assert.True(Measure(cb, "EVM_QAM64_on") < Measure(cb, "EVM_QAM64_off") / 3, "cancellation cleans the 64-QAM carrier");
        Assert.True(Measure(cb, "EVM_QPSK_on") < Measure(cb, "EVM_QPSK_off") / 3, "and the QPSK carrier");
    }

    // ── the demo ─────────────────────────────────────────────────────────────

    /// <summary>The demo bench: the superhet demodulated end to end over seven input powers. Where noise dominates, the
    /// simulated SNR is the budget's and the EVM is 1/√SNR; past compression the EVM's distortion part takes over.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheDemo_EvmFollowsTheBudgetsSnr_UntilDistortionTakesOver()
    {
        var d = Superhet();
        d.Demo = true;
        var dm = Bench(d, "Dm", ChainBench.Demo);
        var snrErr = dm["measurements.SNR_QAM_err"].RealValues;
        var evm = dm["measurements.EVM_QAM_sim"].RealValues;
        var evmCalc = dm["measurements.EVM_QAM_calc"].RealValues;
        var dist = dm["measurements.EVMdist_QAM"].RealValues;
        var noise = dm["measurements.EVMnoise_QAM"].RealValues;
        for (int i = 0; i < evm.Length; i++)
            output.WriteLine($"Demo: point {i}: EVM {evm[i]:G4} % (budget {evmCalc[i]:G4} %), noise {noise[i]:G4} %, distortion {dist[i]:G4} %, SNR err {snrErr[i]:G3} dB");
        Assert.InRange(snrErr[0], -0.5, 0.5);
        Assert.InRange(noise[0] / evmCalc[0], 0.9, 1.1);
        Assert.True(dist[^1] > noise[^1], "past compression, distortion is most of the error");
        Assert.True(dm["measurements.EVM_BPSK_sim"].RealValues[0] > 0, "the DBPSK carrier is demodulated too");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string Csch(string cell) => Path.Combine(_ws, cell, "schematic", cell + ".csch");

    /// <summary>Transfers <paramref name="d"/> and runs one of its benches as Simulate runs it.</summary>
    private DataSet Bench(ChainDesign d, string chain, ChainBench which, Func<string, string>? edit = null)
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
        string text = SchematicCircuit.CnlTextOf(model, cell);
        File.WriteAllText(netlist, edit?.Invoke(text) ?? text);
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        foreach (var x in run.Warnings) output.WriteLine($"{cell}: warning: {x}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        var ds = run.GroupedResults!;
        foreach (var (name, cube) in ds.CubesIn("measurements").Where(kv => kv.Key.EndsWith("_err", StringComparison.Ordinal)))
            output.WriteLine($"{cell}: {name} = {cube.RealValues[0]:G6}");
        return ds;
    }

    private static double Measure(DataSet ds, string name) => ds["measurements." + name].RealValues[0];
}
