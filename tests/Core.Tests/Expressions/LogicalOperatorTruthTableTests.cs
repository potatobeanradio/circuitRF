using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// The full truth table of <c>&amp;&amp;</c> and <c>||</c>.
///
/// <para><b>Written as a table rather than as cases because of what was found here.</b>
/// <see cref="Evaluator"/> short-circuited correctly and then combined the two operands with AND
/// regardless of which operator it was evaluating — so <c>false || true</c> came back FALSE, and
/// every <c>||</c> whose first alternative did not hold silently answered false. Nothing threw and
/// nothing was reported: a conditional simply took its else-branch. Four of the eight rows below
/// were never exercised anywhere.</para>
///
/// <para>Both operands are bound as expressions rather than written as literals because circuitRF's
/// grammar has no boolean literal — a comparison is how a Bool is spelled.</para>
/// </summary>
public class LogicalOperatorTruthTableTests
{
    private static bool Eval(string expression)
    {
        var scope = new Scope("truth");
        scope.Bind("t", "1 > 0");
        scope.Bind("f", "0 > 1");
        return new Evaluator().Eval(expression, scope).AsBool();
    }

    [Theory]
    [InlineData("f || f", false)]
    [InlineData("f || t", true)]
    [InlineData("t || f", true)]
    [InlineData("t || t", true)]
    [InlineData("f && f", false)]
    [InlineData("f && t", false)]
    [InlineData("t && f", false)]
    [InlineData("t && t", true)]
    public void EveryRowOfTheTruthTable(string expression, bool expected)
        => Assert.Equal(expected, Eval(expression));

    /// <summary>
    /// The shape the bug actually presented in: a guard with two alternatives, where the second is
    /// the one that holds. This is what a netlist's logic blocks are made of.
    /// </summary>
    [Fact]
    public void AConditionalTakesItsThenBranchWhenTheSecondAlternativeHolds()
    {
        var scope = new Scope("guard");
        scope.Bind("a", "2");
        scope.Bind("b", "3");
        Assert.Equal(7.0, new Evaluator().Eval("if(a > 9 || b > 1, 7, 9)", scope).AsReal(), 12);
    }
}
