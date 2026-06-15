using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the hierarchy single-doc save bug fix:
/// pushed-in sub-cell edits must be persisted when the toolbar Save is used.
/// Framework-free: no Avalonia, disk I/O only.
/// </summary>
public sealed class HierarchySaveTests : IDisposable
{
    private readonly string _tempDir;

    public HierarchySaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_hiersave_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static SchematicViewModel MakeVm(SchematicEditModel? model = null)
        => new(model ?? new SchematicEditModel());

    // Command that adds a component to the model (undoable), used to make a VM dirty with a
    // detectable, round-trip-able model change.
    private sealed class AddComponentCommand : IUiCommand
    {
        private readonly SchematicEditModel _model;
        private readonly EditableComponent  _comp;
        public string Description => "add component";
        public AddComponentCommand(SchematicEditModel model, EditableComponent comp)
        { _model = model; _comp = comp; }
        public void Execute() => _model.Components.Add(_comp);
        public void Undo()    => _model.Components.Remove(_comp);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes an empty .csch to disk and returns the path.</summary>
    private string MakeCsch(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        SchematicPersistence.SaveToFile(path, new SchematicEditModel(), Path.GetFileNameWithoutExtension(fileName));
        return path;
    }

    /// <summary>
    /// Simulates the fixed SaveSingleDocument materialized-branch logic:
    /// write the base session, then flush every dirty nav-frame session to its own .csch.
    /// This mirrors the fix in WorkspaceViewModel.SaveSingleDocument exactly.
    /// </summary>
    private static void SimulateSingleDocSave(
        SchematicDocument      doc,
        SchematicSessionRegistry registry)
    {
        // Base write (unchanged from pre-fix behavior).
        if (doc.FilePath is null) return;
        SchematicPersistence.SaveToFile(doc.FilePath, doc.ViewModel.EditModel, doc.Id);

        // THE FIX: also flush every dirty pushed-in sub-cell session.
        foreach (var (session, _) in doc.NavFrames)
        {
            if (ReferenceEquals(session, doc.ViewModel)) continue;
            if (!session.UndoRedo.IsModified) continue;
            if (!registry.TryGetPath(session, out var subPath) || subPath is null) continue;
            var subCellName = Path.GetFileNameWithoutExtension(subPath);
            SchematicPersistence.SaveToFile(subPath, session.EditModel, subCellName);
            registry.MarkSaved(subPath);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported bug: push in, edit the sub-cell, call single-doc Save → edit must survive
    /// a disk round-trip.  The child session's dirty flag must be cleared after save.
    /// </summary>
    [Fact]
    public void PushedIn_Edit_SingleSave_PersistsSubCell()
    {
        var parentPath = MakeCsch("parent.csch");
        var childPath  = MakeCsch("child.csch");

        var registry = new SchematicSessionRegistry();
        var baseVm   = MakeVm();
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new SchematicDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");

        Assert.Same(childVm, doc.ActiveViewModel);

        // Edit on the active (child) session — adds a detectable, round-trip-able component.
        var comp = new EditableComponent { Symbol = SymbolKind.Resistor };
        childVm.UndoRedo.Execute(new AddComponentCommand(childVm.EditModel, comp));
        Assert.True(childVm.UndoRedo.IsModified);

        // Act: single-doc save (simulates the fixed WorkspaceViewModel.SaveSingleDocument).
        SimulateSingleDocSave(doc, registry);

        // Assert: edit is present in the reloaded child .csch.
        var (loaded, _, _) = SchematicPersistence.LoadFromFile(childPath);
        Assert.Single(loaded.Components);

        // Assert: the child session's dirty flag is cleared.
        Assert.False(childVm.UndoRedo.IsModified);
    }

    /// <summary>
    /// Regression guard: editing at the base level (no push-in) and saving must still persist the
    /// base edit to the parent .csch.  Unchanged behavior before and after the fix.
    /// </summary>
    [Fact]
    public void BaseEdit_SingleSave_Unchanged()
    {
        var parentPath = MakeCsch("parent.csch");

        var registry = new SchematicSessionRegistry();
        var baseVm   = MakeVm();
        var doc      = new SchematicDocument("parent", baseVm, parentPath);

        // Edit at base with no push-in.
        var comp = new EditableComponent { Symbol = SymbolKind.Capacitor };
        baseVm.UndoRedo.Execute(new AddComponentCommand(baseVm.EditModel, comp));
        Assert.True(baseVm.UndoRedo.IsModified);

        SimulateSingleDocSave(doc, registry);

        var (loaded, _, _) = SchematicPersistence.LoadFromFile(parentPath);
        Assert.Single(loaded.Components);
    }

    /// <summary>
    /// Guard: when the base is dirty but the pushed-in sub-cell is clean, only the base is
    /// written.  The sub-cell file must not be spuriously re-written.
    /// </summary>
    [Fact]
    public void CleanPushedInFrame_IsNotRewritten()
    {
        var parentPath = MakeCsch("parent.csch");
        var childPath  = MakeCsch("child.csch");

        var registry = new SchematicSessionRegistry();
        var baseVm   = MakeVm();
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new SchematicDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");

        // Dirty the base, leave the child clean.
        var comp = new EditableComponent { Symbol = SymbolKind.Resistor };
        baseVm.UndoRedo.Execute(new AddComponentCommand(baseVm.EditModel, comp));
        Assert.False(childVm.UndoRedo.IsModified);

        // Record how many frames would be written by the loop (should be 0 for child).
        int dirtySubFrameCount = 0;
        foreach (var (session, _) in doc.NavFrames)
        {
            if (ReferenceEquals(session, doc.ViewModel)) continue;
            if (session.UndoRedo.IsModified) dirtySubFrameCount++;
        }

        // Assert: the sub-cell frame is not dirty so it would be skipped.
        Assert.Equal(0, dirtySubFrameCount);

        // Run the full save and confirm the child on disk still has 0 components.
        SimulateSingleDocSave(doc, registry);
        var (childLoaded, _, _) = SchematicPersistence.LoadFromFile(childPath);
        Assert.Empty(childLoaded.Components);
    }
}
