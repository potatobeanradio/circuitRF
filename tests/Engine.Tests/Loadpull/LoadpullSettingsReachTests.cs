using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>Issue 65 §3: the caller's DC iteration cap and console trace (the CLI's --max-iter and --diag) reach the
/// loadpull's solves; they were dropped when the engine copied its settings, swept or not.</summary>
public sealed class LoadpullSettingsReachTests
{
    [Fact]
    public void TheCallersDcCapAndTrace_ReachTheLoadpullsSolves()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !Directory.Exists(Path.Combine(d, "testdata", "Hero3"))) d = Path.GetDirectoryName(d);
        string file = Path.Combine(d!, "testdata", "Hero3", "hero3.cnl");
        var (lib, tb) = CnlReader.ReadFile(file);
        using var nl = new Elaborator(lib) { BaseDirectory = Path.GetDirectoryName(file) }.Elaborate(tb);
        var lp = new LoadpullEngine(nl, tb, new AnalysisSettings { NonlinearMaxIter = 7, HbConsoleDiagnostics = true });
        Assert.Equal(7, lp.LoadpullSettings.NonlinearMaxIter);
        Assert.True(lp.LoadpullSettings.HbConsoleDiagnostics);
    }
}
