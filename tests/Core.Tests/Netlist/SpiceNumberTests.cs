using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Numeric literals in the SPICE dialect.
///
/// <para><b>Why this has its own file and this many tests.</b> Every failure available here is a
/// wrong NUMBER, not an error: the value parses, the component stamps, the solve converges, and the
/// answer is off by a power of ten that nothing on screen accounts for. There is no downstream check
/// that catches it.</para>
/// </summary>
public sealed class SpiceNumberTests
{
    private static double Parse(string s)
    {
        Assert.True(SpiceNumber.TryParse(s, out double v), $"'{s}' should read as a number");
        return v;
    }

    /// <summary>
    /// Relative, because most of these values are around 1e-12 and an absolute decimal-places
    /// assertion at that magnitude passes for anything at all — including zero.
    /// </summary>
    private static void AssertClose(double expected, double actual, double rel = 1e-12)
        => Assert.True(Math.Abs(actual - expected) <= rel * Math.Abs(expected),
               $"expected {expected:G17}, got {actual:G17}");

    // ── the prefix that disagrees with SI ─────────────────────────────────────

    /// <summary>
    /// The one that silently corrupts. circuitRF's own table is SI and case-sensitive — <c>M</c> is
    /// mega — while this dialect is case-insensitive and <c>M</c> is milli in either case, with mega
    /// spelled <c>MEG</c>. A capacitance written <c>1M</c> read through the SI table is 10⁹ times too
    /// large and still simulates.
    /// </summary>
    [Fact]
    public void M_IsMilli_AndMegIsMega_WhicheverWayTheyAreSpelled()
    {
        AssertClose(1e-3, Parse("1M"));
        AssertClose(1e-3, Parse("1m"));
        AssertClose(1e6,  Parse("1MEG"));
        AssertClose(1e6,  Parse("1meg"));
        AssertClose(1e6,  Parse("1Meg"));

        // Asserted as a ratio too, so the test states the SIZE of the mistake it exists to prevent.
        Assert.Equal(1e9, Parse("1MEG") / Parse("1M"), 0);
    }

    /// <summary>
    /// <c>MIL</c> also begins with <c>M</c>. Matching a single character first would read it as
    /// milli and carry on through the remaining letters as decoration.
    /// </summary>
    [Fact]
    public void Mil_IsNotReadAsMilli()
    {
        AssertClose(25.4e-6, Parse("1mil"));
        AssertClose(2540e-6, Parse("100MIL"));
    }

    [Theory]
    [InlineData("2T",   2e12)]
    [InlineData("3g",   3e9)]
    [InlineData("4K",   4e3)]
    [InlineData("5u",   5e-6)]
    [InlineData("6N",   6e-9)]
    [InlineData("7p",   7e-12)]
    [InlineData("8f",   8e-15)]
    [InlineData("1.5",  1.5)]
    [InlineData("-2.5n", -2.5e-9)]
    [InlineData(".5p",  0.5e-12)]
    public void ThePrefixTable(string token, double expected)
        => AssertClose(expected, Parse(token));

    // ── trailing text ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1kohm", 1e3)]
    [InlineData("2.5pF", 2.5e-12)]
    [InlineData("10ohm", 10.0)]
    [InlineData("47uF",  47e-6)]
    public void TextAfterThePrefixIsDecoration(string token, double expected)
        => AssertClose(expected, Parse(token));

    /// <summary>
    /// The dialect's own sharp edge, kept rather than smoothed: <c>F</c> is the femto prefix, so a
    /// capacitance written <c>1F</c> is 1e-15 and not one farad. A reader that "fixed" this would
    /// disagree with every file it is meant to read.
    /// </summary>
    [Fact]
    public void ABareF_IsFemto_NotFarad()
        => AssertClose(1e-15, Parse("1F"));

    // ── exponents ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An exponent must win over the prefix table, or <c>1e-12</c> reads as <c>1</c> followed by
    /// something unrecognised.
    /// </summary>
    [Theory]
    [InlineData("1e-12", 1e-12)]
    [InlineData("1E5",   1e5)]
    [InlineData("2.5e3", 2500.0)]
    [InlineData("1e-12F", 1e-12)]     // exponent first, then a unit letter as decoration
    public void ExponentsAreReadAsExponents(string token, double expected)
        => AssertClose(expected, Parse(token));

    // ── what is NOT a number ──────────────────────────────────────────────────

    /// <summary>
    /// The answer this predicate gives is what tells a component's VALUE from the name of a model
    /// card, so a model name that read as a number would become a resistance.
    /// </summary>
    [Theory]
    [InlineData("rmod")]
    [InlineData("nmos_lv")]
    [InlineData("{w*2}")]
    [InlineData("")]
    [InlineData("_x")]
    public void NotANumber(string token)
        => Assert.False(SpiceNumber.TryParse(token, out _));

    // ── inside expressions ────────────────────────────────────────────────────

    /// <summary>
    /// The regression for the trailer rule. Admitting <c>/</c> into the run of characters that may
    /// follow a prefix — so that a unit like <c>F/m</c> could be swallowed whole — eats the division
    /// in <c>1/2</c> and yields <c>12</c>: a wrong number out of a valid expression.
    /// </summary>
    [Fact]
    public void DivisionSurvivesLiteralRewriting()
        => Assert.Equal("1/2", SpiceNumber.NormaliseLiterals("1/2"));

    /// <summary>An identifier that ends in digits must not come apart into a name and a number.</summary>
    [Theory]
    [InlineData("r1*2",     "r1*2")]
    [InlineData("2*r1",     "2*r1")]
    [InlineData("w1+w2",    "w1+w2")]
    public void IdentifiersAreLeftAlone(string expr, string expected)
        => Assert.Equal(expected, SpiceNumber.NormaliseLiterals(expr));

    [Theory]
    [InlineData("2*1k",       "2*1000")]
    [InlineData("1u+2n",      "1E-06+2E-09")]
    [InlineData("(3meg)",     "(3000000)")]
    [InlineData("-1p",        "-1E-12")]
    public void LiteralsInsideExpressionsAreResolved(string expr, string expected)
        => Assert.Equal(expected, SpiceNumber.NormaliseLiterals(expr));
}
