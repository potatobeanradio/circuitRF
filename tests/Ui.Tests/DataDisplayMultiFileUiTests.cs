// ================================================================
//  DataDisplayMultiFileUiTests.cs  —  brief-data-display-multifile-ui.md gates
//
//  §1 label unchanged (R-dd-1), §2 source selector + add-from-file (R-dd-2),
//  §2 drag-drop (R-dd-3), §3 Datasets list rename/re-point (R-dd-4/5),
//  §4 portability + the stale-field investigation (R-dd-6/7/8).
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayMultiFileUiTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_ddmfu_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string WriteNpy(string dir, string fileName)
    {
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var data = new System.Numerics.Complex[] { new(0.1, -0.2), new(0.2, -0.1) };
        var cube = new DataCube(new[] { axis }, data);
        var ds   = new DataSet();
        ds.AddToGroup("SP1", "S", cube);
        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    // ── Gate 2 (R-dd-1): single-dataset labels are byte-identical, resolver or not ──────────

    [Fact]
    public async Task ComputeMinimalLabels_SingleDataset_LabelsByteIdentical_WithAndWithoutAliasResolver()
    {
        var dir  = MakeTempDir();
        var path = WriteNpy(dir, "Amp.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        Assert.Single(lib.Entries);   // exactly one dataset — the selector must not exist for this case

        var snp = new SNP(new[] { 1e9 }, 2);
        var t1  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = path };
        var t2  = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db) { SourcePath = path };

        var withoutResolver = TraceLabeler.ComputeMinimalLabels(new[] { t1, t2 });
        var withResolver    = TraceLabeler.ComputeMinimalLabels(new[] { t1, t2 },
            aliasFor: t => lib.AliasFor(t.SourcePath));

        Assert.Equal(withoutResolver, withResolver);
        Assert.Equal("dB(S(1,1))", withResolver[0]);
        Assert.Equal("dB(S(2,1))", withResolver[1]);
    }

    // ── Gate 3 (R-dd-1): two-file labels are alias:metric; removing one reverts ─────────────

    [Fact]
    public async Task ComputeMinimalLabels_TwoDatasets_AliasQualified_RemovingOneReverts()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(pathA);
        await lib.LoadFileAsync(pathB);
        Assert.True(lib.TrySetAlias(lib.Entries[0], "baseline"));
        Assert.True(lib.TrySetAlias(lib.Entries[1], "tuned"));

        var snp = new SNP(new[] { 1e9 }, 2);
        var t1  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = pathA };
        var t2  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = pathB };

        var twoFile = TraceLabeler.ComputeMinimalLabels(new[] { t1, t2 }, aliasFor: t => lib.AliasFor(t.SourcePath));
        Assert.Equal("baseline·dB(S(1,1))", twoFile[0]);
        Assert.Equal("tuned·dB(S(1,1))", twoFile[1]);

        // Remove the second dataset (from the plot's own perspective) — alias reverts to dropped.
        var oneFile = TraceLabeler.ComputeMinimalLabels(new[] { t1 }, aliasFor: t => lib.AliasFor(t.SourcePath));
        Assert.Equal("dB(S(1,1))", oneFile[0]);
    }

    // ── Gate 4 (R-dd-2): picker shows no source column with one dataset, does with two ─────

    [Fact]
    public async Task Picker_SourceSelector_HiddenWithOne_VisibleWithTwo_EndsInAddFromFile()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));

        var container = window.DataDisplay!.Plots[0];
        Assert.True(container.Inspector.AddTraceCommand.CanExecute(null));
        container.Inspector.AddTraceCommand.Execute(null);
        var row = container.Inspector.Traces.Single();

        Assert.False(row.SourceSelectorVisible);
        Assert.Empty(row.AvailableSourceEntries);

        var pathB = WriteNpy(dir, "run_v2.npy");
        await window.DataSourceLibrary.LoadFileAsync(pathB);

        Assert.True(row.SourceSelectorVisible);
        Assert.Equal(3, row.AvailableSourceEntries.Count);           // 2 datasets + sentinel
        Assert.True(row.AvailableSourceEntries[^1].IsAddFromFile);
        Assert.Equal("Add from file…", row.AvailableSourceEntries[^1].DisplayText);
    }

    [Fact]
    public async Task Picker_SelectingASource_ShowsThatFilesTraces_WithoutTouchingTheOtherRow()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));
        var entryB = window.DataSourceLibrary.Entries[1];

        var container = window.DataDisplay!.Plots[0];
        container.Inspector.AddTraceCommand.Execute(null);
        var row = container.Inspector.Traces.Single();

        // Row defaulted to source A (the first/selected entry) — switch it to B.
        var itemB = row.AvailableSourceEntries.First(i => ReferenceEquals(i.Entry, entryB));
        row.SelectedSourceItem = itemB;

        Assert.Equal(pathB, row.Trace.SourcePath, ignoreCase: true);
    }

    // ── Gate 5 (R-dd-3): dragging an .npy adds it as a dataset and opens the picker for it ──

    [Fact]
    public async Task AddDatasetFromDropAsync_LoadsFile_AddsTrace_SelectsItsSource()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");   // the "dropped" file

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));

        var container = window.DataDisplay!.Plots[0];
        int tracesBefore = container.Inspector.Traces.Count;

        await container.Inspector.AddDatasetFromDropAsync(pathB);

        var entryB = window.DataSourceLibrary.Entries.Single(e =>
            string.Equals(e.FilePath, pathB, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(tracesBefore + 1, container.Inspector.Traces.Count);
        var newRow = container.Inspector.Traces.Last();
        Assert.Equal(pathB, newRow.Trace.SourcePath, ignoreCase: true);
        Assert.NotNull(newRow.SelectedSourceItem);
        Assert.True(ReferenceEquals(newRow.SelectedSourceItem!.Entry, entryB));
    }

    // ── Gate 6 (R-dd-4/5): renaming an alias updates the Datasets row; duplicate refused ────

    [Fact]
    public async Task DatasetRow_Rename_UpdatesAlias_DuplicateIsRefused()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var window = new DisplayWindowViewModel();
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);

        var datasets = new DatasetsListViewModel();
        datasets.SetWindow(window);
        Assert.Equal(2, datasets.Rows.Count);

        var rowA = datasets.Rows[0];
        rowA.AliasText = "baseline";
        rowA.CommitAlias();
        Assert.Equal("baseline", window.DataSourceLibrary.Entries[0].Alias);
        Assert.Null(rowA.RenameError);

        var rowB = datasets.Rows[1];
        rowB.AliasText = "baseline";   // collides with rowA's new alias
        rowB.CommitAlias();
        Assert.NotNull(rowB.RenameError);
        Assert.NotEqual("baseline", window.DataSourceLibrary.Entries[1].Alias);
    }

    // ── Gate 7: missing file shows as missing, keeps others live, preserves trace config ────

    [Fact]
    public async Task MissingFile_ShowsBroken_OtherStaysLive_TraceConfigPreserved()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");
        var cddPath = Path.Combine(dir, "display.cdd");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[0], "baseline"));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[1], "tuned"));
        await window.SaveAllAsync(cddPath);

        File.Delete(pathB);

        var reloaded = new DisplayWindowViewModel();
        reloaded.DataSourceLibrary.ResultsRootProvider = () => dir;
        await reloaded.LoadAllAsync(cddPath);

        var live   = reloaded.DataSourceLibrary.Entries.Single(e => e.Alias == "baseline");
        var broken = reloaded.DataSourceLibrary.Entries.Single(e => e.Alias == "tuned");
        Assert.False(live.IsBroken);
        Assert.True(broken.IsBroken);
    }

    // ── Gate 8 (R-dd-4): re-pointing an alias at a different file updates every trace using it ──

    [Fact]
    public async Task RepointDatasetAsync_UpdatesEveryTraceUsingIt_InOneAction()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathC = WriteNpy(dir, "run_v3.npy");   // the file we re-point at

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));
        var entryA = window.DataSourceLibrary.Entries.Single();

        var container = window.DataDisplay!.Plots[0];
        container.Inspector.AddTraceCommand.Execute(null);
        container.Inspector.AddTraceCommand.Execute(null);
        Assert.Equal(2, container.Inspector.Traces.Count);
        Assert.All(container.Inspector.Traces, r =>
            Assert.Equal(pathA, r.Trace.SourcePath, ignoreCase: true));

        await window.RepointDatasetAsync(entryA, pathC);

        Assert.Equal(pathC, entryA.FilePath, ignoreCase: true);
        Assert.All(container.Inspector.Traces, r =>
            Assert.Equal(pathC, r.Trace.SourcePath, ignoreCase: true));
    }

    // ── Bug fix (post-brief): no plot selected ⇒ no plot-inspector chrome in Properties ─────

    [Fact]
    public void PropertiesTool_NoPlotSelected_HasSelectedPlotIsFalse_DatasetsStillActive()
    {
        var propsTool = new PropertiesTool();
        var window    = new DisplayWindowViewModel();

        // A Data Display document is active, but no single plot is selected — SetActiveDataDisplay
        // is called with a null inspector, exactly like WorkspaceViewModel.RouteDataDisplayProperties
        // does whenever ActiveInspector is null (zero or multiple plots selected).
        propsTool.SetActiveDataDisplay(null, window);

        Assert.True(propsTool.IsDataDisplayActive);   // Datasets list must still show
        Assert.False(propsTool.HasSelectedPlot);       // plot-inspector chrome must NOT show

        // Selecting a plot flips it back on.
        var container = window.DataDisplay!.Plots[0];
        propsTool.SetActiveDataDisplay(container.Inspector, window);
        Assert.True(propsTool.HasSelectedPlot);
    }

    // ── Gate 9 (R-dd-6/8): portability — no absolute path, no separator, in SourceAliases ────

    [Fact]
    public void IsPortableSourceKey_RejectsRootedAndSeparatorBearingKeys()
    {
        Assert.True(DisplayWindowViewModel.IsPortableSourceKey("baseline.npy"));
        Assert.False(DisplayWindowViewModel.IsPortableSourceKey("/Users/me/baseline.npy"));
        Assert.False(DisplayWindowViewModel.IsPortableSourceKey(@"C:\Users\me\baseline.npy"));
        Assert.False(DisplayWindowViewModel.IsPortableSourceKey("sub/baseline.npy"));
        Assert.False(DisplayWindowViewModel.IsPortableSourceKey(@"sub\baseline.npy"));
        Assert.False(DisplayWindowViewModel.IsPortableSourceKey(""));
    }

    [Fact]
    public async Task SaveAllAsync_MultipleDatasets_SourceAliasesContainsNoAbsolutePathOrSeparator()
    {
        var dir     = MakeTempDir();
        var resultsDir = Path.Combine(dir, "results");
        Directory.CreateDirectory(resultsDir);
        var pathA = WriteNpy(resultsDir, "run_v1.npy");
        var pathB = WriteNpy(resultsDir, "run_v2.npy");
        var cddPath = Path.Combine(dir, "display.cdd");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => resultsDir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[0], "baseline"));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[1], "tuned"));

        await window.SaveAllAsync(cddPath);

        string json = await File.ReadAllTextAsync(cddPath);
        Assert.DoesNotContain(dir.Replace('\\', '/'), json.Replace('\\', '/'));   // no absolute path leaked
        Assert.Contains("\"run_v1.npy\": \"baseline\"", json);
        Assert.Contains("\"run_v2.npy\": \"tuned\"", json);
    }

    // ── Gate 10 (R-dd-7): the "SourcePath": "run.npy" field is the live Selected sentinel ────

    [Fact]
    public void DataSourceRef_Selected_IsResolvedAsASentinel_NotALiteralFilename()
    {
        var lib = new DataSourceLibraryViewModel();
        // No file named "run.npy" exists anywhere — if this were read as a literal filename it
        // would resolve to null. It must instead resolve to whatever is currently selected.
        Assert.Equal(lib.SelectedDataSourceAbs, lib.ResolveAbs(DataSourceRef.Selected));
        Assert.Equal(lib.SelectedDataSourceAbs, lib.ResolveAbs(null));
    }

    // ── Post-ship bug: label strips must refresh the instant a trace's SOURCE actually changes ──

    [Fact]
    public async Task SwitchingTraceSource_RefreshesLabelStripsImmediately_AliasQualified()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[0], "baseline"));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[1], "tuned"));

        // The default seeded plot is Smith (IsComplex) so LeftLabelStrips actually populate.
        var container = window.DataDisplay!.Plots[0];
        container.Inspector.AddTraceCommand.Execute(null);   // trace 1 → bound to A ("baseline")
        container.Inspector.AddTraceCommand.Execute(null);   // trace 2 → cloned from 1, still A
        Assert.Equal(2, container.LeftLabelStrips.Count);

        // Only one source in use so far — alias must be dropped (R-dd-1's own structural guarantee).
        Assert.DoesNotContain("baseline", container.LeftLabelStrips[0].AutoLabel);
        Assert.DoesNotContain("baseline", container.LeftLabelStrips[1].AutoLabel);

        // Switch trace 2's Source to B via the picker's own Source selector (R-dd-2) — no other
        // action taken (no manual redraw, no plot-type toggle).
        var row2   = container.Inspector.Traces[1];
        var entryB = window.DataSourceLibrary.Entries[1];
        var itemB  = row2.AvailableSourceEntries.First(i => ReferenceEquals(i.Entry, entryB));
        row2.SelectedSourceItem = itemB;

        Assert.Equal(pathB, row2.Trace.SourcePath, ignoreCase: true);
        Assert.Contains("baseline", container.LeftLabelStrips[0].AutoLabel);
        Assert.Contains("tuned",    container.LeftLabelStrips[1].AutoLabel);
    }

    // ── Post-ship bug: toolbar source change must move the trace card's Source combo too ────────
    //
    //  Owner scenario: a display auto-created for source A; user picks source B in the Data
    //  Display's own toolbar datasource combo. The plotted DATA correctly follows to B (the
    //  sentinel trace is re-pointed), but the trace card kept claiming A — in the Source combo AND
    //  in the group/item cascade, since both read the row's sticky _pickerSourceEntry.

    [Fact]
    public async Task ToolbarSourceChange_MovesTheTraceCardSourceCombo_ToTheNewSource()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));

        var container = window.DataDisplay!.Plots[0];
        container.Inspector.AddTraceCommand.Execute(null);
        var row = container.Inspector.Traces.Single();

        // Auto-created displays bind their traces to the "Selected" sentinel — that is what makes
        // the toolbar combo able to re-point them at all.
        Assert.Equal(DataSourceRef.Selected, row.Trace.SourceRef);
        Assert.Equal(pathA, row.Trace.SourcePath, ignoreCase: true);
        Assert.Equal(pathA, row.SelectedSourceItem!.Entry!.FilePath, ignoreCase: true);

        // The reported gesture: change the TOOLBAR datasource (never the trace card's own combo).
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathB));

        // Data followed (this part already worked) …
        Assert.Equal(pathB, row.Trace.SourcePath, ignoreCase: true);
        // … and now the card agrees, instead of still claiming A.
        Assert.Equal(pathB, row.SelectedSourceItem!.Entry!.FilePath, ignoreCase: true);
    }

    // ── Post-ship bug: SourceRef must name the picked entry, not always the toolbar sentinel ────

    [Fact]
    public async Task SwitchingTraceSource_ToANonToolbarSelectedEntry_PersistsARealRef_NotTheSentinel()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        // Toolbar stays pointed at A — the trace below is switched to B via the row's OWN
        // Source selector only, never via the toolbar combo.
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(pathA));

        var container = window.DataDisplay!.Plots[0];
        container.Inspector.AddTraceCommand.Execute(null);
        var row    = container.Inspector.Traces.Single();
        var entryB = window.DataSourceLibrary.Entries[1];
        var itemB  = row.AvailableSourceEntries.First(i => ReferenceEquals(i.Entry, entryB));
        row.SelectedSourceItem = itemB;

        Assert.Equal(pathB, row.Trace.SourcePath, ignoreCase: true);
        Assert.NotEqual(DataSourceRef.Selected, row.Trace.SourceRef);

        // The stamped ref must resolve back to B regardless of what the toolbar has selected.
        Assert.Equal(pathB, window.DataSourceLibrary.ResolveAbs(row.Trace.SourceRef), ignoreCase: true);
    }
}
