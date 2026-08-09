using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report: a parametric DC sweep plotted as <c>DC1.I[240, :, "IDS"]</c> labelled its Y axis
/// <c>DC1.I[VDS=240, :, branch=0]</c> — the raw slice INDEX for the swept axis, and the axis name
/// plus an index for a branch that already carries the name "IDS". Neither number is anything the
/// user typed or can read; both answers are on the cube.
///
/// The resolution lives with the owner (<c>PlotInspectorViewModel</c>) rather than the trace,
/// because a <c>Trace</c> deliberately never holds a <c>DataSet</c> — mirroring the pinned-spectral
/// pair that already works this way.
/// </summary>
public sealed class PinnedAxisLabelTests
{
    // ── The resolver: what a pinned axis reads as ─────────────────────────────

    [Fact]
    public void ASweptAxis_ReadsAsItsOwnValueAndUnit_NotItsIndex()
    {
        var (ds, trace) = BuildDcSweep(vdsIndex: 3, branchIndex: 1);

        PlotInspectorViewModel.ApplyPinnedAxisDisplay(trace, ds);

        // The fixture steps VDS by 2 V precisely so value and index differ: index 3 is 6 V, so a
        // test that passed on the old index-printing behaviour cannot also pass on this one.
        Assert.Equal("VDS=6 V", trace.PinnedAxisDisplay("VDS"));
    }

    [Fact]
    public void ALabelledAxis_ReadsAsItsLabelAlone_WithoutTheAxisName()
    {
        var (ds, trace) = BuildDcSweep(vdsIndex: 0, branchIndex: 1);

        PlotInspectorViewModel.ApplyPinnedAxisDisplay(trace, ds);

        // "branch=IDS" would say the same thing twice — the label already names the quantity.
        Assert.Equal("IDS", trace.PinnedAxisDisplay("branch"));
    }

    [Fact]
    public void AnAxisWithNoUnit_OmitsTheUnitRatherThanTrailingASpace()
    {
        var ds = new DataSet();
        ds.Add("H", new DataCube(
            [new Axis("harmonic", [0, 1, 2], ""), new Axis("freq", [1e9, 2e9], "Hz")],
            new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 }));

        var t = NewCubeTrace("H",
            new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
            new AxisSlice("freq", AxisRole.KeepAsX, 0));

        PlotInspectorViewModel.ApplyPinnedAxisDisplay(t, ds);

        Assert.Equal("harmonic=2", t.PinnedAxisDisplay("harmonic"));
    }

    [Fact]
    public void ThePortAxes_AreNeverResolved_SoTheyStayPositional()
    {
        // S(1,2) is universally read positionally; giving i/j a name=value token would undo that.
        var ds = new DataSet();
        ds.Add("S", new DataCube(
            [new Axis("freq", [1e9, 2e9], "Hz"), new Axis("i", [0, 1], ""), new Axis("j", [0, 1], "")],
            Enumerable.Repeat(new System.Numerics.Complex(1, 0), 8).ToArray()));

        var t = NewCubeTrace("S",
            new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("i", AxisRole.PinToIndex, 0),
            new AxisSlice("j", AxisRole.PinToIndex, 1));

        PlotInspectorViewModel.ApplyPinnedAxisDisplay(t, ds);

        Assert.Null(t.PinnedAxisDisplay("i"));
        Assert.Null(t.PinnedAxisDisplay("j"));
    }

    // ── The label the user actually sees ─────────────────────────────────────

    [Fact]
    public void TheRenderedLabel_ShowsTheValueAndTheBranchName_NotTwoIndices()
    {
        var (ds, trace) = BuildDcSweep(vdsIndex: 3, branchIndex: 1);
        PlotInspectorViewModel.ApplyPinnedAxisDisplay(trace, ds);

        string label = LabelOf(trace);

        Assert.Equal("DC1.I(VDS=6 V,IDS)", label);
        Assert.DoesNotContain("branch=", label);
        Assert.DoesNotContain("VDS=3", label);   // the index, not the value
    }

    [Fact]
    public void WithoutTheOwnersResolution_TheLabelStillFallsBackToTheIndex()
    {
        // A hand-built trace (and every test that builds one directly) never had an owner resolve
        // anything for it — the raw-index form must remain the fallback rather than becoming a gap.
        var (_, trace) = BuildDcSweep(vdsIndex: 3, branchIndex: 1);

        Assert.Equal("DC1.I(VDS=3,branch=1)", LabelOf(trace));
    }

    [Fact]
    public void APortPairIsStillPositional_EvenWhenOtherAxesResolve()
    {
        var ds = new DataSet();
        ds.Add("S", new DataCube(
            [
                new Axis("Pin", [-10, 0, 10], "dBm"),
                new Axis("freq", [1e9, 2e9], "Hz"),
                new Axis("i", [0, 1], ""),
                new Axis("j", [0, 1], ""),
            ],
            Enumerable.Repeat(new System.Numerics.Complex(1, 0), 24).ToArray()));

        var t = NewCubeTrace("S",
            new AxisSlice("Pin", AxisRole.PinToIndex, 2),
            new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("i", AxisRole.PinToIndex, 1),
            new AxisSlice("j", AxisRole.PinToIndex, 0));

        PlotInspectorViewModel.ApplyPinnedAxisDisplay(t, ds);

        Assert.Equal("S(Pin=10 dBm,2,1)", LabelOf(t));
    }

    // ── The reset contract ───────────────────────────────────────────────────

    [Fact]
    public void SettingNewCubeData_ClearsTheResolution_SoItCannotOutliveItsCube()
    {
        var (ds, trace) = BuildDcSweep(vdsIndex: 3, branchIndex: 1);
        PlotInspectorViewModel.ApplyPinnedAxisDisplay(trace, ds);
        Assert.NotNull(trace.PinnedAxisDisplay("VDS"));

        trace.SetCubeData([0, 1], null, [1.0, 2.0], "freq", "Hz", PlotType.Rect, FreqUnit.GHz);

        Assert.Null(trace.PinnedAxisDisplay("VDS"));
        Assert.Null(trace.PinnedAxisDisplay("branch"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // DC1.I[VDS, VGS, branch] — the owner's own shape: a swept bias axis, an X axis, and a labelled
    // branch axis. VDS steps by 2 V so a value can never be mistaken for its own index.
    private static (DataSet Ds, Trace T) BuildDcSweep(int vdsIndex, int branchIndex)
    {
        var vds = new Axis("VDS", [0, 2, 4, 6, 8], "V");
        var vgs = new Axis("VGS", [0, 0.5, 1.0], "V");
        var branch = new Axis("branch", [0, 1], "", ["IGS", "IDS"]);

        var ds = new DataSet();
        ds.Add("DC1.I", new DataCube([vds, vgs, branch], new double[5 * 3 * 2]));

        var t = NewCubeTrace("DC1.I",
            new AxisSlice("VDS", AxisRole.PinToIndex, vdsIndex),
            new AxisSlice("VGS", AxisRole.KeepAsX, 0),
            new AxisSlice("branch", AxisRole.PinToIndex, branchIndex));
        return (ds, t);
    }

    // A Trace's only constructor takes an SNP (the network path); a cube-bound trace is one whose
    // CubeName/Slice are then set, exactly as the picker does. The SNP is an unused placeholder.
    private static Trace NewCubeTrace(string cubeName, params AxisSlice[] slice)
    {
        var t = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cubeName,
            Slice    = slice,
        };
        return t;
    }

    // ComputeMinimalLabels drops any identity component that is constant across the plot, so a
    // single-trace call yields exactly the quantity string under test.
    private static string LabelOf(Trace t) =>
        TraceLabeler.ComputeMinimalLabels([t], alwaysShowSource: false)[0];
}
