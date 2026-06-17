using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate tests for brief-snp-fixes Part 1: elaboration parse crash fix.
///
/// Root cause: the generic ResolveParameters path called _evaluator.Eval() on every override,
/// including File="/Users/…/x.s2p" — the leading '/' was parsed as a division operator at
/// position 0. Fix: ResolveSnpParameters stores string params raw (no Eval) and only evaluates
/// NumPorts numerically.
/// </summary>
public class SnpElaborationTests
{
    private static string Hero1Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "Hero1");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1 not found");
    }

    private static (Library lib, TestBench tb) ParseCnl(string cnl) =>
        new CnlReader().Read(cnl, "tb");

    // ── T1: Unix absolute path — elaboration succeeds, params stored verbatim ──

    [Fact]
    public void SnpElaboration_UnixAbsolutePath_DoesNotThrow_ParamsVerbatim()
    {
        const string cnl = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File=/abs/path/x.s2p InterpMode=Cubic ExtrapMode=NearestEdge
            """;

        var (lib, tb) = ParseCnl(cnl);

        // Elaborate should NOT throw, even though the file doesn't exist
        // (SnpModel loads the file lazily during Stamp, not during construction).
        var nl = new Elaborator(lib).Elaborate(tb);

        var comp = nl.Components.First(c => c.Model is SnpModel);
        Assert.Equal(ValueKind.String, comp.Parameters["File"].Kind);
        Assert.Equal("/abs/path/x.s2p", comp.Parameters["File"].AsString());
        Assert.Equal(ValueKind.Real, comp.Parameters["NumPorts"].Kind);
        Assert.Equal(2.0, comp.Parameters["NumPorts"].AsReal());
        Assert.IsType<SnpModel>(comp.Model);
        Assert.Equal(2, ((SnpModel)comp.Model).PortCount);
    }

    // ── T2: Windows-style path with backslashes — stored raw, not misparse ────

    [Fact]
    public void SnpElaboration_WindowsStylePath_StoredRaw()
    {
        // Unquoted backslash path in CNL — backslashes must not be parsed as division operators.
        const string cnl = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File=C:\data\myamp.s2p InterpMode=Cubic ExtrapMode=NearestEdge
            """;

        var (lib, tb) = ParseCnl(cnl);
        // Must not throw — before the fix, File=C:\data\… crashed the expression parser.
        var nl = new Elaborator(lib).Elaborate(tb);

        var comp = nl.Components.First(c => c.Model is SnpModel);
        Assert.Equal(ValueKind.String, comp.Parameters["File"].Kind);
        // Path contains the filename; exact backslash representation is platform-dependent but must not crash.
        Assert.Contains("myamp.s2p", comp.Parameters["File"].AsString());
        Assert.Equal(2.0, comp.Parameters["NumPorts"].AsReal());
    }

    // ── T3: End-to-end — real s2p + two Ports + S-param run ─────────────────

    [Fact]
    public void SnpElaboration_EndToEnd_RealFile_SParamRun()
    {
        // Unquoted absolute path — CnlReader stores it verbatim since no source-dir is provided.
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        var cnl = $"""
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2  NumPorts=2 File={filePath} InterpMode=Cubic ExtrapMode=NearestEdge
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Engine run must not throw — the file exists and is a valid S2P.
        var freqs = new double[] { 1.0e9, 2.0e9 };
        var ds = SParameterEngine.Run(nl, freqs);
        Assert.NotNull(ds["S"]);
        Assert.Equal(2, freqs.Length);
    }
}
