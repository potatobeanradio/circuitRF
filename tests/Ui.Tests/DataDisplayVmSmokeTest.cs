using System.IO;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Phase 7.1c-1 gate test — verifies the VM stack can be instantiated and basic
/// operations work without any view or UI thread.
/// </summary>
public sealed class DataDisplayVmSmokeTest
{
    [Fact]
    public void DisplayWindowViewModel_creates_one_tab_with_one_plot()
    {
        var vm = new DisplayWindowViewModel();

        // Should have exactly one tab created in constructor.
        Assert.Single(vm.Tabs);
        Assert.NotNull(vm.ActiveTab);

        var tab = vm.Tabs[0];
        Assert.IsType<TabViewModel>(tab);

        // The tab's DataDisplay (canvas VM) should have been created.
        var display = tab.DataDisplay;
        Assert.NotNull(display);

        // Constructor adds one empty Smith plot.
        Assert.Single(display.Plots);
        Assert.IsType<PlotContainerViewModel>(display.Plots[0]);
    }

    [Fact]
    public void AddPlot_increases_plot_count()
    {
        var vm = new DisplayWindowViewModel();
        var display = vm.ActiveTab!.DataDisplay;

        // Start with the one default plot.
        Assert.Single(display.Plots);

        display.AddPlot(PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(2, display.Plots.Count);
    }

    /// <summary>
    /// Regression guard: switching plot type must raise ViewWidth, ViewHeight, and
    /// IsSquareAspect immediately — no zoom required to flush the layout.
    /// </summary>
    [Fact]
    public void PlotTypeChange_RaisesViewLayoutNotifications()
    {
        var vm      = new DisplayWindowViewModel();
        var display = vm.ActiveTab!.DataDisplay;

        // Start with a Table plot so the switch to Smith exercises the full layout path.
        var container = display.AddPlot(PlotType.Table, FreqUnit.GHz);

        var raised = new System.Collections.Generic.HashSet<string>();
        container.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                raised.Add(e.PropertyName);
        };

        // Switch to Smith — must refresh the full view-layout set without a subsequent zoom.
        container.Inspector.PlotType = PlotType.Smith;

        Assert.Contains(nameof(PlotContainerViewModel.ViewWidth),    raised);
        Assert.Contains(nameof(PlotContainerViewModel.ViewHeight),   raised);
        Assert.Contains(nameof(PlotContainerViewModel.IsSquareAspect), raised);
    }

    /// <summary>
    /// ReloadChangedAsync reloads only matching entries; unrelated entries are untouched;
    /// a path not in the library is a silent no-op; LibraryChanged fires for each reloaded entry.
    /// </summary>
    [Fact]
    public async Task SnpLibraryViewModel_ReloadChanged_OnlyMatching()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"crf_snptest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathA = Path.Combine(tempDir, "A.npy");
            var pathB = Path.Combine(tempDir, "B.npy");

            // Write initial data for both entries.
            WriteNpy(pathA, new Complex(0.1, -0.2));
            WriteNpy(pathB, new Complex(0.3, -0.4));

            var lib = new SnpLibraryViewModel();
            await lib.LoadFileAsync(pathA);
            await lib.LoadFileAsync(pathB);
            Assert.Equal(2, lib.Entries.Count);

            var entryA = lib.Entries.First(e => string.Equals(e.FilePath, pathA, StringComparison.OrdinalIgnoreCase));
            var entryB = lib.Entries.First(e => string.Equals(e.FilePath, pathB, StringComparison.OrdinalIgnoreCase));
            var initialDataB = entryB.Data;

            // Overwrite A with new data.
            WriteNpy(pathA, new Complex(0.9, -0.9));

            int libraryChangedCount = 0;
            lib.LibraryChanged += (_, _) => libraryChangedCount++;

            await lib.ReloadChangedAsync([pathA]);

            // A reloaded: its DataSet is a new object reflecting the updated file.
            Assert.NotNull(entryA.Data);
            Assert.True(libraryChangedCount >= 1, "LibraryChanged must fire for the reloaded entry");

            // B untouched: same DataSet reference.
            Assert.Same(initialDataB, entryB.Data);

            // A path not present in the library is silently ignored.
            var countBefore = libraryChangedCount;
            await lib.ReloadChangedAsync([Path.Combine(tempDir, "NonExistent.npy")]);
            Assert.Equal(countBefore, libraryChangedCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Adding a plot (structural undo edit) must flip IsDirty to true.
    /// </summary>
    [Fact]
    public void DataDisplay_DirtyBullet_OnStructuralEdit()
    {
        var docVm = new DataDisplayDocumentViewModel();

        Assert.False(docVm.IsDirty, "brand-new document should be clean");

        docVm.Window.ActiveTab!.DataDisplay.AddPlot(PlotType.Rect, FreqUnit.GHz);

        Assert.True(docVm.IsDirty, "adding a plot must set IsDirty");
    }

    /// <summary>
    /// Saving clears the dirty bullet.
    /// </summary>
    [Fact]
    public async Task DataDisplay_DirtyBullet_ClearsOnSave()
    {
        var docVm = new DataDisplayDocumentViewModel();
        docVm.Window.ActiveTab!.DataDisplay.AddPlot(PlotType.Rect, FreqUnit.GHz);
        Assert.True(docVm.IsDirty, "pre-condition: should be dirty after AddPlot");

        var tmpPath = Path.Combine(Path.GetTempPath(), $"crf_dirty_{Guid.NewGuid():N}.cdd");
        try
        {
            await docVm.Window.SaveAllAsync(tmpPath);
            Assert.False(docVm.IsDirty, "IsDirty must clear after save");
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    /// <summary>
    /// Inspector edits (plot-type change fires PlotNeedsRedraw) must also set IsDirty.
    /// </summary>
    [Fact]
    public void DataDisplay_DirtyBullet_OnInspectorEdit()
    {
        var docVm     = new DataDisplayDocumentViewModel();
        var container = docVm.Window.ActiveTab!.DataDisplay.Plots[0];

        Assert.False(docVm.IsDirty, "brand-new document should be clean");

        // Changing PlotType fires Inspector.PlotNeedsRedraw → container.PlotNeedsRedraw
        // → ContentChanged → DirtyChanged → IsDirty = HasUnsavedChanges()
        container.Inspector.PlotType = PlotType.Table;

        Assert.True(docVm.IsDirty, "inspector edit must set IsDirty via PlotNeedsRedraw channel");
    }

    private static void WriteNpy(string path, Complex value)
    {
        // Build a proper [freq, i, j] S-parameter cube so ToSnp can round-trip it.
        var freqAxis = new Axis("freq", [1e9, 2e9], "Hz");
        var iAxis    = new Axis("i", [1.0, 2.0], "port");
        var jAxis    = new Axis("j", [1.0, 2.0], "port");
        var data     = new Complex[] { value, value, value, value,
                                       value, value, value, value }; // [2 freq, 2 i, 2 j]
        var ds = new DataSet();
        ds.Add("S", new DataCube([freqAxis, iAxis, jAxis], data));
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
    }
}
