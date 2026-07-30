// ================================================================
//  LabelAndAspectFixTests.cs  —  three owner reports (2026-07-30)
//
//  1. A newly-added Rect plot must open at the configured aspect ratio (golden by default) —
//     the auto-created display opened at a fixed 520x360 (1.444).
//  2. The source-prefix convention must apply to EVERY trace on a plot. A trace with a null
//     SourcePath but a known Data.FilePath contributed no source component, so it alone lost
//     its prefix while its siblings kept theirs.
//  3. S/Y/Z port axes read positionally — "S(1,2)", not "S(i=1,j=2)".
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LabelAndAspectFixTests
{
    private static SNP S2(string? path = null)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.60, -0.30); m[0, 1] = new Complex(0.05,  0.02);
        m[1, 0] = new Complex(3.20,  1.10); m[1, 1] = new Complex(0.45, -0.25);
        return new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0)) { FilePath = path };
    }

    private static Trace CubeS(int i, int j, string source = "/x/run.npy")
        => new(S2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = source,
            CubeName   = "SP1.S",
            Slice      =
            [
                new AxisSlice("freq", AxisRole.KeepAsX,    0),
                new AxisSlice("i",    AxisRole.PinToIndex, i),
                new AxisSlice("j",    AxisRole.PinToIndex, j),
            ],
        };

    // ---- 1. Golden aspect on a new Rect plot ---------------------------

    [Fact]
    public void NewRectPlot_OpensAtTheConfiguredAspectRatio()
    {
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        double ratio = AppSettingsViewModel.Instance.RectAspectRatio;

        var c = vm.AddPlot(PlotType.Rect, FreqUnit.GHz);

        Assert.True(ratio > 0);
        Assert.Equal(ratio, c.Width / c.Height, 3);
        // The old fixed default was 520x360 = 1.444 — it must not come back.
        Assert.NotEqual(360.0, c.Height, 3);
    }

    [Fact]
    public void NewRectPlot_ExplicitSize_IsHonoured_SoRestoringACddNeverGetsResnapped()
    {
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 10, top: 10, width: 600, height: 500);

        Assert.Equal(600.0, c.Width,  3);
        Assert.Equal(500.0, c.Height, 3);
    }

    [Theory]
    [InlineData(PlotType.Smith)]
    [InlineData(PlotType.Polar)]
    public void NewSquarePlot_StaysSquare(PlotType t)
    {
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(t, FreqUnit.GHz);
        Assert.Equal(c.Width, c.Height, 3);
    }

    // ---- 2. The prefix convention applies to every trace ----------------

    [Fact]
    public void SourcePrefix_AppliesToATraceWhoseSourcePathIsUnset()
    {
        // Two sources; the derived (stability) trace carries only its network's FilePath.
        var a  = new Trace(S2("/x/ampA.s2p"), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = "/x/ampA.s2p" };
        var b  = new Trace(S2("/x/ampB.s2p"), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = "/x/ampB.s2p" };
        var mu = new Trace(S2("/x/ampA.s2p"), MatrixType.S, 0, 0, DependentVarFormat.Db) { Derived = DerivedParameters.Mu };

        Assert.Null(mu.SourcePath);
        Assert.Equal("/x/ampA.s2p", mu.EffectiveSourcePath);

        var labels = TraceLabeler.ComputeMinimalLabels([a, b, mu]);
        Assert.All(labels, l => Assert.Contains("·", l));      // every trace, same convention
        Assert.StartsWith("ampA·", labels[2]);
    }

    [Fact]
    public void SourcePrefix_StillDroppedWhenEverySourceIsTheSame()
    {
        // The minimal policy must be unchanged: one source ⇒ no prefix on anything.
        var a  = new Trace(S2("/x/amp.s2p"), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = "/x/amp.s2p" };
        var mu = new Trace(S2("/x/amp.s2p"), MatrixType.S, 0, 0, DependentVarFormat.Db) { Derived = DerivedParameters.Mu };

        Assert.All(TraceLabeler.ComputeMinimalLabels([a, mu]), l => Assert.DoesNotContain("·", l));
    }

    // ---- 3. Concise S(1,2) ---------------------------------------------

    [Fact]
    public void CubeSParameter_ReadsPositionally_NotWithAxisNames()
    {
        var label = TraceLabeler.ComputeMinimalLabels([CubeS(0, 1)])[0];

        Assert.Contains("S(1,2)", label);
        Assert.DoesNotContain("i=", label);
        Assert.DoesNotContain("j=", label);
    }

    [Fact]
    public void CubeSParameter_PortNumbersStay1Based()
    {
        Assert.Contains("S(1,1)", TraceLabeler.ComputeMinimalLabels([CubeS(0, 0)])[0]);
        Assert.Contains("S(2,1)", TraceLabeler.ComputeMinimalLabels([CubeS(1, 0)])[0]);
    }

    [Fact]
    public void NonPortAxes_KeepTheirNames_BecauseTheyCarryMeaning()
    {
        var t = new Trace(S2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = "/x/run.npy",
            CubeName   = "HB1.V",
            Slice      =
            [
                new AxisSlice("harmonic", AxisRole.KeepAsX,    0),
                new AxisSlice("node",     AxisRole.PinToIndex, 3),
            ],
        };

        Assert.Contains("node=3", TraceLabeler.ComputeMinimalLabels([t])[0]);
    }

    [Fact]
    public void OnlyOnePortAxisPinned_KeepsTheName_SoALoneIndexIsNeverAmbiguous()
    {
        // j iterated as a family, i pinned: "S(1)" would not say WHICH index it is.
        var t = new Trace(S2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = "/x/run.npy",
            CubeName   = "SP1.S",
            Slice      =
            [
                new AxisSlice("freq", AxisRole.KeepAsX,       0),
                new AxisSlice("i",    AxisRole.PinToIndex,    0),
                new AxisSlice("j",    AxisRole.FamilyIterate, 0),
            ],
        };

        var label = TraceLabeler.ComputeMinimalLabels([t])[0];
        Assert.Contains("i=1", label);
    }

    [Fact]
    public void NetworkSParameter_LabelUnchanged()
    {
        var t = new Trace(S2("/x/amp.s2p"), MatrixType.S, 0, 1, DependentVarFormat.Db) { SourcePath = "/x/amp.s2p" };
        Assert.Equal("dB(S(1,2))", TraceLabeler.ComputeMinimalLabels([t])[0]);
    }
}
