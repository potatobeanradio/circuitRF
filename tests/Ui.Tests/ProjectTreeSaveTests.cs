using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the Project Tree "Save" context-menu item:
/// IsNodeDirty logic and SaveCellViewsAsync behavior.
/// Framework-free: no Avalonia, disk I/O only.
/// </summary>
public sealed class ProjectTreeSaveTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectTreeSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "crftest_ptsave_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ── Infrastructure helpers ─────────────────────────────────────────────────

    // Creates <tempDir>/<cellName>/schematic/<fileName>.csch and returns its path.
    private string MakeCsch(string cellName, string fileName)
    {
        var dir = Path.Combine(_tempDir, cellName, "schematic");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        SchematicPersistence.SaveToFile(path, new SchematicEditModel(),
            Path.GetFileNameWithoutExtension(fileName));
        return path;
    }

    // Creates <tempDir>/<cellName>/symbol/<fileName>.csym and returns its path.
    private string MakeCsym(string cellName, string fileName)
    {
        var dir = Path.Combine(_tempDir, cellName, "symbol");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        SymbolPersistence.SaveToFile(path, new EditableSymbol { UserEditable = true }.ToSymbol());
        return path;
    }

    // Command that adds a component to mark a session dirty.
    private sealed class AddCompCmd : IUiCommand
    {
        private readonly SchematicEditModel _model;
        private readonly EditableComponent  _comp;
        public string Description => "add";
        public AddCompCmd(SchematicEditModel m, EditableComponent c) { _model = m; _comp = c; }
        public void Execute() => _model.Components.Add(_comp);
        public void Undo()    => _model.Components.Remove(_comp);
    }

    // ── Simulated IsNodeDirty logic (mirrors WorkspaceViewModel, no Avalonia) ──

    private static bool SimIsCellDirty(
        SchematicSessionRegistry   registry,
        IEnumerable<SymbolEditorDocument> symDocs,
        string cellDir)
        => registry.AllDirtyPaths.Any(p => IsViewInCell(p, cellDir))
           || symDocs.Any(d =>
                  d.IsDirty
                  && d.ViewModel.CurrentSymbolPath is { } sp
                  && IsViewInCell(sp, cellDir));

    private static bool SimIsCschNodeDirty(SchematicSessionRegistry registry, string absPath)
    {
        var key = Path.GetFullPath(absPath);
        return registry.AllDirtyPaths.Any(p =>
            string.Equals(Path.GetFullPath(p), key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SimIsCsymNodeDirty(
        IEnumerable<SymbolEditorDocument> symDocs, string absPath)
    {
        var key = Path.GetFullPath(absPath);
        return symDocs.Any(d =>
            d.IsDirty
            && d.ViewModel.CurrentSymbolPath is { } sp
            && string.Equals(Path.GetFullPath(sp), key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsViewInCell(string viewFilePath, string cellDir)
    {
        var sub  = Path.GetDirectoryName(viewFilePath);
        var cell = sub is not null ? Path.GetDirectoryName(sub) : null;
        return cell is not null
            && string.Equals(cell, cellDir, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsNodeDirty: .csch path ────────────────────────────────────────────────

    [Fact]
    public void IsNodeDirty_DirtyCsch_ViewFileNodeAndCellNodeAreTrue()
    {
        var cschPath = MakeCsch("CellA", "main.csch");
        var cellDir  = Path.GetFullPath(Path.Combine(_tempDir, "CellA"));

        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());
        registry.Register(Path.GetFullPath(cschPath), vm, _ => { });

        // Make the session dirty.
        var comp = new EditableComponent { Symbol = SymbolKind.Resistor };
        vm.UndoRedo.Execute(new AddCompCmd(vm.EditModel, comp));
        Assert.True(vm.UndoRedo.IsModified);

        // Both the .csch ViewFile node and the owning Cell node should report dirty.
        Assert.True(SimIsCschNodeDirty(registry, cschPath));
        Assert.True(SimIsCellDirty(registry, [], cellDir));
    }

    [Fact]
    public void IsNodeDirty_AfterNotifySessionSaved_BothFalse()
    {
        var cschPath = MakeCsch("CellB", "tb.csch");
        var cellDir  = Path.GetFullPath(Path.Combine(_tempDir, "CellB"));

        var registry = new SchematicSessionRegistry();
        var vm       = new SchematicViewModel(new SchematicEditModel());
        var normKey  = Path.GetFullPath(cschPath);
        registry.Register(normKey, vm, _ => { });

        // Dirty the session then save it.
        vm.UndoRedo.Execute(new AddCompCmd(vm.EditModel,
            new EditableComponent { Symbol = SymbolKind.Capacitor }));
        Assert.True(vm.UndoRedo.IsModified);

        // Simulate NotifySessionSaved: write + mark saved.
        SchematicPersistence.SaveToFile(normKey, vm.EditModel,
            Path.GetFileNameWithoutExtension(normKey));
        registry.MarkSaved(normKey);

        // Both nodes should now be clean.
        Assert.False(SimIsCschNodeDirty(registry, cschPath));
        Assert.False(SimIsCellDirty(registry, [], cellDir));
    }

    // ── IsNodeDirty: .csym path ────────────────────────────────────────────────

    [Fact]
    public void IsNodeDirty_DirtyCsym_ViewFileNodeAndCellNodeAreTrue()
    {
        var csymPath = MakeCsym("CellC", "CellC.csym");
        var cellDir  = Path.GetFullPath(Path.Combine(_tempDir, "CellC"));

        var symVm  = new SymbolEditorViewModel(new EditableSymbol { UserEditable = true });
        var symDoc = new SymbolEditorDocument("CellC.csym", symVm, csymPath);
        symVm.CurrentSymbolPath = Path.GetFullPath(csymPath);
        symVm.IsDirty           = true;

        Assert.True(SimIsCsymNodeDirty([symDoc], csymPath));
        Assert.True(SimIsCellDirty(new SchematicSessionRegistry(), [symDoc], cellDir));
    }

    [Fact]
    public void IsNodeDirty_CleanCsym_BothFalse()
    {
        var csymPath = MakeCsym("CellD", "CellD.csym");
        var cellDir  = Path.GetFullPath(Path.Combine(_tempDir, "CellD"));

        var symVm  = new SymbolEditorViewModel(new EditableSymbol { UserEditable = true });
        var symDoc = new SymbolEditorDocument("CellD.csym", symVm, csymPath);
        symVm.CurrentSymbolPath = Path.GetFullPath(csymPath);
        symVm.IsDirty           = false;

        Assert.False(SimIsCsymNodeDirty([symDoc], csymPath));
        Assert.False(SimIsCellDirty(new SchematicSessionRegistry(), [symDoc], cellDir));
    }

    // ── SaveCellViewsAsync: saves all dirty views ──────────────────────────────

    [Fact]
    public void SaveCellViewsAsync_SavesAllDirtyViews_CellBecomesClean()
    {
        // Set up: one cell with two dirty schematics + one dirty symbol.
        var cellDir   = Path.GetFullPath(Path.Combine(_tempDir, "CellE"));
        var csch1Path = MakeCsch("CellE", "main.csch");
        var csch2Path = MakeCsch("CellE", "tb.csch");
        var csymPath  = MakeCsym("CellE", "CellE.csym");

        var registry = new SchematicSessionRegistry();
        var vm1      = new SchematicViewModel(new SchematicEditModel());
        var vm2      = new SchematicViewModel(new SchematicEditModel());
        var key1     = Path.GetFullPath(csch1Path);
        var key2     = Path.GetFullPath(csch2Path);
        registry.Register(key1, vm1, _ => { });
        registry.Register(key2, vm2, _ => { });

        // Dirty both schematics.
        vm1.UndoRedo.Execute(new AddCompCmd(vm1.EditModel,
            new EditableComponent { Symbol = SymbolKind.Resistor }));
        vm2.UndoRedo.Execute(new AddCompCmd(vm2.EditModel,
            new EditableComponent { Symbol = SymbolKind.Inductor }));
        Assert.True(vm1.UndoRedo.IsModified);
        Assert.True(vm2.UndoRedo.IsModified);

        // Set up dirty symbol doc.
        var symVm  = new SymbolEditorViewModel(new EditableSymbol { UserEditable = true });
        var symDoc = new SymbolEditorDocument("CellE.csym", symVm, csymPath);
        symVm.CurrentSymbolPath = Path.GetFullPath(csymPath);
        symVm.IsDirty           = true;

        var symDocs = new List<SymbolEditorDocument> { symDoc };

        // Confirm everything is dirty before the simulated save.
        Assert.True(SimIsCellDirty(registry, symDocs, cellDir));

        // ── Act: simulate SaveCellViewsAsync ─────────────────────────────────

        // Save all dirty schematics in the cell.
        foreach (var p in registry.AllDirtyPaths
                     .Where(p => IsViewInCell(p, cellDir)).ToList())
        {
            var normP = Path.GetFullPath(p);
            if (!registry.TryGet(normP, out var vm) || vm is null || !vm.UndoRedo.IsModified)
                continue;
            SchematicPersistence.SaveToFile(normP, vm.EditModel,
                Path.GetFileNameWithoutExtension(normP));
            registry.MarkSaved(normP);
        }

        // Save the dirty symbol doc.
        foreach (var doc in symDocs
                     .Where(d => d.IsDirty
                                 && d.ViewModel.CurrentSymbolPath is { } sp
                                 && IsViewInCell(sp, cellDir))
                     .ToList())
        {
            if (doc.ViewModel.CurrentSymbolPath is not { } path) continue;
            SymbolPersistence.SaveToFile(path,
                doc.ViewModel.EditableSymbol.ToSymbol());
            doc.ViewModel.IsDirty = false;
        }

        // ── Assert ────────────────────────────────────────────────────────────

        // All three files must now exist on disk.
        Assert.True(File.Exists(csch1Path));
        Assert.True(File.Exists(csch2Path));
        Assert.True(File.Exists(csymPath));

        // Sessions must be clean.
        Assert.False(vm1.UndoRedo.IsModified);
        Assert.False(vm2.UndoRedo.IsModified);
        Assert.False(symDoc.IsDirty);

        // IsCellDirty must now return false.
        Assert.False(SimIsCellDirty(registry, symDocs, cellDir));
    }
}
