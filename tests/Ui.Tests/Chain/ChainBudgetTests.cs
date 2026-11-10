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
        Assert.Equal(["LNA", "RFBPF", "MIX", "IFBPF", "IFA", "ATT"], ds["G_dB"].Axes[0].Labels);
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
        foreach (var b in Enum.GetValues<ChainBench>())
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
        // branches and the combiner's lines and resistors included.
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch("Arr 1-Tone"));
        var names = model.Components.Select(c => c.InstanceName).ToHashSet();
        foreach (string n in new[] { "SRC1", "SRC4", "LNA_1", "LNA_4", "PS_1", "PATH2", "CMB_L1", "CMB_L4", "CMB_R3", "CMB_IL", "BPF", "A2", "PO" })
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

        // Every element's branch, the combiner's Wilkinson, the receiver after it; none of a bench's sources or rows.
        foreach (string n in new[] { "LNA_1", "LNA_4", "PS_1", "PS_4", "PATH2", "CMB_L1", "CMB_L4", "CMB_R3", "BPF", "A2" })
            Assert.Contains(n, names.Values);
        Assert.DoesNotContain(dia.Model.Components, c => c.Symbol == SymbolKind.Meas || c.InstanceName.StartsWith("SRC", StringComparison.Ordinal));

        // A click on any of a stage's parts selects that stage: every stage has parts, and each part one stage.
        Assert.Equal(Enumerable.Range(0, d.Stages.Count), dia.StageOfComponent.Values.Distinct().Order());
        Assert.Equal(0, dia.StageOfComponent[names.Single(kv => kv.Value == "LNA_3").Key]);
        Assert.Equal(2, dia.StageOfComponent[names.Single(kv => kv.Value == "CMB_R2").Key]);

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
            if (double.IsFinite(Measure(two, $"OIP3_{s.Name}_calc")))
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
            Assert.True(w.SchematicPaths.Count == 3, string.Join("\n", w.Refusals));
        }
        string cell = ChainBenches.CellName(chain, which);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
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
