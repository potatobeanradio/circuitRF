// ================================================================
//  WsProbeDerivedMetricsCliTests.cs — brief-wsprobe-2's R-wsp2-14(j).
//
//  Every single-probe derived metric of the reference document's function library has a `measure`
//  line in testdata/wsprobe/derived_metrics.cnl. This file holds three things about them:
//
//    * they evaluate through the REAL CLI as a process exactly as they do through
//      MeasurementEvaluator in process — one evaluator, so a designer's measurement cannot answer
//      differently headlessly;
//    * their cube SHAPES are the ones R-wsp2-2 states — {…, freq, i, j} for a reduction,
//      {…, freq} for a scalar-per-frequency, {n} for the Kurokawa frequency list;
//    * a misspelled probe label lists the probes, and the reserved stability margin refuses with
//      the paper's DOI rather than inventing a number.
//
//  The math is gated in tests/RfCore.Tests/Stability/WspNodalTests.cs (the scalar cores) and
//  tests/Engine.Tests/Linear/WspNodalFunctionTests.cs (the circuits).
// ================================================================

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Export;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class WsProbeDerivedMetricsCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wsp2-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Fixture(string name) => Path.Combine(RepoRoot(), "testdata", "wsprobe", name);

    /// <summary>Every measurement in the fixture that is a Complex cube over frequency alone.</summary>
    private static readonly string[] ScalarPerFrequency =
    [
        "h0", "y0", "zg", "zl", "yg", "yl", "zop", "yop",
        "lgbi", "lguni", "lgfor", "lgrev", "lghst", "lgmb", "lgmbr", "lggft", "lggftr",
        "ngam", "zrem", "zrems", "zmp", "zms",
    ];

    /// <summary>Every measurement that is a Real cube over frequency alone.</summary>
    private static readonly string[] RealPerFrequency =
    [
        "encl", "encf", "lgdb", "gdb",
        "rp", "cp", "lp", "rs", "cs", "ls", "zrp", "zcp", "zlp", "zrs", "zcs", "zls",
        // WSP-9: the margin family is unitless and REAL — four proxies, two margins, their
        // minimum, the two pair primitives, and one in dB.
        "rY", "iY", "rH", "iH", "smy", "smh", "sm", "smz", "smy2", "smdb",
    ];

    /// <summary>Every measurement that is a 2-port matrix over frequency.</summary>
    private static readonly string[] TwoPortPerFrequency = ["Yred", "Zred", "szo", "src"];

    [Fact]
    public void Sparam_EvaluatesEveryDerivedMetric_IdenticallyToTheEvaluator_AndInTheStatedShapes()
    {
        string dir = Path.Combine(_root, "metrics");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, "metrics.npy");

        var run = RunCli("sparam", Fixture("derived_metrics.cnl"), "-o", outPath);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.DoesNotContain("failed to evaluate", run.StdOut);
        Assert.DoesNotContain("failed to evaluate", run.StdErr);
        output.WriteLine(run.StdOut);

        var (fromFile, _) = DataSetImporter.Import(outPath);
        var inProcess     = EvaluateInProcess();

        // ── the values: the CLI's and the evaluator's, measurement by measurement ──
        foreach (string name in ScalarPerFrequency.Concat(TwoPortPerFrequency))
        {
            var a = fromFile["measurements." + name].ComplexValues;
            var b = inProcess[name].ComplexValues;
            Assert.Equal(b.Length, a.Length);
            for (int k = 0; k < a.Length; k++)
            {
                Assert.Equal(b[k].Real,      a[k].Real,      12);
                Assert.Equal(b[k].Imaginary, a[k].Imaginary, 12);
            }
        }
        foreach (string name in RealPerFrequency.Append("gains"))
        {
            var a = fromFile["measurements." + name].RealValues;
            var b = inProcess[name].RealValues;
            Assert.Equal(b.Length, a.Length);
            for (int k = 0; k < a.Length; k++) Assert.Equal(b[k], a[k], 12);
        }

        // ── the shapes, R-wsp2-2 ────────────────────────────────────────────
        const int nf = 21;
        foreach (string name in ScalarPerFrequency)
        {
            var c = inProcess[name];
            Assert.Equal(["freq"], c.Axes.Select(a => a.Name).ToArray());
            Assert.Equal(nf, c.Axes[0].Length);
            Assert.Equal(DataKind.Complex, c.DataKind);
        }
        foreach (string name in RealPerFrequency)
        {
            var c = inProcess[name];
            Assert.Equal(["freq"], c.Axes.Select(a => a.Name).ToArray());
            Assert.Equal(nf, c.Axes[0].Length);
            Assert.Equal(DataKind.Real, c.DataKind);
        }
        foreach (string name in TwoPortPerFrequency)
        {
            var c = inProcess[name];
            Assert.Equal(["freq", "i", "j"], c.Axes.Select(a => a.Name).ToArray());
            Assert.Equal([nf, 2, 2], c.Axes.Select(a => a.Length).ToArray());
            // The same shape the S cube has, so every network-parameter path accepts it.
            Assert.Equal([1.0, 2.0], c.Axes[1].Values);
            Assert.Equal("port",     c.Axes[1].Unit);
        }

        // GainDEFs: four NAMED numbers per frequency is a labelled axis and nothing else.
        var g = inProcess["gains"];
        Assert.Equal(["freq", "gaindef"], g.Axes.Select(a => a.Name).ToArray());
        Assert.Equal(["GT_dB", "GP_dB", "GA_dB", "Gmax_dB"], g.Axes[1].Labels!);

        // The Kurokawa search returns a LIST of frequencies — empty here, because this network is
        // stable, and empty is the honest spelling of that (the document returns a zero, E.12).
        foreach (string name in (string[])["kfy", "kfh"])
        {
            var k = inProcess[name];
            Assert.Equal(["n"], k.Axes.Select(a => a.Name).ToArray());
            Assert.Empty(k.Axes[0].Values);
            Assert.Equal("Hz", k.Unit);   // the VALUES are frequencies; the "n" axis is a position
        }

        // Base SI: the capacitance measurements are FARADS, so a picofarad-scale part reads ~1e-12
        // and never ~1 (the document's own y_to_pc multiplies by 1e12).
        double maxC = inProcess["cp"].RealValues.Select(Math.Abs).Max();
        Assert.True(maxC < 1e-6,
            $"y_to_pc must return farads; the largest value is {maxC:G4}, which is a picofarad count.");
    }

    /// <summary>
    /// The identities that make the derived metrics agree with the RUN's own default cubes: one
    /// implementation, two callers (overview D-2). <c>wsp_ZG(SP1.wsp, idx)</c> is bit-identical to
    /// the engine's <c>ZG:P1</c>, and <c>wsp_loopgain(…, "BI")</c> to its <c>LG:P1</c>.
    /// </summary>
    [Fact]
    public void TheDerivedMetrics_AreBitIdenticalToTheRunsOwnDefaultCubes()
    {
        var (ds, meas) = RunAndEvaluate();

        foreach (var (metric, cube) in new (string, string)[]
                 { ("h0", "H0:P1"), ("y0", "Y0:P1"), ("zg", "ZG:P1"), ("zl", "ZL:P1"), ("lgbi", "LG:P1") })
        {
            var a = meas[metric].ComplexValues;
            var b = ds[cube].ComplexValues;
            Assert.Equal(b.Length, a.Length);
            for (int k = 0; k < a.Length; k++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(b[k].Real),      BitConverter.DoubleToInt64Bits(a[k].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(b[k].Imaginary), BitConverter.DoubleToInt64Bits(a[k].Imaginary));
            }
        }

        // "FOR" is an accepted alias of "UNI" (p. 68) — the same function, so the same bits.
        Assert.Equal(meas["lguni"].ComplexValues, meas["lgfor"].ComplexValues);
        // encirculations and enc are one function under two names (E.3).
        Assert.Equal(meas["encf"].RealValues, meas["encl"].RealValues);

        // Feedback is really present at this probe, so ZG is NOT 1/YG (Eq. 79) — without that the
        // whole §2.1 warning would be about nothing here.
        var zg = meas["zg"].ComplexValues;
        var yg = meas["yg"].ComplexValues;
        double worst = 0;
        for (int k = 0; k < zg.Length; k++)
            worst = Math.Max(worst, (zg[k] - Complex.One / yg[k]).Magnitude / zg[k].Magnitude);
        Assert.True(worst > 1e-6, $"ZG and 1/YG differ by only {worst:G3} — this fixture has no feedback.");
        output.WriteLine($"max relative |ZG − 1/YG| = {worst:G4}");
    }

    /// <summary>
    /// The refusal a caller meets first: a probe label that is not there lists the ones that are.
    /// Beside it, WSP-9 gate (j) — <c>wsp_stability_margin</c> now ANSWERS, with a Real cube.
    /// </summary>
    [Fact]
    public void AMisspelledProbe_ListsTheProbes_AndTheStabilityMarginAnswers()
    {
        var (ds, _) = RunAndEvaluate();
        var (lib, _) = CnlReader.ReadFile(Fixture("derived_metrics.cnl"));
        var nl = new Elaborator(lib).Elaborate(CnlReader.ReadFile(Fixture("derived_metrics.cnl")).TestBench);

        string Evaluate(string expr)
        {
            var tb = new TestBench("tb");
            tb.Measurements.Add(new Measurement("x", expr, ""));
            var errs = new MeasurementEvaluator(tb, nl,
                new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { ["SP1"] = ds })
                .EvaluateInto(new DataSet());
            return Assert.Single(errs);
        }

        string bad = Evaluate("wsp_ZG(SP1.wsp, SP1.idx(\"GATE\"))");
        Assert.Contains("GATE", bad);
        Assert.Contains("P1", bad);
        Assert.Contains("P2", bad);


        // A probe index outside the matrix names the legal range rather than reading past it.
        Assert.Contains("outside 1..2", Evaluate("wsp_ZG(SP1.wsp, 3)"));

        // An unknown loop-gain kind lists the document's own spellings.
        string kind = Evaluate("wsp_loopgain(wsp_yparam(SP1.wsp, 1), \"LOOP\")");
        Assert.Contains("GFTR", kind);
        Assert.Contains("FOR is an accepted alias", kind);

        // The Kurokawa search refuses swept data with the document's own note.
        string swept = Evaluate("wsp_unstable_freq_kurokawa(SP1.wsp)");
        Assert.Contains("multi-index swept data", swept);
        Assert.Contains("at(", swept);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DataSet EvaluateInProcess() => RunAndEvaluate().Measurements;

    private static (DataSet Ds, DataSet Measurements) RunAndEvaluate()
    {
        var (lib, tb) = CnlReader.ReadFile(Fixture("derived_metrics.cnl"));
        var nl    = new Elaborator(lib).Elaborate(tb);
        var freqs = tb.Analyses.OfType<SParameterAnalysis>().First().Expand(nl.ResolvedGlobals);
        var ds    = SParameterEngine.Run(nl, freqs);
        var meas  = new DataSet();
        Assert.Empty(new MeasurementEvaluator(tb, nl,
            new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { ["SP1"] = ds })
            .EvaluateInto(meas));
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
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(WsProbeDerivedMetricsCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}
