using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Letting a frequency-dependent value stay an expression as it crosses a cell boundary.
///
/// <para>The load-bearing property is the NEGATIVE one: an expression that is not frequency-dependent
/// must come back untouched, so every existing design takes exactly the path it always did and the
/// HB inner loop never sees a deferred expression.</para>
/// </summary>
public class FreqDeferralTests
{
    private static (FreqDeferral D, Evaluator E, Scope S) Fixture(params (string Name, string Expr)[] bindings)
    {
        var scope = new Scope("cell");
        foreach (var (n, e) in bindings) scope.Bind(n, e);
        return (new FreqDeferral(), new Evaluator(), scope);
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    [Fact]
    public void ADirectFreqReference_IsFreqDependent()
    {
        var (d, _, s) = Fixture();
        Assert.True(d.IsFreqDependent("2 * freq", s));
    }

    [Fact]
    public void DependenceIsTransitive_ThroughEveryNameOnTheWay()
    {
        var (d, _, s) = Fixture(("a", "freq * 2"), ("b", "a + 1"), ("c", "b * 3"));
        Assert.True(d.IsFreqDependent("c", s));
    }

    [Theory]
    [InlineData("1 + 2")]
    [InlineData("k * 3")]
    [InlineData("")]
    [InlineData("\"m1\"")]
    public void SomethingThatNeverReachesFreq_IsNot(string expr)
    {
        var (d, _, s) = Fixture(("k", "7"));
        Assert.False(d.IsFreqDependent(expr, s));
    }

    [Fact]
    public void AnUnresolvedName_IsNotTreatedAsFreqDependent()
    {
        // Somebody else's error to report, with a better message than this class could give.
        var (d, _, s) = Fixture();
        Assert.False(d.IsFreqDependent("nowhere + 1", s));
    }

    [Fact]
    public void ACycleAmongNames_DoesNotHang()
    {
        var (d, _, s) = Fixture(("a", "b"), ("b", "a"));
        Assert.False(d.IsFreqDependent("a", s));
    }

    // ── The negative property: nothing else changes ───────────────────────────

    [Theory]
    [InlineData("1 + 2")]
    [InlineData("k * 3")]
    [InlineData("if(k > 1, 4, 5)")]
    public void AFreqIndependentExpression_ComesBackByteIdentical(string expr)
    {
        var (d, e, s) = Fixture(("k", "7"));
        Assert.Equal(expr, d.InlineForCellBoundary(expr, s, e));
    }

    // ── Inlining ──────────────────────────────────────────────────────────────

    [Fact]
    public void InliningKeepsFreq_AndFoldsEverythingElseToALiteral()
    {
        var (d, e, s) = Fixture(("gain", "3 * 4"), ("w", "2 * freq"));

        string r = d.InlineForCellBoundary("gain * w", s, e);

        Assert.Contains("freq", r);
        Assert.DoesNotContain("gain", r);      // folded to 12
        Assert.Contains("12", r);
        Parser.Parse(r);                       // and it is still an expression
    }

    [Fact]
    public void AnInlinedExpression_EvaluatesToTheSameValueAsTheOriginalWouldHave()
    {
        // The whole point: deferring must not change the arithmetic, only when it happens.
        var (d, e, s) = Fixture(("k", "3"), ("x", "k * freq"), ("y", "x + k"));

        string inlined = d.InlineForCellBoundary("y * 2", s, e);

        var at = new Scope("stamp");
        at.Bind("freq", "5");
        Assert.Equal((3 * 5 + 3) * 2, new Evaluator().Eval(inlined, at).AsReal(), 12);
    }

    [Fact]
    public void AnInlinedBinding_KeepsItsOwnUnit()
    {
        // A deferred `L = 1 nH` must still mean nanohenries once it is text.
        var scope = new Scope("cell");
        scope.Bind("L", "1", "nH");
        scope.Bind("x", "L * freq");
        var d = new FreqDeferral();

        string inlined = d.InlineForCellBoundary("x", scope, new Evaluator());

        var at = new Scope("stamp");
        at.Bind("freq", "2");
        Assert.Equal(2e-9, new Evaluator().Eval(inlined, at).AsReal(), 15);
    }

    [Fact]
    public void AComplexConstant_SurvivesAsAComplexLiteral()
    {
        var (d, e, s) = Fixture(("z", "complex(1, 2)"), ("x", "z * freq"));

        string inlined = d.InlineForCellBoundary("x", s, e);

        var at = new Scope("stamp");
        at.Bind("freq", "3");
        var v = new Evaluator().Eval(inlined, at);
        Assert.Equal(3.0, v.AsComplex().Real,      12);
        Assert.Equal(6.0, v.AsComplex().Imaginary, 12);
    }

    [Fact]
    public void ASelfReferentialFreqDependentBinding_IsReportedAsACycle()
    {
        var (d, e, s) = Fixture(("a", "a + freq"));
        Assert.Throws<CycleException>(() => d.InlineForCellBoundary("a", s, e));
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Rendering is fully parenthesised on purpose. Precedence that survives a round trip by luck is
    /// a bug waiting for the first expression complicated enough to expose it — so the test that
    /// matters is that re-parsing gives back the same VALUE, not the same text.
    /// </summary>
    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("2 ^ 3 ^ 2", 512.0)]           // right-associative
    [InlineData("-2 ^ 2", -4.0)]
    [InlineData("if(1 > 2, 10, 20)", 20.0)]
    [InlineData("sqrt(16) + abs(-3)", 7.0)]
    public void RenderThenReparse_PreservesTheValue(string expr, double expected)
    {
        string rendered = FreqDeferral.Render(Parser.Parse(expr));
        var v = new Evaluator().Eval(rendered, new Scope("s"));
        Assert.Equal(expected, v.AsReal(), 12);
    }
}
