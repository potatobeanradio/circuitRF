using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// Learning a kit's corners AT IMPORT — the step that makes the answer a recorded fact rather than
/// something a workspace open has to re-derive by reading every netlist in the kit.
///
/// <para>All fixtures are synthetic and name no kit.</para>
/// </summary>
public sealed class PdkCornerImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-ci-" + Guid.NewGuid().ToString("N")[..8]);

    public PdkCornerImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string content)
    {
        string abs = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    [Fact]
    public void I1_ACornerFile_BecomesAnAxis_IdentifiedByItsKitRelativePath()
    {
        // Kit-relative, never absolute: a design's recorded corner has to survive the kit being
        // moved, re-cloned, or arriving on another machine.
        Write("models/capCorners.lib", """
            .LIB cap_typ
            .param carea = 1.5E-15
            .ENDL cap_typ
            .LIB cap_wcs
            .param carea = 1.65E-15
            .ENDL cap_wcs
            """);

        var axis = Assert.Single(PdkImporter.Import(_root).CornerAxes);

        Assert.Equal("models/capCorners.lib", axis.AxisId);
        Assert.Equal("capCorners", axis.DisplayName);
        Assert.Equal(["cap_typ", "cap_wcs"], axis.Options);
    }

    [Fact]
    public void I2_TwoFamilies_AreTwoIndependentAxes()
    {
        Write("models/capCorners.lib", ".LIB cap_typ\n.param carea = 1\n.ENDL cap_typ\n");
        Write("models/resCorners.lib", ".LIB res_typ\n.param rsh = 7\n.ENDL res_typ\n");

        var axes = PdkImporter.Import(_root).CornerAxes;

        Assert.Equal(2, axes.Count);
        Assert.Equal(["capCorners", "resCorners"], axes.Select(a => a.DisplayName));
    }

    [Fact]
    public void I3_AKitDeclaringNoSection_OffersNoCorners()
    {
        // The overwhelmingly common case. An axis per netlist regardless would put an empty picker
        // in front of every user of every kit.
        Write("models/caps.lib", ".subckt plate a b\nR1 a b 1k\n.ends\n");

        Assert.Empty(PdkImporter.Import(_root).CornerAxes);
    }

    [Fact]
    public void I4_AModelLibraryIsNotPARSED_JustBecauseItIsANetlist()
    {
        // The pre-filter is what keeps import cheap: a kit's netlists are mostly megabytes of model
        // cards that declare no section, and a `.lib` with TWO words is a REQUEST for a section, not
        // a declaration of one — so a file full of those still yields nothing.
        Write("models/models.lib", """
            .lib "capCorners.lib" cap_typ
            .subckt plate a b
            R1 a b 1k
            .ends
            """);

        Assert.Empty(PdkImporter.Import(_root).CornerAxes);
    }
}
