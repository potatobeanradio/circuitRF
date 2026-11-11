// ================================================================
//  ReceiverNoiseExampleTests.cs — the "Receiver Noise" example workspace (brief NZ-11)
//
//  Every bench runs as Simulate runs it (SchematicRunService.RunNetlist on the schematic's own netlist, filed under
//  results/<cell>.npy), in a temporary copy of the workspace, with no display, and each number the README quotes is
//  asserted here at the precision the README prints it:
//
//    1 LNA Noise Match      NF = NFmin at 2.45 GHz, Sopt at the centre, K above 1, and which part sets the noise
//    2 Receiver Budget      the chain's NF on Friis' sum at every LNA gain, and the stages' shares
//    3 Mixer Noise          SSB and DSB NF and the conversion gain against LO drive, and the image's share
//    4 LNA Under a Blocker  bench 1's NF with the blocker small, and how far it rises toward compression
//    5 Noise Sourcepull     where NF and gain are best on the source plane, and what drive does to NF
//    6 Receiver EVM         EVM against input power: the noise, phase-noise and distortion regimes
//    7 VCO Phase Noise      the tuning range, the phase noise, and bench 6's LO mask is bench 7's PN at 3.5 V
//
//  and every trace of the seven Data Displays resolves on Simulate's own results.
//
//  BuildTheDisplays (CRF_AUTHOR_RXNOISE=1) writes the seven .cdd files from those same results.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Data;
using RfCore.Loadpull;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ReceiverNoiseExampleTests(ITestOutputHelper output) : IDisposable
{
    public const string Folder = "Receiver Noise";
    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    private static readonly string[] Benches =
    [
        "1 LNA Noise Match", "2 Receiver Budget", "3 Mixer Noise", "4 LNA Under a Blocker",
        "5 Noise Sourcepull", "6 Receiver EVM", "7 VCO Phase Noise",
    ];

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-rxnoise-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_RXNOISE") != "1")
                Skip = "re-builds the Receiver Noise example's Data Displays only with CRF_AUTHOR_RXNOISE=1";
        }
    }

    // ── 1. The LNA ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LnaNoiseMatch_NoiseMatchedAt245GHz_Stable_AndTheShares()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("1 LNA Noise Match").GroupedResults!;
        var nf = ds["SP1.NF"];
        int f0 = Index(nf.Axes[0].Values, 2.45e9);
        double n = nf.RealValues[f0], nmin = ds["SP1.NFmin"].RealValues[f0];
        var sopt = ds["SP1.Sopt"].ComplexValues[f0];
        double gain = Measure(ds, "Gain_dB").RealValues[f0];
        output.WriteLine($"2.45 GHz: NF {n:F4} dB, NFmin {nmin:F4} dB, |Sopt| {sopt.Magnitude:F4}, Zopt {ds["SP1.Zopt"].ComplexValues[f0]:F2}, gain {gain:F3} dB");
        Assert.Equal(0.618, n, 0.0005);
        Assert.True(n - nmin < 1e-3, "not noise-matched at 2.45 GHz");
        Assert.True(sopt.Magnitude < 0.02);
        Assert.Equal(11.2, gain, 0.05);
        var k = Measure(ds, "K_rollett").RealValues;
        output.WriteLine($"K {k.Min():F2} to {k.Max():F2}");
        Assert.True(k.All(x => x > 2), "K is not above 2 across the band");

        var shares = Shares(ds["SP1.NFpct"], f0);
        foreach (var (part, pct) in shares.OrderByDescending(s => s.Value)) output.WriteLine($"  {part,-6} {pct,6:F1} %");
        Assert.Equal(100, shares.Values.Sum(), 1e-6);
        Assert.Equal(34.1, shares["F1"], 0.05);
        Assert.Equal(30.4, shares["Rstab"], 0.05);
        Assert.Equal(28.7, shares["L1"], 0.05);
        Assert.Equal(3.5, shares["Rg"], 0.05);
        Assert.True(shares["Rs"] < 2 && shares["Rd"] < 2);
    }

    // ── 2. The budget ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReceiverBudget_NfIsFriis_AtEveryLnaGain_AndTheIfStageDominatesAt16dB()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("2 Receiver Budget").GroupedResults!;
        var nf = ds["SP1.NF"];
        var friis = Measure(ds, "NF_friis");
        var g = nf.Axes[0].Values;
        double worst = 0;
        for (int i = 0; i < g.Length; i++) worst = Math.Max(worst, Math.Abs(nf.RealValues[i] - friis.RealValues[i]));
        output.WriteLine($"LNA gain {g[0]}..{g[^1]} dB: NF {nf.RealValues[0]:F3}..{nf.RealValues[^1]:F3} dB, max |NF - Friis| {worst:E2} dB");
        Assert.True(worst < 1e-9);

        int at16 = Index(g, 16);
        var shares = Shares(ds["SP1.NFpct"], at16);
        output.WriteLine($"at 16 dB: NF {nf.RealValues[at16]:F3} dB, gain {Measure(ds, "Gain_dB").RealValues[at16]:F2} dB, "
                       + string.Join(", ", shares.Select(s => $"{s.Key} {s.Value:F1} %")));
        Assert.Equal(2.12, nf.RealValues[at16], 0.005);
        Assert.Equal(27, Measure(ds, "Gain_dB").RealValues[at16], 1e-6);
        Assert.Equal(24.4, shares["LNA"], 0.05);
        Assert.Equal(47.9, shares["IFA"], 0.05);
        Assert.Equal(0.90, nf.RealValues[^1], 0.005);
        var top = Shares(ds["SP1.NFpct"], g.Length - 1);
        Assert.Equal(67.0, top["LNA"], 0.05);
        Assert.Equal(20.9, top["IFA"], 0.05);
    }

    // ── 3. The mixer ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MixerNoise_SsbAndDsbAgainstLoDrive_AndTheImagesShare()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("3 Mixer Noise").GroupedResults!;
        var plo = ds["HB1.NF"].Axes[0].Values;
        foreach (double p in new[] { -10.0, 0, 6, 10 })
        {
            int i = Index(plo, p);
            output.WriteLine($"LO {p,4} dBm: SSB {ds["HB1.NF"].RealValues[i]:F2} dB, DSB {ds["HB1.NFdsb"].RealValues[i]:F2} dB, Gc {ds["HB1.Gc"].RealValues[i]:F2} dB");
        }
        int at6 = Index(plo, 6);
        Assert.Equal(4.73, ds["HB1.NF"].RealValues[at6], 0.005);
        Assert.Equal(3.46, ds["HB1.NFdsb"].RealValues[at6], 0.005);
        Assert.Equal(-9.20, ds["HB1.Gc"].RealValues[at6], 0.005);
        Assert.True(ds["HB1.NF"].RealValues[Index(plo, -10)] > 39, "a starved LO does not convert");
        foreach (var (p, ssb, dsb, gc) in new[] { (0.0, 7.96, 6.75, -13.40), (10.0, 4.38, 3.09, -8.49) })
        {
            int i = Index(plo, p);
            Assert.Equal(ssb, ds["HB1.NF"].RealValues[i], 0.005);
            Assert.Equal(dsb, ds["HB1.NFdsb"].RealValues[i], 0.005);
            Assert.Equal(gc, ds["HB1.Gc"].RealValues[i], 0.005);
        }

        // SSB − DSB is 10·log10(1 + image gain/signal gain): below the 3 dB of a mixer that converts both equally.
        double gap = ds["HB1.NF"].RealValues[at6] - ds["HB1.NFdsb"].RealValues[at6];
        Assert.Equal(1.27, gap, 0.01);

        var sb = ds["HB1.NFsb"];
        var lab = sb.Axes[^1].Labels!;
        double Sb(string s) => sb.RealValues[at6 * lab.Length + Array.IndexOf(lab, s)];
        var parts = Shares(ds["HB1.NFpct"], at6);
        output.WriteLine($"at +6 dBm: through the image {Sb("-1"):F1} %, the signal sideband {Sb("1"):F1} %, at the IF {Sb("0"):F1} %; "
                       + string.Join(", ", parts.Select(s => $"{s.Key} {s.Value:F1} %")));
        Assert.Equal(17.2, Sb("-1"), 0.05);
        Assert.Equal(38.7, Sb("1"), 0.05);
        Assert.Equal(53.7, parts["D1"], 0.05);
        Assert.Equal(27.7, parts["P1"], 0.05);
        Assert.Equal(9.4, parts["LR"], 0.05);
        Assert.Equal(9.2, parts["LL"], 0.05);
        // The LO is a P1Tone, a source, so it is noiseless here and not a part in the shares.
        Assert.False(parts.ContainsKey("PLO"));
    }

    // ── 4. The blocker ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LnaUnderABlocker_SmallBlockerIsBench1sNf_AndNfRisesTowardCompression()
    {
        CopyDirectory(Workspace, _ws);
        var lna = Simulate("1 LNA Noise Match").GroupedResults!;
        double small = lna["SP1.NF"].RealValues[Index(lna["SP1.NF"].Axes[0].Values, 2.45e9)];
        var ds = Simulate("4 LNA Under a Blocker").GroupedResults!;
        var nf = ds["HB1.NF"];
        var p = nf.Axes[0].Values;
        var gb = Measure(ds, "Gblk_dB").RealValues;
        for (int i = 0; i < p.Length; i += 2)
            output.WriteLine($"blocker {p[i],6:F1} dBm: NF {nf.RealValues[i]:F4} dB, Gc {ds["HB1.Gc"].RealValues[i]:F3} dB, blocker gain {gb[i]:F3} dB");
        output.WriteLine($"bench 1: {small:F6} dB; here at {p[0]} dBm: {nf.RealValues[0]:F6} dB");
        Assert.Equal(small, nf.RealValues[0], 1e-4);
        Assert.Equal(0.67, nf.RealValues[Index(p, -2.5)], 0.005);
        Assert.Equal(1.62, nf.RealValues[Index(p, 2.5)], 0.005);
        Assert.Equal(4.45, nf.RealValues[Index(p, 10)], 0.005);
        Assert.Equal(11.83, gb[0], 0.005);
        // the blocker's own gain is 1 dB down between +2.5 and +5 dBm
        Assert.True(gb[0] - gb[Index(p, 2.5)] < 1 && gb[0] - gb[Index(p, 5)] > 1);
        // desensitisation: the signal's gain falls faster than the blocker's own
        var gc = ds["HB1.Gc"].RealValues;
        Assert.True(gc[0] - gc[^1] > gb[0] - gb[^1]);
    }

    // ── 5. The sourcepull ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoiseSourcepull_NfAndGainOptimaOnTheSourcePlane_AndDriveRaisesNf()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("5 Noise Sourcepull").GroupedResults!;
        var nf = ds["LP1.NF"];
        var gt = ds["LP1.Gt_dB"];
        var z = ds["LP1.__SrcZ"].ComplexValues;
        int np = nf.Axes[0].Length, ns = nf.Axes[1].Length;
        Assert.Equal(4, ns);
        Assert.True(ds["LP1.Converged"].RealValues.All(c => c == 1), "a grid point did not converge");
        (int at, double v) Best(DataCube c, int rung, bool max) =>
            Enumerable.Range(0, np).Select(i => (i, c.RealValues[i * ns + rung])).Aggregate((a, b) => (max ? b.Item2 > a.Item2 : b.Item2 < a.Item2) ? b : a);
        Complex G(int i) => (z[i] - 50) / (z[i] + 50);
        string Polar(Complex g) => $"{g.Magnitude:F2}∠{g.Phase * 180 / Math.PI:F0}°";

        var (iNf, nfMin) = Best(nf, 0, false);
        var (iGt, gtMax) = Best(gt, 0, true);
        var (_, nfMinDriven) = Best(nf, 3, false);
        output.WriteLine($"−30 dBm: NF lowest {nfMin:F3} dB at {Polar(G(iNf))}, gain there {gt.RealValues[iNf * ns]:F2} dB; "
                       + $"Gt highest {gtMax:F2} dB at {Polar(G(iGt))}, NF there {nf.RealValues[iGt * ns]:F3} dB; NF at 50 ohm {nf.RealValues[0]:F3} dB");
        output.WriteLine($"0 dBm: NF lowest {nfMinDriven:F3} dB, at 50 ohm {nf.RealValues[3]:F3} dB");
        Assert.Equal(0.39, nfMin, 0.005);
        Assert.Equal(0.75, G(iNf).Magnitude, 1e-6);
        Assert.Equal(15, G(iNf).Phase * 180 / Math.PI, 1e-3);
        Assert.Equal(11.50, gtMax, 0.005);
        Assert.Equal(0.6, G(iGt).Magnitude, 1e-6);
        Assert.Equal(0.97, nf.RealValues[0], 0.005);
        Assert.Equal(0.53, nfMinDriven, 0.005);
    }

    // ── 6. The EVM ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReceiverEvm_NoisePhaseNoiseAndDistortionRegimes()
    {
        CopyDirectory(Workspace, _ws);
        var run = Simulate("6 Receiver EVM");
        var ds = run.GroupedResults!;
        var evm = ds["ENV1.EVM_pct"];
        var pin = evm.Axes[0].Values;
        int nc = evm.Axes[^1].Length;
        double At(string cube, int i) => ds["ENV1." + cube].RealValues[i * nc];   // probe Iif, the NR carrier
        double At1(string cube, int i) => ds["ENV1." + cube].RealValues[i];       // probe Iif
        for (int i = 0; i < pin.Length; i++)
            output.WriteLine($"Pin {pin[i],5} dBm: EVM {At("EVM_pct", i):F2} %, distortion {At("EVMdist_pct", i):F2} %, noise {At("EVMnoise_pct", i):F2} %, SNR {At("SNR_dB", i):F1} dB");
        // The README's table: Pin, EVM, noise part, distortion part (%), SNR (dB).
        foreach (var (p, e, n, d, snr) in new[]
                 {
                     (-95.0, 31.08, 31.08, 0.40, 10.1), (-80, 5.66, 5.65, 0.40, 25.0), (-65, 1.64, 1.60, 0.40, 36.0),
                     (-50, 1.34, 1.28, 0.40, 37.9), (-35, 1.53, 1.28, 0.83, 37.9), (-20, 11.76, 1.29, 11.69, 37.8),
                 })
        {
            int i = Index(pin, p);
            Assert.Equal(e, At("EVM_pct", i), 0.005);
            Assert.Equal(n, At("EVMnoise_pct", i), 0.005);
            Assert.Equal(d, At("EVMdist_pct", i), 0.005);
            Assert.Equal(snr, At("SNR_dB", i), 0.05);
        }
        // The phase-noise floor: from −65 to −20 dBm the noise part stands still while the signal grows 45 dB.
        Assert.InRange(At("SNR_dB", Index(pin, -50)), 37.8, 38.0);
        Assert.InRange(At("SNR_dB", Index(pin, -20)), 37.7, 38.0);
        // At the IF: the chain's 26 dB of small-signal gain (16 − 2 − 7 − 1 + 20), compressing at −20 dBm.
        Assert.Equal(26.0, At1("Gt_dB", Index(pin, -50)), 0.05);
        Assert.Equal(23.8, At1("Gt_dB", Index(pin, -20)), 0.05);
        Assert.Equal(-24.0, At1("Pavg_dBm", Index(pin, -50)), 0.05);
        // The mixer's NF is in the run, and nothing is named noiseless.
        Assert.DoesNotContain(run.Warnings, w => w.Contains("no noise model", StringComparison.Ordinal));
    }

    /// <summary>Every LO in this workspace is a P1Tone, the source an LO is drawn with.</summary>
    [Fact]
    public void EveryLoIsAP1Tone()
    {
        foreach (var (bench, lo) in new[] { ("3 Mixer Noise", "PLO"), ("6 Receiver EVM", "VCO") })
        {
            var (sch, _, _) = SchematicPersistence.LoadFromFile(Csch(bench, Workspace));
            Assert.Equal(SymbolKind.P1Tone, sch.Components.Single(c => c.InstanceName == lo).Symbol);
            Assert.DoesNotContain(sch.Components, c => c.Symbol == SymbolKind.ToneSource);
        }
    }

    // ── 7. The VCO ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VcoPhaseNoise_TuningRange_PhaseNoise_AndBench6sLoIsItsMaskAt3V5()
    {
        CopyDirectory(Workspace, _ws);
        var run = Simulate("7 VCO Phase Noise");
        var ds = run.GroupedResults!;
        var f = ds["OSC1.Fosc"];
        var vt = f.Axes[0].Values;
        var pn = ds["OSC1.PN"];
        var off = pn.Axes[1].Values;
        int no = off.Length;
        var corner = ds["OSC1.PNcorner"].RealValues;
        var kv = Measure(ds, "Kvco_MHz_per_V").RealValues;
        for (int i = 0; i < vt.Length; i++)
            output.WriteLine($"Vtune {vt[i],4} V: {f.RealValues[i] / 1e6:F2} MHz, Kvco {kv[i]:F1} MHz/V, PN at 100 kHz {pn.RealValues[i * no + Index(off, 1e5)]:F1} dBc/Hz, corner {corner[i]:F0} Hz");
        int v35 = Index(vt, 3.5);
        // The README's table: Vtune, Fosc (MHz), Kvco (MHz/V), PN at 100 kHz (dBc/Hz), flicker corner (kHz; 3.5 V's is the null).
        foreach (var (v, fo, k, l, c) in new[]
                 {
                     (3.0, 2092.7, 17.1, -106.6, 6.9), (3.5, 2101.2, 16.1, -106.5, double.NaN),
                     (4, 2108.7, 14.3, -106.4, 3.2), (9, 2155.6, 6.6, -105.6, 42.3),
                 })
        {
            int i = Index(vt, v);
            Assert.Equal(fo, f.RealValues[i] / 1e6, 0.05);
            Assert.Equal(k, kv[i], 0.05);
            Assert.Equal(l, pn.RealValues[i * no + Index(off, 1e5)], 0.05);
            if (!double.IsNaN(c)) Assert.Equal(c, corner[i] / 1e3, 0.05);
        }
        Assert.Equal(-128.8, pn.RealValues[v35 * no + Index(off, 1e6)], 0.05);
        Assert.True(corner[v35] < 10, "the flicker corner at 3.5 V is not the null");
        var share = Shares(ds["OSC1.PNpct"], v35 * no + Index(off, 1e5));
        output.WriteLine("at 3.5 V, 100 kHz: " + string.Join(", ", share.Where(x => x.Value > 1).Select(x => $"{x.Key} {x.Value:F1} %")));
        Assert.InRange(share["Q1"], 78, 88);
        Assert.True(share["Rt"] > share["RL"] && share["RL"] > 5, "the tuning resistor and the load are not next");

        // Bench 6's LO carries this curve: Copy as Phase-Noise Mask of the PN trace at 3.5 V, pasted on the VCO source.
        string mask = PhaseNoiseMask.Assignment(Enumerable.Range(0, no).Select(i => (off[i], pn.RealValues[v35 * no + i])));
        var (sch, _, _) = SchematicPersistence.LoadFromFile(Csch("6 Receiver EVM", Workspace));
        var lo = sch.Components.Single(c => c.InstanceName == "VCO").Parameters.Single(p => p.Name == "PhaseNoise");
        output.WriteLine(mask);
        Assert.Equal(mask["PhaseNoise=".Length..].Trim('"'), lo.Expression.Trim('"'));
        var flo = sch.Components.Single(c => c.InstanceName == "VAR1").Parameters.Single(p => p.Name == "Flo");
        Assert.Equal(("2101.2", "MHz"), (flo.Expression, flo.Unit));
        Assert.DoesNotContain(run.Warnings, w => w.Contains("differ by", StringComparison.Ordinal));
    }

    // ── check ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>`circuitrf check` on the workspace: no error, and the only warning is the one the README explains, the
    /// ambient set to 290 K, once per bench.</summary>
    [Fact]
    public void Check_HasNoError_AndOnlyTheAmbientWarningPerBench()
    {
        var was = Console.Out;
        var sw = new StringWriter();
        int exit;
        try { Console.SetOut(sw); exit = CliEntry.Run(["check", Workspace, "--json"]); }
        finally { Console.SetOut(was); }
        Assert.Equal(0, exit);
        var diags = JsonNode.Parse(sw.ToString())!["diagnostics"]!.AsArray();
        var warnings = diags.Where(d => (string?)d!["severity"] == "warning").Select(d => (string)d!["message"]!).ToList();
        foreach (var w in warnings) output.WriteLine(w);
        Assert.DoesNotContain(diags, d => (string?)d!["severity"] == "error");
        Assert.Equal(Benches.Length, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("Ambient temperature 16.85", w));
    }

    // ── The Data Displays ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 LNA Noise Match")]
    [InlineData("2 Receiver Budget")]
    [InlineData("3 Mixer Noise")]
    [InlineData("4 LNA Under a Blocker")]
    [InlineData("5 Noise Sourcepull")]
    [InlineData("6 Receiver EVM")]
    [InlineData("7 VCO Phase Noise")]
    public async Task EveryTraceResolves(string bench)
    {
        CopyDirectory(Workspace, _ws);
        var win = await LoadDisplay(bench);
        var plots = win.Tabs.SelectMany(t => t.DataDisplay.Plots).ToList();
        Assert.NotEmpty(plots);
        foreach (var plot in plots)
        {
            Assert.NotEmpty(plot.PlotVM.Plot.Traces);
            foreach (var t in plot.PlotVM.Plot.Traces)
            {
                string trace = $"{bench}: '{plot.PlotVM.Plot.CustomTitle}' {t.CubeName ?? t.Expression ?? t.ContourData?.MetricName}";
                Assert.True(t.InvalidSpecText is null && t.ExpressionError is null, $"{trace}: {t.ExpressionError ?? t.InvalidSpecText}");
                if (t.ContourData is { } cd) { Assert.True(cd.Levels.Levels.Length > 0, trace + " drew no contour"); continue; }
                if (t.SummaryColumn is not null) continue;
                Assert.True(t.Points.Count > 0, trace + " drew nothing");
            }
        }
    }

    /// <summary>
    /// The seven <c>.cdd</c> files beside the benches, which Simulate opens: each bench run as Simulate runs it, its plots
    /// authored on that result (by <c>circuitrf plot --write-cdd</c>, or as the toolbar's presets build them, or as
    /// expression and contour traces), laid out on one tab, with a marker on the sample the README quotes.
    /// </summary>
    [AuthorFact]
    public async Task BuildTheDisplays()
    {
        CopyDirectory(Workspace, _ws);
        var json = new JsonSerializerOptions(DataDisplayJson.Options) { WriteIndented = true };
        foreach (string bench in Benches)
        {
            string npy = Path.Combine(_ws, "results", bench + ".npy");
            string src = bench + ".npy";
            var ds = Simulate(bench).GroupedResults!;
            var plots = new JsonArray();
            JsonNode? first = null;
            var markers = new List<(int Plot, int Trace, int Sample, double BoxX, double BoxY)>();

            // One rectangular or Smith plot by `circuitrf plot`, placed, its traces styled.
            void Plot(double left, double top, string[] args, params (string Style, bool Points)[] traces)
            {
                string cdd = Path.Combine(_ws, $"p{plots.Count}.cdd");
                Assert.Equal(0, CliEntry.Run(["plot", npy, "-o", Path.Combine(_ws, $"p{plots.Count}.svg"), .. args,
                                              "--size", "620x400", "--write-cdd", cdd]));
                var doc = JsonNode.Parse(File.ReadAllText(cdd))!;
                first ??= doc;
                var plot = doc["Tabs"]![0]!["Plots"]![0]!.DeepClone();
                plot["Left"] = left; plot["Top"] = top;
                plot["Legend"] = "Below";
                var ts = plot["Traces"]!.AsArray();
                for (int i = 0; i < ts.Count && i < traces.Length; i++)
                {
                    ts[i]!["SourcePath"] = src;
                    var props = ts[i]!["Properties"]!;
                    if (traces[i].Style.Length > 0) props["LineType"] = traces[i].Style;
                    if (traces[i].Points)
                    {
                        props["LineEnabled"] = false;
                        props["MarkerEnabled"] = true;
                        props["MarkerSize"] = 3.0;
                    }
                }
                plots.Add(plot);
            }
            // One plot built as a configuration: a preset's, or expression and contour traces.
            JsonNode Add(PlotContainerConfig pc, double left, double top, double width = 620, double height = 400)
            {
                pc.Left = left; pc.Top = top; pc.Width = width; pc.Height = height;
                var node = JsonSerializer.SerializeToNode(pc, json)!;
                plots.Add(node);
                return node;
            }
            static string T(string cube, string label, string extra = "") => $"cube={cube},label=\"{label}\"{extra}";
            static TracePropertiesConfig Dots(int color, MarkerType shape, double size) => new()
            {
                LineEnabled = false, MarkerEnabled = true, MarkerType = shape, MarkerSize = size,
                LineColorIndex = color, MarkerColorIndex = color,
            };
            (string, bool) Line = ("", false), Dots_ = ("", true), Dash = ("Dashed", false), Bars = ("", false);

            switch (bench)
            {
                case "1 LNA Noise Match":
                {
                    Plot(0, 0, ["--trace", T("SP1.NF", "NF from 50 Ω"), "--trace", T("SP1.NFmin", "NFmin"),
                                "--title", "Noise figure of the LNA", "--ylabel", "dB", "--y", "0:1.2"], Line, Dash);
                    markers.Add((0, 0, 19, 90, 40));    // 2.45 GHz
                    // The toolbar's Noise and Gain Circles, drawn at 2.45 GHz.
                    var circles = NoiseCirclesPreset.Build(ds, src)!;
                    foreach (var t in circles.Traces) t.ExpressionFreqIndex = 19;
                    circles.CustomTitle = "Noise and gain circles at 2.45 GHz";
                    circles.CustomTitleOn = true;
                    Add(circles, 640, 0, 520, 640);
                    Plot(0, 420, ["--trace", T("SP1.NFpct[19,:]", "Share of F − 1", ",style=bars"),
                                  "--title", "Which part sets the noise at 2.45 GHz", "--ylabel", "%", "--y", "0:40"], Bars);
                    Plot(0, 840, ["--trace", T("Gain_dB", "Gain, dB(S21)"), "--trace", T("K_rollett", "Rollett K", ",axis=right"),
                                    "--title", "Gain and stability", "--ylabel", "dB", "--y2label", "K"], Line, Dash);
                    break;
                }
                case "2 Receiver Budget":
                    Plot(0, 0, ["--trace", T("SP1.NF[:,0]", "NF, circuitRF"), "--trace", T("NF_friis", "Friis, closed form"),
                                "--title", "Receiver noise figure against the LNA's gain", "--xlabel", "LNA gain, dB",
                                "--ylabel", "dB"], Dots_, Line);
                    markers.Add((0, 0, 6, 330, 40));    // 16 dB
                    Plot(640, 0, ["--trace", T("SP1.NFpct[:,0,0]", "LNA"), "--trace", T("SP1.NFpct[:,0,1]", "Filter"),
                                  "--trace", T("SP1.NFpct[:,0,2]", "Mixer stand-in"), "--trace", T("SP1.NFpct[:,0,3]", "IF amplifier"),
                                  "--title", "Each stage's share of F − 1", "--xlabel", "LNA gain, dB", "--ylabel", "%",
                                  "--y", "0:100"], Line, Line, Line, Line);
                    Plot(0, 420, ["--trace", T("Gain_dB[:,0]", "Gain, dB(S21)"), "--title", "Receiver gain",
                                  "--xlabel", "LNA gain, dB", "--ylabel", "dB"], Line);
                    break;
                case "3 Mixer Noise":
                    Plot(0, 0, ["--trace", T("HB1.NF[:,0]", "SSB"), "--trace", T("HB1.NFdsb[:,0]", "DSB"),
                                "--title", "Mixer noise figure against LO drive", "--xlabel", "LO available power, dBm",
                                "--ylabel", "dB", "--y", "0:20"], Line, Dash);
                    markers.Add((0, 0, 8, 330, 40));    // +6 dBm
                    Plot(640, 0, ["--trace", T("HB1.Gc[:,0]", "Conversion gain"), "--title", "Conversion gain, RF to IF",
                                  "--xlabel", "LO available power, dBm", "--ylabel", "dB", "--y", "-20:0"], Line);
                    Plot(0, 420, ["--trace", T("HB1.NFsb[8,0,:]", "Share of the output noise", ",style=bars"),
                                  "--title", "Where the noise comes in, at +6 dBm of LO", "--xlabel", "Input sideband k: f_IF + k·f_LO",
                                  "--ylabel", "%", "--y", "0:45"], Bars);
                    Plot(640, 420, ["--trace", T("HB1.NFpct[8,0,:]", "Share of F − 1", ",style=bars"),
                                    "--title", "Which part sets the noise, at +6 dBm of LO", "--ylabel", "%", "--y", "0:60"], Bars);
                    break;
                case "4 LNA Under a Blocker":
                    Plot(0, 0, ["--trace", T("HB1.NF[:,0]", "NF at 2.45 GHz"),
                                "--title", "LNA noise figure against a 2.3 GHz blocker", "--xlabel", "Blocker available power, dBm",
                                "--ylabel", "dB", "--y", "0:5"], Line);
                    markers.Add((0, 0, 0, 90, 40));     // −40 dBm: bench 1's NF
                    Plot(640, 0, ["--trace", T("Gblk_dB", "Blocker's own gain"), "--trace", T("HB1.Gc[:,0]", "Signal gain at 2.45 GHz"),
                                  "--title", "Gain, blocker and signal", "--xlabel", "Blocker available power, dBm",
                                  "--ylabel", "dB", "--y", "0:13"], Line, Dash);
                    break;
                case "5 Noise Sourcepull":
                {
                    // NF from 0.5 dB in 0.25 dB steps; gain from 7 dB in 1 dB steps; each with its optimum marked.
                    TraceConfig Contour(string metric, double pavl, int colour, double start, double step, double stop) => new()
                    {
                        SourcePath = src,
                        ContourTrace = new ContourTraceConfig
                        {
                            MetricName = metric, LoadpullGroup = "LP1", ConstraintKind = ConstraintKind.ConstantMetric,
                            ConstraintMetricName = "PavlDbm", ConstraintValue = pavl, LevelMode = ContourLevelMode.Range,
                            LevelStart = start, LevelStep = step, LevelStop = stop, ShowIsoLines = true, DrawLabels = true,
                            LabelSpacing = 1.5, DisplayMxp = true, FadeLineOpacity = false,
                            LineColor = TraceColor(colour), LineColorOverridden = true, StrokeWidth = 1.5f,
                        },
                        Properties = new TracePropertiesConfig { LineColorIndex = TraceProperties.LineColorOrder[colour] },
                    };
                    PlotContainerConfig Smith(string title, params TraceConfig[] t) => new()
                    {
                        PlotType = PlotType.Smith, CustomTitle = title, CustomTitleOn = true, Legend = LegendPlacement.Below,
                        Traces = [.. t],
                    };
                    Add(Smith("NF and gain on the source plane, −30 dBm", Contour("NF", -30, 0, 0.5, 0.25, 2),
                              Contour("Gt_dB", -30, 1, 7, 1, 11)), 0, 0, 560, 680);
                    Add(Smith("NF on the source plane, −30 and 0 dBm", Contour("NF", -30, 0, 0.5, 0.25, 2),
                              Contour("NF", 0, 2, 0.5, 0.25, 2)), 640, 0, 560, 680);
                    break;
                }
                case "6 Receiver EVM":
                {
                    TraceConfig Expr(string e, string label, int colour, string style = "Solid") => new()
                    {
                        SourcePath = src, Expression = e, Label = label,
                        Properties = new TracePropertiesConfig { LineColorIndex = TraceProperties.LineColorOrder[colour], LineType = Enum.Parse<LineType>(style) },
                    };
                    Add(new PlotContainerConfig
                    {
                        PlotType = PlotType.Rect, CustomTitle = "EVM against input power", CustomTitleOn = true,
                        CustomXLabel = "Input power, dBm", CustomXLabelOn = true, CustomYLabel = "dB", CustomYLabelOn = true,
                        Legend = LegendPlacement.Below,
                        Traces =
                        [
                            Expr("ENV1.EVM_dB[:, 0, 0]", "EVM", 0),
                            Expr("20*log10(ENV1.EVMnoise_pct[:, 0, 0]/100)", "Noise part (thermal and phase noise)", 1, "Dashed"),
                            Expr("20*log10(ENV1.EVMdist_pct[:, 0, 0]/100)", "Distortion part", 2, "Dotted"),
                        ],
                    }, 0, 0);
                    markers.Add((0, 0, 3, 250, 40));    // −50 dBm, the phase-noise floor
                    Plot(640, 0, ["--trace", T("ENV1.SNR_dB[:,0,0]", "In-channel SNR"),
                                  "--title", "Signal over the noise the run added", "--xlabel", "Input power, dBm",
                                  "--ylabel", "dB", "--y", "0:45"], Line);
                    TraceConfig Points(string cube, string slice, string label, int colour, MarkerType shape, double size) => new()
                    {
                        SourcePath = src, Expression = $"imag(ENV1.{cube}{slice})", XSpec = $"real(ENV1.{cube}{slice})",
                        Label = label, Properties = Dots(colour, shape, size),
                    };
                    PlotContainerConfig Constellation(string title, int pin) => new()
                    {
                        PlotType = PlotType.Rect, CustomTitle = title, CustomTitleOn = true,
                        CustomXLabel = "In-phase (I)", CustomXLabelOn = true, CustomYLabel = "Quadrature (Q)", CustomYLabelOn = true,
                        Legend = LegendPlacement.Below,
                        Traces =
                        [
                            Points("Const", $"[{pin}, 0, 0, :]", "Received", TraceProperties.LineColorOrder[1], MarkerType.Circle, 0.8),
                            Points("ConstRef", $"[{pin}, 0, :]", "Ideal", TraceProperties.LineColorOrder[0], MarkerType.Cross, 1.6),
                        ],
                        Axes = new AxesConfig { WindowWidth = 0, WindowHeight = 0, WindowSecondaryWidth = 0, WindowSecondaryHeight = 0, EqualAspect = true },
                    };
                    Add(Constellation("−50 dBm: phase noise", 3), 0, 420, 420, 440);
                    Add(Constellation("−20 dBm: compression", 5), 440, 420, 420, 440);
                    Add(Constellation("−95 dBm: thermal noise", 0), 880, 420, 420, 440);
                    break;
                }
                case "7 VCO Phase Noise":
                {
                    Plot(0, 0, ["--trace", T("Fosc_MHz", "Fosc"), "--title", "Tuning curve", "--xlabel", "Vtune, V",
                                "--ylabel", "MHz"], Line);
                    Plot(640, 0, ["--trace", T("Kvco_MHz_per_V", "Kvco"), "--title", "Tuning sensitivity", "--xlabel", "Vtune, V",
                                  "--ylabel", "MHz/V"], Line);
                    Plot(0, 420, ["--trace", T("OSC1.PN[1,:]", "3.5 V (bench 6's LO)"), "--trace", T("OSC1.PN[6,:]", "6 V"),
                                  "--trace", T("OSC1.PN[12,:]", "9 V"), "--title", "Phase noise", "--xlabel", "Offset from the carrier, Hz",
                                  "--ylabel", "dBc/Hz", "--freq-unit", "Hz"], Line, Dash, Line);
                    plots[^1]!["Axes"] = JsonSerializer.SerializeToNode(new AxesConfig
                    {
                        XScale = AxisScale.Log, WindowWidth = 0, WindowHeight = 0, WindowSecondaryWidth = 0, WindowSecondaryHeight = 0,
                    }, json);
                    markers.Add((2, 0, 12, 330, 40));   // 3.5 V at 100 kHz
                    Plot(640, 420, ["--trace", T("OSC1.PNcorner", "Flicker corner"), "--title", "Flicker corner against tuning",
                                    "--xlabel", "Vtune, V", "--ylabel", "Hz"], Line);
                    Plot(640, 840, ["--trace", T("OSC1.PNpct[1,12,:]", "Share of PN at 100 kHz", ",style=bars"),
                                  "--title", "Which part sets the phase noise, 3.5 V", "--ylabel", "%", "--y", "0:80"], Bars);
                    break;
                }
            }

            first ??= JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace, "..", "S-Parameter Noise", "1 Attenuator.cdd")));
            var page = first!.DeepClone();
            page["SelectedDataSource"] = src;
            page["Tabs"]![0]!["Name"] = bench;
            page["Tabs"]![0]!["Plots"] = plots;
            string target = Path.Combine(_ws, bench + ".cdd");
            File.WriteAllText(target, page.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

            // A marker stores its place as a point of the drawn trace, so read that point off the display as it loads.
            if (markers.Count > 0)
            {
                var win = await LoadDisplay(bench, simulate: false);
                var drawn = win.Tabs[0].DataDisplay.Plots;
                int n = 1;
                foreach (var (p, t, sample, dx, dy) in markers)
                {
                    var pt = drawn[p].PlotVM.Plot.Traces[t].Points[sample];
                    var trace = plots[p]!["Traces"]![t]!;
                    double boxX = (double)plots[p]!["Left"]! + dx, boxY = (double)plots[p]!["Top"]! + dy;
                    trace["Markers"] = new JsonArray(new JsonObject
                    {
                        ["Name"] = $"m{n++}", ["Index"] = sample, ["Freq"] = 0, ["FreqUnits"] = "GHz",
                        ["MatrixFormat"] = "MA", ["MatrixFormatImpedance"] = "RI", ["Style"] = "Medium",
                        ["UseNormalizedImpedance"] = true, ["MaximumFractionDigits"] = 3,
                        ["InfoBoxX"] = boxX, ["InfoBoxY"] = boxY, ["IsMulti"] = false, ["IsDelta"] = false,
                        ["PositionStaticX"] = pt.X, ["PositionStaticY"] = pt.Y, ["FreePosition"] = false,
                        ["SnappedToCurve"] = false, ["MarkerKind"] = "Polyline", ["ShowInfoBox"] = true,
                        ["ContourSnapped"] = false, ["VswrEnabled"] = false, ["VswrValue"] = 2.0,
                    });
                    output.WriteLine($"{bench}: marker on plot {p} trace {t} at sample {sample}: ({pt.X}, {pt.Y})");
                }
                File.WriteAllText(target, page.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            File.Copy(target, Path.Combine(Workspace, bench + ".cdd"), overwrite: true);
        }

        // The results these were authored on, kept for a look at the pages (UiSnapshot <bench>.cdd --results <dir>).
        string kept = Path.Combine(Path.GetTempPath(), "crf-rxnoise-author-results");
        if (Directory.Exists(kept)) Directory.Delete(kept, true);
        CopyDirectory(Path.Combine(_ws, "results"), kept);
        output.WriteLine("results kept at " + kept);
    }

    /// <summary>The ARGB of the palette colour a trace at <paramref name="i"/> in the legend order is drawn in.</summary>
    private static uint TraceColor(int i)
    {
        var c = TraceProperties.ColorLUT[TraceProperties.LineColorOrder[i]];
        return (uint)c.Alpha << 24 | (uint)c.Red << 16 | (uint)c.Green << 8 | c.Blue;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static int Index(double[] axis, double v)
    {
        int i = Array.FindIndex(axis, x => Math.Abs(x - v) <= 1e-9 * Math.Max(1, Math.Abs(v)));
        Assert.True(i >= 0, $"the axis does not land on {v}");
        return i;
    }

    /// <summary>A [lead…, part] percentage cube's shares at one index of its leading axes.</summary>
    private static Dictionary<string, double> Shares(DataCube pct, int at)
    {
        var parts = pct.Axes[^1].Labels!;
        return parts.Select((p, j) => (p, pct.RealValues[at * parts.Length + j])).ToDictionary(x => x.p, x => x.Item2);
    }

    /// <summary>The bench's display opened as Simulate opens it, on the bench's own fresh result.</summary>
    private async Task<DisplayWindowViewModel> LoadDisplay(string bench, bool simulate = true)
    {
        var win = new DisplayWindowViewModel();
        win.DataSourceLibrary.ResultsRootProvider = () => Path.Combine(_ws, "results");
        await win.LoadAllAsync(Path.Combine(_ws, bench + ".cdd"));
        if (simulate) Simulate(bench);
        var lib = win.DataSourceLibrary;
        win.RefreshAvailableDataSources();
        await lib.ReloadChangedAsync([Path.Combine(_ws, "results", bench + ".npy")]);
        await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);
        return win;
    }

    private string Csch(string cell, string? root = null) => Path.Combine(root ?? _ws, cell, "schematic", cell + ".csch");

    /// <summary>Simulate: netlist at the run base, run, file the results under the schematic's key.</summary>
    private RunResult Simulate(string cell)
    {
        string csch = Csch(cell);
        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, cell));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        Assert.True(run.Status == RunStatus.Success, $"{cell}: {run.StatusMessage}");
        foreach (var w in run.Warnings) output.WriteLine($"{cell}: warning: {w}");
        foreach (var e in run.Errors) output.WriteLine($"{cell}: error: {e}");
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("noiseless", StringComparison.Ordinal));
        RunResultsWriter.WriteRun(_ws, RunResultsWriter.SchematicKey(csch, cell), run.GroupedResults, null, model.ResultsFileName);
        return run;
    }

    private static DataCube Measure(DataSet ds, string name) => ds["measurements." + name];

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
