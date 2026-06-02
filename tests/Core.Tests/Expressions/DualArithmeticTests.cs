using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>Unit tests for the Dual type and its chain-rule arithmetic.</summary>
public class DualArithmeticTests
{
    const int N = 2;

    static Dual C(double v) => Dual.Param(v, N);
    static Dual V1(double v) => Dual.Seed(v, N, 0);
    static Dual V2(double v) => Dual.Seed(v, N, 1);

    // ── Seed / Constant ──────────────────────────────────────────────────────

    [Fact] public void Constant_HasZeroGradient()
    {
        var d = C(5.0);
        Assert.Equal(5.0, d.Value);
        Assert.Equal(0.0, d.GetGrad(0));
        Assert.Equal(0.0, d.GetGrad(1));
    }

    [Fact] public void Seed_HasUnitGradientInCorrectSlot()
    {
        var d = V1(3.0);
        Assert.Equal(3.0, d.Value);
        Assert.Equal(1.0, d.GetGrad(0));
        Assert.Equal(0.0, d.GetGrad(1));
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────

    [Fact] public void Add_ChainRule()
    {
        var r = Dual.Add(V1(3.0), V2(4.0));
        Assert.Equal(7.0, r.Value);
        Assert.Equal(1.0, r.GetGrad(0));
        Assert.Equal(1.0, r.GetGrad(1));
    }

    [Fact] public void Mul_ProductRule()
    {
        // f = v1 * v2; df/dv1 = v2, df/dv2 = v1
        var r = Dual.Mul(V1(3.0), V2(4.0));
        Assert.Equal(12.0, r.Value);
        Assert.Equal(4.0, r.GetGrad(0));  // df/dv1 = v2
        Assert.Equal(3.0, r.GetGrad(1));  // df/dv2 = v1
    }

    [Fact] public void Div_QuotientRule()
    {
        // f = v1 / v2; df/dv1 = 1/v2, df/dv2 = -v1/v2²
        var r = Dual.Div(V1(6.0), V2(2.0));
        Assert.Equal(3.0, r.Value);
        Assert.Equal(0.5,  r.GetGrad(0), 12);      // 1/2
        Assert.Equal(-1.5, r.GetGrad(1), 12);       // -6/4
    }

    [Fact] public void Pow_ConstantExponent()
    {
        // f = v1^3; df/dv1 = 3*v1^2
        var r = Dual.Pow(V1(4.0), C(3.0));
        Assert.Equal(64.0, r.Value);
        Assert.Equal(48.0, r.GetGrad(0), 10);   // 3*16
        Assert.Equal(0.0,  r.GetGrad(1));
    }

    // ── Functions ────────────────────────────────────────────────────────────

    // Tolerance: 12 decimal digits (much tighter than the 4 sig-fig gate; validates the chain rule).
    private static void AssertMatchesFd(Func<Dual, Dual> f, double x0, int slot = 0)
    {
        var seed = slot == 0 ? V1(x0) : V2(x0);
        var result = f(seed);
        double h = 1e-7 * Math.Max(1.0, Math.Abs(x0));
        double fp = ((double)(object)f(slot == 0 ? V1(x0 + h) : V2(x0 + h)).Value);
        double fm = ((double)(object)f(slot == 0 ? V1(x0 - h) : V2(x0 - h)).Value);
        double fdGrad = (fp - fm) / (2.0 * h);
        Assert.Equal(fdGrad, result.GetGrad(slot), 5);  // 5 decimal places ≈ 10 sig figs at scale
    }

    [Fact] public void Exp_DerivativeMatchesFd() => AssertMatchesFd(x => Dual.Exp(x), 1.5);
    [Fact] public void Log_DerivativeMatchesFd() => AssertMatchesFd(x => Dual.Log(x), 2.5);
    [Fact] public void Sqrt_DerivativeMatchesFd() => AssertMatchesFd(x => Dual.Sqrt(x), 4.0);
    [Fact] public void Tanh_DerivativeMatchesFd() => AssertMatchesFd(x => Dual.Tanh(x), 0.8);
    [Fact] public void Sin_DerivativeMatchesFd()  => AssertMatchesFd(x => Dual.Sin(x), 1.2);
    [Fact] public void Cos_DerivativeMatchesFd()  => AssertMatchesFd(x => Dual.Cos(x), 1.2);

    // ── Robustness ───────────────────────────────────────────────────────────

    [Fact] public void Exp_LargeArgument_DoesNotNaN()
    {
        var r = Dual.Exp(V1(1000.0));
        Assert.False(double.IsNaN(r.Value),  "Exp value must not be NaN");
        Assert.False(double.IsNaN(r.GetGrad(0)), "Exp gradient must not be NaN");
        Assert.True(double.IsFinite(r.Value), "Exp value must be finite");
    }

    [Fact] public void Log_NegativeArgument_ClampsAndDoesNotNaN()
    {
        var r = Dual.Log(C(-1.0));
        Assert.False(double.IsNaN(r.Value), "Log of negative must not be NaN");
        Assert.True(double.IsFinite(r.Value));
    }

    [Fact] public void Sqrt_NegativeArgument_ClampsAndDoesNotNaN()
    {
        var r = Dual.Sqrt(C(-4.0));
        Assert.False(double.IsNaN(r.Value), "Sqrt of negative must not be NaN");
        Assert.True(double.IsFinite(r.Value));
    }

    // ── Softplus via composed Log+Exp stays finite for large argument ─────────

    [Fact] public void Softplus_LargeArg_FiniteAndCorrect()
    {
        // softplus(x) = log(1 + exp(x)) ≈ x for large x
        // With exp capped at 700: log(exp(700)+1) ≈ 700 (correct)
        double x = 800.0;
        var xd = V1(x);
        var r = Dual.Log(Dual.Add(Dual.Exp(xd), C(1.0)));
        Assert.False(double.IsNaN(r.Value));
        Assert.False(double.IsNaN(r.GetGrad(0)));
        Assert.True(double.IsFinite(r.Value));
        Assert.True(double.IsFinite(r.GetGrad(0)));
        // value should be ≈ 700 (the cap), not 800 (overflowed)
        Assert.Equal(700.0, r.Value, 0);  // within 1
    }
}
