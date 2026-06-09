using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class ParameterCommandTests
{
    [Fact]
    public void EditParameter_Execute_ChangesExpression()
    {
        var m     = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        var param = new EditableParameter { Name = "R", Expression = "50" };
        comp.Parameters.Add(param);
        m.Components.Add(comp);

        var cmd = new EditParameterCommand(m, param, "100");
        cmd.Execute();
        Assert.Equal("100", param.Expression);
    }

    [Fact]
    public void EditParameter_Undo_RestoresExpression()
    {
        var m     = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        var param = new EditableParameter { Name = "R", Expression = "50" };
        comp.Parameters.Add(param);
        m.Components.Add(comp);

        var cmd = new EditParameterCommand(m, param, "100");
        cmd.Execute();
        cmd.Undo();
        Assert.Equal("50", param.Expression);
    }

    [Fact]
    public void RenameComponent_Execute_ChangesName()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        m.Components.Add(comp);

        var cmd = new RenameComponentCommand(m, comp, "R99");
        cmd.Execute();
        Assert.Equal("R99", comp.InstanceName);
    }

    [Fact]
    public void RenameComponent_Undo_RestoresName()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        m.Components.Add(comp);

        var cmd = new RenameComponentCommand(m, comp, "R99");
        cmd.Execute();
        cmd.Undo();
        Assert.Equal("R1", comp.InstanceName);
    }

    [Fact]
    public void SetDisableState_Execute_DisablesComponent()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        m.Components.Add(comp);

        var cmd = new SetDisableStateCommand(m, new[] { comp.Id }, DisableState.Open);
        cmd.Execute();
        Assert.Equal(DisableState.Open, comp.Disable);
    }

    [Fact]
    public void SetDisableState_Undo_RestoresState()
    {
        var m    = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        m.Components.Add(comp);

        var cmd = new SetDisableStateCommand(m, new[] { comp.Id }, DisableState.Short);
        cmd.Execute();
        cmd.Undo();
        Assert.Equal(DisableState.None, comp.Disable);
    }
}
