using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>Owner-reported bugs: (1) default parameters showed a unit twice (e.g. "2.9mm mm");
/// (2) defaults were hardcoded to mm regardless of the placing workspace's own technology.</summary>
public class MicrostripDefaultParameterTests
{
    [Theory]
    [InlineData(SymbolKind.Mlin, "W")]
    [InlineData(SymbolKind.MBend, "W")]
    [InlineData(SymbolKind.MTee, "W1")]
    [InlineData(SymbolKind.MCross, "W1")]
    public void DefaultParameters_LengthParams_ExpressionIsBareNumber_UnitIsSeparate(SymbolKind kind, string paramName)
    {
        var dp = ComponentTypeRegistry.DefaultParameters(kind, 0).First(p => p.Name == paramName);
        Assert.Equal("mm", dp.Unit);
        Assert.DoesNotContain("mm", dp.Expression); // the double-unit bug: Expression must never itself carry the unit
        Assert.True(double.TryParse(dp.Expression, out _));
    }

    [Fact]
    public void ApplyTechnologyLengthUnit_PcbTechnology_ConvertsMmDefaultsToMil()
    {
        var parameters = new List<EditableParameter>
        {
            new() { Name = "W", Expression = "2.9", Unit = "mm", Dimension = UnitDimension.Length },
            new() { Name = "L", Expression = "10",  Unit = "mm", Dimension = UnitDimension.Length },
        };

        MicrostripSubstrateInjection.ApplyTechnologyLengthUnit(parameters, StarterTechnologies.Pcb2Layer());

        var w = parameters.First(p => p.Name == "W");
        Assert.Equal("mil", w.Unit);
        Assert.Equal(114.1732, double.Parse(w.Expression), 3); // 2.9mm / 0.0254
    }

    [Fact]
    public void ApplyTechnologyLengthUnit_MmicTechnology_ConvertsMmDefaultsToMicrons()
    {
        var parameters = new List<EditableParameter>
        {
            new() { Name = "W", Expression = "2.9", Unit = "mm", Dimension = UnitDimension.Length },
        };

        MicrostripSubstrateInjection.ApplyTechnologyLengthUnit(parameters, StarterTechnologies.MmicGaAs());

        var w = parameters.First(p => p.Name == "W");
        Assert.Equal("µm", w.Unit);
        Assert.Equal(2900.0, double.Parse(w.Expression), 3);
    }

    [Fact]
    public void ApplyTechnologyLengthUnit_NoTechnology_LeavesMmDefaultsUnchanged()
    {
        var parameters = new List<EditableParameter>
        {
            new() { Name = "W", Expression = "2.9", Unit = "mm", Dimension = UnitDimension.Length },
        };

        MicrostripSubstrateInjection.ApplyTechnologyLengthUnit(parameters, (Technology?)null);

        var w = parameters.First(p => p.Name == "W");
        Assert.Equal("mm", w.Unit);
        Assert.Equal("2.9", w.Expression);
    }

    [Fact]
    public void ApplyTechnologyLengthUnit_NonLengthParameters_AreUntouched()
    {
        var parameters = new List<EditableParameter>
        {
            new() { Name = "Angle", Expression = "90", Unit = "deg", Dimension = UnitDimension.Angle },
            new() { Name = "Mitered", Expression = "0", Unit = "", Dimension = UnitDimension.None },
        };

        MicrostripSubstrateInjection.ApplyTechnologyLengthUnit(parameters, StarterTechnologies.Pcb2Layer());

        Assert.Equal("deg", parameters[0].Unit);
        Assert.Equal("90", parameters[0].Expression);
        Assert.Equal("", parameters[1].Unit);
    }
}
