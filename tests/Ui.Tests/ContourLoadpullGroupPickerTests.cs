using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A run.npy that carries more than one loadpull view (e.g. a standalone Loadpull "LP1" + a
/// Loadpull-Pursuit follow-on "LPP1") exposes a group picker on the contour card so the user can choose
/// which surface to contour. Single-group sources hide the picker.
/// </summary>
public sealed class ContourLoadpullGroupPickerTests
{
    private static string? SplFile()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null)
        {
            var c = Path.Combine(d, "testdata", "spl_test_data", "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
            if (File.Exists(c)) return c;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    // Build a multi-group .npy (the run.npy shape: cubes nested under analysis-name groups) from the spl.
    private static void WriteMultiGroupNpyAt(string path, DataSet flat, params string[] groups)
    {
        var multi = new DataSet();
        foreach (var g in groups)
            foreach (var srcGroup in flat.Groups)
                foreach (var kv in flat.CubesIn(srcGroup))
                    multi.AddToGroup(g, kv.Key, kv.Value);
        DataSetExporter.Export(multi, path, ExportFormat.Npy);
    }

    private static string WriteMultiGroupNpy(DataSet flat, params string[] groups)
    {
        var path = Path.Combine(Path.GetTempPath(), $"multi_lp_{Guid.NewGuid():N}.npy");
        WriteMultiGroupNpyAt(path, flat, groups);
        return path;
    }

    [Fact]
    public async Task TwoLoadpullGroups_PickerShows_DefaultsToFirst_SwitchUpdatesTrace()
    {
        var spl = SplFile();
        if (spl is null) return;   // fixture absent — skip

        var npy = WriteMultiGroupNpy(SplReader.ReadSpl(spl), "LP1", "LPP1");
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AddContourTraceCommand.Execute(null);

            var row = Assert.Single(inspector.Traces);
            Assert.True(row.ShowContourGroupPicker);
            Assert.Equal(new[] { "LP1", "LPP1" }, row.AvailableLoadpullGroups);
            Assert.Equal("LP1", row.SelectedLoadpullGroup);                 // picker shows the active (first) view
            Assert.Null(row.Trace.ContourData!.LoadpullGroup);             // not persisted until the user picks

            string metricBefore = row.Trace.ContourData!.MetricName;
            Assert.NotEmpty(metricBefore);

            // Switch to the follow-on analysis — persisted on the trace, surface rebuilds for that group.
            row.SelectedLoadpullGroup = "LPP1";
            Assert.Equal("LPP1", row.Trace.ContourData!.LoadpullGroup);
            // Data still shows: the metric is preserved (both analyses share it) and the grid is rebuilt.
            Assert.Equal(metricBefore, row.Trace.ContourData!.MetricName);
            Assert.Equal(metricBefore, row.ContourMetricName);
            Assert.NotNull(row.Trace.ContourData!.Grid);
        }
        finally { File.Delete(npy); }
    }

    // Re-run staleness: the run.npy is overwritten at the same path with renamed analyses; an existing
    // contour trace's Analysis picker must reflect the NEW analyses, not the previous run's.
    [Fact]
    public async Task ReRun_SamePath_RefreshesAnalysisPicker()
    {
        var spl = SplFile();
        if (spl is null) return;

        var flat = SplReader.ReadSpl(spl);
        var path = Path.Combine(Path.GetTempPath(), $"rerun_{Guid.NewGuid():N}.npy");
        try
        {
            WriteMultiGroupNpyAt(path, flat, "LP1", "LPP1");          // first run
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(path);

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AddContourTraceCommand.Execute(null);

            var row = Assert.Single(inspector.Traces);
            Assert.Equal(new[] { "LP1", "LPP1" }, row.AvailableLoadpullGroups);

            // Re-run: same path, renamed analyses → reload fires LibraryChanged.
            WriteMultiGroupNpyAt(path, flat, "LPX", "LPY");
            await lib.ReloadAsync(lib.Entries.First());

            Assert.Equal(new[] { "LPX", "LPY" }, row.AvailableLoadpullGroups);   // refreshed, not stale
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SingleLoadpullGroup_PickerHidden()
    {
        var spl = SplFile();
        if (spl is null) return;

        var npy = WriteMultiGroupNpy(SplReader.ReadSpl(spl), "LP1");   // one group only
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AddContourTraceCommand.Execute(null);

            var row = Assert.Single(inspector.Traces);
            Assert.False(row.ShowContourGroupPicker);   // only one view → no picker
        }
        finally { File.Delete(npy); }
    }
}
