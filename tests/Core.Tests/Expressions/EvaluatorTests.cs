using System.Numerics;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Expressions;

public class EvaluatorTests
{
    private static Evaluator Ev() => new();
    private static Scope Empty() => new("test");

    private static double Real(string expr, Scope? scope = null)
    {
        var ev = Ev();
        var v = ev.Eval(expr, scope ?? Empty());
        Assert.Equal(ValueKind.Real, v.Kind);
        return v.AsReal();
    }

    private static Complex Cmplx(string expr, Scope? scope = null)
    {
        var ev = Ev();
        var v = ev.Eval(expr, scope ?? Empty());
        Assert.Equal(ValueKind.Complex, v.Kind);
        return v.AsComplex();
    }

    // ── Constants ────────────────────────────────────────────────────────────

    [Fact] public void ConstPi()
        => Assert.Equal(Math.PI, Real("pi"), 1e-15);

    [Fact] public void ConstE()
        => Assert.Equal(Math.E, Real("e"), 1e-15);

    [Fact] public void ConstJ()
    {
        var v = new Evaluator().Eval("j", Empty());
        Assert.Equal(ValueKind.Complex, v.Kind);
        Assert.Equal(Complex.ImaginaryOne, v.AsComplex());
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────

    [Fact] public void Add()            => Assert.Equal(5.0,  Real("2+3"));
    [Fact] public void Sub()            => Assert.Equal(1.0,  Real("3-2"));
    [Fact] public void Mul()            => Assert.Equal(6.0,  Real("2*3"));
    [Fact] public void Div()            => Assert.Equal(2.0,  Real("6/3"));
    [Fact] public void Pow()            => Assert.Equal(8.0,  Real("2^3"));
    [Fact] public void UnaryMinus()     => Assert.Equal(-3.0, Real("-3"));
    [Fact] public void UnaryPlus()      => Assert.Equal(3.0,  Real("+3"));

    [Fact] public void NegPowerRule()
    {
        // -2^2 == -(2^2) == -4, NOT (-2)^2 == 4
        Assert.Equal(-4.0, Real("-2^2"));
    }

    [Fact] public void CaretRightAssoc()
    {
        // 2^3^2 == 2^(3^2) == 2^9 == 512
        Assert.Equal(512.0, Real("2^3^2"));
    }

    [Fact] public void ComplexArithmetic()
    {
        var c = Cmplx("2 + j*3");
        Assert.Equal(2.0, c.Real,      1e-14);
        Assert.Equal(3.0, c.Imaginary, 1e-14);
    }

    [Fact] public void ComplexArithmetic2()
    {
        var c = Cmplx("2 - j*(-3)");
        Assert.Equal(2.0, c.Real,      1e-14);
        Assert.Equal(3.0, c.Imaginary, 1e-14);
    }

    [Fact] public void ComplexArithmetic3()
    {
        var c = Cmplx("-1*(j*j)/-1");
        Assert.Equal(-1, c.Real,      1e-14);
        Assert.Equal(0, c.Imaginary, 1e-14);
    }

    [Fact] public void DivisionByZeroThrows()
        => Assert.Throws<ExpressionException>(() => Real("1/0"));

    // ── Comparison & logic ───────────────────────────────────────────────────

    [Fact] public void LessThanTrue()
    {
        var v = Ev().Eval("2 < 3", Empty());
        Assert.Equal(ValueKind.Bool, v.Kind);
        Assert.True(v.AsBool());
    }

    [Fact] public void EqualOnReals()
    {
        var v = Ev().Eval("5 == 5", Empty());
        Assert.True(v.AsBool());
    }

    [Fact] public void OrderingOnComplexThrows()
        => Assert.Throws<ExpressionException>(() => Ev().Eval("j < 1", Empty()));

    [Fact] public void AndShortCircuit()
    {
        var v = Ev().Eval("1 > 2 && 3 > 0", Empty());
        Assert.False(v.AsBool());
    }

    [Fact] public void OrShortCircuit()
    {
        var v = Ev().Eval("1 > 0 || 3 > 4", Empty());
        Assert.True(v.AsBool());
    }

    [Fact] public void NotOperator()
    {
        var v = Ev().Eval("!(1 > 2)", Empty());
        Assert.True(v.AsBool());
    }

    // ── Conditional ──────────────────────────────────────────────────────────

    [Fact] public void IfTrue()
        => Assert.Equal(1.0, Real("if(1 > 0, 1, -1)"));

    [Fact] public void IfFalse()
        => Assert.Equal(-1.0, Real("if(1 < 0, 1, -1)"));

    [Fact] public void TernaryTrue()
        => Assert.Equal(10.0, Real("1 > 0 ? 10 : 20"));

    [Fact] public void TernaryFalse()
        => Assert.Equal(20.0, Real("1 < 0 ? 10 : 20"));

    [Fact] public void IfResultKindFollowsBranch()
    {
        // then branch is Complex (j), else Real — result kind is that of the selected branch
        var v = Ev().Eval("if(1>0, j, 0)", Empty());
        Assert.Equal(ValueKind.Complex, v.Kind);
    }

    // ── Built-in functions ───────────────────────────────────────────────────

    [Fact] public void SinCos()
    {
        Assert.Equal(0.0, Real("sin(0)"),  1e-15);
        Assert.Equal(1.0, Real("cos(0)"),  1e-15);
    }

    [Fact] public void Tanh()
        => Assert.Equal(Math.Tanh(1.0), Real("tanh(1)"), 1e-14);

    [Fact] public void Exp()
        => Assert.Equal(Math.E, Real("exp(1)"), 1e-14);

    [Fact] public void Log()
        => Assert.Equal(0.0, Real("log(1)"), 1e-15);

    [Fact] public void LogOfZeroThrows()
        => Assert.Throws<DomainException>(() => Real("log(0)"));

    [Fact] public void SqrtNegativeThrows()
        => Assert.Throws<DomainException>(() => Real("sqrt(-1)"));

    [Fact] public void Abs()
    {
        Assert.Equal(3.0, Real("abs(-3)"), 1e-15);
        var v = Ev().Eval("abs(j*3)", Empty());
        Assert.Equal(3.0, v.AsReal(), 1e-14);
    }

    [Fact] public void MinMax()
    {
        Assert.Equal(2.0, Real("min(2, 5)"));
        Assert.Equal(5.0, Real("max(2, 5)"));
    }

    [Fact] public void Atan2()
        => Assert.Equal(Math.Atan2(1, 1), Real("atan2(1, 1)"), 1e-14);

    [Fact] public void UnknownFunctionThrows()
        => Assert.Throws<UnknownFunctionException>(() => Real("bogus(1)"));

    [Fact] public void ArityMismatchThrows()
        => Assert.Throws<ArityException>(() => Real("sin(1, 2)"));

    // ── Units ────────────────────────────────────────────────────────────────

    [Fact] public void UnitNano()
    {
        var ev = Ev();
        var v = ev.Eval("1", Empty(), "nH");
        Assert.Equal(1e-9, v.AsReal(), 1e-24);
    }

    [Fact] public void UnitGHz()
    {
        var ev = Ev();
        var v = ev.Eval("2.4", Empty(), "GHz");
        Assert.Equal(2.4e9, v.AsReal(), 1e-3);
    }

    // ── Scope + variable resolution ──────────────────────────────────────────

    [Fact] public void SimpleVarRef()
    {
        var scope = new Scope("test");
        scope.Bind("x", "42");
        var v = Ev().Resolve("x", scope);
        Assert.Equal(42.0, v.AsReal());
    }

    [Fact] public void ChainedVarRef()
    {
        var scope = new Scope("test");
        scope.Bind("x", "y");
        scope.Bind("y", "7");
        var v = Ev().Resolve("x", scope);
        Assert.Equal(7.0, v.AsReal());
    }

    [Fact] public void UnresolvedNameThrows()
    {
        var scope = new Scope("test");
        Assert.Throws<UnresolvedNameException>(() => Ev().Resolve("z", scope));
    }

    [Fact] public void ParentScopeVisible()
    {
        var global = new Scope("global");
        global.Bind("G", "100");
        var child = new Scope("child", global);
        var v = Ev().Resolve("G", child);
        Assert.Equal(100.0, v.AsReal());
    }

    [Fact] public void LocalShadowsParent()
    {
        var global = new Scope("global");
        global.Bind("x", "1");
        var child = new Scope("child", global);
        child.Bind("x", "2");
        var v = Ev().Resolve("x", child);
        Assert.Equal(2.0, v.AsReal());
    }

    // ── User-defined functions ───────────────────────────────────────────────

    [Fact] public void UserFunctionBasic()
    {
        var ev = Ev();
        ev.RegisterFunction(new UserFunction("double", ["x"], "x * 2"));
        var v = ev.Eval("double(5)", Empty());
        Assert.Equal(10.0, v.AsReal());
    }

    [Fact] public void UserFunctionMultiArg()
    {
        var ev = Ev();
        ev.RegisterFunction(new UserFunction("add", ["a", "b"], "a + b"));
        var v = ev.Eval("add(3, 4)", Empty());
        Assert.Equal(7.0, v.AsReal());
    }

    [Fact] public void UserFunctionArityMismatch()
    {
        var ev = Ev();
        ev.RegisterFunction(new UserFunction("f", ["x"], "x"));
        Assert.Throws<ArityException>(() => ev.Eval("f(1, 2)", Empty()));
    }

    // ── Type errors ──────────────────────────────────────────────────────────

    [Fact] public void BoolInArithmeticThrows()
        => Assert.Throws<ExpressionException>(() => Ev().Eval("(1>0) + 1", Empty()));

    [Fact] public void IfConditionMustBeBoolThrows()
        => Assert.Throws<TypeErrorException>(() => Ev().Eval("if(1, 2, 3)", Empty()));
}
