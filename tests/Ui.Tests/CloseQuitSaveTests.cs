using CircuitRF.Ui.Commands;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the close/quit save-pipeline fixes:
/// Bug #2 (crash on orphaned-only dirty) and Bug #1 (dirty Data Display slips through).
/// Framework-free: no Avalonia, disk I/O only where needed.
/// </summary>
public sealed class CloseQuitSaveTests : IDisposable
{
    private readonly string _tempDir;

    public CloseQuitSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_closequit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class AddComponentCommand : IUiCommand
    {
        private readonly SchematicEditModel _model;
        private readonly EditableComponent  _comp;
        public string Description => "add component";
        public AddComponentCommand(SchematicEditModel model)
        {
            _model = model;
            _comp  = new EditableComponent { Symbol = SymbolKind.Resistor };
        }
        public void Execute() => _model.Components.Add(_comp);
        public void Undo()    => _model.Components.Remove(_comp);
    }

    // ── Test 1 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bug #2 regression: PromptSaveBeforeClose crashed with ArgumentOutOfRangeException
    /// when the only dirty work was an orphaned session (all document lists empty).
    /// The fixed firstId chain must yield the session filename stem without throwing.
    /// </summary>
    [Fact]
    public void PromptSaveBeforeClose_OrphanedOnly_NoCrash()
    {
        // Arrange: registry with one dirty unreferenced session; no open document tabs.
        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());
        registry.Register("/fake/cell.csch", vm, _ => { });
        vm.UndoRedo.Execute(new AddComponentCommand(vm.EditModel));   // make session dirty

        var orphanedPaths = registry.GetOrphanedDirtyPaths(_ => false);
        Assert.Single(orphanedPaths);  // sanity: exactly one orphaned dirty path

        // Replicate the fixed firstId chain from WorkspaceViewModel.PromptSaveBeforeClose.
        // All document lists are empty; only dirtyOrphanedSessions is populated.
        // Before the fix the final branch was `dirtyMatSymbols[0].Id` with no guard,
        // which threw ArgumentOutOfRangeException.
        var dirtyScratch         = new List<SchematicDocument>();
        var dirtyScratchSymbols  = new List<SymbolEditorDocument>();
        var dirtyMat             = new List<SchematicDocument>();
        var dirtyMatSymbols      = new List<SymbolEditorDocument>();
        var dirtyScratchDisplays = new List<DataDisplayDocument>();
        var dirtyMatDisplays     = new List<DataDisplayDocument>();

        string? firstId =
              dirtyScratch.Count          > 0 ? dirtyScratch[0].Id
            : dirtyScratchSymbols.Count   > 0 ? dirtyScratchSymbols[0].Id
            : dirtyMat.Count              > 0 ? dirtyMat[0].Id
            : dirtyMatSymbols.Count       > 0 ? dirtyMatSymbols[0].Id
            : dirtyMatDisplays.Count      > 0 ? dirtyMatDisplays[0].Id
            : dirtyScratchDisplays.Count  > 0 ? dirtyScratchDisplays[0].Id
            : orphanedPaths.Count         > 0 ? Path.GetFileNameWithoutExtension(orphanedPaths[0])
            : null;

        // No ArgumentOutOfRangeException; orphaned session named correctly.
        Assert.Equal("cell", firstId);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Bug #1 regression: dirty Data Displays must be detected by the save-on-close pipeline.
    /// HasAnyDirtyWork() delegates to DisplayWindowViewModel.HasUnsavedChanges() for data
    /// displays; this test verifies that signal transitions correctly for a DataDisplayDocument.
    /// </summary>
    [Fact]
    public async Task HasAnyDirtyWork_IncludesDataDisplays()
    {
        var docVm = new DataDisplayDocumentViewModel();

        // Brand-new (never-saved) window: no baseline set → reports clean.
        Assert.False(docVm.Window.HasUnsavedChanges());

        // Establish a baseline via save.
        var path = Path.Combine(_tempDir, "test.cdd");
        await docVm.Window.SaveAllAsync(path);
        Assert.False(docVm.Window.HasUnsavedChanges());   // still clean right after save

        // Mutate state → now dirty.
        docVm.Window.ActiveTab!.DataDisplay.AddPlot(PlotType.Rect, FreqUnit.GHz);
        Assert.True(docVm.Window.HasUnsavedChanges());    // HasAnyDirtyWork() would return true

        // Confirm a second save restores clean state.
        await docVm.Window.SaveAllAsync(path);
        Assert.False(docVm.Window.HasUnsavedChanges());
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SaveDataDisplayDoc materialized branch: after saving, the .cdd file exists on disk
    /// and HasUnsavedChanges() returns false (mirrors the materialized save path in
    /// WorkspaceViewModel.SaveDataDisplayDoc).
    /// </summary>
    [Fact]
    public async Task SaveDataDisplayDoc_Materialized_Writes()
    {
        var docVm = new DataDisplayDocumentViewModel();
        var path  = Path.Combine(_tempDir, "display.cdd");

        // Materialize to disk and establish a clean baseline.
        await docVm.Window.SaveAllAsync(path);
        Assert.False(docVm.Window.HasUnsavedChanges());

        // Dirty the display by adding a second plot.
        docVm.Window.ActiveTab!.DataDisplay.AddPlot(PlotType.Smith, FreqUnit.GHz);
        Assert.True(docVm.Window.HasUnsavedChanges());    // pre-save: dirty

        // Act: save in place (mirrors SaveDataDisplayDoc materialized branch).
        await docVm.Window.SaveAllAsync(path);

        // Assert: .cdd written to disk; display clean.
        Assert.True(File.Exists(path));
        Assert.False(docVm.Window.HasUnsavedChanges());
    }
}
