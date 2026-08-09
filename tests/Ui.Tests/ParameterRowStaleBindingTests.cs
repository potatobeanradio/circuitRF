using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported against VerilogA — "after browsing for the .osdi file, the Model name and the Pins
/// parameter do not update if there is only one Model" — and the cause is not VerilogA's.
///
/// <para><c>SetParametersCommand</c> writes fresh clones of EVERY parameter, even ones it did not
/// touch. The parameter NAMES are then unchanged, so the editor took its same-name path and refreshed
/// each row from the row's own <c>EditableParameter</c> — which is now an orphan holding the value
/// that was there before. The model was correct throughout; only the dialog disagreed with it.</para>
///
/// <para>It surfaced on VerilogA because that is where the editor writes parameters unprompted, and
/// on a single-model file because that is the case where it fills anything in without being asked.
/// The defect is general, so the gate is.</para>
/// </summary>
public class ParameterRowStaleBindingTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeResistor()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R1", X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter { Name = "R", Expression = "50", Unit = "Ω", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "Temp", Expression = "27", Unit = "", ShowOnSchematic = false });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    [Fact]
    public void ASameNameSetParametersCommand_ShowsTheNewValues_NotTheOnesItReplaced()
    {
        var (vm, comp, editor) = MakeResistor();
        Assert.Equal("50", editor.Rows.Single(r => r.Name == "R").StagedExpression);

        // Exactly the shape the VerilogA autofill uses: clone the list, change a value, replace.
        // Every name is unchanged, so nothing about the ROW SET moved.
        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        updated.Single(p => p.Name == "R").Expression = "75";
        vm.Execute(new SetParametersCommand(vm.EditModel, comp, updated));

        Assert.Equal("75", editor.Rows.Single(r => r.Name == "R").StagedExpression);
    }

    [Fact]
    public void EveryRow_IsBoundToAParameterTheComponentActuallyHolds()
    {
        // The invariant behind the test above, asserted directly: a row reading an object no longer
        // in the component is a row that can neither show a change nor commit one, and nothing about
        // it looks wrong from the outside.
        var (vm, comp, editor) = MakeResistor();

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        updated.Single(p => p.Name == "Temp").Expression = "85";
        vm.Execute(new SetParametersCommand(vm.EditModel, comp, updated));

        foreach (var row in editor.Rows)
            Assert.Contains(comp.Parameters, p => ReferenceEquals(p, row.BoundParameter));
    }

    [Fact]
    public void EditingThroughARow_AfterSuchACommand_ReachesTheModel()
    {
        // The consequence a user meets next: a stale row commits onto its orphan, so the edit lands
        // nowhere and the box springs back on the following refresh.
        var (vm, comp, editor) = MakeResistor();

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        updated.Single(p => p.Name == "R").Expression = "75";
        vm.Execute(new SetParametersCommand(vm.EditModel, comp, updated));

        var row = editor.Rows.Single(r => r.Name == "R");
        row.StagedExpression = "120";
        row.CommitExpression();

        Assert.Equal("120", comp.Parameters.Single(p => p.Name == "R").Expression);
    }
}
