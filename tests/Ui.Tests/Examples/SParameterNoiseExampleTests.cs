// ================================================================
//  SParameterNoiseExampleTests.cs — the "S-Parameter Noise" example workspace
//
//  Every bench runs as Simulate runs it (SchematicRunService.RunNetlist on the schematic's own netlist, filed under
//  results/<cell>.npy), in a temporary copy of the workspace, with no display, and is held to an answer worked out
//  without circuitRF:
//
//    - 1 Attenuator: the matched 6 dB pad's NF is 6 dB at 290 K and 1 + (T/T0)(L - 1) at every other ambient, and
//      the wave leaving port 2 carries Bosma's kT(1 - 1/L);
//    - 2 Friis Cascade: the run's NF is Friis' sum at every pad loss and frequency, and the bench's VAR block states
//      exactly what the two stage files hold;
//    - 3 LNA Noise Circles: the LNA is noise-matched at 4 GHz, stable across the band, and every point of the two
//      circles gives its own noise figure back;
//    - every trace of the three Data Displays resolves on Simulate's own results.
//
//  BuildTheDisplays (CRF_AUTHOR_NOISE=1) writes the three .cdd files from those same results with `circuitrf plot`.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class SParameterNoiseExampleTests(ITestOutputHelper output) : IDisposable
{
    public const string Folder = "S-Parameter Noise";
    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    private static readonly string[] Benches = ["1 Attenuator", "2 Friis Cascade", "3 LNA Noise Circles"];

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-spnoise-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_NOISE") != "1")
                Skip = "re-builds the S-Parameter Noise example's Data Displays only with CRF_AUTHOR_NOISE=1";
        }
    }

    // ── 1. The attenuator ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Attenuator_NfIsTheLossAt290K_AndOnePlusTOverT0TimesLMinusOneAtEveryAmbient()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("1 Attenuator").GroupedResults!;
        var nf = ds["SP1.NF"];
        double[] tempC = nf.Axes[0].Values;
        double l = Math.Pow(10, 0.6);

        int at290 = Array.FindIndex(tempC, t => Math.Abs(t - 16.85) < 1e-9);
        Assert.True(at290 >= 0, "the sweep does not land on 16.85 degC");
        output.WriteLine($"NF at 290 K: {nf.RealValues[at290]:F12} dB");
        Assert.Equal(6.0, nf.RealValues[at290], 9);

        double worst = 0, worstTn = 0;
        var tn = Measure(ds, "Tn");
        for (int k = 0; k < tempC.Length; k++)
        {
            double tK = tempC[k] + 273.15;
            worst = Math.Max(worst, Math.Abs(nf.RealValues[k] - 10 * Math.Log10(1 + tK / 290 * (l - 1))));
            worstTn = Math.Max(worstTn, Math.Abs(tn.RealValues[k] - tK * (1 - 1 / l)) / tK);
            // The measurement written in the bench is the same closed form.
            Assert.Equal(Measure(ds, "NF_closed").RealValues[k], nf.RealValues[k], 9);
        }
        output.WriteLine($"{tempC.Length} ambients from {tempC[0] + 273.15:F2} K to {tempC[^1] + 273.15:F2} K: "
                       + $"max |NF - closed form| {worst:E2} dB, max relative |Cs22/k - T(1 - 1/L)| {worstTn:E2}");
        Assert.True(worst < 1e-9);
        Assert.True(worstTn < 1e-12);
    }

    // ── 2. Friis ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FriisCascade_RunNfIsFriisSum_AtEveryPadLossAndFrequency()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("2 Friis Cascade").GroupedResults!;
        var nf = ds["SP1.NF"];
        var nfMin = ds["SP1.NFmin"];
        var friis = Measure(ds, "NF_friis");
        var gain = Measure(ds, "Gain_dB");
        var gainSum = Measure(ds, "Gain_sum");
        int nl = nf.Axes[0].Length, nfreq = nf.Axes[1].Length;
        Assert.Equal(10, nl);

        double worst = 0, worstGain = 0;
        for (int i = 0; i < nl; i++)
            for (int f = 0; f < nfreq; f++)
            {
                double v = nf.RealValues[i * nfreq + f];
                worst = Math.Max(worst, Math.Abs(v - friis.RealValues[i]));
                worstGain = Math.Max(worstGain, Math.Abs(gain.RealValues[i * nfreq + f] - gainSum.RealValues[i]));
                Assert.True(nfMin.RealValues[i * nfreq + f] < v, "NFmin above NF");
            }
        output.WriteLine($"pad {nf.Axes[0].Values[0]}..{nf.Axes[0].Values[^1]} dB: NF {nf.RealValues[0]:F4}..{nf.RealValues[^1]:F4} dB, "
                       + $"max |NF - Friis| {worst:E2} dB, max |gain - sum| {worstGain:E2} dB");
        Assert.True(worst < 1e-9);
        Assert.True(worstGain < 1e-9);
        // The pad first costs its whole loss and more.
        Assert.True(Measure(ds, "NF_padfirst").RealValues[0] > friis.RealValues[0] + 0.9);
    }

    /// <summary>The bench's VAR block is the arithmetic's input, so it must say what the two stage files say.</summary>
    [Fact]
    public void FriisCascade_TheVarBlockStatesWhatTheStageFilesHold()
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(Workspace, "2 Friis Cascade", "schematic", "2 Friis Cascade.csch"));
        var vars = model.Components.Single(c => c.InstanceName == "VAR1").Parameters.ToDictionary(p => p.Name, p => p.Expression);
        Assert.Equal("16", vars["GA_dB"]);
        Assert.Equal("0.8", vars["NFminA_dB"]);
        Assert.Equal("polar(0.25, 45)", vars["GoptA"]);
        Assert.Equal("0.25", vars["rnA"]);
        Assert.Equal("12", vars["GB_dB"]);
        Assert.Equal("3", vars["NFB_dB"]);

        void Stage(string file, double gainDb, double nfMinDb, Complex gOpt, double rn)
        {
            var snp = TouchstoneIO.ReadFile(Path.Combine(Workspace, "data", file));
            foreach (var m in snp.Matrices)
            {
                Assert.Equal(gainDb, 20 * Math.Log10(m[1, 0].Magnitude), 9);
                Assert.Equal(0, m[0, 0].Magnitude + m[0, 1].Magnitude + m[1, 1].Magnitude);
            }
            Assert.Equal(snp.Frequencies.Length, snp.NoiseParameters.Count);
            foreach (var n in snp.NoiseParameters)
            {
                Assert.Equal(nfMinDb, n.NFminDb, 12);
                Assert.True((n.GammaOpt - gOpt).Magnitude < 1e-12, file);
                Assert.Equal(rn, n.RnNormalized, 12);
            }
        }
        Stage("amp_a.s2p", 16, 0.8, Complex.FromPolarCoordinates(0.25, Math.PI / 4), 0.25);
        Stage("amp_b.s2p", 12, 3, Complex.Zero, 0.4);
    }

    // ── 3. The LNA ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LnaNoiseCircles_NoiseMatchedAt4GHz_StableAcrossTheBand_AndEveryCirclePointGivesItsFigureBack()
    {
        CopyDirectory(Workspace, _ws);
        var ds = Simulate("3 LNA Noise Circles").GroupedResults!;
        var nf = ds["SP1.NF"];
        var nfMin = ds["SP1.NFmin"];
        var rn = ds["SP1.Rn"];
        var gOpt = ds["SP1.GammaOpt"];
        double[] freq = nf.Axes[0].Values;
        int f0 = Array.FindIndex(freq, f => Math.Abs(f - 4e9) < 1);
        Assert.Equal(20, f0);

        output.WriteLine($"4 GHz: NF {nf.RealValues[f0]:F4} dB, NFmin {nfMin.RealValues[f0]:F4} dB, Rn {rn.RealValues[f0]:F3} ohm, "
                       + $"|GammaOpt| {gOpt.ComplexValues[f0].Magnitude:E2}");
        Assert.True(gOpt.ComplexValues[f0].Magnitude < 2e-3, "not noise-matched at 4 GHz");
        Assert.True(nf.RealValues[f0] - nfMin.RealValues[f0] < 1e-4);
        Assert.InRange(nfMin.RealValues[f0], 0.95, 1.0);   // the README's 0.98 dB
        for (int k = 0; k < freq.Length; k++) Assert.True(nf.RealValues[k] >= nfMin.RealValues[k] - 1e-12);

        var kf = Measure(ds, "K_rollett");
        output.WriteLine($"K from {kf.RealValues.Min():F3} to {kf.RealValues.Max():F3}");
        Assert.True(kf.RealValues.All(k => k > 1), "not unconditionally stable in the band");

        // Every point of each circle, at every frequency where it exists, gives its noise figure back.
        foreach (var (name, level) in new[] { ("NF_125", 1.25), ("NF_200", 2.0) })
        {
            var c = Measure(ds, name);
            int npts = c.Axes[1].Length;
            double worst = 0;
            int circles = 0;
            for (int k = 0; k < freq.Length; k++)
            {
                if (nfMin.RealValues[k] >= level) continue;
                var p = new TwoPortNoise(Math.Pow(10, nfMin.RealValues[k] / 10), gOpt.ComplexValues[k], rn.RealValues[k], 50);
                for (int j = 0; j < npts; j++)
                    worst = Math.Max(worst, Math.Abs(10 * Math.Log10(NoiseMetrics.NoiseFactor(p, c.ComplexValues[k * npts + j])) - level));
                circles++;
            }
            output.WriteLine($"{name}: {circles} circles of {npts} points, max |nf - {level}| {worst:E2} dB");
            Assert.True(circles > 0 && worst < 1e-9, name);
        }
    }

    // ── 4. The Data Displays ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 Attenuator")]
    [InlineData("2 Friis Cascade")]
    [InlineData("3 LNA Noise Circles")]
    public async Task EveryTraceResolves(string bench)
    {
        CopyDirectory(Workspace, _ws);
        var win = await LoadDisplay(bench);

        var plots = win.Tabs.SelectMany(t => t.DataDisplay.Plots).ToList();
        Assert.NotEmpty(plots);
        Assert.All(plots, p => Assert.NotEmpty(p.PlotVM.Plot.Traces));
        foreach (var plot in plots)
            foreach (var t in plot.PlotVM.Plot.Traces)
            {
                string trace = $"{bench}: '{plot.PlotVM.Plot.CustomTitle}' {t.CubeName}";
                output.WriteLine($"{trace}: {t.Points.Count} points, {t.Markers.Count} marker(s)");
                Assert.True(t.InvalidSpecText is null && t.ExpressionError is null, $"{trace}: {t.ExpressionError ?? t.InvalidSpecText}");
                Assert.True(t.Points.Count > 0, trace + " drew nothing");
            }
    }

    /// <summary>
    /// The three <c>.cdd</c> files beside the benches, which Simulate opens: each bench run as Simulate runs it, each plot
    /// authored on that result by <c>circuitrf plot --write-cdd</c>, the plots of one bench laid out on one tab, and a
    /// marker placed on the sample the README quotes.
    /// </summary>
    [AuthorFact]
    public async Task BuildTheDisplays()
    {
        CopyDirectory(Workspace, _ws);
        foreach (string bench in Benches)
        {
            string npy = Path.Combine(_ws, "results", bench + ".npy");
            Simulate(bench);
            var plots = new JsonArray();
            JsonNode? first = null;
            var markers = new System.Collections.Generic.List<(int Plot, int Trace, int Sample, double BoxX, double BoxY)>();
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
                    ts[i]!["SourcePath"] = bench + ".npy";
                    var props = ts[i]!["Properties"]!;
                    if (traces[i].Style.Length > 0) props["LineType"] = traces[i].Style;
                    if (traces[i].Points)
                    {
                        // circuitRF's own result as markers alone, so the reference line under it stays visible.
                        props["LineEnabled"] = false;
                        props["MarkerEnabled"] = true;
                        props["MarkerSize"] = 3.0;
                    }
                }
                plots.Add(plot);
            }
            static string T(string cube, string label, string extra = "") => $"cube={cube},label=\"{label}\"{extra}";
            (string, bool) Line = ("", false), Dots = ("", true), Dash = ("Dashed", false);

            if (bench == "1 Attenuator")
            {
                Plot(0, 0, ["--trace", T("SP1.NF[:,0] vs T_K", "NF, circuitRF"), "--trace", T("NF_closed vs T_K", "Closed form"),
                            "--trace", T("Loss_dB[:,0] vs T_K", "Loss, −dB(S21)"),
                            "--title", "Noise figure of a 6 dB pad against its temperature", "--xlabel", "Pad temperature, K",
                            "--ylabel", "dB", "--y", "0:8"], Dots, Line, Dash);
                markers.Add((0, 0, 27, 330, 190));   // 290 K; the box in the empty lower right
                Plot(640, 0, ["--trace", T("Tn[:,0] vs T_K", "Cs22/k, circuitRF"), "--trace", T("Tn_closed vs T_K", "T(1 − 1/L), Bosma"),
                              "--title", "Noise temperature of the wave leaving port 2", "--xlabel", "Pad temperature, K",
                              "--ylabel", "K"], Dots, Line);
            }
            else if (bench == "2 Friis Cascade")
            {
                Plot(0, 0, ["--trace", T("SP1.NF[:,0]", "NF, circuitRF"), "--trace", T("NF_friis", "Friis, closed form"),
                            "--trace", T("SP1.NFmin[:,0]", "NFmin, circuitRF"),
                            "--title", "Cascade noise figure against the pad's loss", "--xlabel", "Pad loss, dB",
                            "--ylabel", "dB"], Dots, Line, Dash);
                markers.Add((0, 0, 2, 90, 40));     // the 3 dB pad
                Plot(640, 0, ["--trace", T("SP1.NF[:,0]", "Stage A first, circuitRF"), "--trace", T("NF_friis", "Stage A first, Friis"),
                              "--trace", T("NF_padfirst", "Pad first, Friis"),
                              "--title", "Why the low-noise stage goes first", "--xlabel", "Pad loss, dB",
                              "--ylabel", "dB", "--y", "0:12"], Dots, Line, Dash);
                Plot(0, 420, ["--trace", T("Gain_dB[:,0]", "Gain, circuitRF"), "--trace", T("Gain_sum", "GA − L + GB"),
                              "--title", "Cascade gain", "--xlabel", "Pad loss, dB", "--ylabel", "dB"], Dots, Line);
            }
            else
            {
                Plot(0, 0, ["--trace", T("SP1.NF", "NF from 50 Ω"), "--trace", T("SP1.NFmin", "NFmin"),
                            "--title", "Noise figure of the LNA", "--ylabel", "dB", "--y", "0.6:1.8"], Line, Dash);
                markers.Add((0, 0, 20, 90, 40));    // 4 GHz
                Plot(640, 0, ["--type", "smith", "--trace", T("SP1.GammaOpt", "GammaOpt"),
                              "--trace", T("NF_125[20,:]", "1.25 dB at 4 GHz"), "--trace", T("NF_200[20,:]", "2 dB at 4 GHz"),
                              "--title", "GammaOpt and two noise circles"], Line, Line, Line);
                markers.Add((1, 0, 20, 10, 40));
                Plot(0, 480, ["--trace", T("Gain_dB", "Gain, dB(S21)"), "--title", "Gain", "--ylabel", "dB"], Line);
                Plot(640, 480, ["--trace", T("K_rollett", "Rollett K"), "--title", "Stability: K above 1 across the band",
                                "--ylabel", "K", "--y", "0.8:1.8"], Line);
            }

            var page = first!;
            page["SelectedDataSource"] = bench + ".npy";
            page["Tabs"]![0]!["Name"] = bench;
            page["Tabs"]![0]!["Plots"] = plots;
            string target = Path.Combine(_ws, bench + ".cdd");
            File.WriteAllText(target, page.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

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
                    // The info box is in the display's own coordinates, so the plot's place is added to it.
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
                File.WriteAllText(target, page.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            File.Copy(target, Path.Combine(Workspace, bench + ".cdd"), overwrite: true);
        }

        // The results these were authored on, kept for a look at the pages (UiSnapshot <bench>.cdd --results <dir>).
        string kept = Path.Combine(Path.GetTempPath(), "crf-spnoise-author-results");
        if (Directory.Exists(kept)) Directory.Delete(kept, true);
        CopyDirectory(Path.Combine(_ws, "results"), kept);
        output.WriteLine("results kept at " + kept);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

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

    private string Csch(string cell) => Path.Combine(_ws, cell, "schematic", cell + ".csch");

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
        // Every part is noisy or is a port: the run names nothing as noiseless.
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
