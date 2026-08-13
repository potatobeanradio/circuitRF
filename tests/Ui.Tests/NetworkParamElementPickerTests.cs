// ================================================================
//  NetworkParamElementPickerTests.cs  —  brief-dd-network-params-and-stability.md §4
//
//  For S-parameter data the port indices are never an x-axis. Before this brief the cube-bound
//  picker offered a single bare "S" quantity item plus "i"/"j" as ordinary axis-role rows, so the
//  user could promote a port index to X (a plot nobody wants) and saw two rows of clutter on
//  every S trace. Now: one item per ordered (i,j) pair (S(1,1), S(1,2), …, row-major), listed
//  above the stability-metric items in the same S-Parameters group; the i/j axis-role rows are
//  suppressed; every other axis row (a parametric sweep) still appears.
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

public sealed class NetworkParamElementPickerTests : IDisposable
{
    private readonly string _dir;

    public NetworkParamElementPickerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-elempicker-" + Guid.NewGuid().ToString("N")[..8]);
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
        var rnd = new Random(11);
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? new Complex(0.5, -0.1) : new Complex(rnd.NextDouble() * 0.3, rnd.NextDouble() * 0.1);

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

    private static DataSet SweptGroupedRun(string group, int nSweep, int nPorts)
    {
        double[] sweep = Enumerable.Range(0, nSweep).Select(k => (double)k).ToArray();
        double[] freqs = [1e9, 2e9];
        var s = new Complex[nSweep * freqs.Length * nPorts * nPorts];
        var rnd = new Random(13);
        for (int k = 0; k < s.Length; k++)
            s[k] = new Complex(rnd.NextDouble() * 0.5, rnd.NextDouble() * 0.5 - 0.25);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("Pin", sweep, "dBm"),
             new Axis("freq", freqs, "Hz"),
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

    private static TraceRowViewModel BuildRow(DataSourceLibraryViewModel lib, string path,
                                              string cubeName, PlotType plotType)
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path, CubeName = cubeName, Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    [Fact]
    public async Task FourPortSource_CombosListsSixteenElementItems_ThenMetricItems_RowMajorOrder()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        var labels = row.AvailableSignals.Select(x => x.Label).ToList();

        string[] expectedElements =
        [
            "S(1,1)", "S(1,2)", "S(1,3)", "S(1,4)",
            "S(2,1)", "S(2,2)", "S(2,3)", "S(2,4)",
            "S(3,1)", "S(3,2)", "S(3,3)", "S(3,4)",
            "S(4,1)", "S(4,2)", "S(4,3)", "S(4,4)",
        ];
        Assert.Equal(expectedElements, labels.Take(16));

        int firstMetricIdx = labels.IndexOf("Source Stability Circles");
        Assert.True(firstMetricIdx == 16, $"expected metrics to start right after the 16 elements, got index {firstMetricIdx}");

        // No bare "S" quantity item anywhere.
        Assert.DoesNotContain("S", labels);
    }

    [Fact]
    public async Task NoIJAxisRoleRows_OnAnSTrace()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "S(2,3)");

        Assert.DoesNotContain(row.AxisRoles, r => r.AxisName == "i");
        Assert.DoesNotContain(row.AxisRoles, r => r.AxisName == "j");
        Assert.Contains(row.AxisRoles, r => r.AxisName == "freq");
    }

    [Fact]
    public async Task SweptSCube_StillShowsItsSweepAxisRow_WithNoIJRows()
    {
        string p = WriteNpy(SweptGroupedRun("SP1", nSweep: 3, nPorts: 2));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "S(1,2)");

        Assert.DoesNotContain(row.AxisRoles, r => r.AxisName == "i");
        Assert.DoesNotContain(row.AxisRoles, r => r.AxisName == "j");
        Assert.Contains(row.AxisRoles, r => r.AxisName == "freq");
        Assert.Contains(row.AxisRoles, r => r.AxisName == "Pin");   // the sweep axis survives
    }

    [Fact]
    public async Task SelectingElement_AndTypingEquivalentSpec_ProduceIdenticalTraceAndSpecText()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var pickedRow = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        pickedRow.SelectedGroup = "SP1";
        pickedRow.SelectedSignal = pickedRow.AvailableSignals.First(x => x.Label == "S(3,2)");

        // The picked item auto-applies dB20 (bareName == "S"), so the equivalent typed spec must
        // carry the same transform to be the identical spec (BuildPickerExpression round-trips
        // 1-based ports).
        Assert.Equal(CubeTransform.dB20, pickedRow.Trace.Transform);

        var typedRow = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        typedRow.SelectedGroup = "SP1";
        typedRow.CommitSpec("dB20(SP1.S[:, 3, 2])");
        Assert.False(typedRow.HasSpecError);

        Assert.Equal(pickedRow.Trace.CubeName, typedRow.Trace.CubeName);
        Assert.Equal(
            pickedRow.Trace.Slice!.OrderBy(s => s.AxisName).Select(s => (s.AxisName, s.Role, s.Index)),
            typedRow.Trace.Slice!.OrderBy(s => s.AxisName).Select(s => (s.AxisName, s.Role, s.Index)));

        // Same spec text.
        Assert.Equal(pickedRow.Trace.BuildPickerExpression(), typedRow.Trace.BuildPickerExpression());

        // Typing the spec also selects the matching combo item.
        Assert.Equal("S(3,2)", typedRow.SelectedSignal?.Label);
    }

    [Fact]
    public async Task TwoPortSource_OffersFourElementItems_NoPairPickerSubstitute()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 2));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        var labels = row.AvailableSignals.Select(x => x.Label).ToList();

        Assert.Equal(["S(1,1)", "S(1,2)", "S(2,1)", "S(2,2)"], labels.Take(4));
    }
}
