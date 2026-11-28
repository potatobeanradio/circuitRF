// Owner: PCD-4 (brief-pcd-4 §3).

using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-4 R-pcd4-1 with PCD-3's drawn metal: an MSHAPE dropped on a pin, then edited as text.</summary>
[Collection(PCellResolverCollection.Name)]
public sealed class Pcd4MShapeTests
{
    [Fact]
    public void AnMShapeDropsOnAPinAndMergesByDesignator()
    {
        var m = Pcd4MergeTests.Drawn("Port:P1 in 0 Num=1 Z=50 Ohm\nMLIN:TL1 in end W=100 um L=1 mm\n");
        var pad = new EditableComponent { Symbol = SymbolKind.MShape };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 0))
            pad.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                                                       ShowOnSchematic = d.ShowOnSchematic, Dimension = d.Dimension });
        pad.Stretch = [new StretchBinding { Parameter = "Lpad", Anchor = 1, Moving = [2, 3] }];

        var dropped = PinDrop.Apply(m, pad, 1, "TL1", 2);
        Assert.Null(dropped.Refusal);
        string sh = dropped.InstanceName!;
        Assert.StartsWith("SH", sh);

        string text = string.Join("\n", SchematicCircuit.CnlTextOf(dropped.Schematic!, "cell").Split('\n').Select(l =>
            !l.StartsWith($"{DrawnMetal.GeneratorId}:{sh} ", StringComparison.Ordinal) ? l
            : Regex.Replace(l, "Model=(\"?)none\\1", "Model=$1open$1")));
        Assert.Contains("open", text);
        var r = CellNetlistMerge.Merge(dropped.Schematic!, text);

        Assert.Empty(r.Refusals);
        var merged = r.Schematic!.Components.Single(c => c.InstanceName == sh);
        Assert.Equal(DrawnMetal.ModelOpen, merged.Parameters.Single(p => p.Name == DrawnMetal.Model).Expression.Trim('"'));
        var stretch = Assert.Single(merged.Stretch!);   // a non-netlist field, through the drop and the merge
        Assert.Equal("Lpad", stretch.Parameter);
        Assert.Equal(new[] { 2, 3 }, stretch.Moving);
        Assert.Equal(dropped.Schematic!.Wires.Select(w => w.Points.ToList()), r.Schematic.Wires.Select(w => w.Points.ToList()));
    }
}
