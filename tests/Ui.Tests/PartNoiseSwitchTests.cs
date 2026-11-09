using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Brief NZ-1: a part's <c>Noise=no</c> in the parameter editor is a check box in the part's own list, not a text row.
/// Ticked (the default) writes nothing; unticked writes <c>Noise=no</c>, which the schematic's extraction carries to the
/// netlist a run reads. The row's display box draws it on the symbol, and is off by default.
/// </summary>
public sealed class PartNoiseSwitchTests
{
    private static (ParameterEditorViewModel Editor, EditableComponent Comp, SchematicEditModel Model) Open(SymbolKind kind, string name)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = name, Symbol = kind };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(kind, 2))
            comp.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, ShowOnSchematic = d.ShowOnSchematic });
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);
        return (editor, comp, model);
    }

    [Fact]
    public void UntickingWritesNoiseNo_NotDisplayed_AndTickingRemovesIt()
    {
        var (editor, comp, model) = Open(SymbolKind.Resistor, "R1");
        Assert.True(editor.ShowNoiseRow);
        Assert.True(editor.PartNoise);
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "Noise");

        editor.PartNoise = false;
        var written = Assert.Single(comp.Parameters, p => p.Name == "Noise");
        Assert.Equal("no", written.Expression);
        Assert.False(written.ShowOnSchematic);
        Assert.False(editor.ShowNoiseLabel);
        Assert.DoesNotContain(editor.Rows, r => r.Name == "Noise");   // the box owns it, not a text row

        var r1 = NetExtractor.Extract(model, "tb").TestBench.Instances.Single(i => i.InstanceName == "R1");
        Assert.Contains(r1.Overrides, o => o.Name == "Noise" && o.Expression == "no");

        editor.PartNoise = true;
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "Noise");
    }

    /// <summary>The display box draws the key on the symbol, either state; un-displayed and noisy writes nothing again.</summary>
    [Fact]
    public void TheDisplayBox_IsTheParameterShowOnSchematic()
    {
        var (editor, comp, _) = Open(SymbolKind.Resistor, "R1");
        Assert.False(editor.ShowNoiseLabel);

        editor.PartNoise = false;
        editor.ShowNoiseLabel = true;
        var shown = Assert.Single(comp.Parameters, p => p.Name == "Noise");
        Assert.Equal(("no", true), (shown.Expression, shown.ShowOnSchematic));

        editor.PartNoise = true;
        shown = Assert.Single(comp.Parameters, p => p.Name == "Noise");
        Assert.Equal(("yes", true), (shown.Expression, shown.ShowOnSchematic));

        editor.ShowNoiseLabel = false;
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "Noise");
    }

    /// <summary>Noise=yes and not displayed is a state of its own: a part carrying it opens ticked and undisplayed, keeps it
    /// untouched, extracts as Noise=yes and draws no Noise label.</summary>
    [Fact]
    public void NoiseYes_NotDisplayed_IsKept()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        comp.Parameters.Add(new EditableParameter { Name = "R", Expression = "50", Unit = "Ohm", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "Noise", Expression = "yes", ShowOnSchematic = false });
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.True(editor.PartNoise);
        Assert.False(editor.ShowNoiseLabel);
        var kept = Assert.Single(comp.Parameters, p => p.Name == "Noise");
        Assert.Equal(("yes", false), (kept.Expression, kept.ShowOnSchematic));
        var r1 = NetExtractor.Extract(model, "tb").TestBench.Instances.Single(i => i.InstanceName == "R1");
        Assert.Contains(r1.Overrides, o => o.Name == "Noise" && o.Expression == "yes");
    }

    /// <summary>No box on a part with no noise to switch off — a port, a source.</summary>
    [Theory]
    [InlineData(SymbolKind.Term)]
    [InlineData(SymbolKind.Vdc)]
    public void APartThatCannotBeNoisy_HasNoBox(SymbolKind kind)
        => Assert.False(Open(kind, "X1").Editor.ShowNoiseRow);
}
