using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// Gate tests for brief-snp-relative-path: Elaborator.ResolveSnpFilePath resolves
/// relative SnP File= paths against BaseDirectory (the workspace root).
/// </summary>
public class SnpRelativePathTests : IDisposable
{
    // Minimal single-frequency 2-port Touchstone file (MA format).
    private const string MinimalS2p = """
        # GHz S MA R 50
        1.0   0.9 170   0.1 45   0.1 45   0.9 170
        """;

    // Temp root that is unique per test run.
    private readonly string _root;

    public SnpRelativePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"snp_rp_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteS2p(string relativePath)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, MinimalS2p);
    }

    private static (Library lib, TestBench tb) ParseCnl(string cnl) =>
        new CnlReader().Read(cnl, "tb");   // no sourceDirectory → CnlReader won't resolve relative paths

    private static string SnpParam(ElaboratedNetlist nl, string paramName) =>
        nl.Components.First(c => c.ComponentType.Equals("SnP", StringComparison.OrdinalIgnoreCase))
          .Parameters[paramName].AsString();

    // ── T1: relative path + BaseDirectory → resolved to absolute root/amp.s2p ──

    [Fact]
    public void Relative_ResolvesAgainstRoot()
    {
        WriteS2p("amp.s2p");

        const string cnl = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="amp.s2p"
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib) { BaseDirectory = _root }.Elaborate(tb);

        var resolved = SnpParam(nl, "File");
        Assert.True(Path.IsPathRooted(resolved), $"Expected absolute path, got: {resolved}");
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "amp.s2p")), resolved);
    }

    // ── T2: absolute path is passed through unchanged regardless of BaseDirectory ──

    [Fact]
    public void Absolute_Unchanged()
    {
        WriteS2p("amp.s2p");
        var absPath = Path.GetFullPath(Path.Combine(_root, "amp.s2p"));

        var cnl = $"""
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="{absPath}"
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib) { BaseDirectory = _root }.Elaborate(tb);

        Assert.Equal(absPath, SnpParam(nl, "File"));
    }

    // ── T3: no BaseDirectory → relative path left as-authored (legacy CWD behavior) ──

    [Fact]
    public void NoBaseDirectory_Legacy()
    {
        const string cnl = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="amp.s2p"
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);   // BaseDirectory = null

        // Stored value must equal the input (no rooting applied).
        Assert.Equal("amp.s2p", SnpParam(nl, "File"));
    }

    // ── T4: sub-directory path; forward-slash and backslash both resolve to the same absolute path ──

    [Fact]
    public void Subdir_Relative_SeparatorTolerance()
    {
        WriteS2p("touchstone/amp.s2p");

        const string cnlFwd = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="touchstone/amp.s2p"
            """;
        const string cnlBak = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="touchstone\amp.s2p"
            """;

        var expected = Path.GetFullPath(Path.Combine(_root, "touchstone", "amp.s2p"));

        var (libF, tbF) = ParseCnl(cnlFwd);
        var (libB, tbB) = ParseCnl(cnlBak);

        var nlF = new Elaborator(libF) { BaseDirectory = _root }.Elaborate(tbF);
        var nlB = new Elaborator(libB) { BaseDirectory = _root }.Elaborate(tbB);

        Assert.Equal(expected, SnpParam(nlF, "File"));
        Assert.Equal(expected, SnpParam(nlB, "File"));
    }

    // ── T5: missing file — resolved to an absolute path (not a bare relative name) ──
    //   When SnpModel.Stamp eventually loads the file, FileNotFoundException will contain the
    //   resolved absolute path so the user sees exactly where the engine looked.

    [Fact]
    public void MissingFile_ResolvedToAbsolutePath()
    {
        const string cnl = """
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="missing.s2p"
            """;

        var (lib, tb) = ParseCnl(cnl);
        var nl = new Elaborator(lib) { BaseDirectory = _root }.Elaborate(tb);

        var resolved = SnpParam(nl, "File");
        // Even though the file doesn't exist, the stored path must be absolute so
        // any downstream FileNotFoundException message names the absolute path.
        Assert.True(Path.IsPathRooted(resolved), $"Expected absolute path, got: {resolved}");
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "missing.s2p")), resolved);
    }
}
