using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>Gate tests for Units.BaseUnit (brief-sweep-axis-marker-units Part B).</summary>
public class BaseUnitTests
{
    // T1 — frequency prefixes → Hz
    [Theory]
    [InlineData("kHz", "Hz")]
    [InlineData("MHz", "Hz")]
    [InlineData("GHz", "Hz")]
    [InlineData("THz", "Hz")]
    public void BaseUnit_FrequencyPrefixes_ReturnsHz(string unit, string expected)
        => Assert.Equal(expected, Units.BaseUnit(unit));

    // T2 — Hz is already base
    [Fact]
    public void BaseUnit_Hz_PassesThrough()
        => Assert.Equal("Hz", Units.BaseUnit("Hz"));

    // T3 — capacitance prefixes → F
    [Theory]
    [InlineData("pF", "F")]
    [InlineData("nF", "F")]
    [InlineData("uF", "F")]
    [InlineData("mF", "F")]
    [InlineData("fF", "F")]
    public void BaseUnit_CapacitancePrefixes_ReturnsF(string unit, string expected)
        => Assert.Equal(expected, Units.BaseUnit(unit));

    // T4 — voltage prefixes → V
    [Theory]
    [InlineData("mV", "V")]
    [InlineData("uV", "V")]
    public void BaseUnit_VoltagePrefixes_ReturnsV(string unit, string expected)
        => Assert.Equal(expected, Units.BaseUnit(unit));

    // T5 — resistance prefixes → Ohm
    [Theory]
    [InlineData("kOhm", "Ohm")]
    [InlineData("MOhm", "Ohm")]
    public void BaseUnit_ResistancePrefixes_ReturnsOhm(string unit, string expected)
        => Assert.Equal(expected, Units.BaseUnit(unit));

    // T6 — already-base units pass through unchanged
    [Theory]
    [InlineData("V")]
    [InlineData("Ohm")]
    [InlineData("A")]
    [InlineData("dBm")]
    [InlineData("F")]
    [InlineData("H")]
    public void BaseUnit_AlreadyBase_PassesThrough(string unit)
        => Assert.Equal(unit, Units.BaseUnit(unit));

    // T7 — empty / null pass through
    [Fact]
    public void BaseUnit_Empty_ReturnsEmpty()
        => Assert.Equal("", Units.BaseUnit(""));

    // T8 — unknown unit passes through unchanged
    [Fact]
    public void BaseUnit_Unknown_PassesThrough()
        => Assert.Equal("frobble", Units.BaseUnit("frobble"));
}
