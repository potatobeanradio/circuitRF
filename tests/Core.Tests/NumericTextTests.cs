// Owner, 2026-09-21: much of the world writes one and a half as 1,5, and circuitRF used to refuse it
// in every editable field. It now accepts both spellings — on every machine, not only on a machine
// whose locale says so. This file pins the ONE rule that makes that safe.

using CircuitRF.Text;

namespace CircuitRF.Core.Tests;

/// <summary>
/// <see cref="NumericText.NormalizeDecimalSeparator"/> rewrites a comma to a point in exactly one
/// position — between two digits, at bracket depth 0, outside a string literal — and leaves every
/// other comma alone. Everywhere else the comma already has a job: separating arguments, indices, or
/// the columns of a pasted table.
/// </summary>
public class NumericTextTests
{
    /// <summary>
    /// The half a comma-decimal user actually types. Each of these was a parse error before, which is
    /// what makes accepting them a widening rather than a change of meaning.
    /// </summary>
    [Theory]
    [InlineData("1,5",        "1.5")]
    [InlineData("1,5e9",      "1.5e9")]
    [InlineData("1234,5678",  "1234.5678")]
    [InlineData("2,5 * 2",    "2.5 * 2")]
    [InlineData("-0,25",      "-0.25")]
    public void ADepthZeroCommaBetweenDigits_BecomesADecimalPoint(string typed, string expected)
        => Assert.Equal(expected, NumericText.NormalizeDecimalSeparator(typed));

    /// <summary>
    /// The boundary, and the reason the rule is stated in terms of brackets rather than "commas".
    /// Inside an argument list <c>max(1,5)</c> is irreducibly ambiguous — the larger of 1 and 5, or
    /// the single value 1.5 — so the comma stays a separator and a decimal point must be written as
    /// one. A comma inside a string literal is somebody's data and is never touched.
    /// </summary>
    [Theory]
    [InlineData("max(1,5)")]                       // argument separator
    [InlineData("if(a>1,5,7)")]                    // ditto, three arguments
    [InlineData("X[1,2]")]                         // cube index
    [InlineData("HB1.V(\"n_drain\",1,All)")]       // both — and a quoted comma
    [InlineData("V(\"a,b\")")]                     // a comma that is somebody's data
    [InlineData("a, b")]                           // not between digits
    public void EveryOtherComma_IsLeftExactlyAsTyped(string typed)
        => Assert.Equal(typed, NumericText.NormalizeDecimalSeparator(typed));

    /// <summary>
    /// A grouped number IS rewritten by the scan — <c>1,234.5</c> becomes <c>1.234.5</c>, because the
    /// comma does sit between two digits at depth 0 — and is then refused at the PARSE, which is the
    /// step that decides. Worth pinning separately so nobody reads the case above as a claim that the
    /// scan recognises grouping: it does not, and does not need to.
    /// </summary>
    [Fact]
    public void AGroupedNumber_IsRefusedAtTheParse_NotByTheScan()
    {
        Assert.Equal("1.234.5", NumericText.NormalizeDecimalSeparator("1,234.5"));
        Assert.False(NumericText.TryParseDouble("1,234.5", out _));
    }

    /// <summary>
    /// A field a person typed into takes either separator and no thousands mark in either culture:
    /// <c>1.234</c> is one-point-two-three-four here, on every machine, and <c>1,234</c> is the same
    /// number. circuitRF's fields carry engineering units; none of them needs a grouped number, and
    /// guessing which reading was meant is the one outcome worth refusing.
    /// </summary>
    [Theory]
    [InlineData("4,4",      true,  4.4)]
    [InlineData("4.4",      true,  4.4)]
    [InlineData("  4,4  ",  true,  4.4)]
    [InlineData("1,234",    true,  1.234)]
    [InlineData("1.234,5",  false, 0)]
    [InlineData("1,234.5",  false, 0)]
    [InlineData("",         false, 0)]
    public void TryParseDouble_TakesEitherSeparatorAndNoGrouping(string typed, bool ok, double expected)
    {
        Assert.Equal(ok, NumericText.TryParseDouble(typed, out double v));
        if (ok) Assert.Equal(expected, v, 12);
    }
}
