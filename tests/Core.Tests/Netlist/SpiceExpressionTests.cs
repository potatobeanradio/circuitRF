using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Translating an expression written in the SPICE dialect into circuitRF's own grammar.
///
/// <para>Every rewrite here is a SPELLING change except one — reducing a statistical distribution to
/// its nominal value — and that one is reported rather than performed quietly. The tests are written
/// against the re-parsed VALUE wherever the shape matters, never against the text, because two
/// spellings of the same expression are the same expression.</para>
/// </summary>
public sealed class SpiceExpressionTests
{
    private static double Value(string rewritten)
    {
        var scope = new Scope("test");
        scope.Bind("a", "2");
        scope.Bind("b", "3");
        scope.Bind("c", "4");
        return new Evaluator().Eval(rewritten, scope).AsReal();
    }

    // ── ternary ───────────────────────────────────────────────────────────────

    [Fact]
    public void ATernaryBecomesAConditional()
    {
        Assert.Equal(3.0, Value(SpiceExpression.Rewrite("a > 1 ? b : c")), 12);
        Assert.Equal(4.0, Value(SpiceExpression.Rewrite("a > 9 ? b : c")), 12);
    }

    /// <summary>
    /// Association is the part that fails silently. Splitting on the FIRST <c>:</c> instead of the
    /// one matching the first <c>?</c> re-brackets a nested conditional into an expression that
    /// parses perfectly and computes something else.
    /// </summary>
    [Fact]
    public void ANestedTernaryAssociatesToTheRight()
    {
        // a>1 ? (b>9 ? 10 : 20) : 30  →  b is 3, so 20.
        Assert.Equal(20.0, Value(SpiceExpression.Rewrite("a > 1 ? b > 9 ? 10 : 20 : 30")), 12);
    }

    /// <summary>A two-character comparison must not have its <c>=</c> read as part of anything else.</summary>
    [Fact]
    public void AComparisonInTheConditionSurvives()
        => Assert.Equal(3.0, Value(SpiceExpression.Rewrite("a <= 2 ? b : c")), 12);

    [Fact]
    public void AColonThatIsNotPartOfAConditionalIsLeftAlone()
        => Assert.Equal("a+b", SpiceExpression.Rewrite("a + b"));

    // ── power ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DoubleStarBecomesCaret()
        => Assert.Equal(8.0, Value(SpiceExpression.Rewrite("a ** b")), 12);

    /// <summary>Right-associative in both dialects, so the rewrite must not change the answer.</summary>
    [Fact]
    public void PowerStaysRightAssociative()
        => Assert.Equal(512.0, Value(SpiceExpression.Rewrite("2 ** 3 ** 2")), 12);

    // ── delimiters ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{a*b}")]
    [InlineData("'a*b'")]
    [InlineData("{ 'a*b' }")]
    public void BracketedAndQuotedExpressionsAreUnwrapped(string written)
        => Assert.Equal(6.0, Value(SpiceExpression.Rewrite(written)), 12);

    /// <summary>
    /// Only a pair spanning the WHOLE value is a delimiter. A bracket in the middle groups, and
    /// stripping it would change the arithmetic.
    /// </summary>
    [Fact]
    public void AnInteriorBracketIsGrouping_NotADelimiter()
        => Assert.Equal(20.0, Value(SpiceExpression.Rewrite("{(a+b)*c}")), 12);

    // ── statistical distributions ─────────────────────────────────────────────

    /// <summary>
    /// circuitRF does not sample distributions, so a card asking for one gets its nominal value.
    /// That is an ordinary and useful run — what is not acceptable is doing it in silence, because
    /// the number that comes out is indistinguishable from one that carried no distribution at all.
    /// </summary>
    [Fact]
    public void ADistributionIsReducedToItsNominal_AndSaidSoOutLoud()
    {
        var used = new List<SpiceStatisticalUse>();

        string rewritten = SpiceExpression.Rewrite("agauss(2.5e-6, 0.1e-6, 3)", used);

        Assert.Equal(2.5e-6, Value(rewritten), 12);

        var one = Assert.Single(used);
        Assert.Equal("agauss", one.Function);
    }

    [Theory]
    [InlineData("gauss(1, 0.1, 3)")]
    [InlineData("aunif(1, 0.1)")]
    [InlineData("unif(1, 0.1)")]
    [InlineData("limit(1, 0.1)")]
    public void EveryDistributionFormIsCovered(string written)
    {
        var used = new List<SpiceStatisticalUse>();
        Assert.Equal(1.0, Value(SpiceExpression.Rewrite(written, used)), 12);
        Assert.Single(used);
    }

    /// <summary>A distribution nested inside arithmetic is reduced in place, not around.</summary>
    [Fact]
    public void ADistributionInsideArithmeticIsReducedInPlace()
    {
        var used = new List<SpiceStatisticalUse>();
        Assert.Equal(6.0, Value(SpiceExpression.Rewrite("2 * agauss(b, 0.1, 3)", used)), 12);
        Assert.Single(used);
    }

    [Fact]
    public void AnOrdinaryExpressionReportsNoStatistics()
    {
        var used = new List<SpiceStatisticalUse>();
        SpiceExpression.Rewrite("a * b + c", used);
        Assert.Empty(used);
    }

    // ── whitespace ────────────────────────────────────────────────────────────

    /// <summary>
    /// Not tidying. circuitRF's own generic instance-line parser splits on whitespace and reads bare
    /// words as nets, so an unquoted value containing a space becomes a value plus phantom nets —
    /// which shifts every later node index and still runs.
    /// </summary>
    [Fact]
    public void ARewrittenValueCarriesNoWhitespace()
    {
        Assert.DoesNotContain(' ', SpiceExpression.Rewrite("a > 1 ? b : c"));
        Assert.DoesNotContain(' ', SpiceExpression.Rewrite("{ a * b + c }"));
        Assert.DoesNotContain(' ', SpiceExpression.Rewrite("agauss( 1 , 0.1 , 3 )"));
    }

    /// <summary>…but a quoted string is data, and a path with a space in it must survive.</summary>
    [Fact]
    public void WhitespaceInsideQuotesSurvives()
        => Assert.Equal("\"my models/x.va\"", SpiceExpression.Rewrite("\"my models/x.va\""));
}
