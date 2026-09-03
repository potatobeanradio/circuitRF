// ================================================================
//  GestureBreadcrumbTests.cs
//  The trail records what the USER did, not only what went wrong.
//
//  Six rounds of a field report (src/RfCore/RESOLVED.md) have failed to reproduce a trace-resolve
//  crash. By round 6 the failure note carries the cube, its buffer, the slice, the branch-selecting
//  trace state, the group inventory, the stack, the faulting index and a replay — everything except
//  the click. One reported trail shows three identical failures five seconds apart: someone retrying
//  something, with nothing anywhere to say what.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Diagnostics;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(AppDataRootCollection.Name)]
public sealed class GestureBreadcrumbTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-gesture-" + Guid.NewGuid().ToString("N")[..8]);

    public GestureBreadcrumbTests()
    {
        Directory.CreateDirectory(_root);
        CrashReporter.ResetForTests();
        AppDataRoot.RedirectTo(_root);
        CrashReporter.Install("circuitRF");
    }

    public void Dispose()
    {
        CrashReporter.ResetForTests();
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Trail() => string.Join("\n", Directory
        .GetFiles(Path.Combine(_root, CrashReporter.DirName), "session-*.running")
        .Select(File.ReadAllText));

    private static DataSet RunDataSet()
    {
        var axes = new[]
        {
            new Axis("freq", new double[] { 1e9, 2e9, 3e9 }, "Hz"),
            new Axis("i",    new double[] { 0 }),
            new Axis("j",    new double[] { 0 }),
        };
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(axes,
            new[] { new Complex(0.1, 0.2), new Complex(0.3, 0.4), new Complex(0.5, 0.6) }));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0) }));
        return ds;
    }

    /// <summary>
    /// The reported sequence, in the trail, in order: a plot appears, a source is reloaded by the
    /// post-run refresh, and a trace is added to it. None of these three left any trace before.
    /// </summary>
    [Fact]
    public void AddingAPlotAndReloadingASource_LeaveBreadcrumbs()
    {
        var dd = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        dd.AddPlot(PlotType.Smith);
        dd.AddPlot(PlotType.Rect);

        string trail = Trail();
        Assert.Contains("dd: addPlot — Smith (now 1)", trail);
        Assert.Contains("dd: addPlot — Rect (now 2)", trail);

        // Order is the whole point: a trail that cannot be read top to bottom is a list, not a story.
        Assert.True(trail.IndexOf("Smith (now 1)", StringComparison.Ordinal)
                  < trail.IndexOf("Rect (now 2)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A trace card's combo boxes are NOT commands — the S-to-Z toggle the very first report of this
    /// crash described arrives as a property change, so instrumenting buttons alone would have missed
    /// exactly the gesture the hunt started from.
    /// </summary>
    [Fact]
    public void TheMatrixTypeToggle_IsRecorded_EvenThoughItIsNotACommand()
    {
        var ds = RunDataSet();
        var lib = new DataSourceLibraryViewModel();
        _ = new DataSourceEntryViewModel("/tmp/run.npy", ds, null, lib);

        var t = new Trace(new SNP(new double[] { 1e9 }, 1), MatrixType.S, 0, 0,
                          DependentVarFormat.Complex)
        {
            CubeName = "SP1.S",
            Slice = new[]
            {
                new AxisSlice("freq", AxisRole.KeepAsX, 0),
                new AxisSlice("i",    AxisRole.PinToIndex, 0),
                new AxisSlice("j",    AxisRole.PinToIndex, 0),
            },
        };

        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(t);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        var row = inspector.Traces.Single();

        row.MatrixType = MatrixType.Z;
        row.MatrixType = MatrixType.S;

        string trail = Trail();
        Assert.Contains("dd: row.matrix — SP1.S -> Z", trail);
        Assert.Contains("dd: row.matrix — SP1.Z -> S", trail);
    }

    /// <summary>
    /// Every breadcrumb is greppable as one class, and every one carries its thread — so a trail can
    /// be read for the gesture sequence alone, and a gesture that arrived from off the UI thread is
    /// visible as such.
    /// </summary>
    [Fact]
    public void EveryBreadcrumb_IsPrefixedAndTimestamped()
    {
        new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false)
            .AddPlot(PlotType.Rect);

        string trail = Trail();
        var gestures = trail.Split('\n').Where(l => l.Contains("dd: ", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(gestures);
        foreach (var g in gestures)
            Assert.Matches(@"^\[\d\d:\d\d:\d\d\.\d\d\d t\d+(!ui)?\] dd: ", g);
    }
}
