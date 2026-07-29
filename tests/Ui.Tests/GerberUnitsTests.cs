using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>R-L4c-1: 1 DBU must map to 1 output unit by literal integer copy — no scaling, no rounding.
/// At the default DbuPerMicron=1000, that is exactly the brief's own worked example: %MOMM*% (mm) +
/// %FSLAX46Y46*% (4 integer + 6 decimal digits).</summary>
public class GerberUnitsTests
{
    [Fact]
    public void Resolve_DefaultResolution_Is4Integer6Decimal()
    {
        var format = GerberUnits.Resolve(1000);
        Assert.Equal(4, format.IntegerDigits);
        Assert.Equal(6, format.DecimalDigits);
        Assert.Equal("46", format.DigitPair);
    }

    [Fact]
    public void Resolve_FinerResolution_WidensDecimalDigits_NeverRounds()
    {
        var format = GerberUnits.Resolve(10_000);
        Assert.Equal(7, format.DecimalDigits);
    }

    [Fact]
    public void Resolve_CoarserResolution_NarrowsDecimalDigits()
    {
        var format = GerberUnits.Resolve(100);
        Assert.Equal(5, format.DecimalDigits);
    }

    [Fact]
    public void Resolve_NonPowerOfTenDbuPerMicron_Throws_RatherThanSilentlyRounding()
    {
        Assert.Throws<GerberUnitsException>(() => GerberUnits.Resolve(1500));
    }

    [Fact]
    public void Resolve_ZeroOrNegativeDbuPerMicron_Throws()
    {
        Assert.Throws<GerberUnitsException>(() => GerberUnits.Resolve(0));
        Assert.Throws<GerberUnitsException>(() => GerberUnits.Resolve(-1000));
    }

    [Fact]
    public void Resolve_ExtentWithinDefaultIntegerDigits_KeepsFourDigits()
    {
        // 9999 mm at 1000 DBU/um = 9,999,000,000 DBU — comfortably inside 4 integer digits.
        var format = GerberUnits.Resolve(1000, maxAbsCoordinateDbu: 9_999_000_000L);
        Assert.Equal(4, format.IntegerDigits);
    }

    [Fact]
    public void Resolve_ExtentExceedsDefaultIntegerDigits_Widens()
    {
        // Just over 9999 mm — must widen past 4 integer digits rather than truncating.
        long overLimit = 10_000_000_000L; // 10,000 mm
        var format = GerberUnits.Resolve(1000, overLimit);
        Assert.True(format.IntegerDigits > 4);
        Assert.True(overLimit <= format.MaxAbsCoordinateDbu(1000));
    }

    [Fact]
    public void FormatCoordinate_IsLiteralIntegerCopy_NoScalingNoDecimalPoint()
    {
        var format = GerberUnits.Resolve(1000);
        Assert.Equal("1234500", format.FormatCoordinate(1_234_500));
        Assert.Equal("-1234500", format.FormatCoordinate(-1_234_500));
        Assert.Equal("0", format.FormatCoordinate(0));
    }

    [Fact]
    public void FormatDecimalMm_InsertsDecimalPointExactly_PureIntegerMath()
    {
        var format = GerberUnits.Resolve(1000); // 6 decimals
        Assert.Equal("0.500000", format.FormatDecimalMm(500_000));
        Assert.Equal("1.234500", format.FormatDecimalMm(1_234_500));
        Assert.Equal("-0.500000", format.FormatDecimalMm(-500_000));
        Assert.Equal("0.000001", format.FormatDecimalMm(1));
    }
}
