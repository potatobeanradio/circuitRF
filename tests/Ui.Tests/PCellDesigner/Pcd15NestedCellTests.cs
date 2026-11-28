// Owner: PCD-15. S1 across a hierarchy: a designed cell placed inside another designed cell takes its parameters as
// expressions of the outer cell's parameters, and regenerates when they change.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd15NestedCellTests
{
    [Fact]
    public void AnInnerCellsParameter_IsAnExpressionOfTheOuterCells_AndFollowsIt()
    {
        using var ws = new Pcd5Workspace();
        string inner = ws.Designed("NxInner");
        var created = PCellDesign.Create(ws.Root, "NxOuter");
        var model = new SchematicEditModel();
        var x1 = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic,
            CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(Path.GetDirectoryName(created.SchematicPath!)!, inner)),
        };
        x1.Parameters.Add(new EditableParameter { Name = "Lmid", Expression = "2*a", Unit = "mm", Dimension = UnitDimension.Length });
        model.Components.Add(x1);
        SchematicPersistence.SaveToFile(created.SchematicPath!, model, cellName: "NxOuter");
        string ccellPath = Path.Combine(created.CellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Parameters = [new CcellParameter { Name = "a", DefaultExpression = "1", Unit = "mm", Dimension = UnitDimension.Length,
                                                 Minimum = 0.5e-3, Maximum = 3e-3 }];
        CellPersistence.SaveToFile(ccellPath, ccell);

        // The inner line is 5 + Lmid + 7 mm long; Lmid = 2*a.
        long LengthAt(double a)
        {
            var at = DesignedCellLayout.At(created.CellDir, new Dictionary<string, Value> { ["a"] = new Value(a) }, ws.Root, ws.Tech, ws.TechPath);
            Assert.True(at.Layout is not null, at.Refusal);
            return Pcd6Fixture.Length(Pcd6Fixture.Box(at.Layout!, Path.Combine(created.CellDir, "layout"), "X1"));
        }
        long um = 1000;
        Assert.Equal((5 + 2 + 7) * 1000 * um, LengthAt(1e-3));
        Assert.Equal((5 + 4 + 7) * 1000 * um, LengthAt(2e-3));
    }
}
