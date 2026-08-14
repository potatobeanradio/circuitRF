using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-harmonicarf-r3b-frame-rate-and-loadpull.md §7 gate 2 — the compiled slot-resolved evaluator
/// (<see cref="CompiledSddExpr"/>) must agree EXACTLY (value and every gradient slot, not a
/// tolerance) with the untouched reference (<see cref="SddEvaluator.EvalDual(Expr,
/// IReadOnlyDictionary{string,double},double[],string)"/>) it replaces on SddModel's hot path. Built
/// FIRST, before any evaluator change, so this is a real before/after rather than a rationalisation.
///
/// Corpus: the shipped default's own two equations, every SDD equation found in <c>testdata/</c>,
/// and hand-written ones exercising <c>^</c>, conditionals, every supported function, and the
/// ExpCap/LogFloor clamp paths — each at several operating points.
/// </summary>
public sealed class SddCompiledBitIdenticalTests
{
    private static readonly Dictionary<string, double> HeroParams = new(StringComparer.Ordinal)
    {
        ["Sv"] = -0.837, ["Sc"] = 0.71, ["TV0"] = 4.268, ["TC"] = 1.507,
        ["th"] = 0.001, ["a"] = 0.176, ["g"] = 0.089, ["lam"] = 0.0012, ["B"] = 1130.0,
    };

    private const string HeroI2 =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*log(exp(-(Sv - _v1)/Sc) + 1))) + 1))" +
        " * log(exp(-(2*TV0 - 2*_v1 + 2*_v2*th + 2*Sc*log(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1)" +
        " * (_v2*lam + 1)) / 2";

    // testdata/Hero3B's *10 variant and _v1/80 gate — same shape, different scale, so a compile-time
    // constant-folding bug that happens to cancel on one fixture would still show on the other.
    private const string Hero10xI2 = "10*(" + HeroI2 + ")";

    // The shipped default (HarmonicaViewModel.DefaultModel) — coefficients literal, no named params.
    private const string ShippedI1 = "_v1/50";
    private const string ShippedI2 =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";

    private static readonly (string Name, string Expr, IReadOnlyDictionary<string, double> Params)[] Corpus =
    [
        ("shipped-gate",  ShippedI1, new Dictionary<string, double>()),
        ("shipped-drain", ShippedI2, new Dictionary<string, double>()),
        ("testdata-gate-80", "_v1/80", new Dictionary<string, double>()),
        ("testdata-hero-i2", HeroI2, HeroParams),
        ("testdata-hero-10x-i2", Hero10xI2, HeroParams),
        ("pow", "_v1^2 + _v2^0.5", new Dictionary<string, double>()),
        ("pow-func", "pow(_v1, 3) + pow(_v2, _v1)", new Dictionary<string, double>()),
        ("conditional-simple", "if(_v1>0, _v1*2, _v1/2)", new Dictionary<string, double>()),
        ("conditional-nested", "if(_v1>0 && _v2<10, if(_v1>5,1,2), if(_v2>0 || _v1==0, 3, 4))", new Dictionary<string, double>()),
        ("conditional-not", "if(!(_v1>0), _v1, -_v1)", new Dictionary<string, double>()),
        ("logic-ops", "if(_v1>0 && _v2>0, 1, if(_v1<0 || _v2<0, 2, 3))", new Dictionary<string, double>()),
        ("every-fn-1", "exp(_v1)+ln(abs(_v2)+1)+sqrt(abs(_v1)+1)+tanh(_v2)+sin(_v1)+cos(_v2)+abs(_v1-_v2)", new Dictionary<string, double>()),
        ("every-fn-2", "sinh(_v1)+cosh(_v2)+tan(_v1*0.1)+log10(abs(_v2)+1)+min(_v1,_v2)+max(_v1,_v2)+sign(_v1)", new Dictionary<string, double>()),
        ("every-fn-3", "atan(_v1)+atan2(_v1,_v2)+asin(_v1/100)+acos(_v2/100)", new Dictionary<string, double>()),
        ("expcap-clamp", "exp(_v1*1000)", new Dictionary<string, double>()),
        ("logfloor-log", "log(_v1)", new Dictionary<string, double>()),
        ("logfloor-sqrt", "sqrt(_v1)", new Dictionary<string, double>()),
        ("with-param", "P*_v1 + Q*_v2*_v2", new Dictionary<string, double> { ["P"] = 3.5, ["Q"] = -2.1 }),
        ("unary-plus-minus", "-(-_v1) + +(_v2)", new Dictionary<string, double>()),
        ("deep-nest", "(((_v1+_v2)*(_v1-_v2))/(_v1*_v1+1))^2", new Dictionary<string, double>()),
    ];

    private static readonly double[][] OperatingPoints =
    [
        [-3.05, 48.0], [0.0, 0.0], [-1.0, 1.0], [5.0, -5.0], [-10.0, 100.0], [1e-6, -1e-6],
    ];

    public static IEnumerable<object[]> CasesAndPoints()
    {
        foreach (var c in Corpus)
            foreach (var p in OperatingPoints)
                yield return [c.Name, c.Expr, c.Params, p];
    }

    [Theory]
    [MemberData(nameof(CasesAndPoints))]
    public void Compiled_MatchesReference_ExactlyOnValueAndEveryGradientSlot(
        string name, string expr, IReadOnlyDictionary<string, double> parameters, double[] point)
    {
        var ast = Parser.Parse(expr);

        (double refVal, double[] refGrad) = SddEvaluator.EvalDual(ast, parameters, point, name);

        var compiled = CompiledSddExpr.Compile(ast, parameters, point.Length, [], name);
        (double newVal, double[] newGrad) = compiled.EvalDual(point, [], name);

        Assert.True(refGrad.Length == newGrad.Length,
            $"{name}: gradient width differs ({refGrad.Length} vs {newGrad.Length})");
        Assert.Equal(refVal, newVal);
        for (int i = 0; i < refGrad.Length; i++)
            Assert.True(refGrad[i] == newGrad[i] || (double.IsNaN(refGrad[i]) && double.IsNaN(newGrad[i])),
                $"{name} grad[{i}]: reference {refGrad[i]:R} vs compiled {newGrad[i]:R}");
    }

    /// <summary>Control-current path — a distinct call shape from the plain port-voltage one, with its
    /// own slot region and its own EvalDual overload on both sides.</summary>
    [Theory]
    [InlineData(-3.05, 48.0, 0.01)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(2.0, -2.0, -0.5)]
    public void Compiled_MatchesReference_WithControlCurrents(double v1, double v2, double c1)
    {
        var ast = Parser.Parse("_v1 + _v2*0.5 + _c1*3.0 - _c1*_v1");
        var parameters = new Dictionary<string, double>();
        var controls = new (int N, double Value)[] { (1, c1) };

        (double refVal, double[] refGrad) = SddEvaluator.EvalDual(ast, parameters, [v1, v2], controls, "ctrl");

        var compiled = CompiledSddExpr.Compile(ast, parameters, 2, [1], "ctrl");
        (double newVal, double[] newGrad) = compiled.EvalDual([v1, v2], controls, "ctrl");

        Assert.Equal(refVal, newVal);
        Assert.Equal(refGrad.Length, newGrad.Length);
        for (int i = 0; i < refGrad.Length; i++)
            Assert.Equal(refGrad[i], newGrad[i]);
    }
}
