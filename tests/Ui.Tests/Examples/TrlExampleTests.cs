// ================================================================
//  TrlExampleTests.cs — the "TRL and De-embedding" example workspace (brief DE-5)
//
//  Every bench runs as Simulate runs it (SchematicRunService.RunNetlist on the schematic's own netlist, filed under
//  results/<cell>.npy), in a temporary copy of the workspace, with no display:
//
//    - the four Lab benches and Lab Extrinsic reproduce the committed measurements, which is how a stale file is caught;
//    - TRL Calibration writes both fixtures, and Deembed DUT then returns the DUT cell from its measurement;
//    - FET Deembed's intrinsic Cgs and Rds are flat across 1-20 GHz after de-embedding and bent before it;
//    - every trace of the three Data Displays resolves on Simulate's own results.
//
//  BuildTheDisplays (CRF_AUTHOR_TRL=1) writes the three .cdd files from those same results with `circuitrf plot`.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class TrlExampleTests(ITestOutputHelper output) : IDisposable
{
    public const string Folder = "TRL and De-embedding";
    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-trl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_TRL") != "1")
                Skip = "re-builds the TRL and De-embedding example's Data Displays only with CRF_AUTHOR_TRL=1";
        }
    }

    // ── 1. The committed measurements are what the Lab benches make ──────────────────────────────────────────────

    [Theory]
    [InlineData("Lab Thru", "thru.s2p")]
    [InlineData("Lab Reflect", "reflect.s2p")]
    [InlineData("Lab Line", "line.s2p")]
    [InlineData("Lab DUT", "dut.s2p")]
    [InlineData("Lab Extrinsic", "gan_extrinsic.s6p")]
    public void ALabBench_MakesItsCommittedMeasurement(string bench, string file)
    {
        CopyDirectory(Workspace, _ws);
        string made = Path.Combine(_ws, "made_" + file);
        Assert.Equal(0, CliEntry.Run(["sparam", Csch(bench), "-o", made]));

        var want = TouchstoneIO.ReadFile(Path.Combine(_ws, "measurements", file));
        var got  = TouchstoneIO.ReadFile(made);
        Assert.Equal(want.Frequencies, got.Frequencies);
        double worst = want.Matrices.Zip(got.Matrices).Max(p => MaxDiff(p.First, p.Second));
        output.WriteLine($"{bench}: max |dS| {worst:E2}");
        Assert.True(worst < 1e-9, $"{bench}: measurements/{file} is stale (max |dS| {worst:E2}); re-run the bench to rewrite it");
    }

    // ── 2. Calibrate, then de-embed ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrlCalibration_ThenDeembedDut_ReturnsTheDutCell()
    {
        CopyDirectory(Workspace, _ws);
        var cal = Simulate("1 TRL Calibration");
        Assert.True(File.Exists(Path.Combine(_ws, "fixtures", "fixture_A.s2p")));
        Assert.True(File.Exists(Path.Combine(_ws, "fixtures", "fixture_B.s2p")));
        // The Line is cut for exactly 20 and 160 degrees at the band's edges, and the whole band is usable.
        Assert.DoesNotContain(cal.Warnings, w => w.Contains("degrees longer than the Thru", StringComparison.Ordinal));

        var run = Simulate("2 Deembed DUT");
        var s = run.GroupedResults!["SP1.S"];
        double worst = 0, raw = 0;
        for (int f = 0; f < s.Axes[0].Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    worst = Math.Max(worst, (S(s, f, i, j) - S(s, f, i + 2, j + 2)).Magnitude);
                    raw   = Math.Max(raw,   (S(s, f, i + 4, j + 4) - S(s, f, i + 2, j + 2)).Magnitude);
                }
        output.WriteLine($"de-embedded vs the DUT cell: max |dS| {worst:E2}; the raw measurement: {raw:E2}");
        Assert.True(worst < 1e-6, $"max |dS| {worst:E2}");
        Assert.True(raw > 0.1);
    }

    [Fact]
    public void DeembedDut_RunBeforeTheCalibration_NamesTheBenchToRunFirst()
    {
        CopyDirectory(Workspace, _ws);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch("2 Deembed DUT"));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, "2 Deembed DUT"));
        var run = SchematicRunService.RunNetlist(netlist, _ws);

        string said = string.Join("\n", run.Errors.Append(run.StatusMessage));
        output.WriteLine(said);
        Assert.Contains("fixture_A.s2p", said);
        Assert.Contains("TRL 'CAL' writes, on 1 TRL Calibration: run that first.", said);
    }

    // ── 3. The FET at its intrinsic plane ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FetDeembed_IntrinsicCgsAndRds_AreFlatAfterDeembedding_AndBentBefore()
    {
        CopyDirectory(Workspace, _ws);
        var run = Simulate("3 FET Deembed");
        Assert.DoesNotContain(run.Warnings, w => w.Contains("both on ground", StringComparison.Ordinal));
        var ds = run.GroupedResults!;

        // 1-20 GHz: below 1 GHz the 10 uH gate choke outside the Deembed still adds to Im(Y11).
        double Spread(string name)
        {
            var c = Measure(ds, name);
            var v = Enumerable.Range(0, c.Axes[0].Length).Where(k => c.Axes[0].Values[k] >= 1e9 - 1).Select(k => c.RealValues[k]).ToList();
            return (v.Max() - v.Min()) / Math.Abs(v.Average());
        }
        foreach (var q in new[] { "Cgs_pF", "Rds" })
        {
            output.WriteLine($"{q}: de-embedded spread {Spread(q + "_deembedded"):P2}, raw {Spread(q + "_raw"):P2}");
            Assert.True(Spread(q + "_deembedded") < 0.01, q);
            Assert.True(Spread(q + "_raw") > 0.1, q);
        }
        // The drain current flows through the Deembed at 0 Hz, and the HB point lands at the intrinsic plane.
        Assert.True(Measure(ds, "Id_deembedded_mA").RealValues[0] > 1);
        Assert.True(Measure(ds, "Pout_intrinsic_W").RealValues[0] > 0.1);
    }

    // ── 4. The Data Displays ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 TRL Calibration", "")]
    [InlineData("2 Deembed DUT", "1 TRL Calibration")]
    [InlineData("3 FET Deembed", "")]
    public async Task EveryTraceResolves(string bench, string first)
    {
        CopyDirectory(Workspace, _ws);
        if (first.Length > 0) Simulate(first);
        var win = new DisplayWindowViewModel();
        win.DataSourceLibrary.ResultsRootProvider = () => Path.Combine(_ws, "results");
        await win.LoadAllAsync(Path.Combine(_ws, bench + ".cdd"));
        Simulate(bench);
        var lib = win.DataSourceLibrary;
        win.RefreshAvailableDataSources();
        await lib.ReloadChangedAsync([Path.Combine(_ws, "results", bench + ".npy")]);
        await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);

        var plots = win.Tabs.SelectMany(t => t.DataDisplay.Plots).ToList();
        Assert.NotEmpty(plots);
        foreach (var plot in plots)
            foreach (var t in plot.PlotVM.Plot.Traces)
            {
                string trace = $"{bench}: '{plot.PlotVM.Plot.CustomTitle}' {t.CubeName}";
                output.WriteLine($"{trace}: {t.Points.Count} points");
                Assert.True(t.InvalidSpecText is null && t.ExpressionError is null, $"{trace}: {t.ExpressionError ?? t.InvalidSpecText}");
                Assert.True(t.Points.Count > 0, trace + " drew nothing");
            }
    }

    /// <summary>
    /// The three <c>.cdd</c> files beside the benches, which Simulate opens: each bench run as Simulate runs it, each plot
    /// authored on that result by <c>circuitrf plot --write-cdd</c>, the plots of one bench laid out on one tab.
    /// </summary>
    [AuthorFact]
    public void BuildTheDisplays()
    {
        CopyDirectory(Workspace, _ws);
        Simulate("1 TRL Calibration");
        foreach (string bench in new[] { "1 TRL Calibration", "2 Deembed DUT", "3 FET Deembed" })
        {
            string npy = Path.Combine(_ws, "results", bench + ".npy");
            if (bench != "1 TRL Calibration") Simulate(bench);
            var plots = new JsonArray();
            JsonNode? first = null;
            void Plot(double left, double top, string[] args, params (string Label, string Style)[] traces)
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
                for (int i = 0; i < ts.Count; i++)
                {
                    ts[i]!["SourcePath"] = bench + ".npy";
                    if (i < traces.Length && traces[i].Style.Length > 0) ts[i]!["Properties"]!["LineType"] = traces[i].Style;
                }
                plots.Add(plot);
            }
            static string T(string cube, string label, string extra = "") => $"cube={cube},label=\"{label}\"{extra}";

            if (bench == "1 TRL Calibration")
            {
                Plot(0, 0, ["--trace", T("LineDeg", "Line, longer than the Thru"), "--trace", T("Lower", "20 degrees"),
                            "--trace", T("Upper", "160 degrees"), "--title", "The Line's extra length", "--ylabel", "degrees",
                            "--y", "0:180"], ("", ""), ("", "Dashed"), ("", "Dashed"));
                Plot(640, 0, ["--trace", T("SA21", "|SA21|"), "--trace", T("SA11", "|SA11|"), "--trace", T("SB21", "|SB21|"),
                              "--trace", T("SB22", "|SB22|"), "--title", "The two fixtures, from TRL", "--ylabel", "|S|"]);
                Plot(0, 420, ["--trace", T("SP1.S", "Thru S21", ",i=2,j=1,y=db20"), "--trace", T("SP1.S", "Line S65", ",i=6,j=5,y=db20"),
                              "--trace", T("SP1.S", "Reflect S33", ",i=3,j=3,y=db20"), "--trace", T("SP1.S", "Reflect S44", ",i=4,j=4,y=db20"),
                              "--title", "The measured standards", "--ylabel", "dB", "--y", "-1.2:0.2"]);
            }
            else if (bench == "2 Deembed DUT")
            {
                Plot(0, 0, ["--trace", T("S21_raw", "Measured, with the fixtures"), "--trace", T("S21_truth", "The DUT cell"),
                            "--trace", T("S21_deembedded", "De-embedded"), "--title", "S21", "--ylabel", "dB"],
                     ("", ""), ("", ""), ("", "Dashed"));
                Plot(640, 0, ["--trace", T("S11_raw", "Measured, with the fixtures"), "--trace", T("S11_truth", "The DUT cell"),
                              "--trace", T("S11_deembedded", "De-embedded"), "--title", "S11", "--ylabel", "dB"],
                     ("", ""), ("", ""), ("", "Dashed"));
            }
            else
            {
                (string Q, string Title, string Unit, double L, double Top)[] qs =
                [
                    ("Cgs_pF", "Cgs = Im(Y11 + Y12)/ω", "pF", 0, 0), ("Cgd_pF", "Cgd = −Im(Y12)/ω", "pF", 640, 0),
                    ("gm_mS", "gm = |Y21 − Y12|", "mS", 0, 420), ("Rds", "Rds = 1/Re(Y22 + Y12)", "Ω", 640, 420),
                ];
                foreach (var (q, title, unit, l, top) in qs)
                    Plot(l, top, ["--trace", T(q + "_deembedded", "De-embedded"), "--trace", T(q + "_raw", "As measured"),
                                  "--title", title, "--ylabel", unit]);
            }

            var page = first!;
            page["SelectedDataSource"] = bench + ".npy";
            page["Tabs"]![0]!["Name"] = bench;
            page["Tabs"]![0]!["Plots"] = plots;
            File.WriteAllText(Path.Combine(Workspace, bench + ".cdd"),
                page.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        // The results these were authored on, kept for a look at the pages (UiSnapshot <bench>.cdd --results <dir>).
        string kept = Path.Combine(Path.GetTempPath(), "crf-trl-author-results");
        if (Directory.Exists(kept)) Directory.Delete(kept, true);
        CopyDirectory(Path.Combine(_ws, "results"), kept);
        output.WriteLine("results kept at " + kept);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

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
        foreach (var n in run.Notes) output.WriteLine($"{cell}: note: {n}");
        foreach (var e in run.Errors) output.WriteLine($"{cell}: error: {e}");
        Assert.Empty(run.Errors);
        RunResultsWriter.WriteRun(_ws, RunResultsWriter.SchematicKey(csch, cell), run.GroupedResults, null, model.ResultsFileName);
        return run;
    }

    private static DataCube Measure(DataSet ds, string name)
        => ds["measurements." + name];

    private static Complex S(DataCube s, int f, int i, int j) => s.ComplexValues[(f * s.Axes[1].Length + i) * s.Axes[2].Length + j];

    private static double MaxDiff(NumFlat.Mat<Complex> a, NumFlat.Mat<Complex> b)
    {
        double worst = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
                worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
        return worst;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
    }
}
