using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-core-length-units — M1: a metre is representable.
///
/// <para>Before this brief, three of the six length units the parameter editor offers evaluated to
/// the wrong number, silently: <c>nm</c> and <c>cm</c> sat in <c>_identityUnits</c> (multiplier 1),
/// and <c>m</c> was the SI prefix MILLI. The base symbol for length is now <b>"metre"</b> — a
/// distinct symbol, because <c>"m"</c> stays milli (owner's decision, §5 q1 shape (b)).</para>
/// </summary>
public class LengthUnitsTests
{
    private static double Eval(string expr, string unit)
        => new Evaluator().Eval(expr, new Scope("test"), unit).AsReal();

    // ── M1 gate 1: every length unit evaluates to its correct SI value ────────

    /// <summary>
    /// The §1.1 table, with the CORRECT column. Every one of these except <c>um</c>, <c>mm</c> and
    /// <c>mil</c> was wrong before this brief.
    /// </summary>
    [Theory]
    [InlineData("nm",    1e-9)]     // was 1 — 1e9 high (identity unit)
    [InlineData("um",    1e-6)]     // was already correct
    [InlineData("mm",    1e-3)]     // was already correct
    [InlineData("cm",    1e-2)]     // was 1 — 100 high (identity unit)
    [InlineData("metre", 1.0)]      // did not exist at all
    [InlineData("mil",   2.54e-5)]  // was already correct
    [InlineData("in",    2.54e-2)]  // threw "Unknown unit 'in'"
    [InlineData("inch",  2.54e-2)]  // threw "Unknown unit 'inch'"
    public void EveryLengthUnit_EvaluatesToItsSiValue(string unit, double expected)
        => Assert.Equal(expected, Eval("1", unit), 15);

    /// <summary>
    /// <c>"m"</c> is deliberately STILL the SI prefix milli — the owner's own §5 q1 decision. This
    /// is a pin, not an oversight: re-pointing it at the metre would silently change the meaning of
    /// every bare-prefix value in a hand-authored netlist.
    /// </summary>
    [Fact]
    public void BarePrefixM_IsStillMilli_NotTheMetre()
        => Assert.Equal(1e-3, Eval("1", "m"), 15);

    /// <summary>The other eight bare SI prefixes are untouched by this brief.</summary>
    [Theory]
    [InlineData("T", 1e12)]
    [InlineData("G", 1e9)]
    [InlineData("M", 1e6)]
    [InlineData("k", 1e3)]
    [InlineData("u", 1e-6)]
    [InlineData("n", 1e-9)]
    [InlineData("p", 1e-12)]
    [InlineData("f", 1e-15)]
    public void EveryOtherBarePrefix_IsUnchanged(string unit, double expected)
        => Assert.Equal(expected, Eval("1", unit), 15);

    // ── M1 gate 2: BaseUnit returns a genuine scale-1 symbol ─────────────────

    /// <summary>
    /// The property <see cref="Units.BaseUnit"/> exists for, stated in
    /// <c>ParametricSweepEngine</c>'s own comment ("BaseUnit reduces it to scale-1 … so injecting it
    /// leaves the value unchanged") — and which length has NEVER satisfied. Before this brief every
    /// row of this theory failed: mm/um/nm/cm all mapped to <c>"m"</c> (scale 1e-3), and <c>mil</c>
    /// was absent from the map entirely so it passed through to its own 2.54e-5.
    /// </summary>
    [Theory]
    [InlineData("nm")]
    [InlineData("um")]
    [InlineData("mm")]
    [InlineData("cm")]
    [InlineData("mil")]
    [InlineData("in")]
    [InlineData("inch")]
    [InlineData("metre")]
    public void BaseUnitOfALength_HasScaleExactlyOne(string unit)
    {
        string b = Units.BaseUnit(unit);
        Assert.Equal("metre", b);
        Assert.Equal(1.0, Units.Scale(b));
    }

    /// <summary>The frequency control — the one dimension this property already held for.</summary>
    [Theory]
    [InlineData("Hz")]
    [InlineData("kHz")]
    [InlineData("MHz")]
    [InlineData("GHz")]
    [InlineData("THz")]
    public void BaseUnitOfAFrequency_StillHasScaleExactlyOne(string unit)
    {
        Assert.Equal("Hz", Units.BaseUnit(unit));
        Assert.Equal(1.0, Units.Scale(Units.BaseUnit(unit)));
    }

    /// <summary>
    /// <c>"m"</c> is NOT a length base symbol and must not be mapped to one — it is milli, and
    /// mapping it would re-open exactly the hole this brief closes.
    /// </summary>
    [Fact]
    public void BarePrefixM_IsNotMappedToTheLengthBase()
        => Assert.Equal("m", Units.BaseUnit("m"));

    // ── R-len-2: nm/cm moving into _scales flips the .cnl token gates ────────

    /// <summary>
    /// R-len-2 — <b>this fixes a second latent bug, verified rather than assumed.</b>
    /// <c>CnlReader</c> and <c>VendorAReader</c> consume a trailing unit token via
    /// <see cref="Units.IsKnown"/> (not <c>IsRecognizedUnit</c>) in several places. While <c>nm</c>
    /// and <c>cm</c> were identity-only, <c>IsKnown</c> was false for both, so those sites left the
    /// token unconsumed — the exact shape of the phantom-node failure <c>Units.cs</c>'s own
    /// <c>TOhm</c> comment records.
    /// </summary>
    [Theory]
    [InlineData("nm")]
    [InlineData("cm")]
    [InlineData("metre")]
    [InlineData("in")]
    [InlineData("inch")]
    public void EveryLengthUnit_IsKnown_NotMerelyRecognised(string unit)
    {
        Assert.True(Units.IsKnown(unit), $"'{unit}' must be in _scales, not _identityUnits");
        Assert.True(Units.IsRecognizedUnit(unit));
        Assert.NotNull(Units.Scale(unit));
    }

    /// <summary>The identity units that genuinely carry no multiplier are untouched.</summary>
    [Theory]
    [InlineData("V")]
    [InlineData("dBm")]
    [InlineData("%")]
    public void GenuineIdentityUnits_AreStillIdentityOnly(string unit)
    {
        Assert.False(Units.IsKnown(unit));
        Assert.True(Units.IsRecognizedUnit(unit));
        Assert.Equal(1.0, Eval("1", unit), 15);
    }
}
