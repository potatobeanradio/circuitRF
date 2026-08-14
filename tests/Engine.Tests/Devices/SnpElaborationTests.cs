using System.Numerics;
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

    // ── InterpMode / InterpDomain: honored end-to-end through elaboration + simulation ──────

    [Fact]
    public void SnpElaboration_InterpModeMakima_DoesNotThrow_AndElaboratesToSnpModel()
    {
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        var cnl = $"""
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2  NumPorts=2 File={filePath} InterpMode=Makima InterpDomain=RI ExtrapMode=NearestEdge
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.IsType<SnpModel>(nl.Components.First(c => c.Model is SnpModel).Model);

        var freqs = new double[] { 1.05e9 }; // off-grid — exercises the interpolation path
        var ds = SParameterEngine.Run(nl, freqs);
        Assert.NotNull(ds["S"]);
    }

    [Fact]
    public void SnpElaboration_InterpDomainMA_ProducesADifferentResult_ThanRI_AtAnOffGridFrequency()
    {
        // Proves InterpDomain reaches the simulated result rather than being silently accepted and
        // ignored: RI (real/imag) and MA (magnitude/angle) cubic-spline interpolation of the same
        // file, at the same off-grid frequency, must disagree.
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        string CnlWith(string interpDomain) => $"""
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2  NumPorts=2 File={filePath} InterpMode=CubicSpline InterpDomain={interpDomain} ExtrapMode=NearestEdge
            """;
        var freqs = new double[] { 1.05e9 };

        var (libRi, tbRi) = ParseCnl(CnlWith("RI"));
        var dsRi = SParameterEngine.Run(new Elaborator(libRi).Elaborate(tbRi), freqs);

        var (libMa, tbMa) = ParseCnl(CnlWith("MA"));
        var dsMa = SParameterEngine.Run(new Elaborator(libMa).Elaborate(tbMa), freqs);

        var s11Ri = (Complex)dsRi["S"][0, 0, 0];
        var s11Ma = (Complex)dsMa["S"][0, 0, 0];

        Assert.True((s11Ri - s11Ma).Magnitude > 1e-6,
            $"RI S11={s11Ri:G6} and MA S11={s11Ma:G6} coincide — InterpDomain is not reaching the simulated result.");
    }

    [Fact]
    public void SnpElaboration_InterpDomainOmitted_DefaultsToMA_NotRI()
    {
        // Owner: "change the default Domain parameter for all SNP components ... to be MA (not RI)."
        // A hand-written .cnl that never states InterpDomain at all must resolve the same way an
        // explicit InterpDomain=MA does, and differently from InterpDomain=RI.
        var filePath = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
        string CnlWith(string? interpDomain) => $"""
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2  NumPorts=2 File={filePath} InterpMode=CubicSpline{(interpDomain is null ? "" : $" InterpDomain={interpDomain}")} ExtrapMode=NearestEdge
            """;
        var freqs = new double[] { 1.05e9 };

        Complex S11(string? interpDomain)
        {
            var (lib, tb) = ParseCnl(CnlWith(interpDomain));
            var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), freqs);
            return (Complex)ds["S"][0, 0, 0];
        }

        var s11Omitted = S11(null);
        var s11Ma      = S11("MA");
        var s11Ri      = S11("RI");

        Assert.True((s11Omitted - s11Ma).Magnitude < 1e-12,
            $"Omitted InterpDomain (S11={s11Omitted:G6}) does not match explicit MA (S11={s11Ma:G6}).");
        Assert.True((s11Omitted - s11Ri).Magnitude > 1e-6,
            $"Omitted InterpDomain (S11={s11Omitted:G6}) matches RI (S11={s11Ri:G6}) — default is not MA.");
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
