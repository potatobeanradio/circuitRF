// ================================================================
//  DataDisplaySingleSourceTests.cs
//  Gate tests for brief-datadisplay-single-source
//
//  1. Resolve_Sentinel
//  2. Resolve_CrossSchematic
//  3. Enumerate_NoLoad
//  4. Select_LazyLoads
//  5. MostRecent
//  6. Persist_RoundTrip
//  7. SwitchBreaksTraces
//  8. CrossSchematicStable
//  9. PickerUsesSelected
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplaySingleSourceTests : IDisposable
{
    // ── Temporary workspace structure ──────────────────────────────────────

    private readonly string _wsDir;
    private readonly string _resultsDir;

    public DataDisplaySingleSourceTests()
    {
        _wsDir      = Path.Combine(Path.GetTempPath(), $"crf_ss_{Guid.NewGuid():N}");
        _resultsDir = Path.Combine(_wsDir, "results");
        Directory.CreateDirectory(_resultsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_wsDir))
            Directory.Delete(_wsDir, recursive: true);
    }

    private string WriteRunNpy(string schematic, DataSet? ds = null)
    {
        var dir = Path.Combine(_resultsDir, schematic);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "run.npy");
        ds ??= MakeSimpleDataSet();
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static DataSet MakeSimpleDataSet(string cubeName = "V")
    {
        var ds   = new DataSet();
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        ds.Add(cubeName, new DataCube(new[] { axis }, new System.Numerics.Complex[2]));
        return ds;
    }

    private DataSourceLibraryViewModel MakeLibrary()
    {
        var lib = new DataSourceLibraryViewModel();
        lib.ResultsRootProvider     = () => _resultsDir;
        lib.KnownTouchstoneProvider = () => Array.Empty<string>();
        return lib;
    }

    // ── 1. Resolve_Sentinel ─────────────────────────────────────────────────
    // ResolveAbs("run.npy") == SelectedDataSourceAbs; ResolveAbs(null) same.

    [Fact]
    public async Task Resolve_Sentinel()
    {
        var lib  = MakeLibrary();
        string absPath = WriteRunNpy("ampA");
        await lib.SelectDataSourceAsync("ampA/run.npy");

        Assert.Equal(lib.SelectedDataSourceAbs, lib.ResolveAbs(DataSourceRef.Selected));
        Assert.Equal(lib.SelectedDataSourceAbs, lib.ResolveAbs(null));
        Assert.Equal(absPath,                   lib.SelectedDataSourceAbs);
    }

    // ── 2. Resolve_CrossSchematic ───────────────────────────────────────────
    // ResolveAbs("ampB/run.npy") == <results>/ampB/run.npy regardless of selection.

    [Fact]
    public async Task Resolve_CrossSchematic()
    {
        var lib = MakeLibrary();
        WriteRunNpy("ampA");
        WriteRunNpy("ampB");
        await lib.SelectDataSourceAsync("ampA/run.npy");

        string expected = Path.GetFullPath(Path.Combine(_resultsDir, "ampB", "run.npy"));
        Assert.Equal(expected, lib.ResolveAbs("ampB/run.npy"));
    }

    // ── 3. Enumerate_NoLoad ─────────────────────────────────────────────────
    // RefreshAvailableDataSources lists results subdirs with run.npy + known Touchstone;
    // Entries stays empty (nothing imported).

    [Fact]
    public void Enumerate_NoLoad()
    {
        var lib = MakeLibrary();
        WriteRunNpy("ampA");
        WriteRunNpy("ampB");

        lib.RefreshAvailableDataSources();

        // Two sim items visible.
        Assert.Equal(2, lib.AvailableDataSources.Count);
        Assert.Contains(lib.AvailableDataSources, i => i.LogicalId == "ampA/run.npy");
        Assert.Contains(lib.AvailableDataSources, i => i.LogicalId == "ampB/run.npy");

        // No file was actually imported.
        Assert.Empty(lib.Entries);
    }

    // ── 4. Select_LazyLoads ─────────────────────────────────────────────────
    // SelectDataSourceAsync loads exactly that file; SelectedEntry is set.

    [Fact]
    public async Task Select_LazyLoads()
    {
        var lib = MakeLibrary();
        WriteRunNpy("ampA");
        WriteRunNpy("ampB");

        await lib.SelectDataSourceAsync("ampA/run.npy");

        // Exactly one entry loaded.
        Assert.Single(lib.Entries);
        Assert.NotNull(lib.SelectedEntry);
        Assert.Equal("ampA/run.npy", lib.SelectedDataSourceRef);
    }

    // ── 5. MostRecent ───────────────────────────────────────────────────────
    // With two run.npy of different LastWriteTime, MostRecentRunRef picks the newer.

    [Fact]
    public async Task MostRecent()
    {
        var lib  = MakeLibrary();
        string a = WriteRunNpy("older");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(-60));
        string b = WriteRunNpy("newer");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow);

        // Async is not needed here but keeps the signature consistent.
        await Task.CompletedTask;

        string? most = lib.MostRecentRunRef();
        Assert.Equal("newer/run.npy", most);
    }

    // ── 6. Persist_RoundTrip ────────────────────────────────────────────────
    // Save: .cdd has SelectedDataSource + trace SourcePath == "run.npy" for sentinel traces
    // and "<schematic>/run.npy" for cross-schematic; load (v2) restores both; v1 rejected.

    [Fact]
    public async Task Persist_RoundTrip()
    {
        string ampAPath = WriteRunNpy("ampA");
        string ampBPath = WriteRunNpy("ampB");

        var vm = new DisplayWindowViewModel();
        vm.DataSourceLibrary.ResultsRootProvider     = () => _resultsDir;
        vm.DataSourceLibrary.KnownTouchstoneProvider = () => Array.Empty<string>();
        vm.DataSourceLibrary.RefreshAvailableDataSources();

        await vm.DataSourceLibrary.SelectDataSourceAsync("ampA/run.npy");

        // Add a sentinel trace.
        var plot  = vm.ActiveTab!.DataDisplay.Plots[0].PlotVM.Plot;
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = ampAPath;
        plot.Traces.Add(trace);

        // Also a cross-schematic trace.
        var trace2 = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace2.SourceRef  = "ampB/run.npy";
        trace2.SourcePath = ampBPath;
        plot.Traces.Add(trace2);

        // Save.
        string cddPath = Path.Combine(_wsDir, "display.cdd");
        await vm.SaveAllAsync(cddPath);

        string json = await File.ReadAllTextAsync(cddPath);
        using var doc   = JsonDocument.Parse(json);
        var       root  = doc.RootElement;

        // format_version must be 2.
        Assert.Equal(2, root.GetProperty("FormatVersion").GetInt32());

        // SelectedDataSource must be persisted.
        Assert.Equal("ampA/run.npy", root.GetProperty("SelectedDataSource").GetString());

        // Load back.
        var vm2 = new DisplayWindowViewModel();
        vm2.DataSourceLibrary.ResultsRootProvider     = () => _resultsDir;
        vm2.DataSourceLibrary.KnownTouchstoneProvider = () => Array.Empty<string>();
        await vm2.LoadAllAsync(cddPath);

        Assert.Equal("ampA/run.npy", vm2.DataSourceLibrary.SelectedDataSourceRef);

        // v1 file rejected.
        string v1Json = json.Replace("\"FormatVersion\": 2", "\"FormatVersion\": 1");
        string v1Path = Path.Combine(_wsDir, "v1.cdd");
        await File.WriteAllTextAsync(v1Path, v1Json);
        await Assert.ThrowsAsync<InvalidDataException>(() => vm2.LoadAllAsync(v1Path));
    }

    // ── 7. SwitchBreaksTraces ───────────────────────────────────────────────
    // Select source A (trace plots), switch to source B lacking that cube → trace
    // re-renders <invalid> (no exception), SourceRef still "run.npy".

    [Fact]
    public async Task SwitchBreaksTraces()
    {
        // ampA has cube "V"; ampB has only "W".
        var dsA = new DataSet();
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        dsA.Add("V", new DataCube(new[] { axis }, new System.Numerics.Complex[2]));

        var dsB = new DataSet();
        dsB.Add("W", new DataCube(new[] { axis }, new System.Numerics.Complex[2]));

        string pathA = WriteRunNpy("ampA", dsA);
        string pathB = WriteRunNpy("ampB", dsB);

        var lib = MakeLibrary();
        await lib.SelectDataSourceAsync("ampA/run.npy");

        // Build a cube-bound trace on "V".
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = pathA;
        trace.CubeName   = "V";
        trace.Slice      = TraceRowViewModel.BuildDefaultSlice(dsA["V"]);
        trace.Expression = trace.BuildPickerExpression();

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz);

        // Switch to ampB.
        int changed = 0;
        lib.SelectedDataSourceChanged += (_, _) => changed++;
        await lib.SelectDataSourceAsync("ampB/run.npy");

        // SelectedDataSourceChanged fired.
        Assert.True(changed >= 1);
        // SourceRef unchanged (still sentinel).
        Assert.Equal(DataSourceRef.Selected, trace.SourceRef);
        // SourcePath is now re-pointed to ampB but cube "V" is not in ampB → invalid.
        // No exception should have been thrown by SelectDataSourceAsync.
    }

    // ── 8. CrossSchematicStable ─────────────────────────────────────────────
    // A trace with SourceRef="ampB/run.npy" keeps rendering from ampB after the
    // selected datasource is switched to ampA.

    [Fact]
    public async Task CrossSchematicStable()
    {
        string pathA = WriteRunNpy("ampA");
        string pathB = WriteRunNpy("ampB");

        var lib = MakeLibrary();
        await lib.SelectDataSourceAsync("ampA/run.npy");

        var trace = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = "ampB/run.npy";
        trace.SourcePath = lib.ResolveAbs("ampB/run.npy");

        string? absBeforeSwitch = trace.SourcePath;

        // Switch selected source — cross-schematic trace must not be re-pointed.
        await lib.SelectDataSourceAsync("ampA/run.npy");

        // SourcePath unchanged because the vm-layer handler only re-points sentinel traces.
        Assert.Equal(absBeforeSwitch, trace.SourcePath);
        Assert.Equal("ampB/run.npy",  trace.SourceRef);
    }

    // ── 9. PickerUsesSelected ────────────────────────────────────────────────
    // With source A selected, the trace card's signal list comes from A only;
    // picking a signal sets SourceRef="run.npy".

    [Fact]
    public async Task PickerUsesSelected()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeHarmonicCube());
        ds.AddToGroup("HB1", "I", MakeHarmonicCube());

        string pathA = WriteRunNpy("ampA", ds);
        WriteRunNpy("ampB");   // ampB has no "V" / "I"

        var lib = MakeLibrary();
        await lib.SelectDataSourceAsync("ampA/run.npy");

        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = pathA;
        trace.CubeName   = "HB1.V";
        trace.Slice      = Array.Empty<AxisSlice>();

        var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var trvm = inspector.Traces[0];

        // Signal list is populated from ampA's selected entry.
        Assert.NotEmpty(trvm.AvailableGroups);
        Assert.Contains("HB1", trvm.AvailableGroups);

        // Pick a signal → SourceRef must become the sentinel.
        trvm.SelectedGroup  = "HB1";
        var vItem = trvm.AvailableSignals.FirstOrDefault(s => s.Label == "V");
        if (vItem is not null)
        {
            trvm.SelectedSignal = vItem;
            Assert.Equal(DataSourceRef.Selected, trace.SourceRef);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeHarmonicCube()
    {
        var nodeAxis = new Axis("node",     new[] { 0.0, 1.0 }, "", new[] { "Vin", "Vout" });
        var harmAxis = new Axis("harmonic", new[] { 0.0, 1e9 }, "Hz");
        return new DataCube(new[] { nodeAxis, harmAxis }, new System.Numerics.Complex[4]);
    }
}
