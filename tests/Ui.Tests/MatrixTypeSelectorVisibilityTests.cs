// ================================================================
//  MatrixTypeSelectorVisibilityTests.cs  —  brief-dd-network-params-and-stability.md §1
//
//  The S/Z/Y IconSelectButton used to be gated `!trace.IsCubeBound` — exactly inverted for a
//  simulated run, where S(i,j) is a CUBE item (selector hidden, though it's the one place the
//  selector is meaningful) and the derived metrics are NETWORK-bound (selector shown, though
//  Trace.Derived's setter force-pins MatrixType to S there, so the control could only lie).
//
//  Fixed with a positive predicate: shown for (a) a non-derived network trace with non-empty
//  Data, and (b) a cube-bound trace whose cube is the network-parameter cube of a group that
//  carries S + Z0 (RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec) — never for a derived
//  metric trace.
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

public sealed class MatrixTypeSelectorVisibilityTests : IDisposable
{
    private readonly string _dir;

    public MatrixTypeSelectorVisibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-mtvis-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static DataSet GroupedRun(string group, int nPorts)
    {
        double[] freqs = [1e9, 2e9];
        var s = new Complex[freqs.Length * nPorts * nPorts];
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? new Complex(0.1, 0) : new Complex(0.02, 0.01);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), ""),
             new Axis("j", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            s));
        var z0Vals = Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray();
        ds.AddToGroup(group, "Z0", new DataCube(
            [new Axis("port", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            z0Vals));
        return ds;
    }

    private string WriteNpy(DataSet ds, string name = "run.npy")
    {
        string p = Path.Combine(_dir, name);
        DataSetExporter.Export(ds, p, ExportFormat.Npy);
        return p;
    }

    [Fact]
    public async Task FourPortSimulatedRun_S21Item_ShowsSelector_SourceStabilityMuPrime_DoesNot()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var sTrace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = p, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(sTrace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var row = inspector.Traces[0];

        Assert.True(row.ShowMatrixTypeCombo);   // S(2,1) — a network-parameter matrix element

        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Source Stability µ'");

        Assert.False(row.ShowMatrixTypeCombo);  // derived metric — never shows S/Z/Y
    }

    [Fact]
    public void TouchstoneFourPort_S21_ShowsSelector_DerivedMetric_HidesIt()
    {
        var m = new NumFlat.Mat<Complex>(4, 4);
        for (int i = 0; i < 4; i++) m[i, i] = new Complex(0.1, 0);
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        var trace = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db);   // S(2,1), 0-based row=1
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: null);
        var row = inspector.Traces[0];

        Assert.True(row.ShowMatrixTypeCombo);   // unchanged from today for a network trace

        trace.Derived = DerivedParameters.MuPrime;
        row.RefreshDescription();
        Assert.False(row.ShowMatrixTypeCombo);  // the selector disappears from the metric
    }
}
