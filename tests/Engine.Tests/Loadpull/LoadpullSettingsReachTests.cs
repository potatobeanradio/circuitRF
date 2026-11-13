using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>Issue 65 §3: one loadpull gives one answer however it is run, and the caller's DC iteration cap and console
/// trace (the CLI's --max-iter and --diag) reach the loadpull's solves; they were dropped when the engine copied its
/// settings, swept or not.</summary>
public sealed class LoadpullSettingsReachTests
{
    private static string Hero3() => LoadpullParallelTests.Hero3("hero3.cnl");

    private static (Library Lib, TestBench Tb) Read() =>
        new CnlReader().Read(File.ReadAllText(Hero3())
            .Replace("Grid=\"hero3_load.gam\"", $"Grid=\"{LoadpullParallelTests.Hero3("hero3_load.gam")}\""));

    [Fact]
    public void TheCallersDcCapAndTrace_ReachTheLoadpullsSolves()
    {
        string file = Hero3();
        var (lib, tb) = CnlReader.ReadFile(file);
        using var nl = new Elaborator(lib) { BaseDirectory = Path.GetDirectoryName(file) }.Elaborate(tb);
        var lp = new LoadpullEngine(nl, tb, new AnalysisSettings { NonlinearMaxIter = 7, HbConsoleDiagnostics = true });
        Assert.Equal(7, lp.LoadpullSettings.NonlinearMaxIter);
        Assert.True(lp.LoadpullSettings.HbConsoleDiagnostics);
    }

    /// <summary>The brief's R-5: Hero 3 run unswept under the CLI's settings (gmin <c>Always</c>), swept over one point
    /// under the same settings, and unswept under the GUI's (the defaults) gives the same cubes bit for bit.</summary>
    [Fact]
    public void OneLoadpull_UnsweptSweptAndGui_IsBitIdentical()
    {
        var cli = new AnalysisSettings { ConductanceRegularization = RegularizationMode.Always };

        DataSet Unswept(AnalysisSettings? settings)
        {
            var (lib, tb) = Read();
            using var nl = new Elaborator(lib).Elaborate(tb);
            var lp = LoadpullEngine.Resolve(tb.Analyses.OfType<LoadpullAnalysis>().Single(), nl.ResolvedGlobals,
                                            nl.GlobalsWithExplicitUnit);
            return LoadpullPostProcessor.Enrich(new LoadpullEngine(nl, tb, settings).Run(lp));
        }

        DataSet SweptOnePoint()
        {
            var (lib, tb) = Read();
            return ParametricSweepEngine.Run(new ParametricSweepAnalysis("SW1", "RFfreq", [2e9], "LP1"), lib, tb, cli);
        }

        var unswept = Unswept(cli);
        AssertSameCubes(unswept, Unswept(null), leadingAxis: false);
        AssertSameCubes(unswept, SweptOnePoint(), leadingAxis: true);
    }

    /// <summary>Every cube of <paramref name="a"/> is in <paramref name="b"/> with the same bits; with
    /// <paramref name="leadingAxis"/> <paramref name="b"/>'s cubes carry the one-point sweep axis in front.</summary>
    private static void AssertSameCubes(DataSet a, DataSet b, bool leadingAxis)
    {
        Assert.NotEmpty(a.Cubes);
        foreach (var (name, x) in a.Cubes)
        {
            Assert.True(b.Cubes.TryGetValue(name, out var y), $"cube {name} is missing");
            // A __-prefixed metadata cube passes through a sweep unstacked (src/Engine/CLAUDE.md).
            int lead = leadingAxis && !name.StartsWith("__", StringComparison.Ordinal) ? 1 : 0;
            Assert.True(x.Axes.Count + lead == y!.Axes.Count, $"cube {name}: {x.Axes.Count} axes against {y.Axes.Count}");
            Assert.Equal(x.DataKind, y.DataKind);
            if (x.DataKind == DataKind.Complex)
                Assert.True(Bits(x.ComplexValues.SelectMany(c => new[] { c.Real, c.Imaginary }))
                            .SequenceEqual(Bits(y.ComplexValues.SelectMany(c => new[] { c.Real, c.Imaginary }))), $"cube {name} differs");
            else
                Assert.True(Bits(x.RealValues).SequenceEqual(Bits(y.RealValues)), $"cube {name} differs");
        }
    }

    private static long[] Bits(IEnumerable<double> v) => [.. v.Select(BitConverter.DoubleToInt64Bits)];
}
