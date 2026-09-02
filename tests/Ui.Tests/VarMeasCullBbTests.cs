using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// VAR/MEAS (and any text-heavy annotation) must keep a viewport-cull bounding box (FullBb) wide enough
/// to cover their actual label text. A long label used to exceed the fixed 500-unit width estimate, so the
/// component was culled — and vanished — when only its label's right portion should still be on screen.
/// </summary>
public sealed class VarMeasCullBbTests
{
    [Fact]
    public void Var_FullBb_CoversLongLabelWidth()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 1000, Y = 1000 };
        comp.Parameters.Add(new EditableParameter
            { Name = "RFfreq", Expression = "2000000000", Unit = "GHz", ShowOnSchematic = true });
        model.Components.Add(comp);

        var rc = model.BuildRenderModel().Model.Components.First();

        // Reconstruct the widest label exactly as the model builder does ("<Name> = <Expr> <Unit>").
        string label = "RFfreq = 2000000000 GHz";
        double anchorX  = rc.X + SchematicComponent.LabelBaseXFor(SymbolKind.Var);
        double oldRight = anchorX + SchematicComponent.LabelWidthEstimate;             // old fixed estimate
        double newRight = anchorX + SchematicComponent.LabelWidthFor(label);           // length-based

        Assert.True(SchematicComponent.LabelWidthFor(label) > SchematicComponent.LabelWidthEstimate,
            "a 23-char label should exceed the fixed floor estimate");
        Assert.True(rc.FullBbMaxX >= newRight - 1,
            $"FullBbMaxX {rc.FullBbMaxX} must cover the long label to ~{newRight}");
        Assert.True(rc.FullBbMaxX > oldRight,
            $"FullBbMaxX {rc.FullBbMaxX} must extend past the old fixed estimate {oldRight}");
    }

    [Fact]
    public void LabelWidthFor_FloorsAtFixedEstimate_ForShortLabels()
    {
        Assert.Equal(SchematicComponent.LabelWidthEstimate, SchematicComponent.LabelWidthFor("R1"));
        Assert.True(SchematicComponent.LabelWidthFor(new string('x', 40)) > SchematicComponent.LabelWidthEstimate);
    }
}
