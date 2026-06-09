using System.IO;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class CschRoundTripTests
{
    private static SchematicEditModel BuildModel()
    {
        var m = new SchematicEditModel { GridSize = 100, GridSnap = true };
        m.Components.Add(new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X            = 0, Y = 0,
            Rotation     = SymbolRotation.R0,
            Parameters   = { new EditableParameter { Name = "R", Expression = "50", ShowOnSchematic = true } },
        });
        m.Components.Add(new EditableComponent
        {
            InstanceName = "Q1",
            Symbol       = SymbolKind.FetSdd,
            X            = 600, Y = 200,
            Rotation     = SymbolRotation.R90,
            MirrorX      = true,
            Disable      = DisableState.Open,
        });
        m.Wires.Add(new EditableWire
        {
            Points = { (0, 0), (100, 0), (100, 200) },
        });
        m.NetLabels.Add(new EditableNetLabel { X = 0, Y = 0, Name = "VDD" });
        m.Dots.Add(new EditableDot { X = 100, Y = 0 });
        m.ViewPanX = 10.0;
        m.ViewPanY = 20.0;
        m.ViewZoom = 1.5;
        return m;
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var original = BuildModel();
        string json  = SchematicPersistence.Serialize(original, "TestCell", 10, 20, 1.5);

        var (restored, view, cellName) = SchematicPersistence.Deserialize(json);

        Assert.Equal("TestCell", cellName);
        Assert.Equal(10.0, view.PanX, 1e-9);
        Assert.Equal(20.0, view.PanY, 1e-9);
        Assert.Equal(1.5,  view.Zoom, 1e-9);

        Assert.Equal(original.Components.Count, restored.Components.Count);
        Assert.Equal(original.Wires.Count,      restored.Wires.Count);
        Assert.Equal(original.NetLabels.Count,  restored.NetLabels.Count);
        Assert.Equal(original.Dots.Count,       restored.Dots.Count);

        var r1 = restored.Components.First(c => c.InstanceName == "R1");
        Assert.Equal(SymbolKind.Resistor,   r1.Symbol);
        Assert.Equal(0.0,                   r1.X, 1e-9);
        Assert.Equal(0.0,                   r1.Y, 1e-9);
        Assert.Equal(SymbolRotation.R0,     r1.Rotation);
        Assert.False(r1.MirrorX);
        Assert.Single(r1.Parameters);
        Assert.Equal("R",     r1.Parameters[0].Name);
        Assert.Equal("50",    r1.Parameters[0].Expression);
        Assert.True(r1.Parameters[0].ShowOnSchematic);

        var q1 = restored.Components.First(c => c.InstanceName == "Q1");
        Assert.Equal(SymbolKind.FetSdd,   q1.Symbol);
        Assert.Equal(SymbolRotation.R90,  q1.Rotation);
        Assert.True(q1.MirrorX);
        Assert.Equal(DisableState.Open,   q1.Disable);

        var wire = restored.Wires[0];
        Assert.Equal(3, wire.Points.Count);
        Assert.Equal((0.0, 0.0),   wire.Points[0]);
        Assert.Equal((100.0, 0.0), wire.Points[1]);

        var label = restored.NetLabels[0];
        Assert.Equal("VDD", label.Name);
    }

    [Fact]
    public void SaveToFile_LoadFromFile_RoundTrip()
    {
        var original = BuildModel();
        var tmp      = Path.GetTempFileName();
        try
        {
            SchematicPersistence.SaveToFile(tmp, original, "TestCell", 10, 20, 1.5);
            var (restored, view, cellName) = SchematicPersistence.LoadFromFile(tmp);

            Assert.Equal("TestCell", cellName);
            Assert.Equal(original.Components.Count, restored.Components.Count);
            Assert.Equal(original.Wires.Count,      restored.Wires.Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void SerializeSelection_DeserializeSelection_RoundTrip()
    {
        var m = BuildModel();
        var json = SchematicPersistence.SerializeSelection(
            m.Components, m.Wires, m.CanvasObjects);

        var (comps, wires, cobjs, srcGrid) = SchematicPersistence.DeserializeSelection(json);
        Assert.Equal(m.Components.Count, comps.Count);
        Assert.Equal(m.Wires.Count,      wires.Count);
        Assert.Equal(100.0, srcGrid, 1e-9);
    }

    [Fact]
    public void WrongVersion_ThrowsInvalidDataException()
    {
        var m    = BuildModel();
        string j = SchematicPersistence.Serialize(m, "Test", 0, 0, 1);
        string broken = j.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");
        Assert.Throws<InvalidDataException>(() => SchematicPersistence.Deserialize(broken));
    }

    // ── Layer 5: fine authoring grid ─────────────────────────────────────────

    [Fact]
    public void AuthorGridDivisor_DefaultIs20_AndRoundTrips()
    {
        var m = new SchematicEditModel { GridSize = 100, GridSnap = true };
        Assert.Equal(20, m.AuthorGridDivisor);
        Assert.Equal(5.0, m.AuthorGridSize, 1e-9);

        string json = SchematicPersistence.Serialize(m, "Test", 0, 0, 1);
        var (restored, _, _) = SchematicPersistence.Deserialize(json);
        Assert.Equal(20, restored.AuthorGridDivisor);
        Assert.Equal(5.0, restored.AuthorGridSize, 1e-9);
    }

    [Fact]
    public void AuthorGridDivisor_Custom_RoundTrips()
    {
        var m = new SchematicEditModel { GridSize = 100, GridSnap = true, AuthorGridDivisor = 10 };
        Assert.Equal(10.0, m.AuthorGridSize, 1e-9);

        string json = SchematicPersistence.Serialize(m, "Test", 0, 0, 1);
        var (restored, _, _) = SchematicPersistence.Deserialize(json);
        Assert.Equal(10, restored.AuthorGridDivisor);
        Assert.Equal(10.0, restored.AuthorGridSize, 1e-9);
    }

    [Fact]
    public void AbsentAuthorGridDivisor_InJson_DefaultsTo20()
    {
        var m    = new SchematicEditModel { GridSize = 100, GridSnap = true };
        string j = SchematicPersistence.Serialize(m, "Test", 0, 0, 1);
        // Strip the field to simulate an old .csch file that predates Layer 5.
        string old = j.Replace("\"AuthorGridDivisor\": 20,", "")
                      .Replace(",\n  \"AuthorGridDivisor\": 20", "")
                      .Replace(",\"AuthorGridDivisor\":20", "");
        var (restored, _, _) = SchematicPersistence.Deserialize(old);
        Assert.Equal(20, restored.AuthorGridDivisor);
    }

    [Fact]
    public void SnapToAuthorGrid_SnapsToFinePitch()
    {
        // With P=100, k=20: p=5. SnapToAuthorGrid(7) → 5; SnapToGrid(7) → 0.
        var m = new SchematicEditModel { GridSize = 100, GridSnap = true, AuthorGridDivisor = 20 };
        Assert.Equal(5.0, m.SnapToAuthorGrid(7.0),  1e-9);
        Assert.Equal(0.0, m.SnapToGrid(7.0),         1e-9);
        // Connection points are NOT on author grid (they go to P=100, not p=5).
        Assert.Equal(100.0, m.SnapToGrid(60.0),    1e-9);
        Assert.Equal(60.0,  m.SnapToAuthorGrid(60.0), 1e-9);
    }
}
