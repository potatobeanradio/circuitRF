// Owner: PCD-5 (brief-pcd-5 §3). Needs PCD-3's MSHAPE and PCD-2's offset placement.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-5 R-pcd5-2: a Stretch binding on a drawn shape is a Length handle on the generated cell.</summary>
public sealed class Pcd5HandlesTests
{
    [Fact]
    public void R2_AStretchBindingIsALengthHandleThatFollowsItsParameter()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Pad2", edit: m =>
        {
            // A 100 µm pad offset 1 mm beyond TL3's far end, its right edge (vertices 2 and 3) stretched by Lpad.
            var pad = new EditableComponent { InstanceName = "SH1", Symbol = SymbolKind.MShape, X = 9000, Y = 0 };
            foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 0))
                pad.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
            pad.Parameters.Single(p => p.Name == DrawnMetal.Model).Expression = DrawnMetal.ModelOpen;
            pad.Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1, Dx = "1", Unit = "mm" };
            var scope = new Scope("cell");
            scope.Bind("Lpad", "100", "um");
            m.Components.Add(DrawnMetal.Stretch(pad, new StretchBinding { Parameter = "Lpad", Anchor = 1, Moving = [2, 3], AxisDeg = 0 },
                                                scope, new Evaluator()));
        });
        string ccellPath = Path.Combine(cell, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Parameters.Add(new CcellParameter { Name = "Lpad", DefaultExpression = "100", Unit = "um", Dimension = UnitDimension.Length,
                                                  Minimum = 5e-5, Maximum = 5e-4 });
        CellPersistence.SaveToFile(ccellPath, ccell);
        using var registration = DesignedPCellResolver.Register(ws.Root);
        Assert.True(PCellRegistry.TryGet("cpcell:Pad2", out var generator));

        PCellHandle At(double lpad)
        {
            var r = generator(new Dictionary<string, PCellValue> { ["Lmid"] = 3e-3, ["Lpad"] = lpad }, ws.Tech, PCellLayerSelection.Default);
            var at = CircuitRF.Design.Layout.SchematicSync.DesignedCellLayout.At(cell, new Dictionary<string, Value>(), ws.Root, ws.Tech, ws.TechPath);
            Assert.True(r.Handles is { Count: 1 }, string.Join("\n", (r.Diagnostics ?? []).Concat(at.Lines.Select(l => l.Text))
                .Append("instances: " + string.Join(",", at.Layout!.Instances.Select(i => i.SchematicId + "@" + i.CellRef)))));
            return r.Handles![0];
        }
        var small = At(100e-6);
        var large = At(300e-6);
        Assert.Equal(("Lpad", PCellHandleQuantity.Length, 5e-5, 5e-4), (small.Parameter, small.Quantity, small.Min, small.Max));
        Assert.Equal(200_000, Math.Abs(large.ProjectedPosition - small.ProjectedPosition), 2.0);   // 200 µm at 1 nm per unit
        Assert.Equal((small.AnchorX, small.AnchorY), (large.AnchorX, large.AnchorY));
    }
}
