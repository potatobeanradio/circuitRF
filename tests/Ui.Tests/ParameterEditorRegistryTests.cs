using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class ParameterEditorRegistryTests
{
    // ── UnitOptions ───────────────────────────────────────────────────────────

    [Fact]
    public void UnitOptions_NoneFirst_AllDimensions()
    {
        foreach (UnitDimension dim in Enum.GetValues<UnitDimension>())
        {
            var opts = ComponentTypeRegistry.UnitOptions(dim);
            Assert.NotEmpty(opts);
            Assert.Equal("None", opts[0]);
        }
    }

    [Theory]
    [InlineData(UnitDimension.Resistance,  "Ω")]
    [InlineData(UnitDimension.Inductance,  "nH")]
    [InlineData(UnitDimension.Capacitance, "pF")]
    [InlineData(UnitDimension.Frequency,   "GHz")]
    [InlineData(UnitDimension.Voltage,     "V")]
    [InlineData(UnitDimension.Current,     "mA")]
    [InlineData(UnitDimension.Power,       "dBm")]
    [InlineData(UnitDimension.Length,      "mm")]
    [InlineData(UnitDimension.Angle,       "deg")]
    public void UnitOptions_ContainsExpectedUnit(UnitDimension dim, string expectedUnit)
    {
        Assert.Contains(expectedUnit, ComponentTypeRegistry.UnitOptions(dim));
    }

    [Fact]
    public void UnitOptions_None_ReturnsSingletonNone()
    {
        var opts = ComponentTypeRegistry.UnitOptions(UnitDimension.None);
        Assert.Single(opts);
        Assert.Equal("None", opts[0]);
    }

    // ── DefaultParam dimension tagging ────────────────────────────────────────

    [Fact]
    public void DefaultParameters_Resistor_HasResistanceDimension()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Resistor, 0);
        var r = Assert.Single(ps);
        Assert.Equal("R", r.Name);
        Assert.Equal(UnitDimension.Resistance, r.Dimension);
        Assert.Contains(r.Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Resistance));
    }

    [Fact]
    public void DefaultParameters_Inductor_HasInductanceDimension()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Inductor, 0);
        var p = Assert.Single(ps);
        Assert.Equal(UnitDimension.Inductance, p.Dimension);
        Assert.Contains(p.Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Inductance));
    }

    [Fact]
    public void DefaultParameters_Capacitor_HasCapacitanceDimension()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Capacitor, 0);
        var p = Assert.Single(ps);
        Assert.Equal(UnitDimension.Capacitance, p.Dimension);
        Assert.Contains(p.Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Capacitance));
    }

    [Fact]
    public void DefaultParameters_ToneSource_HasVoltageAndFrequencyDimensions()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.ToneSource, 0);
        Assert.Equal(2, ps.Count);
        Assert.Equal(UnitDimension.Voltage,   ps[0].Dimension);
        Assert.Equal(UnitDimension.Frequency, ps[1].Dimension);
        Assert.Contains(ps[0].Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Voltage));
        Assert.Contains(ps[1].Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Frequency));
    }

    [Fact]
    public void DefaultParameters_ZPort_NumPortsNone_ZijResistance()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.ZPort, 2);
        var numPorts = ps.First(p => p.Name == "NumPorts");
        Assert.Equal(UnitDimension.None, numPorts.Dimension);

        foreach (var zij in ps.Where(p => p.Name != "NumPorts"))
        {
            Assert.Equal(UnitDimension.Resistance, zij.Dimension);
            Assert.Contains(zij.Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Resistance));
        }
    }

    [Fact]
    public void DefaultParameters_Sdd_NumPortsNone()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Sdd, 2);
        var numPorts = Assert.Single(ps);
        Assert.Equal("NumPorts", numPorts.Name);
        Assert.Equal(UnitDimension.None, numPorts.Dimension);
    }

    // ── EditableParameter carries Dimension ───────────────────────────────────

    [Fact]
    public void EditableParameter_Clone_CopiesDimension()
    {
        var p = new EditableParameter { Name = "R", Expression = "50", Unit = "Ω", Dimension = UnitDimension.Resistance };
        var c = p.Clone();
        Assert.Equal(UnitDimension.Resistance, c.Dimension);
    }

    // ── .csch round-trip includes Dimension ──────────────────────────────────

    [Fact]
    public void CschRoundTrip_Dimension_Preserved()
    {
        var m = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Inductor, X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter
            { Name = "L", Expression = "10", Unit = "nH", ShowOnSchematic = true, Dimension = UnitDimension.Inductance });
        m.Components.Add(comp);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csch");
        try
        {
            SchematicPersistence.SaveToFile(path, m);
            var (loaded, _, _) = SchematicPersistence.LoadFromFile(path);
            var param = loaded.Components[0].Parameters[0];
            Assert.Equal(UnitDimension.Inductance, param.Dimension);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
