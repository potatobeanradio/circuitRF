// Owner: PCD-4 (brief-pcd-4 §3).

using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-4 R-pcd4-2: a part dropped on a free pin joins its net; on a joined pin it is refused.</summary>
[Collection(PCellResolverCollection.Name)]
public sealed class Pcd4PinDropTests
{
    private static EditableComponent PaletteMlin()
    {
        var c = new EditableComponent { Symbol = SymbolKind.Mlin, InstanceName = "TL1" };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            c.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                                                     ShowOnSchematic = d.ShowOnSchematic, Dimension = d.Dimension });
        return c;
    }

    [Fact]
    public void ADropOnAFreePinJoinsItsNetAndOnAJoinedPinIsRefused()
    {
        // TL1's far end is open.
        var m = Pcd4MergeTests.Drawn("Port:P1 in 0 Num=1 Z=50 Ohm\nMLIN:TL1 in end W=100 um L=1 mm\n");

        var r = PinDrop.Apply(m, PaletteMlin(), 1, "TL1", 2);

        Assert.Null(r.Refusal);
        string name = ComponentTypeRegistry.InstancePrefix(SymbolKind.Mlin) + "1";   // the next free one for the prefix
        Assert.Equal(name, r.InstanceName);
        var x = Pcd4MergeTests.Extracted(r.Schematic!).Instances.ToDictionary(i => i.InstanceName);
        Assert.Equal(x["TL1"].NetBindings[1], x[name].NetBindings[0]);
        Assert.DoesNotContain(x.Values, i => i.InstanceName != name && i.NetBindings.Contains(x[name].NetBindings[1]));
        Assert.DoesNotContain(m.Components, c => c.InstanceName == name);   // current untouched

        var refused = PinDrop.Apply(m, PaletteMlin(), 1, "TL1", 1);
        Assert.Null(refused.Schematic);
        Assert.Contains("P1.1", refused.Refusal!);
    }
}
