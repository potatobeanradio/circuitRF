// ================================================================
//  WsProbeGlobalCliTests.cs — brief-wsprobe-3's multi-probe derived metrics through the real CLI.
//
//  Every multi-probe built-in has a `measure` line in testdata/wsprobe/multi_probe_metrics.cnl. This
//  file holds three things about them:
//
//    * they evaluate through the CLI as a process exactly as they do through MeasurementEvaluator
//      in process — one evaluator, so a designer's measurement cannot answer differently headlessly;
//    * their cube SHAPES are the ones the brief states — {…, freq, k} for the document's flattened
//      lists, {…, freq, i, j} for a network with the port axes labelled by probe, {…, freq, node}
//      for Ohtomo, {…, freq, row, col} for a re-terminated wsp, {gS, gL, freq, env} and
//      {gS, gL, item} for the envelope;
//    * the refusals: a probe label that is not there, a probe list that is not a list, an envelope
//      asked of a probe that is not at a Term, and a sliced wsp whose metadata cannot be found.
//
//  The math is gated in tests/RfCore.Tests/Stability/WspGlobalTests.cs (synthetic matrices) and
//  tests/Engine.Tests/Linear/WSProbeGlobalTests.cs (real circuits).
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Export;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class WsProbeGlobalCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wsp3-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Fixture(string name) => Path.Combine(RepoRoot(), "testdata", "wsprobe", name);

    private static readonly string[] Complex2Port = ["yp2i", "yp2f", "bb", "fbb", "bd"];
    private static readonly string[] ComplexOther = ["yp2", "bc", "bc75", "ybg", "ya", "yf", "za", "zf", "oht", "ohtl", "ym", "ym2", "ndf", "term", "lp", "lpl"];
    private static readonly string[] RealCubes    = ["res", "lgf", "lpu"];

    [Fact]
    public void Sparam_EvaluatesEveryMultiProbeMetric_IdenticallyToTheEvaluator_AndInTheStatedShapes()
    {
        string dir = Path.Combine(_root, "metrics");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, "metrics.npy");

        var run = RunCli("sparam", Fixture("multi_probe_metrics.cnl"), "-o", outPath);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.DoesNotContain("failed to evaluate", run.StdOut);
        Assert.DoesNotContain("failed to evaluate", run.StdErr);
        output.WriteLine(run.StdOut);

        var (fromFile, _) = DataSetImporter.Import(outPath);
        var (_, inProcess) = RunAndEvaluate();

        foreach (string name in Complex2Port.Concat(ComplexOther))
        {
            var a = fromFile["measurements." + name].ComplexValues;
            var b = inProcess[name].ComplexValues;
            Assert.True(b.Length == a.Length, $"{name}: {a.Length} values from the CLI, {b.Length} in process");
            for (int k = 0; k < a.Length; k++)
            {
                Assert.Equal(b[k].Real,      a[k].Real,      12);
                Assert.Equal(b[k].Imaginary, a[k].Imaginary, 12);
            }
        }
        foreach (string name in RealCubes)
        {
            var a = fromFile["measurements." + name].RealValues;
            var b = inProcess[name].RealValues;
            Assert.Equal(b.Length, a.Length);
            for (int k = 0; k < a.Length; k++)
                if (double.IsNaN(b[k])) Assert.True(double.IsNaN(a[k]), $"{name}[{k}] should be NaN");
                else Assert.Equal(b[k], a[k], 12);
        }

        // ── the shapes ──────────────────────────────────────────────────────
        const int nf = 21;
        string[] Names(DataCube c) => c.Axes.Select(a => a.Name).ToArray();

        foreach (string name in Complex2Port)
        {
            var c = inProcess[name];
            Assert.Equal(["freq", "i", "j"], Names(c));
            Assert.Equal([nf, 2, 2], c.Axes.Select(a => a.Length).ToArray());
            Assert.Equal([1.0, 2.0], c.Axes[1].Values);
        }

        // The document's flattened lists: YP(1..8) and FET2(1..16), labelled with its names.
        Assert.Equal(["freq", "k"], Names(inProcess["yp2"]));
        Assert.Equal(["y11", "y12", "y21", "y22", "yf11", "yf12", "yf21", "yf22"], inProcess["yp2"].Axes[1].Labels!);
        Assert.Equal(["freq", "k"], Names(inProcess["bc"]));
        Assert.Equal(16, inProcess["bc"].Axes[1].Length);
        Assert.Equal("LGM", inProcess["bc"].Axes[1].Labels![15]);
        Assert.Equal("F_LGa", inProcess["bc"].Axes[1].Labels![8]);

        // A bifurcation over one probe: a 1×1 network whose port axes carry the probe's LABEL.
        foreach (string name in (string[])["ybg", "ya", "yf", "za", "zf"])
        {
            var c = inProcess[name];
            Assert.Equal(["freq", "i", "j"], Names(c));
            Assert.Equal([nf, 1, 1], c.Axes.Select(a => a.Length).ToArray());
            Assert.Equal(["PS"], c.Axes[1].Labels!);
            Assert.Equal([1.0], c.Axes[1].Values);
        }
        // wsp_ymatrix over every probe: 3×3 labelled PS, PL, P3 — the idx order; over a list, that order.
        Assert.Equal(["PS", "PL", "P3"], inProcess["ym"].Axes[1].Labels!);
        Assert.Equal([1.0, 2.0, 3.0], inProcess["ym"].Axes[2].Values);
        Assert.Equal(["PS", "P3"], inProcess["ym2"].Axes[1].Labels!);
        Assert.Equal([1.0, 3.0], inProcess["ym2"].Axes[1].Values);

        // Ohtomo: {freq, node}, node labelled by probe.
        Assert.Equal(["freq", "node"], Names(inProcess["oht"]));
        Assert.Equal(["PL"], inProcess["oht"].Axes[1].Labels!);

        // wsp_ndf of a run against itself is 1 everywhere.
        Assert.Equal(["freq"], Names(inProcess["ndf"]));
        Assert.All(inProcess["ndf"].ComplexValues, v => Assert.True((v - Complex.One).Magnitude < 1e-12));

        // wsp_terminate: the same shape as the wsp it came from.
        Assert.Equal(["freq", "row", "col"], Names(inProcess["term"]));
        Assert.Equal([nf, 6, 6], inProcess["term"].Axes.Select(a => a.Length).ToArray());

        // The envelope: {gS, gL, freq, env} with each Γ as a label; the unpulled side is one row.
        var lp = inProcess["lp"];
        Assert.Equal(["gS", "gL", "freq", "env"], Names(lp));
        Assert.Equal([6, 4, nf, 2], lp.Axes.Select(a => a.Length).ToArray());
        Assert.Equal(["H0env", "Y0env"], lp.Axes[3].Labels!);
        Assert.Equal("0.5@60", lp.Axes[0].Labels![1]);
        Assert.Equal("0.3@-90", lp.Axes[1].Labels![3]);
        var lpl = inProcess["lpl"];
        Assert.Equal([1, 8, nf, 2], lpl.Axes.Select(a => a.Length).ToArray());
        Assert.Equal(["unpulled"], lpl.Axes[0].Labels!);
        Assert.Equal("0.6@15", lpl.Axes[1].Labels![0]);

        var lpu = inProcess["lpu"];
        Assert.Equal(["gS", "gL", "item"], Names(lpu));
        Assert.Equal("unstable", lpu.Axes[2].Labels![0]);
        Assert.Equal("unstable_Y0", lpu.Axes[2].Labels![2]);
        Assert.Equal(DataKind.Real, lpu.DataKind);

        // The residual of a pair that brackets nothing but the whole amplifier is still small — the
        // two Terms are the feedback block, and nothing bypasses the probes.
        Assert.All(inProcess["res"].RealValues, r => Assert.True(r < 1e-8, $"residual {r:G3}"));
    }

    /// <summary>The refusals a caller meets first, each naming what would have answered.</summary>
    [Fact]
    public void TheRefusals_NameTheProbes_TheListSpelling_TheTermination_AndTheSlicedCube()
    {
        var (ds, _) = RunAndEvaluate();
        var (lib, tb) = CnlReader.ReadFile(Fixture("multi_probe_metrics.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);

        string Evaluate(string expr)
        {
            var t = new TestBench("tb");
            t.Measurements.Add(new Measurement("x", expr, ""));
            var errs = new MeasurementEvaluator(t, nl,
                new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { ["SP1"] = ds })
                .EvaluateInto(new DataSet());
            return Assert.Single(errs);
        }

        string bad = Evaluate("wsp_ymatrix(SP1.wsp, \"PS,GATE\")");
        Assert.Contains("GATE", bad);
        Assert.Contains("PS", bad);
        Assert.Contains("PL", bad);

        Assert.Contains("probe list", Evaluate("wsp_YA(SP1.wsp, SP1.wsp)"));
        Assert.Contains("must differ", Evaluate("wsp_yparam2(SP1.wsp, 1, 1)"));
        Assert.Contains("\"inner\"", Evaluate("wsp_yparam2(SP1.wsp, 1, 2, \"outer\")"));
        Assert.Contains("side must be", Evaluate("wsp_bifurcate(SP1.wsp, \"Y\", \"A\")"));

        // P3 is not at a Term: the envelope refuses by name.
        string notAtTerm = Evaluate("wsp_terminate(SP1.wsp, 3, 0.02, 2, 0.02)");
        Assert.Contains("wsprobe.envelope-probe-not-at-termination", notAtTerm);
        Assert.Contains("'P3'", notAtTerm);

        // A sliced wsp is a new object: its metadata cannot be found, and the refusal says what to pass.
        string sliced = Evaluate("wsp_terminate(at(SP1.wsp, \"freq\", 0), 1, 0.02, 2, 0.02)");
        Assert.Contains("SP1.wsp", sliced);
        output.WriteLine(sliced);

        // The Γ-grid spelling.
        string grid = Evaluate("wsp_loadpull(SP1.wsp, 1, 2, 3, \"circle\", \"0.3:4\")");
        Assert.Contains("|Γ|:count", grid);

        // The loop-gain search refuses swept data with the same instruction the Kurokawa search gives.
        Assert.Contains("at(", Evaluate("wsp_unstable_freq_loopgain(SP1.wsp)"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (DataSet Ds, DataSet Measurements) RunAndEvaluate()
    {
        var (lib, tb) = CnlReader.ReadFile(Fixture("multi_probe_metrics.cnl"));
        var nl    = new Elaborator(lib).Elaborate(tb);
        var freqs = tb.Analyses.OfType<SParameterAnalysis>().First().Expand(nl.ResolvedGlobals);
        var ds    = SParameterEngine.Run(nl, freqs);
        var meas  = new DataSet();
        var errs  = new MeasurementEvaluator(tb, nl,
            new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { ["SP1"] = ds })
            .EvaluateInto(meas);
        Assert.True(errs.Count == 0, string.Join("\n", errs));
        return (ds, meas);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.Result, errTask.Result);
    }

    private static string CliDll()
    {
        string cfg = AppContext.BaseDirectory.Contains("/Release/") || AppContext.BaseDirectory.Contains("\\Release\\")
            ? "Release" : "Debug";
        var candidates = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Cli", "bin", cfg), "CircuitRF.Cli.dll", SearchOption.AllDirectories);
        Assert.True(candidates.Length > 0, "the CLI must be built before this test runs");
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.False(string.IsNullOrEmpty(dir), "repo root not found above " + AppContext.BaseDirectory);
        return dir;
    }
}
