using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>Task 2: log10 and ln (unambiguous natural log) in the expression engine and AD.</summary>
public class Log10AndLnTests
{
    // ── log10 via SddEvaluator ────────────────────────────────────────────────

    private static double EvalSdd(string expr, double x)
    {
        var ast = Parser.Parse(expr);
        return SddEvaluator.EvalDouble(ast, new Dictionary<string, double>(), [x]);
    }

    [Fact] public void Log10_1000_Equals3()     => Assert.Equal(3.0, EvalSdd("log10(_v1)", 1000.0), 10);
    [Fact] public void Log10_1_Equals0()        => Assert.Equal(0.0, EvalSdd("log10(_v1)", 1.0),    10);
    [Fact] public void Log10_10_Equals1()       => Assert.Equal(1.0, EvalSdd("log10(_v1)", 10.0),   10);
    [Fact] public void Log10_PointOne_NegOne()  => Assert.Equal(-1.0, EvalSdd("log10(_v1)", 0.1),   10);

    // ── ln (unambiguous natural log) ──────────────────────────────────────────

    [Fact] public void Ln_E_Equals1()           => Assert.Equal(1.0, EvalSdd("ln(_v1)", Math.E), 10);
    [Fact] public void Ln_1_Equals0()           => Assert.Equal(0.0, EvalSdd("ln(_v1)", 1.0),   10);
    [Fact] public void Ln_MatchesLog()
    {
        // ln and log must return the same value (both are natural log)
        double val = 7.389056;  // ≈ e²
        Assert.Equal(EvalSdd("log(_v1)", val), EvalSdd("ln(_v1)", val), 12);
    }

    // ── AD derivative of log10 ────────────────────────────────────────────────

    [Fact]
    public void Log10_AdDerivative_MatchesFd()
    {
        var ast = Parser.Parse("log10(_v1)");
        (_, double[] grad) = SddEvaluator.EvalDual(ast, new Dictionary<string, double>(), [100.0]);
        double[] gradFd = FiniteDiff.Gradient(
            v => SddEvaluator.EvalDouble(ast, new Dictionary<string, double>(), v),
            [100.0]);
        // d/dx log10(x) = 1/(x·ln10) = 1/(100·2.3026) ≈ 4.343e-4
        Assert.Equal(gradFd[0], grad[0], 6);
        Assert.Equal(1.0 / (100.0 * Math.Log(10.0)), grad[0], 8);
    }

    [Fact]
    public void Ln_AdDerivative_MatchesFd()
    {
        var ast = Parser.Parse("ln(_v1)");
        (_, double[] grad) = SddEvaluator.EvalDual(ast, new Dictionary<string, double>(), [5.0]);
        double[] gradFd = FiniteDiff.Gradient(
            v => SddEvaluator.EvalDouble(ast, new Dictionary<string, double>(), v),
            [5.0]);
        Assert.Equal(gradFd[0], grad[0], 8);
        Assert.Equal(1.0 / 5.0, grad[0], 10);
    }

    // ── log10 via main Evaluator (elaboration path) ───────────────────────────

    [Fact]
    public void Log10_InEvaluator_Works()
    {
        var ev = new Evaluator();
        var scope = new Scope("test");
        scope.Bind("x", "1000");
        var result = ev.EvalExpr(Parser.Parse("log10(x)"), scope);
        Assert.Equal(ValueKind.Real, result.Kind);
        Assert.Equal(3.0, result.AsReal(), 10);
    }

    [Fact]
    public void Ln_InEvaluator_Works()
    {
        var ev = new Evaluator();
        var scope = new Scope("test");
        scope.Bind("x", "1");
        var result = ev.EvalExpr(Parser.Parse("ln(x)"), scope);
        Assert.Equal(ValueKind.Real, result.Kind);
        Assert.Equal(0.0, result.AsReal(), 10);
    }
}
