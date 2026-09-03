using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 2 (brief-L4a-gdsii-interchange.md): excess-64 base-16 real ↔ double round-trips for a table
/// of known bit patterns, in both directions. Every hex byte sequence below is hand-derived directly
/// from the GDSII spec's definition (mantissa fraction × 16^(exponent-64)), independently of
/// <see cref="GdsiiReal8"/>'s own implementation — not merely a self-consistency round trip.
/// </summary>
public class GdsiiReal8Tests
{
    public static readonly TheoryData<double, byte[]> KnownPatterns = new()
    {
        { 0.0,  [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { 1.0,  [0x41, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { -1.0, [0xC1, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { 0.5,  [0x40, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { 0.25, [0x40, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { 2.0,  [0x41, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { 4.0,  [0x41, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
        { -2.0, [0xC1, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] },
    };

    [Theory]
    [MemberData(nameof(KnownPatterns))]
    public void ToDouble_KnownBytes_YieldsExpectedValue(double expected, byte[] bytes)
        => Assert.Equal(expected, GdsiiReal8.ToDouble(bytes), 12);

    [Theory]
    [MemberData(nameof(KnownPatterns))]
    public void FromDouble_KnownValue_YieldsExpectedBytes(double value, byte[] expectedBytes)
        => Assert.Equal(expectedBytes, GdsiiReal8.FromDouble(value));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(0.5)]
    [InlineData(-0.5)]
    [InlineData(1e-9)]   // a typical UNITS database-unit-in-meters value (1 nm)
    [InlineData(0.001)]  // a typical UNITS user-unit-in-meters value
    [InlineData(180.0)]  // a typical ANGLE value
    [InlineData(2.5)]    // a typical MAG value
    [InlineData(123456.789)]
    [InlineData(-987.654321)]
    public void RoundTrip_ArbitraryValues_PreservesValueWithinDouble16BitPrecision(double value)
    {
        var bytes = GdsiiReal8.FromDouble(value);
        var back = GdsiiReal8.ToDouble(bytes);
        // Base-16 floating point has ~14 significant decimal digits of precision (56-bit mantissa).
        Assert.Equal(value, back, 10);
    }

    [Fact]
    public void ToDouble_RejectsWrongLength()
        => Assert.Throws<ArgumentException>(() => GdsiiReal8.ToDouble(new byte[7]));

    [Fact]
    public void WriteTo_RejectsWrongLength()
        => Assert.Throws<ArgumentException>(() => GdsiiReal8.WriteTo(new byte[9], 1.0));
}
