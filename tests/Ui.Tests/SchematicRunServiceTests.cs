using System.IO;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 1 gate tests for SchematicRunService (Phase 6e Step 5).
/// Verify: S-param raw directive runs to DataSet; no-analysis returns NoAnalysis;
/// engine exception is captured as EngineError (never thrown).
/// </summary>
public sealed class SchematicRunServiceTests
{
    private static string TestDataDir => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "testdata"));

    // ── L1a: S-param raw directive path ──────────────────────────────────────

    [Fact]
    public void RunNetlist_SparamRawDirective_ReturnsSuccessWithDataSet()
    {
        // pi_network.cnl: "analysis SP   type=sparam  start=1 GHz stop=10 GHz step=1 GHz"
        // Three ports, two cell instances → 3-port S-param run.
        var path   = Path.Combine(TestDataDir, "pi_network.cnl");
        var result = SchematicRunService.RunNetlist(path);

        Assert.Equal(RunStatus.Success,  result.Status);
        Assert.Single(result.DataSets);
        Assert.True(result.DataSets[0].Contains("S"),
            "DataSet must contain an 'S' cube from the S-parameter analysis.");
    }

    // ── L1b: no analysis declared ─────────────────────────────────────────────

    [Fact]
    public void RunNetlist_NoAnalysis_ReturnsNoAnalysisStatus()
    {
        // hero1.cnl has no analysis directive at all.
        var path   = Path.Combine(TestDataDir, "Hero1", "hero1.cnl");
        var result = SchematicRunService.RunNetlist(path);

        Assert.Equal(RunStatus.NoAnalysis, result.Status);
        Assert.Empty(result.DataSets);
    }

    // ── L1c: engine exception captured (not thrown) ───────────────────────────

    [Fact]
    public void RunNetlist_EngineThrows_CapturedAsEngineError()
    {
        // A netlist with an S-param directive but NO Port/Term components.
        // SParameterEngine.Run throws InvalidOperationException("…requires at least one Port").
        // The service must capture this and return EngineError, not re-throw.
        const string cnl = """
            R:R1  a b  R=50 Ohm
            analysis NoPort  type=sparam  start=1 GHz stop=3 GHz step=1 GHz
            """;

        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, cnl);
            var result = SchematicRunService.RunNetlist(tmpPath);

            Assert.Equal(RunStatus.EngineError, result.Status);
            Assert.Empty(result.DataSets);
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    // ── L1d: elaboration failure captured ────────────────────────────────────

    [Fact]
    public void RunNetlist_ElaborationFails_CapturedAsEngineError()
    {
        // A netlist that references an unknown cell type — Elaborator throws.
        const string cnl = """
            BogusDevice:X1  a b  param=1
            analysis S1  type=sparam  start=1 GHz stop=3 GHz step=1 GHz
            Port:T1  a 0  Num=1
            Port:T2  b 0  Num=2
            """;

        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, cnl);
            var result = SchematicRunService.RunNetlist(tmpPath);

            // BogusDevice can't be resolved → elaboration or engine error, not a throw.
            Assert.NotEqual(RunStatus.Success, result.Status);
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    // ── L1e: diagnostics channel — floating node produces non-empty Warnings ──

    [Fact]
    public void RunNetlist_FloatingNodeFromBuriedTerm_WarningsNonEmpty()
    {
        // Same floating-node circuit as EngineDiagnosticsChannelTests T1 but exercised
        // via SchematicRunService.  RunResult.Warnings must be non-empty and contain
        // the regularization notice surfaced by SParameterEngine.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1  n1 0  R=50 Ohm
            define Sub(A)
              Term:T_buried  A 0  Num=1  Z=50 Ohm
            end Sub
            Sub:X1  n_float
            analysis SP  type=sparam  start=1 GHz  stop=1 GHz  step=1 GHz
            """;

        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, cnl);
            var result = SchematicRunService.RunNetlist(tmpPath);

            Assert.NotEmpty(result.Warnings);
            Assert.Contains(result.Warnings,
                w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    // ── L1f: diagnostics channel — clean netlist yields empty Warnings ────────

    [Fact]
    public void RunNetlist_CleanNetlist_WarningsEmpty()
    {
        // A well-formed 2-port circuit: no floating nodes, no regularization needed.
        // RunResult.Status must be Success and Warnings must be empty.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1  n1 n2  R=50 Ohm
            Port:P2  n2 0  Num=2  Z=50 Ohm
            analysis SP  type=sparam  start=1 GHz  stop=1 GHz  step=1 GHz
            """;

        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, cnl);
            var result = SchematicRunService.RunNetlist(tmpPath);

            Assert.Equal(RunStatus.Success, result.Status);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }
}
