using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for Brief G — Pin component and cell port mapping.
///
/// Gate 1: Pin can be placed and wired (covered by visual tests + placement logic).
/// Gate 2: CellPorts derived from Pin instances, ordered by Num.
/// Gate 3: Pin instances are NOT emitted into TestBench.Instances.
/// Gate 4: Conformance — duplicate/gap Num → conflicts.
/// Gate 5: Differential ports (Plus/Minus pair → "{base}+"/"{base}-").
/// </summary>
public class NetExtractorPinTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EditableComponent MakePin(
        int num, string name = "", string polarity = "",
        double x = 0, double y = 0)
    {
        var comp = new EditableComponent
        {
            InstanceName = $"Pin{num}",
            Symbol       = SymbolKind.Pin,
            X            = x,
            Y            = y,
        };
        comp.Parameters.Add(new EditableParameter { Name = "Num",      Expression = num.ToString() });
        comp.Parameters.Add(new EditableParameter { Name = "Name",     Expression = name });
        comp.Parameters.Add(new EditableParameter { Name = "Polarity", Expression = polarity });
        return comp;
    }

    private static EditableComponent MakeResistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // ── Gate 2: CellPorts populated from Pin instances ───────────────────────

    [Fact]
    public void TwoPins_CellPorts_OrderedByNum()
    {
        // Pin(Num=1, Name="in") at (0,200) → port at (0,0)
        // Pin(Num=2, Name="out") at (400,200) → port at (400,0)
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "in",  y: 200));
        model.Components.Add(MakePin(2, "out", x: 400, y: 200));

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.CellPorts.Count);
        Assert.Equal("in",  result.CellPorts[0]);
        Assert.Equal("out", result.CellPorts[1]);
    }

    [Fact]
    public void Pin_WithoutName_DefaultsToP_Num()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(3)); // Num=3, no name

        var result = NetExtractor.Extract(model);

        Assert.Single(result.CellPorts);
        Assert.Equal("P3", result.CellPorts[0]);
    }

    [Fact]
    public void Pin_NetNamedAfterPort()
    {
        // Pin port is at local (100,0); Pin at (X,Y) connects at world (X+100,Y).
        // Resistor port0 is at local (0,-200); Resistor at (X,Y) → port0 at world (X,Y-200).
        // Pin at (-100,200): port at (0,200). R1 at (0,400): port0 at (0,200). They share (0,200).
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "in", x: -100, y: 200));  // port at (0, 200)
        model.Components.Add(MakeResistor("R1", 0, 400));          // port0 at (0, 200)

        var result = NetExtractor.Extract(model);

        // The Pin-connected net should be named "in" (the port name), not an auto-name.
        var r1Inst = result.TestBench.Instances.First(i => i.InstanceName == "R1");
        Assert.Equal("in", r1Inst.NetBindings[0]);
    }

    // ── Gate 3: Pin instances NOT emitted into TestBench ─────────────────────

    [Fact]
    public void PinInstances_NotInTestBench()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "in",  y: 200));
        model.Components.Add(MakePin(2, "out", x: 400, y: 200));
        model.Components.Add(MakeResistor("R1", 200, 200));

        var result = NetExtractor.Extract(model);

        // No Pin instances in the emitted TestBench.
        var pinInsts = result.TestBench.Instances
            .Where(i => i.Reference.Equals("Pin", System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(pinInsts);
        // R1 is still there.
        Assert.Single(result.TestBench.Instances, i => i.InstanceName == "R1");
    }

    [Fact]
    public void NoPins_CellPorts_IsEmpty()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakeResistor("R1", 0, 200));

        var result = NetExtractor.Extract(model);

        Assert.Empty(result.CellPorts);
    }

    // ── Gate 4: Conformance — duplicate/gap Num → conflicts ──────────────────

    [Fact]
    public void DuplicatePinNum_AddsConflict()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "a", y: 0));
        model.Components.Add(MakePin(1, "b", x: 200, y: 0)); // duplicate Num=1

        var result = NetExtractor.Extract(model);

        Assert.Contains(result.Conflicts, c => c.Contains("Duplicate") && c.Contains("Num=1"));
    }

    [Fact]
    public void GapInPinNums_AddsConflict()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "a", y: 0));
        model.Components.Add(MakePin(3, "c", x: 400, y: 0)); // Num=2 missing

        var result = NetExtractor.Extract(model);

        Assert.Contains(result.Conflicts, c => c.Contains("Num=2") && c.Contains("missing"));
    }

    [Fact]
    public void NoDuplicatesOrGaps_NoConflicts()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "a", y: 0));
        model.Components.Add(MakePin(2, "b", x: 200, y: 0));
        model.Components.Add(MakePin(3, "c", x: 400, y: 0));

        var result = NetExtractor.Extract(model);

        var pinConflicts = result.Conflicts
            .Where(c => c.Contains("Num=") || c.Contains("Duplicate") || c.Contains("missing"))
            .ToList();
        Assert.Empty(pinConflicts);
    }

    // ── Gate 5: Differential port pair ──────────────────────────────────────

    [Fact]
    public void DifferentialPair_CellPorts_PlusAndMinus()
    {
        // Pin(Num=1, Polarity=Plus) + Pin(Num=1, Polarity=Minus) → "P1+" and "P1-"
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "", "Plus",  y: 0));
        model.Components.Add(MakePin(1, "", "Minus", x: 200, y: 0));

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.CellPorts.Count);
        Assert.Equal("P1+", result.CellPorts[0]);
        Assert.Equal("P1-", result.CellPorts[1]);
    }

    [Fact]
    public void DifferentialPair_WithName_UsesNameAsBases()
    {
        // Pin(Num=1, Name="rf", Polarity=Plus) + Pin(Num=1, Name="rf", Polarity=Minus)
        // → "rf+" and "rf-"
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "rf", "Plus",  y: 0));
        model.Components.Add(MakePin(1, "rf", "Minus", x: 200, y: 0));

        var result = NetExtractor.Extract(model);

        Assert.Equal(2, result.CellPorts.Count);
        Assert.Equal("rf+", result.CellPorts[0]);
        Assert.Equal("rf-", result.CellPorts[1]);
    }

    [Fact]
    public void MixedSingleEndedAndDifferential_CellPorts()
    {
        // Port 1: single-ended "in"
        // Port 2: differential "rf+" and "rf-"
        // Expected: ["in", "rf+", "rf-"]
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "in",  "",      y: 0));
        model.Components.Add(MakePin(2, "rf",  "Plus",  x: 200, y: 0));
        model.Components.Add(MakePin(2, "rf",  "Minus", x: 400, y: 0));

        var result = NetExtractor.Extract(model);

        Assert.Equal(3, result.CellPorts.Count);
        Assert.Equal("in",  result.CellPorts[0]);
        Assert.Equal("rf+", result.CellPorts[1]);
        Assert.Equal("rf-", result.CellPorts[2]);
    }

    // ── Net-name priority: Pin beats coincident label ───────────────────────────

    [Fact]
    public void Pin_WithCoincidentLabel_PinNameWins()
    {
        // Pin at (-100,200): port at (0,200). R1 at (0,400): port0 at (0,200).
        // Label "mylabel" also at (0,200). Pin owns the net identity — no conflict.
        var model = new SchematicEditModel();
        model.Components.Add(MakePin(1, "in", x: -100, y: 200));  // port at (0,200)
        model.Components.Add(MakeResistor("R1", 0, 400));           // port0 at (0,200)
        model.NetLabels.Add(new EditableNetLabel { Name = "mylabel", X = 0, Y = 200 });

        var result = NetExtractor.Extract(model);

        var r1Inst = result.TestBench.Instances.First(i => i.InstanceName == "R1");
        Assert.Equal("in", r1Inst.NetBindings[0]);  // Pin wins over label
        Assert.Equal("in", result.CellPorts[0]);     // interface port correctly named
        Assert.DoesNotContain(result.Conflicts, c => c.Contains("mylabel") || c.Contains("conflict"));
    }

    [Fact]
    public void Label_WithoutPin_NamesNet()
    {
        // Same topology as Pin_WithCoincidentLabel_PinNameWins but no Pin present.
        // Label wins the fallthrough when no Pin is on the net.
        var model = new SchematicEditModel();
        model.Components.Add(MakeResistor("R1", 0, 400));      // port0 at (0,200)
        model.NetLabels.Add(new EditableNetLabel { Name = "mylabel", X = 0, Y = 200 });

        var result = NetExtractor.Extract(model);

        var r1Inst = result.TestBench.Instances.First(i => i.InstanceName == "R1");
        Assert.Equal("mylabel", r1Inst.NetBindings[0]); // label wins with no Pin
        Assert.Empty(result.CellPorts);
    }

    [Fact]
    public void TwoDifferentLabels_StillConflict()
    {
        // Two labels with different names on the same wired net (no Pin) → conflict still fired.
        // Regression guard: reordering Pin vs label must not drop the label-vs-label conflict check.
        var model = new SchematicEditModel();
        model.Wires.Add(Wire((0, 200), (0, 600)));
        model.NetLabels.Add(new EditableNetLabel { Name = "foo", X = 0, Y = 200 });
        model.NetLabels.Add(new EditableNetLabel { Name = "bar", X = 0, Y = 600 });

        var result = NetExtractor.Extract(model);

        Assert.Contains(result.Conflicts, c => c.Contains("foo") && c.Contains("bar"));
    }

    [Fact]
    public void Pin_OnGroundNet_Warns()
    {
        // Ground at (0,0): port at (0,0). Pin at (-100,0): port at (-100+100,0)=(0,0).
        // Same world coordinate → Pin is on the ground net.
        // Ground still wins ("0"); a "tied to ground" conflict is emitted.
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "GND1",
            Symbol       = SymbolKind.Ground,
            X            = 0,
            Y            = 0,
        });
        model.Components.Add(MakePin(1, "in", x: -100, y: 0)); // port at (0,0)

        var result = NetExtractor.Extract(model);

        Assert.Contains(result.Conflicts, c => c.Contains("tied to ground"));
    }
}
