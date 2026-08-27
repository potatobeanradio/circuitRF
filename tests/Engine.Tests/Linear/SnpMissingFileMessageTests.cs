using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// An SnP that cannot load its Touchstone must say WHICH SnP, by its elaborated instance path, so a
/// design holding several of them — one of them nested inside a cell — points at the offender rather
/// than at the analysis that happened to stamp it.
///
/// <para>Owner-reported, 2026-08-26: the only name in the message was the analysis's, and a blank
/// <c>File</c> was reported as a file "not found" at the netlist's own FOLDER, because an empty
/// relative path combines to its base directory.</para>
/// </summary>
public class SnpMissingFileMessageTests
{
    private static ElaboratedComponent Ec(ComponentModel model, string instancePath)
        => new("SnP", instancePath, [1, 2],
               new Dictionary<string, Value>(StringComparer.Ordinal), model)
           { ReferenceNode = 0 };

    private static string StampFailure(string filePath, string instancePath)
    {
        var model = new SnpModel(portCount: 2, absoluteFilePath: filePath);
        var mna   = new MnaSystem(nonGroundNodes: 2);
        return Assert.Throws<FileNotFoundException>(
            () => model.Stamp(mna, Ec(model, instancePath), 2.0 * Math.PI * 1e9)).Message;
    }

    // ── T1: a blank File reads as MISSING, not as a wrong path ────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFile_SaysNoFileIsSpecified_AndNamesTheInstance(string blank)
    {
        var msg = StampFailure(blank, "SP2");

        Assert.Contains("SnP 'SP2'", msg);
        Assert.Contains("no Touchstone file is specified", msg);
        Assert.Contains("'File'", msg);
        Assert.DoesNotContain("not found", msg);
    }

    // ── T2: a nested instance is named by its full dotted path, and nothing more ──
    // The path read left to right IS the route through the hierarchy; a "(inside 'X1' then 'X2')"
    // gloss said the same thing twice and was removed (owner, 2026-08-26).

    [Fact]
    public void NestedInstance_IsNamedByItsFullDottedPath()
    {
        var msg = StampFailure("", "X1.X2.SP1");

        Assert.Contains("SnP 'X1.X2.SP1'", msg);
        Assert.DoesNotContain("inside", msg);
    }

    // ── T3: a genuinely missing file still says so, and still names the instance ──

    [Fact]
    public void MissingFile_StillReportsNotFound_WithTheInstancePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crf_absent_{Path.GetRandomFileName()}.s2p");
        var msg  = StampFailure(path, "AMP1.SP3");

        Assert.Contains("SnP 'AMP1.SP3'", msg);
        Assert.Contains("Touchstone file not found", msg);
        Assert.Contains(path, msg);
    }

    // ── T4: a FOLDER is reported as a folder, not as a missing file ───────────

    [Fact]
    public void FolderPath_IsReportedAsAFolder()
    {
        var msg = StampFailure(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "SP1");

        Assert.Contains("SnP 'SP1'", msg);
        Assert.Contains("names a folder", msg);
    }

    // ── T5: root cause — File="" no longer resolves to the netlist's own folder ──

    [Fact]
    public void BlankFileInCnl_DoesNotResolveToTheSourceDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_snp_blank_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        try
        {
            const string cnl = """
                Port:T1  n1 0  Num=1 Z=50 Ohm
                Port:T2  n2 0  Num=2 Z=50 Ohm
                SnP:S1   n1 n2  NumPorts=2 File=""
                """;
            var (lib, tb) = new CnlReader().Read(cnl, "tb", sourceDirectory: dir);
            var nl        = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);

            var ec = nl.Components.Single(
                c => c.ComponentType.Equals("SnP", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("", ec.Parameters["File"].AsString());

            // And the run-time refusal that follows names the component, not the folder.
            var msg = Assert.Throws<FileNotFoundException>(
                () => ec.Stamp(new MnaSystem(nonGroundNodes: 2), 2.0 * Math.PI * 1e9)).Message;
            Assert.Contains("SnP 'S1'", msg);
            Assert.Contains("no Touchstone file is specified", msg);
            Assert.DoesNotContain(dir, msg);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
