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
/// Table Performance Summary header controls: the MXP/MXE segmented selector (replacing the Load combobox)
/// and the loadpull-analysis picker (shown when the source carries more than one loadpull view).
/// </summary>
public sealed class SummaryTablePickerTests
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

    private static void WriteMultiGroupNpyAt(string path, DataSet flat, params string[] groups)
    {
        var multi = new DataSet();
        foreach (var g in groups)
            foreach (var srcGroup in flat.Groups)
                foreach (var kv in flat.CubesIn(srcGroup))
                    multi.AddToGroup(g, kv.Key, kv.Value);
        DataSetExporter.Export(multi, path, ExportFormat.Npy);
    }

    // ── MXP/MXE segmented selector ─────────────────────────────────────────────

    [Fact]
    public void TableOptimum_SegmentedButtons_ToggleAndReflectState()
    {
        var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: null);

        inspector.SetTableOptimumMxeCommand.Execute(null);
        Assert.Equal(TableOptimum.Mxe, plot.TableOptimum);
        Assert.False(inspector.IsTableOptimumMxp);
        Assert.True(inspector.IsTableOptimumMxe);

        inspector.SetTableOptimumMxpCommand.Execute(null);
        Assert.Equal(TableOptimum.Mxp, plot.TableOptimum);
        Assert.True(inspector.IsTableOptimumMxp);
        Assert.False(inspector.IsTableOptimumMxe);
    }

    // ── Summary analysis picker ────────────────────────────────────────────────

    [Fact]
    public async Task Summary_TwoAnalyses_PickerShows_SwitchUpdatesPlot()
    {
        var spl = SplFile();
        if (spl is null) return;   // fixture absent — skip

        var npy = Path.Combine(Path.GetTempPath(), $"summary_{Guid.NewGuid():N}.npy");
        WriteMultiGroupNpyAt(npy, SplReader.ReadSpl(spl), "LP1", "LPP1");
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AutoFillSummaryCommand.Execute(null);   // adds the standard summary columns

            Assert.True(inspector.IsSummaryTable);
            Assert.True(inspector.ShowSummaryAnalysisPicker);
            Assert.Equal(new[] { "LP1", "LPP1" }, inspector.SummaryAvailableAnalyses);
            Assert.Equal("LP1", inspector.SummarySelectedAnalysis);   // defaults to first view

            // Switch the analysis → persisted on the plot, summary rebuilds against it.
            inspector.SummarySelectedAnalysis = "LPP1";
            Assert.Equal("LPP1", plot.SummaryLoadpullGroup);
        }
        finally { File.Delete(npy); }
    }

    // Re-run staleness: same path, renamed analyses → the summary's Analysis picker must reflect the new run.
    [Fact]
    public async Task ReRun_SamePath_RefreshesSummaryAnalysisPicker()
    {
        var spl = SplFile();
        if (spl is null) return;

        var flat = SplReader.ReadSpl(spl);
        var npy  = Path.Combine(Path.GetTempPath(), $"summary_rerun_{Guid.NewGuid():N}.npy");
        try
        {
            WriteMultiGroupNpyAt(npy, flat, "LP1", "LPP1");
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AutoFillSummaryCommand.Execute(null);
            Assert.Equal(new[] { "LP1", "LPP1" }, inspector.SummaryAvailableAnalyses);

            WriteMultiGroupNpyAt(npy, flat, "LPX", "LPY");
            await lib.ReloadAsync(lib.Entries.First());

            // Columns survive the reload (not wiped as stale) and the picker reflects the new run.
            Assert.True(inspector.IsSummaryTable);
            Assert.Equal(new[] { "LPX", "LPY" }, inspector.SummaryAvailableAnalyses);   // refreshed, not stale
        }
        finally { File.Delete(npy); }
    }

    // The live ComboBox nulls its SelectedItem while the bound ItemsSource is Cleared during a rebuild.
    // That must NOT re-enter the rebuild and double-add the analyses (the "two sims repeated" bug).
    [Fact]
    public async Task Summary_AnalysisPicker_NoDuplicate_WhenComboNullsSelectionDuringClear()
    {
        var spl = SplFile();
        if (spl is null) return;

        var npy = Path.Combine(Path.GetTempPath(), $"summary_dup_{Guid.NewGuid():N}.npy");
        WriteMultiGroupNpyAt(npy, SplReader.ReadSpl(spl), "LP1", "LPP1");
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AutoFillSummaryCommand.Execute(null);
            Assert.Equal(new[] { "LP1", "LPP1" }, inspector.SummaryAvailableAnalyses);

            // Mimic the live ComboBox: when its ItemsSource is Cleared (Reset), it nulls SelectedItem.
            inspector.SummaryAvailableAnalyses.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    inspector.SummarySelectedAnalysis = null;
            };

            // Force a list rebuild (re-run, renamed analyses → Clear + Add).
            WriteMultiGroupNpyAt(npy, SplReader.ReadSpl(spl), "LPX", "LPY");
            await lib.ReloadAsync(lib.Entries.First());

            Assert.Equal(new[] { "LPX", "LPY" }, inspector.SummaryAvailableAnalyses);   // no re-entrant double-add
        }
        finally { File.Delete(npy); }
    }

    [Fact]
    public async Task Summary_SingleAnalysis_PickerHidden()
    {
        var spl = SplFile();
        if (spl is null) return;

        var npy = Path.Combine(Path.GetTempPath(), $"summary1_{Guid.NewGuid():N}.npy");
        WriteMultiGroupNpyAt(npy, SplReader.ReadSpl(spl), "LP1");   // single group
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
            inspector.AutoFillSummaryCommand.Execute(null);

            Assert.True(inspector.IsSummaryTable);
            Assert.False(inspector.ShowSummaryAnalysisPicker);   // only one view → no picker
        }
        finally { File.Delete(npy); }
    }
}
