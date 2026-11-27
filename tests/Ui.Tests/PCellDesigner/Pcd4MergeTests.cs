// Owner: PCD-4 (brief-pcd-4 §3).

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-4 R-pcd4-1 and R-pcd4-2: the text view merged by designator, and a part dropped on a pin.</summary>
public sealed class Pcd4MergeTests
{
    private const string Line =
        "Port:P1 in 0 Num=1 Z=50 Ohm\nMLIN:TL1 in mid W=100 um L=1 mm\nMLIN:TL2 mid out W=100 um L=2 mm\n"
      + "Port:P2 out 0 Num=2 Z=50 Ohm\n";

    internal static SchematicEditModel Drawn(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return NetlistSchematic.Build(lib, tb).Schematic!;
    }

    internal static TestBench Extracted(SchematicEditModel m) => new CnlReader().Read(SchematicCircuit.CnlTextOf(m, "cell")).TestBench;

    private static EditableComponent Part(SchematicEditModel m, string name) => m.Components.Single(c => c.InstanceName == name);

    /// <summary>The schematic's own text with TL2's line replaced.</summary>
    private static string WithTl2(SchematicEditModel m, string line)
    {
        string text = SchematicCircuit.CnlTextOf(m, "cell");
        Assert.Contains("MLIN:TL2 ", text);
        return string.Join("\n", text.Split('\n').Select(l => l.StartsWith("MLIN:TL2 ", StringComparison.Ordinal) ? line : l));
    }

    [Fact]
    public void ParametersMergeByDesignatorAndTheWiresAreKept()
    {
        var m = Drawn(Line);
        string before = Part(m, "TL2").Parameters.Single(p => p.Name == "L").Expression;

        var r = CellNetlistMerge.Merge(m, WithTl2(m, "MLIN:TL2 mid out W=100 um L=3 mm"));

        Assert.Empty(r.Refusals);
        var tl2 = Part(r.Schematic!, "TL2").Parameters.Single(p => p.Name == "L");
        Assert.Equal(("3", "mm"), (tl2.Expression, tl2.Unit));
        Assert.Equal(m.Wires.Select(w => w.Points.ToList()), r.Schematic!.Wires.Select(w => w.Points.ToList()));
        Assert.Equal(before, Part(m, "TL2").Parameters.Single(p => p.Name == "L").Expression);   // current untouched
    }

    [Fact]
    public void AddedAndRemovedPartsRedrawTheWiresAndKeptPartsKeepTheirPlaceAndFields()
    {
        var m = Drawn(Line);
        var tl1 = Part(m, "TL1");
        var offset = new OffsetPlacement { Anchor = "P1.1", Dx = "g", Unit = "um" };
        tl1.Offset = offset;
        tl1.ShowTypeLabel = false;
        tl1.LabelOffsets.Add((15, -5));
        var at = (tl1.X, tl1.Y, tl1.Rotation);

        var r = CellNetlistMerge.Merge(m, WithTl2(m, "R:R1 mid out R=50 Ohm"));

        Assert.Empty(r.Refusals);
        var s = r.Schematic!;
        Assert.DoesNotContain(s.Components, c => c.InstanceName == "TL2");
        var kept = Part(s, "TL1");
        Assert.Equal(at, (kept.X, kept.Y, kept.Rotation));
        Assert.Equal(offset, kept.Offset);
        Assert.False(kept.ShowTypeLabel);
        Assert.Equal((15.0, -5.0), Assert.Single(kept.LabelOffsets));
        // The drawing is the text's circuit: R1 sits between TL1's far end and the second port.
        var x = Extracted(s).Instances.ToDictionary(i => i.InstanceName);
        Assert.Equal(x["TL1"].NetBindings[1], x["R1"].NetBindings[0]);
        Assert.Equal(x["P2"].NetBindings[0], x["R1"].NetBindings[1]);
        Assert.Contains(m.Components, c => c.InstanceName == "TL2");   // current untouched
    }

    [Fact]
    public void ATypeChangeIsRefused()
    {
        var m = Drawn(Line);
        var r = CellNetlistMerge.Merge(m, WithTl2(m, "R:TL2 mid out R=50 Ohm"));

        Assert.Null(r.Schematic);
        Assert.Contains(r.Refusals, s => s.Contains("TL2") && s.Contains("MLIN"));
    }
}
