using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class SelectionCommandTests
{
    private static SchematicEditModel BuildModel()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        m.Components.Add(new EditableComponent
            { InstanceName = "C1", Symbol = SymbolKind.Capacitor, X = 600, Y = 0 });
        return m;
    }

    // ── PlaceComponent ────────────────────────────────────────────────────────

    [Fact]
    public void PlaceComponent_Execute_AddsToModel()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Inductor };
        var cmd  = new PlaceComponentCommand(m, comp);
        cmd.Execute();
        Assert.Contains(comp, m.Components);
    }

    [Fact]
    public void PlaceComponent_Undo_RemovesFromModel()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Inductor };
        var cmd  = new PlaceComponentCommand(m, comp);
        cmd.Execute();
        cmd.Undo();
        Assert.DoesNotContain(comp, m.Components);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_Execute_RemovesComponents()
    {
        var m   = BuildModel();
        var ids = m.Components.Select(c => c.Id).ToList();
        var cmd = new DeleteCommand(m, ids, _ => { });
        cmd.Execute();
        Assert.Empty(m.Components);
    }

    [Fact]
    public void Delete_Undo_RestoresComponents()
    {
        var m    = BuildModel();
        var ids  = m.Components.Select(c => c.Id).ToList();
        int count = m.Components.Count;
        var cmd  = new DeleteCommand(m, ids, _ => { });
        cmd.Execute();
        cmd.Undo();
        Assert.Equal(count, m.Components.Count);
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Move_Execute_UpdatesPosition()
    {
        var m    = BuildModel();
        var comp = m.Components[0];
        var snap = new ComponentMoveSnapshot(comp, 0, 0, 300, 200);
        var cmd  = new MoveCommand(m, new List<ComponentMoveSnapshot> { snap },
                                   new List<WireMoveSnapshot>(),
                                   new List<CanvasObjectMoveSnapshot>());
        cmd.Execute();
        Assert.Equal(300, comp.X);
        Assert.Equal(200, comp.Y);
    }

    [Fact]
    public void Move_Undo_RestoresPosition()
    {
        var m    = BuildModel();
        var comp = m.Components[0];
        var snap = new ComponentMoveSnapshot(comp, 0, 0, 300, 200);
        var cmd  = new MoveCommand(m, new List<ComponentMoveSnapshot> { snap },
                                   new List<WireMoveSnapshot>(),
                                   new List<CanvasObjectMoveSnapshot>());
        cmd.Execute();
        cmd.Undo();
        Assert.Equal(0, comp.X);
        Assert.Equal(0, comp.Y);
    }

    // ── Rotate ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotate_Execute_ChangesRotation()
    {
        var m    = BuildModel();
        var id   = m.Components[0].Id;
        var cmd  = new RotateCommand(m, new[] { id }, clockwise: false);
        cmd.Execute();
        Assert.Equal(SymbolRotation.R90, m.Components[0].Rotation);
    }

    [Fact]
    public void Rotate_Undo_RestoresRotation()
    {
        var m    = BuildModel();
        var id   = m.Components[0].Id;
        var cmd  = new RotateCommand(m, new[] { id }, clockwise: false);
        cmd.Execute();
        cmd.Undo();
        Assert.Equal(SymbolRotation.R0, m.Components[0].Rotation);
    }

    // ── UndoRedoStack round-trip ──────────────────────────────────────────────

    [Fact]
    public void UndoRedo_Stack_RoundTrip()
    {
        var m     = new SchematicEditModel();
        var stack = new UndoRedoStack();
        var comp  = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };

        stack.Execute(new PlaceComponentCommand(m, comp));
        Assert.True(stack.CanUndo);
        Assert.Single(m.Components);

        stack.Undo();
        Assert.Empty(m.Components);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Single(m.Components);
    }
}
