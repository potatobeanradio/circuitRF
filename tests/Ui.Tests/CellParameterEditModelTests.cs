using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Cell;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer-1 gate: framework-free CellParameterEditModel + commands round-trip tests.
/// No Avalonia types referenced.
/// </summary>
public class CellParameterEditModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CellParameterEditModel model, string path) CreateModel(
        params (string Name, string Default)[] initialParams)
    {
        var file = new CcellFile();
        foreach (var (n, d) in initialParams)
            file.Parameters.Add(new CcellParameter { Name = n, DefaultExpression = d, ShowOnSchematic = true });

        var path = Path.GetTempFileName();
        CellPersistence.SaveToFile(path, file);

        return (new CellParameterEditModel(path, file), path);
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_AppendsToList_AndPersists()
    {
        var (model, path) = CreateModel();
        try
        {
            var stack = new UndoRedoStack();
            var p = new CcellParameter { Name = "W", DefaultExpression = "100e-6" };
            stack.Execute(new AddCellParameterCommand(model, p));

            Assert.Single(model.Parameters);
            Assert.Equal("W", model.Parameters[0].Name);

            var reloaded = CellPersistence.LoadFromFile(path);
            Assert.Single(reloaded.Parameters);
            Assert.Equal("W", reloaded.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Add_Undo_RemovesAndPersists()
    {
        var (model, path) = CreateModel();
        try
        {
            var stack = new UndoRedoStack();
            stack.Execute(new AddCellParameterCommand(model, new CcellParameter { Name = "W" }));
            stack.Undo();

            Assert.Empty(model.Parameters);
            Assert.Empty(CellPersistence.LoadFromFile(path).Parameters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Add_Undo_Redo_RoundTrips()
    {
        var (model, path) = CreateModel();
        try
        {
            var stack = new UndoRedoStack();
            stack.Execute(new AddCellParameterCommand(model, new CcellParameter { Name = "W" }));
            stack.Undo();
            stack.Redo();

            Assert.Single(model.Parameters);
            Assert.Equal("W", model.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_RemovesFromList_AndPersists()
    {
        var (model, path) = CreateModel(("W", "100e-6"), ("L", "50e-9"));
        try
        {
            var stack  = new UndoRedoStack();
            var target = model.MutableParameters[0]; // "W"
            stack.Execute(new RemoveCellParameterCommand(model, target));

            Assert.Single(model.Parameters);
            Assert.Equal("L", model.Parameters[0].Name);
            Assert.Single(CellPersistence.LoadFromFile(path).Parameters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remove_Undo_RestoresAtOriginalIndex()
    {
        var (model, path) = CreateModel(("W", "100e-6"), ("L", "50e-9"));
        try
        {
            var stack  = new UndoRedoStack();
            var target = model.MutableParameters[0]; // "W" at index 0
            stack.Execute(new RemoveCellParameterCommand(model, target));
            stack.Undo();

            Assert.Equal(2, model.Parameters.Count);
            Assert.Equal("W", model.Parameters[0].Name); // restored to index 0
            Assert.Equal("L", model.Parameters[1].Name);
        }
        finally { File.Delete(path); }
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_UpdatesName_AndPersists()
    {
        var (model, path) = CreateModel(("W", "100e-6"));
        try
        {
            var stack = new UndoRedoStack();
            var p = model.MutableParameters[0];
            stack.Execute(new SetCellParameterCommand(
                model, p,
                newName: "Width", newDefault: p.DefaultExpression,
                newUnit: p.Unit, newDimension: p.Dimension, newShow: p.ShowOnSchematic,
                description: "Rename W → Width"));

            Assert.Equal("Width", model.Parameters[0].Name);
            Assert.Equal("Width", CellPersistence.LoadFromFile(path).Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rename_Undo_RestoresOldName()
    {
        var (model, path) = CreateModel(("W", "100e-6"));
        try
        {
            var stack = new UndoRedoStack();
            var p = model.MutableParameters[0];
            stack.Execute(new SetCellParameterCommand(
                model, p,
                newName: "Width", newDefault: p.DefaultExpression,
                newUnit: p.Unit, newDimension: p.Dimension, newShow: p.ShowOnSchematic,
                description: "Rename W → Width"));
            stack.Undo();

            Assert.Equal("W", model.Parameters[0].Name);
            Assert.Equal("W", CellPersistence.LoadFromFile(path).Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }

    // ── Default edit ──────────────────────────────────────────────────────────

    [Fact]
    public void SetDefault_UpdatesAndPersists()
    {
        var (model, path) = CreateModel(("W", "100e-6"));
        try
        {
            var stack = new UndoRedoStack();
            var p = model.MutableParameters[0];
            stack.Execute(new SetCellParameterCommand(
                model, p,
                newName: p.Name, newDefault: "200e-6",
                newUnit: p.Unit, newDimension: p.Dimension, newShow: p.ShowOnSchematic,
                description: "Edit default of W"));

            Assert.Equal("200e-6", model.Parameters[0].DefaultExpression);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SetDefault_Undo_RestoresOldDefault()
    {
        var (model, path) = CreateModel(("W", "100e-6"));
        try
        {
            var stack = new UndoRedoStack();
            var p = model.MutableParameters[0];
            stack.Execute(new SetCellParameterCommand(
                model, p,
                newName: p.Name, newDefault: "200e-6",
                newUnit: p.Unit, newDimension: p.Dimension, newShow: p.ShowOnSchematic,
                description: "Edit default of W"));
            stack.Undo();

            Assert.Equal("100e-6", model.Parameters[0].DefaultExpression);
        }
        finally { File.Delete(path); }
    }

    // ── Changed event ─────────────────────────────────────────────────────────

    [Fact]
    public void Changed_FiredOnEachMutation()
    {
        var (model, path) = CreateModel();
        try
        {
            int changeCount = 0;
            model.Changed += (_, _) => changeCount++;

            var stack = new UndoRedoStack();
            var p = new CcellParameter { Name = "X" };
            stack.Execute(new AddCellParameterCommand(model, p)); // +1
            stack.Undo();                                          // +1
            stack.Redo();                                          // +1

            Assert.Equal(3, changeCount);
        }
        finally { File.Delete(path); }
    }

    // ── Full round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void AddThenEditThenUndo_FullRoundTrip()
    {
        var (model, path) = CreateModel();
        try
        {
            var stack = new UndoRedoStack();
            var p = new CcellParameter { Name = "Tmp", DefaultExpression = "0" };
            stack.Execute(new AddCellParameterCommand(model, p));
            stack.Execute(new SetCellParameterCommand(
                model, p,
                newName: "MyParam", newDefault: "1e-3",
                newUnit: "nH", newDimension: UnitDimension.Inductance, newShow: false,
                description: "Configure MyParam"));

            var reloaded = CellPersistence.LoadFromFile(path);
            Assert.Single(reloaded.Parameters);
            Assert.Equal("MyParam",              reloaded.Parameters[0].Name);
            Assert.Equal("1e-3",                 reloaded.Parameters[0].DefaultExpression);
            Assert.Equal("nH",                   reloaded.Parameters[0].Unit);
            Assert.Equal(UnitDimension.Inductance, reloaded.Parameters[0].Dimension);
            Assert.False(reloaded.Parameters[0].ShowOnSchematic);

            // Undo edit
            stack.Undo();
            Assert.Equal("Tmp", model.Parameters[0].Name);
            Assert.Equal("0",   model.Parameters[0].DefaultExpression);

            // Undo add
            stack.Undo();
            Assert.Empty(model.Parameters);
            Assert.Empty(CellPersistence.LoadFromFile(path).Parameters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RemoveWithPrecedingRename_UndoBothRestoresOriginal()
    {
        var (model, path) = CreateModel(("A", "1"), ("B", "2"));
        try
        {
            var stack = new UndoRedoStack();
            var pA = model.MutableParameters[0]; // "A"

            // Rename A → Alpha
            stack.Execute(new SetCellParameterCommand(
                model, pA,
                newName: "Alpha", newDefault: pA.DefaultExpression,
                newUnit: pA.Unit, newDimension: pA.Dimension, newShow: pA.ShowOnSchematic,
                description: "Rename A → Alpha"));

            // Remove Alpha
            stack.Execute(new RemoveCellParameterCommand(model, pA));
            Assert.Single(model.Parameters);
            Assert.Equal("B", model.Parameters[0].Name);

            // Undo remove — pA re-inserted at index 0
            stack.Undo();
            Assert.Equal(2, model.Parameters.Count);
            Assert.Equal("Alpha", model.Parameters[0].Name);

            // Undo rename — pA.Name restored to "A"
            stack.Undo();
            Assert.Equal("A", model.Parameters[0].Name);
        }
        finally { File.Delete(path); }
    }
}
