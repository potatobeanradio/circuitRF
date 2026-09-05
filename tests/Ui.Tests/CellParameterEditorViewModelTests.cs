using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer-2 gate: CellParameterEditorViewModel headless tests.
/// No Avalonia types referenced — tests only framework-free model + VM logic.
/// </summary>
public class CellParameterEditorViewModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CellParameterEditorViewModel vm, CellParameterEditModel model, string path)
        CreateVm(params (string Name, string Default, UnitDimension Dim)[] initialParams)
    {
        var file = new CcellFile();
        foreach (var (n, d, dim) in initialParams)
            file.Parameters.Add(new CcellParameter
            {
                Name = n, DefaultExpression = d, Dimension = dim,
                Unit = ComponentTypeRegistry.UnitOptions(dim)[0], ShowOnSchematic = true,
            });

        var path  = Path.GetTempFileName();
        CellPersistence.SaveToFile(path, file);
        var model = new CellParameterEditModel(path, file);
        var vm    = new CellParameterEditorViewModel("MyCell", model);
        return (vm, model, path);
    }

    // ── Loads parameters into rows ────────────────────────────────────────────

    [Fact]
    public void Constructor_LoadsParametersAsRows()
    {
        var (vm, _, path) = CreateVm(("W", "1e-4", UnitDimension.Length), ("L", "5e-9", UnitDimension.Length));
        try
        {
            Assert.Equal(2,   vm.Rows.Count);
            Assert.Equal("W", vm.Rows[0].StagedName);
            Assert.Equal("L", vm.Rows[1].StagedName);
            Assert.True(vm.HasParameters);
            Assert.False(vm.HasNoParameters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyCell_HasNoParameters()
    {
        var (vm, _, path) = CreateVm();
        try
        {
            Assert.Empty(vm.Rows);
            Assert.False(vm.HasParameters);
            Assert.True(vm.HasNoParameters);
        }
        finally { File.Delete(path); }
    }

    // ── Add parameter ─────────────────────────────────────────────────────────

    [Fact]
    public void AddParameter_AddsRowAndEnablesUndo()
    {
        var (vm, _, path) = CreateVm();
        try
        {
            vm.AddParameterCommand.Execute(null);

            Assert.Single(vm.Rows);
            Assert.StartsWith("Param", vm.Rows[0].StagedName);
            Assert.True(vm.UndoRedo.CanUndo);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddParameter_Undo_RemovesRow()
    {
        var (vm, _, path) = CreateVm();
        try
        {
            vm.AddParameterCommand.Execute(null);
            vm.UndoRedo.Undo();

            Assert.Empty(vm.Rows);
            Assert.False(vm.UndoRedo.CanUndo);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddParameter_GeneratesUniqueNames()
    {
        var (vm, _, path) = CreateVm();
        try
        {
            vm.AddParameterCommand.Execute(null);
            vm.AddParameterCommand.Execute(null);
            vm.AddParameterCommand.Execute(null);

            var names = vm.Rows.Select(r => r.StagedName).ToList();
            Assert.Equal(names.Distinct().Count(), names.Count); // all unique
        }
        finally { File.Delete(path); }
    }

    // ── Remove parameter ──────────────────────────────────────────────────────

    [Fact]
    public void RemoveRow_RemovesFromCollection()
    {
        var (vm, _, path) = CreateVm(("W", "1", UnitDimension.None), ("L", "2", UnitDimension.None));
        try
        {
            var row = vm.Rows[0];
            row.RemoveCommand.Execute(null);

            Assert.Single(vm.Rows);
            Assert.Equal("L", vm.Rows[0].StagedName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RemoveRow_Undo_RestoresRow()
    {
        var (vm, _, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            vm.Rows[0].RemoveCommand.Execute(null);
            vm.UndoRedo.Undo();

            Assert.Single(vm.Rows);
            Assert.Equal("W", vm.Rows[0].StagedName);
        }
        finally { File.Delete(path); }
    }

    // ── Rename consequence warning ────────────────────────────────────────────

    [Fact]
    public void RenameWarning_AppearsWhenStagedNameDiverges()
    {
        var (vm, _, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            var row = vm.Rows[0];
            row.StagedName = "Width";

            Assert.NotNull(row.RenameWarning);
            Assert.True(row.HasRenameWarning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RenameWarning_ClearsWhenNameReverted()
    {
        var (vm, _, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            var row = vm.Rows[0];
            row.StagedName = "Width"; // diverges
            row.StagedName = "W";     // reverts

            Assert.Null(row.RenameWarning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CommitName_ValidName_UpdatesModel()
    {
        var (vm, model, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            vm.Rows[0].StagedName = "Width";
            vm.Rows[0].CommitName();

            // After commit, RebuildRows fires → new row created with new name
            Assert.Equal("Width", vm.Rows[0].StagedName);
            Assert.Equal("Width", model.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CommitName_EmptyName_Reverts()
    {
        var (vm, model, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            vm.Rows[0].StagedName = "";
            vm.Rows[0].CommitName();

            // Empty name is invalid → reverted; model unchanged
            Assert.Equal("W", model.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CommitName_InvalidIdentifier_Reverts()
    {
        var (vm, model, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            vm.Rows[0].StagedName = "1invalid"; // starts with digit
            vm.Rows[0].CommitName();

            Assert.Equal("W", model.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    // ── Default expression ────────────────────────────────────────────────────

    [Fact]
    public void CommitDefault_UpdatesAndPersists()
    {
        var (vm, model, path) = CreateVm(("W", "1", UnitDimension.None));
        try
        {
            vm.Rows[0].StagedDefault = "100e-6";
            vm.Rows[0].CommitDefault();

            Assert.Equal("100e-6", model.Parameters[0].DefaultExpression);
        }
        finally { File.Delete(path); }
    }

    // ── Undo/Redo chain ───────────────────────────────────────────────────────

    [Fact]
    public void UndoRedo_OwnStack_IndependentOfOtherDocs()
    {
        var (vm, _, path) = CreateVm();
        try
        {
            // Stack starts empty
            Assert.False(vm.UndoRedo.CanUndo);
            Assert.False(vm.UndoRedo.CanRedo);

            vm.AddParameterCommand.Execute(null);
            Assert.True(vm.UndoRedo.CanUndo);
            Assert.False(vm.UndoRedo.CanRedo);

            vm.UndoRedo.Undo();
            Assert.False(vm.UndoRedo.CanUndo);
            Assert.True(vm.UndoRedo.CanRedo);
        }
        finally { File.Delete(path); }
    }

    // ── Name validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("W",       true)]
    [InlineData("Width",   true)]
    [InlineData("_priv",   true)]
    [InlineData("R1",      true)]
    [InlineData("",        false)]
    [InlineData("1bad",    false)]
    [InlineData("has-dash",false)]
    [InlineData("has.dot", false)]
    [InlineData("has space", false)]
    public void IsValidParamName_Covers_IdentifierRules(string name, bool expected)
    {
        Assert.Equal(expected, CellParameterRowViewModel.IsValidParamName(name));
    }

    // ── Primary Symbol drop-down tracks the cell folder ───────────────────────

    /// <summary>
    /// Draw a symbol for a cell whose editor is already open and the new <c>.csym</c> must become
    /// selectable.  It used to appear only after a restart: the file lists were built once at
    /// construction and nothing rebuilt them, so the drop-down kept offering "(none specified)"
    /// alone and the symbol could never be made primary.
    /// </summary>
    [Fact]
    public void RefreshAvailableFiles_PicksUpASymbolDrawnAfterTheEditorWasOpened()
    {
        var (vm, model, cellDir) = CreateCellFolderVm();
        try
        {
            Assert.Equal("(none specified)", Assert.Single(vm.AvailableSymbols));

            File.WriteAllText(Path.Combine(cellDir, CellFolder.SymbolSubFolder, "MyCell.csym"), "{}");

            vm.RefreshAvailableFiles();
            Assert.Contains("MyCell.csym", vm.AvailableSymbols);

            vm.SelectedPrimarySymbol = "MyCell.csym";
            Assert.Equal("MyCell.csym", model.PrimarySymbol);
            Assert.Equal("MyCell.csym", CellPersistence.LoadFromFile(model.CcellPath).PrimarySymbol);
        }
        finally { Directory.Delete(Path.GetDirectoryName(cellDir)!, recursive: true); }
    }

    /// <summary>
    /// Rebuilding the ItemsSource makes the bound ComboBox clear its own SelectedItem and write the
    /// null back after the suppression window has closed.  That must not be recorded as an edit:
    /// it is what wiped the saved primary before, and is why the lists were frozen at construction.
    /// </summary>
    [Fact]
    public void AComboBoxClearingItsOwnSelection_DoesNotWipeTheSavedPrimary()
    {
        var (vm, model, cellDir) = CreateCellFolderVm();
        try
        {
            File.WriteAllText(Path.Combine(cellDir, CellFolder.SymbolSubFolder, "a.csym"), "{}");
            File.WriteAllText(Path.Combine(cellDir, CellFolder.SymbolSubFolder, "b.csym"), "{}");
            vm.RefreshAvailableFiles();

            vm.SelectedPrimarySymbol = "b.csym";
            Assert.Equal("b.csym", model.PrimarySymbol);

            // The deferred write-back, exactly as the control makes it — outside any guard.
            vm.SelectedPrimarySymbol = null!;

            Assert.Equal("b.csym", model.PrimarySymbol);
            Assert.Equal("b.csym", CellPersistence.LoadFromFile(model.CcellPath).PrimarySymbol);
            Assert.Equal("b.csym", vm.SelectedPrimarySymbol);   // and the combo shows it again
        }
        finally { Directory.Delete(Path.GetDirectoryName(cellDir)!, recursive: true); }
    }

    /// <summary>"(none specified)" is a real choice and must still clear the primary.</summary>
    [Fact]
    public void SelectingNoneSpecified_ClearsThePrimary()
    {
        var (vm, model, cellDir) = CreateCellFolderVm();
        try
        {
            File.WriteAllText(Path.Combine(cellDir, CellFolder.SymbolSubFolder, "a.csym"), "{}");
            vm.RefreshAvailableFiles();
            vm.SelectedPrimarySymbol = "a.csym";
            Assert.Equal("a.csym", model.PrimarySymbol);

            vm.SelectedPrimarySymbol = "(none specified)";
            Assert.Null(model.PrimarySymbol);
            Assert.Null(CellPersistence.LoadFromFile(model.CcellPath).PrimarySymbol);
        }
        finally { Directory.Delete(Path.GetDirectoryName(cellDir)!, recursive: true); }
    }

    /// <summary>A real cell FOLDER (not a bare .ccell) — the file lists read its sub-folders.</summary>
    private static (CellParameterEditorViewModel vm, CellParameterEditModel model, string cellDir)
        CreateCellFolderVm()
    {
        var root    = Path.Combine(Path.GetTempPath(), "crf-cellvm-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        var cellDir = CellFolder.CreateCellFolder(root, "MyCell");

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var model     = new CellParameterEditModel(ccellPath, CellPersistence.LoadFromFile(ccellPath));
        return (new CellParameterEditorViewModel("MyCell", model), model, cellDir);
    }
}
