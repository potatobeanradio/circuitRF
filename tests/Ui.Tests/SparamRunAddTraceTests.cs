// ================================================================
//  SparamRunAddTraceTests.cs
//  Gate tests for brief-sparam-run-add-trace
//
//  1. DefaultXAxis_Sparam
//  2. BuildDefaultSlice_Sparam
//  3. IsParameterCube
//  4. Picker_OffersGroupedS
//  5. Picker_HidesTouchstoneDefaultS
//  6. FirstPlottableCubeName_GroupedS
//  7. AddTrace_AfterSparamRun
//  8. SweptS_Family
//  9. Smith_ComplexS
// 10. Touchstone_Unchanged
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

public sealed class SparamRunAddTraceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DataCube MakeSCube(int nFreq = 3, int nPorts = 2)
    {
        var freqVals = Enumerable.Range(0, nFreq).Select(k => (k + 1) * 1e9).ToArray();
        var portVals = Enumerable.Range(0, nPorts).Select(i => (double)i).ToArray();
        var freqAxis = new Axis("freq", freqVals, "Hz");
        var iAxis    = new Axis("i",    portVals, "");
        var jAxis    = new Axis("j",    portVals, "");
        return new DataCube(new[] { freqAxis, iAxis, jAxis },
                            new Complex[nFreq * nPorts * nPorts]);
    }

    private static DataCube MakeSweptSCube(int nSweep = 4, int nFreq = 3, int nPorts = 2)
    {
        var sweepVals = Enumerable.Range(0, nSweep).Select(k => (double)k).ToArray();
        var freqVals  = Enumerable.Range(0, nFreq).Select(k => (k + 1) * 1e9).ToArray();
        var portVals  = Enumerable.Range(0, nPorts).Select(i => (double)i).ToArray();
        var sweepAxis = new Axis("sweep", sweepVals, "");
        var freqAxis  = new Axis("freq",  freqVals,  "Hz");
        var iAxis     = new Axis("i",     portVals,  "");
        var jAxis     = new Axis("j",     portVals,  "");
        return new DataCube(new[] { sweepAxis, freqAxis, iAxis, jAxis },
                            new Complex[nSweep * nFreq * nPorts * nPorts]);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_sparam_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static TraceRowViewModel BuildInspector(
        DataSourceLibraryViewModel lib, string sourcePath, string cubeName,
        PlotType plotType = PlotType.Rect)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = null;
        trace.Transform  = CubeTransform.None;

        var plot      = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    // ── 1. DefaultXAxis_Sparam ─────────────────────────────────────────────────
    // [freq, i, j]        → 0 (freq wins over i/j).
    // [sweep, freq, i, j] → 1 (freq, not sweep — sweep is dim 0).

    [Fact]
    public void DefaultXAxis_Sparam()
    {
        var sCube     = MakeSCube();       // [freq(0), i(1), j(2)]
        var sweptCube = MakeSweptSCube();  // [sweep(0), freq(1), i(2), j(3)]

        Assert.Equal(0, TraceRowViewModel.DefaultXAxis(sCube));
        Assert.Equal(1, TraceRowViewModel.DefaultXAxis(sweptCube));
    }

    // ── 2. BuildDefaultSlice_Sparam ───────────────────────────────────────────
    // [freq, i, j]        → freq=KeepAsX, i=PinToIndex, j=PinToIndex.
    // [sweep, freq, i, j] → sweep=PinToIndex, freq=KeepAsX, i=PinToIndex, j=PinToIndex.

    [Fact]
    public void BuildDefaultSlice_Sparam()
    {
        var sCube     = MakeSCube();       // [freq, i, j]
        var sweptCube = MakeSweptSCube();  // [sweep, freq, i, j]

        var slice = TraceRowViewModel.BuildDefaultSlice(sCube);
        Assert.Equal(3, slice.Length);
        Assert.Equal("freq", slice[0].AxisName);
        Assert.Equal(AxisRole.KeepAsX,    slice[0].Role);
        Assert.Equal(AxisRole.PinToIndex, slice[1].Role); // i
        Assert.Equal(AxisRole.PinToIndex, slice[2].Role); // j

        var sweptSlice = TraceRowViewModel.BuildDefaultSlice(sweptCube);
        Assert.Equal(4, sweptSlice.Length);
        Assert.Equal("sweep", sweptSlice[0].AxisName);
        Assert.Equal(AxisRole.PinToIndex, sweptSlice[0].Role); // sweep pinned
        Assert.Equal("freq",  sweptSlice[1].AxisName);
        Assert.Equal(AxisRole.KeepAsX,    sweptSlice[1].Role); // freq → X
        Assert.Equal(AxisRole.PinToIndex, sweptSlice[2].Role); // i pinned
        Assert.Equal(AxisRole.PinToIndex, sweptSlice[3].Role); // j pinned
    }

    // ── 3. IsParameterCube ─────────────────────────────────────────────────────
    // true for [freq, i, j]; false for an HB V cube [node, harmonic].

    [Fact]
    public void IsParameterCube()
    {
        Assert.True(TraceRowViewModel.IsParameterCube(MakeSCube()));

        var vCube = new DataCube(
            new[] {
                new Axis("node",     new double[] { 0, 1 }, "", new[] { "Vin", "Vout" }),
                new Axis("harmonic", new double[] { 0, 1, 2 }, "")
            },
            new Complex[2 * 3]);
        Assert.False(TraceRowViewModel.IsParameterCube(vCube));
    }

    // ── 4. Picker_OffersGroupedS ──────────────────────────────────────────────
    // AddToGroup("SP1","S") → SP1.S offered under group "SP1"; Z0 is not.

    [Fact]
    public async Task Picker_OffersGroupedS()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S",  MakeSCube());
        ds.AddToGroup("SP1", "Z0", MakeSCube());  // must be skipped

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "SP1.S");

            Assert.Contains("SP1", trvm.AvailableGroups);

            trvm.SelectedGroup = "SP1";
            var labels = trvm.AvailableSignals.Select(s => s.Label).ToList();
            Assert.Contains("S",  labels);
            Assert.DoesNotContain("Z0", labels);

            var sItem = trvm.AvailableSignals.First(s => s.Label == "S");
            Assert.True(sItem.IsCubeBound);
            Assert.Equal("SP1.S", sItem.CubeName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. Picker_HidesTouchstoneDefaultS ─────────────────────────────────────
    // A Touchstone entry (S in default group, entry.Snp != null) → S is NOT in
    // cube signals; network S-Parameters group IS offered.

    [Fact]
    public async Task Picker_HidesTouchstoneDefaultS()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_ts_{Guid.NewGuid():N}.s2p");
        try
        {
            File.WriteAllText(path,
                "! minimal 2-port\n" +
                "# GHz S RI R 50\n" +
                "1.0 0.1 0.0 0.2 0.0 0.2 0.0 0.1 0.0\n");

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var entry = lib.Entries.First();
            Assert.NotNull(entry.Snp);  // Touchstone entry has SNP

            // Build a network-bound trace
            var snp   = entry.Snp!;
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = null;

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();
            var trvm = inspector.Traces[0];

            // Network path: S-Parameters group present with S(1,1) etc.
            Assert.Contains("S-Parameters", trvm.AvailableGroups);
            trvm.SelectedGroup = "S-Parameters";
            Assert.Contains("S(1,1)", trvm.AvailableSignals.Select(s => s.Label).ToList());

            // Cube path: default-group S must NOT appear as a cube item
            if (trvm.AvailableGroups.Contains("Signals"))
            {
                trvm.SelectedGroup = "Signals";
                var cubeLabels = trvm.AvailableSignals
                    .Where(s => s.IsCubeBound)
                    .Select(s => s.Label)
                    .ToList();
                Assert.DoesNotContain("S", cubeLabels);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 6. FirstPlottableCubeName_GroupedS ────────────────────────────────────
    // Sim entry (Snp==null, SP1.S present) → CanAddTrace is true (GroupedS is plottable).

    [Fact]
    public async Task FirstPlottableCubeName_GroupedS()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S",  MakeSCube());
        ds.AddToGroup("SP1", "Z0", MakeSCube());  // skipped

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            Assert.Null(lib.Entries.First().Snp);   // .npy sim entry: no SNP

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            // Before the fix, CanAddTrace was false because S was skipped everywhere.
            Assert.True(inspector.CanAddTrace);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 7. AddTrace_AfterSparamRun ─────────────────────────────────────────────
    // Empty Rect plot + sim entry (Snp null, SP1.S) → AddTrace seeds CubeName=="SP1.S",
    // freq as X, Transform==dB20.

    [Fact]
    public async Task AddTrace_AfterSparamRun()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S",  MakeSCube());
        ds.AddToGroup("SP1", "Z0", MakeSCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            Assert.True(inspector.CanAddTrace);
            inspector.AddTraceCommand.Execute(null);

            Assert.Single(plot.Traces);
            var trace = plot.Traces[0];

            Assert.True(trace.IsCubeBound);
            Assert.Equal("SP1.S", trace.CubeName);
            Assert.Equal(CubeTransform.dB20, trace.Transform);

            // freq axis must be KeepAsX; i and j pinned
            Assert.NotNull(trace.Slice);
            Assert.Contains(trace.Slice!, s => s.AxisName == "freq" && s.Role == AxisRole.KeepAsX);
            Assert.Contains(trace.Slice!, s => s.AxisName == "i"    && s.Role == AxisRole.PinToIndex);
            Assert.Contains(trace.Slice!, s => s.AxisName == "j"    && s.Role == AxisRole.PinToIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 8. SweptS_Family ──────────────────────────────────────────────────────
    // Swept SP1.S seeded (freq=X, sweep Pin0); set sweep to Family →
    // FamilyCurves populated (one curve per sweep point).

    [Fact]
    public async Task SweptS_Family()
    {
        const int nSweep = 4;
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", MakeSweptSCube(nSweep));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            inspector.AddTraceCommand.Execute(null);
            Assert.Single(plot.Traces);

            var trvm = inspector.Traces[0];

            // After seed: sweep is pinned, freq is X
            var sweepRole = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "sweep");
            Assert.NotNull(sweepRole);
            Assert.False(sweepRole!.IsX);
            Assert.False(sweepRole.IsFamily);

            // Promote sweep to Family → TrySetCubeData runs → FamilyCurves populated
            sweepRole.IsFamily = true;

            var trace = plot.Traces[0];
            Assert.True(trace.FamilyCurves.Count > 0);
            Assert.True(trace.FamilyCurves.Count <= Math.Min(nSweep, Trace.MaxFamilyCurves));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 9. Smith_ComplexS ──────────────────────────────────────────────────────
    // SP1.S on a Smith plot: no Rect transform is applied; freq is X.

    [Fact]
    public async Task Smith_ComplexS()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", MakeSCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var plot      = new Plot(PlotType.Smith, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            inspector.AddTraceCommand.Execute(null);
            Assert.Single(plot.Traces);

            var trace = plot.Traces[0];
            Assert.True(trace.IsCubeBound);
            Assert.Equal("SP1.S", trace.CubeName);

            // dB20 is a Rect-only first-add nicety; Smith must leave Transform==None
            Assert.Equal(CubeTransform.None, trace.Transform);

            // freq still wins as X on Smith
            Assert.NotNull(trace.Slice);
            Assert.Contains(trace.Slice!, s => s.AxisName == "freq" && s.Role == AxisRole.KeepAsX);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 10. Touchstone_Unchanged ────────────────────────────────────────────────
    // A Touchstone entry → AddTrace seeds a network S trace (!IsCubeBound).

    [Fact]
    public async Task Touchstone_Unchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_ts2_{Guid.NewGuid():N}.s2p");
        try
        {
            File.WriteAllText(path,
                "! minimal 2-port\n" +
                "# GHz S RI R 50\n" +
                "1.0 0.1 0.0 0.2 0.0 0.2 0.0 0.1 0.0\n");

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            Assert.True(inspector.CanAddTrace);
            inspector.AddTraceCommand.Execute(null);

            Assert.Single(plot.Traces);
            var trace = plot.Traces[0];

            // Touchstone path: network-bound (not cube-bound)
            Assert.False(trace.IsCubeBound);
            Assert.Null(trace.CubeName);
            Assert.Equal(MatrixType.S, trace.MatrixType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
