using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>Gate tests for brief-schematic-housecleaning Item 5: lowercase ohm/ohms units.</summary>
public class OhmLowercaseTests
{
    // T1 — ohm scales to 1.0
    [Fact]
    public void Scale_ohm_IsOne()
        => Assert.Equal(1.0, Units.Scale("ohm"));

    // T2 — ohms scales to 1.0
    [Fact]
    public void Scale_ohms_IsOne()
        => Assert.Equal(1.0, Units.Scale("ohms"));

    // T3 — Ohm and Ohms still work (regression guard)
    [Theory]
    [InlineData("Ohm")]
    [InlineData("Ohms")]
    public void Scale_TitleCase_StillWorks(string unit)
        => Assert.Equal(1.0, Units.Scale(unit));

    // T4 — IsKnown returns true for ohm/ohms
    [Theory]
    [InlineData("ohm")]
    [InlineData("ohms")]
    public void IsKnown_LowercaseOhm(string unit)
        => Assert.True(Units.IsKnown(unit));

    // T5 — unrelated units not affected
    [Fact]
    public void Scale_Ohm_NotCaseInsensitive()
    {
        // OHM (all caps) is NOT a valid unit — the map is case-sensitive
        Assert.Null(Units.Scale("OHM"));
    }
}
