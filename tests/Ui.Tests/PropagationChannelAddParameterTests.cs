using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A placed Propagation Channel carries a dozen rows, and its other parameters (the path-loss model's, its own, the
/// added losses) had no way onto it from the dialog: a user could not give a fresh channel the Environment, Floors,
/// Partitions, Fading or Drop rows a shipped example's channel shows. Its "+" picks one by name.
/// </summary>
public class PropagationChannelAddParameterTests
{
    [Fact]
    public void TheChoicesLeadWithTheChosenModel_AndLeaveOtherModelsOut()
    {
        var names = ComponentTypeRegistry.ChannelAddableParameters("P1238").Select(c => c.Name).ToList();

        Assert.Equal(["Environment", "Los", "Floors"], names.Take(3));
        Assert.Contains("Partitions", names);   // an added loss
        Assert.Contains("Fading",     names);   // the channel's own
        Assert.Contains("Drop",       names);
        Assert.DoesNotContain("Scenario", names);   // TR 38.901's, which a P1238 channel would not read
    }

    [Fact]
    public async Task ThePlusButtonAddsThePickedParameter_RemovablyAndAtItsDefault()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.PropagationChannel, InstanceName = "CH1" };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.PropagationChannel, 2))
            comp.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit });
        comp.Parameters.Single(p => p.Name == "PathLoss").Expression = "P1238";
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        Assert.True(editor.ShowAddChannelParameter);
        // The picker itself hides what the channel already carries; it is handed that set to do so.
        editor.PickModelParameterAsync = (_, offered, present) =>
        {
            Assert.Contains("PathLoss", present);
            return Task.FromResult<CircuitRF.Design.Schematic.VerilogAParameterInfo?>(offered.Single(o => o.Name == "Environment"));
        };

        await editor.AddChannelParameterCommand.ExecuteAsync(null);

        Assert.Equal("office", comp.Parameters.Single(p => p.Name == "Environment").Expression);
        Assert.True(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.PropagationChannel, "Environment"));
        Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.PropagationChannel, "PathLoss"));
    }

    /// <summary>
    /// A blank value draws no schematic label, so a parameter added blank and then ticked to Display showed nothing. One whose
    /// absence is a fixed value arrives stating it, and ticking it draws it.
    /// </summary>
    [Fact]
    public async Task AnAddedSeed_StatesItsValue_SoDisplayingItDrawsIt()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.PropagationChannel, InstanceName = "CH1" };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.PropagationChannel, 2))
            comp.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, ShowOnSchematic = d.ShowOnSchematic });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        editor.PickModelParameterAsync = (_, offered, _) =>
            Task.FromResult<CircuitRF.Design.Schematic.VerilogAParameterInfo?>(offered.Single(o => o.Name == "Seed"));

        await editor.AddChannelParameterCommand.ExecuteAsync(null);
        editor.Rows.Single(r => r.Name == "Seed").ShowOnSchematic = true;

        Assert.Contains("Seed = 1", comp.ToRenderComponent().Labels);
    }
}
