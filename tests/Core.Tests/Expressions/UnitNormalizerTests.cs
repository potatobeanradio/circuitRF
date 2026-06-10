using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Gate tests for <see cref="UnitNormalizer.ToEngineUnit"/>.
/// Verifies glyph→ASCII normalization and that normalized units resolve through <see cref="Units.Scale"/>.
/// </summary>
public class UnitNormalizerTests
{
    // ── Omega (Ω → Ohm) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ω",  "Ohm")]
    [InlineData("mΩ", "mOhm")]
    [InlineData("kΩ", "kOhm")]
    [InlineData("MΩ", "MOhm")]
    [InlineData("GΩ", "GOhm")]
    public void Omega_NormalizesToOhm(string input, string expected)
        => Assert.Equal(expected, UnitNormalizer.ToEngineUnit(input));

    // ── Micro sign (µ U+00B5 and μ U+03BC → u) ──────────────────────────────────

    [Theory]
    [InlineData("µH", "uH")]
    [InlineData("µF", "uF")]
    [InlineData("µV", "uV")]
    [InlineData("µA", "uA")]
    [InlineData("µW", "uW")]
    [InlineData("µm", "um")]
    public void MicroSign_NormalizesToU(string input, string expected)
        => Assert.Equal(expected, UnitNormalizer.ToEngineUnit(input));

    [Theory]
    [InlineData("μH", "uH")]   // Greek mu (U+03BC) — defensive
    [InlineData("μF", "uF")]
    public void GreekMu_NormalizesToU(string input, string expected)
        => Assert.Equal(expected, UnitNormalizer.ToEngineUnit(input));

    // ── Already-ASCII units pass through unchanged ───────────────────────────────

    [Theory]
    [InlineData("nH",  "nH")]
    [InlineData("pF",  "pF")]
    [InlineData("nF",  "nF")]
    [InlineData("Hz",  "Hz")]
    [InlineData("kHz", "kHz")]
    [InlineData("MHz", "MHz")]
    [InlineData("GHz", "GHz")]
    [InlineData("deg", "deg")]
    [InlineData("rad", "rad")]
    [InlineData("mil", "mil")]
    [InlineData("mm",  "mm")]
    [InlineData("mH",  "mH")]
    [InlineData("mF",  "mF")]
    public void AsciiUnits_PassThrough(string input, string expected)
        => Assert.Equal(expected, UnitNormalizer.ToEngineUnit(input));

    // ── None / empty → empty ────────────────────────────────────────────────────

    [Theory]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    public void NoneOrEmpty_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, UnitNormalizer.ToEngineUnit(input));

    // ── Table-uncovered units emit as-is (no crash) ──────────────────────────────

    [Theory]
    [InlineData("dBm", "dBm")]
    [InlineData("V",   "V")]
    [InlineData("A",   "A")]
    [InlineData("W",   "W")]
    [InlineData("kV",  "kV")]
    [InlineData("cm",  "cm")]
    public void TableUncoveredUnits_EmitAsIs(string input, string expected)
        => Assert.Equal(expected, UnitNormalizer.ToEngineUnit(input));

    // ── Normalized units resolve through Units.Scale ────────────────────────────

    // mΩ→mOhm normalizes correctly but mOhm is not in the engine table (table-uncovered).
    // It is tested in Omega_NormalizesToOhm (glyph substitution) not here.
    [Theory]
    [InlineData("µF",  "uF",   1e-6)]
    [InlineData("kΩ",  "kOhm", 1e3)]
    [InlineData("Ω",   "Ohm",  1.0)]
    [InlineData("GΩ",  "GOhm", 1e9)]
    [InlineData("µH",  "uH",   1e-6)]
    [InlineData("MΩ",  "MOhm", 1e6)]
    public void NormalizedUnit_ResolvesInUnitsTable(string editorUnit, string expectedUnit, double expectedScale)
    {
        var normalized = UnitNormalizer.ToEngineUnit(editorUnit);
        Assert.Equal(expectedUnit, normalized);
        var scale = Units.Scale(normalized);
        Assert.NotNull(scale);
        Assert.Equal(expectedScale, scale!.Value, precision: 10);
    }
}
