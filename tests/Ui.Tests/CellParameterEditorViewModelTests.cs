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
}
