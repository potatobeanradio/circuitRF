// ================================================================
//  StabilityCircleSignalSwitchTests.cs — brief-dd-stability-circle-signal-switch
//
//  Picking "Load Stability Circles" on a simulated 4-port run and then picking S(1,1) again left
//  the trace in BOTH states at once: the cube branch of OnSelectedSignalChanged never reset
//  Trace.Derived (only the network branch does). Two visible consequences, one cause:
//
//   • The S(1,1) curve never appeared. BuildPath tests IsCubeBound BEFORE IsDerived, so it built a
//     cube path — but TraceRenderer.BuildPath branches on IsStabilityCircle, which was still true,
//     so it drew the stale circle geometry and ignored Points entirely.
//   • The In/Out port selectors stayed on the card: TraceRowViewModel.ShowPortSelectors reads
//     Trace.Derived, and the property WAS re-raised — it just still answered "yes".
//
//  Fixed on the Trace.CubeName/Expression setters (the two things that make a trace cube-bound),
//  so the picker, a typed spec and a .cdd load are all covered by one rule: cube binding wins,
//  exactly as BuildPath already decided.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class StabilityCircleSignalSwitchTests : IDisposable
{
    private readonly string _dir;

    public StabilityCircleSignalSwitchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-stbswitch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Circles = "Load Stability Circles";

    /// <summary>A 4-port grouped run whose (1,2) sub-network is potentially unstable, so the load
    /// stability circles genuinely exist rather than degenerating to nothing.</summary>
    private static DataSet GroupedRun(string group = "SP1", int nPorts = 4)
    {
        double[] freqs = [1e9, 2e9];
        var s = new Complex[freqs.Length * nPorts * nPorts];
        var rnd = new Random(3);
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? Complex.FromPolarCoordinates(0.7, -1.0)
                               : new Complex(rnd.NextDouble() * 0.3, rnd.NextDouble() * 0.2);
        // Make S21 large so the 1→2 pair is potentially unstable.
        for (int f = 0; f < freqs.Length; f++)
            s[f * nPorts * nPorts + 1 * nPorts + 0] = Complex.FromPolarCoordinates(3.0, 1.4);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), ""),
             new Axis("j", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            s));
        ds.AddToGroup(group, "Z0", new DataCube(
            [new Axis("port", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray()));
        return ds;
    }

    private async Task<TraceRowViewModel> BuildRowAsync(PlotType plotType = PlotType.Smith)
    {
        string p = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(GroupedRun(), p, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = p, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var row = inspector.Traces[0];
        row.SelectedGroup = row.AvailableGroups.First(g => g.Contains("SP1"));
        return row;
    }

    /// <summary>Selects a picker item by label, forcing OnSelectedSignalChanged to run even when the
    /// item is already the reference-equal current selection.</summary>
    private static void Select(TraceRowViewModel row, string label)
    {
        var target = row.AvailableSignals.First(x => x.Label == label);
        if (!ReferenceEquals(row.SelectedSignal, target)) { row.SelectedSignal = target; return; }
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label != label);
        row.SelectedSignal = target;
    }

    // ---- The reported bug ---------------------------------------------------

    [Fact]
    public async Task CirclesThenS11_LeavesNoCircleState_AndRendersS11()
    {
        var row = await BuildRowAsync();

        Select(row, Circles);
        Assert.True(row.Trace.IsStabilityCircle, "pre-condition: the circle trace is live");
        Assert.NotEmpty(row.Trace.StabilityCircleCentres);
        Assert.True(row.ShowPortSelectors, "pre-condition: a 4-port shows the In/Out selectors");
        Assert.True(row.IsNetworkMetricTrace);

        Select(row, "S(1,1)");

        // The trace is a plain cube trace again...
        Assert.Equal(DerivedParameters.None, row.Trace.Derived);
        Assert.False(row.Trace.IsStabilityCircle);
        Assert.Equal("SP1.S", row.Trace.CubeName);

        // ...the circle geometry is gone, so the renderer cannot draw it over the curve...
        Assert.Empty(row.Trace.StabilityCircleCentres);
        Assert.Empty(row.Trace.StabilityCircleRadii);

        // ...S(1,1) actually has a locus to draw...
        Assert.NotEmpty(row.Trace.Points);

        // ...and the card drops the In/Out row.
        Assert.False(row.ShowPortSelectors, "In/Out selectors must be removed from the card");
        Assert.False(row.IsNetworkMetricTrace);
    }

    [Fact]
    public async Task S11ThenCircles_StillWorks()
    {
        // The reverse direction must not be over-corrected into never showing circles again.
        var row = await BuildRowAsync();

        Select(row, "S(1,1)");
        Assert.Equal(DerivedParameters.None, row.Trace.Derived);
        Assert.NotEmpty(row.Trace.Points);

        Select(row, Circles);
        Assert.Equal(DerivedParameters.LoadStabilityCircle, row.Trace.Derived);
        Assert.True(row.Trace.IsStabilityCircle);
        Assert.NotEmpty(row.Trace.StabilityCircleCentres);
        Assert.Null(row.Trace.CubeName);
        Assert.True(row.ShowPortSelectors);
    }

    [Fact]
    public async Task CirclesThenS11_ThenCirclesAgain_RoundTrips()
    {
        var row = await BuildRowAsync();

        Select(row, Circles);
        var centresFirst = row.Trace.StabilityCircleCentres.ToArray();

        Select(row, "S(1,1)");
        Select(row, Circles);

        Assert.Equal(centresFirst.Length, row.Trace.StabilityCircleCentres.Count);
        for (int i = 0; i < centresFirst.Length; i++)
            Assert.Equal(centresFirst[i], row.Trace.StabilityCircleCentres[i]);
    }

    // ---- The model-level rule, independent of the picker --------------------

    [Fact]
    public void CubeNameSetter_DropsDerivedAndItsGeometry()
    {
        var snp = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        snp.Matrices[0][0, 0] = Complex.FromPolarCoordinates(0.7, -1.0);
        snp.Matrices[0][0, 1] = new Complex(0.05, 0.0);
        snp.Matrices[0][1, 0] = Complex.FromPolarCoordinates(3.0, 1.4);
        snp.Matrices[0][1, 1] = Complex.FromPolarCoordinates(0.6, -0.7);

        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex)
        { Derived = DerivedParameters.LoadStabilityCircle };
        t.BuildPath(PlotType.Smith, FreqUnit.GHz);
        Assert.NotEmpty(t.StabilityCircleCentres);

        t.CubeName = "SP1.S";

        Assert.Equal(DerivedParameters.None, t.Derived);
        Assert.False(t.IsStabilityCircle);
        Assert.Empty(t.StabilityCircleCentres);
        Assert.Empty(t.StabilityCircleRadii);
    }

    [Fact]
    public void ExpressionSetter_DropsDerivedToo()
    {
        var snp = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex)
        { Derived = DerivedParameters.Mu };

        t.Expression = "mag(V[:, 0])";

        Assert.Equal(DerivedParameters.None, t.Derived);
        Assert.False(t.IsDerived);
    }

    [Fact]
    public void ClearingACubeBinding_LeavesDerivedAlone()
    {
        // Setting CubeName/Expression to NULL is how the network branch un-binds a trace before it
        // assigns Derived — the drop must be gated on a non-null value or it would fight that.
        var snp = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex) { CubeName = "SP1.S" };

        t.CubeName   = null;
        t.Expression = null;
        t.Derived    = DerivedParameters.Mu;

        Assert.Equal(DerivedParameters.Mu, t.Derived);
    }
}
