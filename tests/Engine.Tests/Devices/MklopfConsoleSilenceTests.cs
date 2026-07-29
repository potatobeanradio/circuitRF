using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate for brief-housekeeping-tearoff-palette-repo.md §5 (R-hk-9/R-hk-10): an S-parameter
/// simulation involving MKLOPF geometry that warns must produce Messages-pipeline entries
/// (ElaboratedNetlist.Warnings) and NOTHING on stdout or stderr. The actual leak was
/// ElaboratedNetlist.AddWarning itself echoing every warning to Console.Error — not
/// MicrostripKlopfModel.cs (already clean per a prior brief) — so this test exercises the full
/// Elaborator -> SParameterEngine.Run pipeline, not just the model in isolation.
/// </summary>
public class MklopfConsoleSilenceTests
{
    [Fact]
    public void SParamRun_MklopfWarningGeometry_WarningsSurfaced_NothingOnConsole()
    {
        // Same worked geometry as MklopfPerformanceAndMessagesTests.Gate9_CurvatureAndSectionCount
        // (a short/sharp offset taper that trips R-klp-10) with N forced above the section-count
        // reporting threshold (200) so both the curvature warning and the section-count
        // informational line fire.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            Port:P2  n2 0  Num=2  Z=50 Ohm
            MKLOPF:X1  n1 n2  Z1=50 Z2=100 L=3e-3 GammaMax=0.05 Offset=2e-3 H=1.6e-3 T=35e-6 Er=4.4 Sigma=5.8e7 TanD=0.02 N=300
            """;

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var capturedOut = new StringWriter();
        var capturedErr = new StringWriter();

        ElaboratedNetlist nl;
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedErr);

            var (lib, tb) = new CnlReader().Read(cnl);
            nl = new Elaborator(lib).Elaborate(tb);
            _ = SParameterEngine.Run(nl, [3e9]);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        Assert.Contains(nl.Warnings, w => w.Contains("R-klp-10", StringComparison.Ordinal));
        Assert.Contains(nl.Warnings, w => w.Contains("N=300", StringComparison.Ordinal));

        // Not a strict Assert.Equal("", ...) — xUnit runs test classes in parallel by default, and
        // Console.Out/Error are process-global mutable state; an unrelated concurrently-running
        // test (e.g. LoadpullEngine's own pre-existing, out-of-scope "[LP] ..." progress prints)
        // can land in this capture window with no relation to the fix under test. Assert the
        // SPECIFIC leak this brief closed never appears, rather than that literally nothing does.
        Assert.DoesNotContain("R-klp-10", capturedOut.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("R-klp-10", capturedErr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("N=300", capturedOut.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("N=300", capturedErr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[circuitRF]", capturedOut.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[circuitRF]", capturedErr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Deterministic, parallelism-immune companion gate: the actual fixed call site
    /// (<c>ElaboratedNetlist.AddWarning</c>) no longer writes to the console at all — verified by
    /// reading the source directly rather than by capturing global mutable Console state.
    /// </summary>
    [Fact]
    public void ElaboratedNetlist_AddWarning_ContainsNoLiveConsoleCall()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Core", "Elaboration", "ElaboratedNetlist.cs"));
        Assert.DoesNotContain("Console.Error.WriteLine(", src);
        Assert.DoesNotContain("Console.WriteLine(", src);
        Assert.DoesNotContain("Console.Out.Write", src);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }
}
