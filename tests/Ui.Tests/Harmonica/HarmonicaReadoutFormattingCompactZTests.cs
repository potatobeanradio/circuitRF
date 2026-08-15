// ================================================================
//  HarmonicaReadoutFormattingCompactZTests.cs — R9A §4
//
//  FormatZCompact is the MXP/MXE header's own ONE-decimal impedance formatter — an argmax read off a
//  fitted RBF surface, which does not carry the three decimals every other complex row (FormatZ)
//  claims. Pins the new formatter's output and that FormatZ itself is untouched.
// ================================================================

using System.Numerics;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaReadoutFormattingCompactZTests
{
    [Fact]
    public void FormatZCompact_RoundsToOneDecimal()
        => Assert.Equal("96.3-j0.2 Ω",
            HarmonicaReadoutFormatting.FormatZCompact(new Complex(96.3312, -0.1523), ReadoutFormat.RealImaginary));

    [Fact]
    public void FormatZ_OnTheSameValue_IsStillThreeDecimals()
        => Assert.Equal("96.331-j0.152 Ω",
            HarmonicaReadoutFormatting.FormatZ(new Complex(96.3312, -0.1523), ReadoutFormat.RealImaginary));
}
