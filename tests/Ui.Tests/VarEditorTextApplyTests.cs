using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The VAR editor's text mode shows <c>name = expression</c> and nothing else, so applying it must
/// keep what it does not show. It rebuilt every row from the text alone: adding <c>dist = 1</c> to
/// a VAR holding <c>Fc = 868.95 MHz</c> left <c>Fc</c> at 868.95 Hz, and the design then failed
/// elaboration over a frequency nobody had typed.
/// </summary>
public class VarEditorTextApplyTests
{
    [Fact]
    public void AddingAVariableAsText_KeepsTheOtherVariablesUnits()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = "VAR1" };
        comp.Parameters.Add(new EditableParameter { Name = "Fc", Expression = "868.95", Unit = "MHz", ShowOnSchematic = false });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);

        using var editor = new VarEditorViewModel();
        editor.SetTarget(vm, comp, showClose: false);
        editor.TextContent += "\ndist = 1";
        editor.ApplyTextCommand.Execute(null);

        var fc = comp.Parameters.Single(p => p.Name == "Fc");
        Assert.Equal("MHz", fc.Unit);
        Assert.False(fc.ShowOnSchematic);
        Assert.Equal("1", comp.Parameters.Single(p => p.Name == "dist").Expression);
    }
}
