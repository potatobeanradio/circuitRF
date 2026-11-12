// ================================================================
//  MathOnACubeTests.cs — the unary math built-ins over a cube, element by element
//
//  sin, cos, tan, exp and the rest of the UnaryMath family took a number only: over a sweep's cube
//  (a swept variable reads as one, one element per point) they failed with "Value is Cube, not
//  Complex", while + - * / ^ already broadcast. A measurement wanting e^(jx) had to write it as a
//  power of e. They now map over a cube the way arithmetic does: axes kept in order and length, a
//  real cube staying real, a complex one complex, and a NaN element (a point with no value) staying
//  NaN. imag, phase, phase_rad, conj and sign also take a REAL cube now, giving what they give a
//  real number. A number's result is unchanged, which the last section pins.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class MathOnACubeTests
{
    // Two axes, so "axes preserved" is a statement about order and length, not only about one name.
    private static readonly Axis X    = new("x", [0.0, 0.5, 1.0]);
    private static readonly Axis Freq = new("freq", [1e9, 2e9], "Hz");

    private static DataCube RealCube(params double[] v) => new([X, Freq], v);
    private static DataCube ComplexCube(params Complex[] v) => new([X, Freq], v);

    private static readonly double[] RealData = [0.0, 0.1, 0.5, -0.7, 1.0, 2.5];

    private static Value Eval(string expr, DataCube cube)
    {
        var ds = new DataSet();
        ds.Add("c", cube);
        var ctx = new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds });
        return new Evaluator(ctx).Eval(expr, new Scope("t"));
    }

    private static void AssertSameAxes(DataCube got)
    {
        Assert.Equal(new[] { "x", "freq" }, got.Axes.Select(a => a.Name));
        Assert.Equal(X.Values, got.Axes[0].Values);
        Assert.Equal(Freq.Values, got.Axes[1].Values);
    }

    private static readonly Dictionary<string, Func<double, double>> RealFunctions = new()
    {
        ["sin"]   = Math.Sin,
        ["cos"]   = Math.Cos,
        ["tan"]   = Math.Tan,
        ["sinh"]  = Math.Sinh,
        ["cosh"]  = Math.Cosh,
        ["tanh"]  = Math.Tanh,
        ["atan"]  = Math.Atan,
        ["exp"]   = Math.Exp,
        ["floor"] = Math.Floor,
        ["ceil"]  = Math.Ceiling,
        ["int"]   = Math.Truncate,
        ["round"] = x => Math.Round(x, MidpointRounding.AwayFromZero),
    };

    [Theory]
    [InlineData("sin")] [InlineData("cos")] [InlineData("tan")] [InlineData("sinh")] [InlineData("cosh")] [InlineData("tanh")]
    [InlineData("atan")] [InlineData("exp")] [InlineData("floor")] [InlineData("ceil")] [InlineData("int")] [InlineData("round")]
    public void ARealCube_MapsElementByElement_StaysReal_AndKeepsItsAxes(string fn)
    {
        var got = Eval($"{fn}(SP1.c)", RealCube(RealData)).AsCube();

        Assert.Equal(DataKind.Real, got.DataKind);
        AssertSameAxes(got);
        Assert.Equal(RealData.Select(RealFunctions[fn]), got.RealValues);
    }

    private static readonly Dictionary<string, Func<Complex, Complex>> ComplexFunctions = new()
    {
        ["sin"]  = Complex.Sin,
        ["cos"]  = Complex.Cos,
        ["tan"]  = Complex.Tan,
        ["exp"]  = Complex.Exp,
        ["sinh"] = Complex.Sinh,
        ["tanh"] = Complex.Tanh,
    };

    [Theory]
    [InlineData("sin")] [InlineData("cos")] [InlineData("tan")] [InlineData("exp")] [InlineData("sinh")] [InlineData("tanh")]
    public void AComplexCube_TakesTheComplexFunction_StaysComplex_AndKeepsItsAxes(string fn)
    {
        Complex[] data = [new(0, 0), new(0.3, -1), new(-2, 0.5), new(1, 1), new(0, Math.PI), new(4, -3)];
        var got = Eval($"{fn}(SP1.c)", ComplexCube(data)).AsCube();

        Assert.Equal(DataKind.Complex, got.DataKind);
        AssertSameAxes(got);
        Assert.Equal(data.Select(ComplexFunctions[fn]), got.ComplexValues);
    }

    /// <summary>The spelling the workaround used, e^(j·x), and the one it stood in for, exp(j·x), are the same value at
    /// every point — the Steering and Canceller benches' cos and sin.</summary>
    [Fact]
    public void ExpOfJTimesACube_IsEToThePowerOfIt()
    {
        var cube = RealCube(RealData);
        var exp  = Eval("exp(j*SP1.c)", cube).AsCube().ComplexValues;
        var pow  = Eval("e^(j*SP1.c)", cube).AsCube().ComplexValues;

        for (int i = 0; i < RealData.Length; i++)
        {
            Assert.Equal(Math.Cos(RealData[i]), exp[i].Real,      14);
            Assert.Equal(Math.Sin(RealData[i]), exp[i].Imaginary, 14);
            Assert.Equal(pow[i].Real,      exp[i].Real,      14);
            Assert.Equal(pow[i].Imaginary, exp[i].Imaginary, 14);
        }
        // and imag(exp(jx)) is sin x, still over both axes
        var sin = Eval("imag(exp(j*SP1.c))", cube).AsCube();
        AssertSameAxes(sin);
        for (int i = 0; i < RealData.Length; i++) Assert.Equal(Math.Sin(RealData[i]), sin.RealValues[i], 14);
    }

    /// <summary>A NaN element is a point with no value: it stays NaN, and the cube around it is computed as before.</summary>
    [Theory]
    [InlineData("sin")]
    [InlineData("exp")]
    [InlineData("round")]
    [InlineData("sign")]
    [InlineData("imag")]
    [InlineData("phase")]
    [InlineData("sqrt")]
    [InlineData("log10")]
    public void ANaNElement_StaysNaN_AndTheOthersAreComputed(string fn)
    {
        var got = Eval($"{fn}(SP1.c)", RealCube(1.0, double.NaN, 2.0, 0.5, 4.0, 3.0)).AsCube();

        Assert.Equal(DataKind.Real, got.DataKind);
        var v = got.RealValues;
        Assert.True(double.IsNaN(v[1]), $"{fn}(NaN) = {v[1]}");
        Assert.All(v.Where((_, i) => i != 1), x => Assert.False(double.IsNaN(x)));
    }

    [Fact]
    public void AComplexNaN_StaysNaN()
    {
        var got = Eval("exp(SP1.c)", ComplexCube(1, new(double.NaN, 0), 2, 3, 4, 5)).AsCube().ComplexValues;
        Assert.True(double.IsNaN(got[1].Real));
        Assert.Equal(Complex.Exp(2), got[2]);
    }

    /// <summary>imag, phase, phase_rad, conj and sign over a REAL cube give what they give a real number.</summary>
    [Fact]
    public void TheComplexHelpers_TakeARealCube_AsTheyTakeARealNumber()
    {
        var cube = RealCube(RealData);
        Assert.All(Eval("imag(SP1.c)", cube).AsCube().RealValues, x => Assert.Equal(0.0, x));
        Assert.Equal(RealData.Select(x => x >= 0 ? 0.0 : 180.0), Eval("phase(SP1.c)", cube).AsCube().RealValues);
        Assert.Equal(RealData.Select(x => x >= 0 ? 0.0 : Math.PI), Eval("phase_rad(SP1.c)", cube).AsCube().RealValues);
        Assert.Equal(RealData, Eval("conj(SP1.c)", cube).AsCube().RealValues);
        Assert.Equal(RealData.Select(x => (double)Math.Sign(x)), Eval("sign(SP1.c)", cube).AsCube().RealValues);
        AssertSameAxes(Eval("sign(SP1.c)", cube).AsCube());
    }

    [Fact]
    public void SignOfAComplexCube_IsRefused_AsSignOfAComplexNumberIs()
    {
        Assert.Throws<TypeErrorException>(() => Eval("sign(SP1.c)", ComplexCube(1, 2, 3, 4, 5, 6)));
        Assert.Throws<TypeErrorException>(() => new Evaluator().Eval("sign(j)", new Scope("t")));
    }

    // ── A number's result is unchanged ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sin(0.5)",   0.479425538604203)]
    [InlineData("cos(0.5)",   0.8775825618903728)]
    [InlineData("tan(0.5)",   0.5463024898437905)]
    [InlineData("exp(1)",     Math.E)]
    [InlineData("round(2.5)", 3.0)]
    [InlineData("sign(-3)",   -1.0)]
    [InlineData("imag(2)",    0.0)]
    [InlineData("phase(-2)",  180.0)]
    [InlineData("conj(2)",    2.0)]
    public void ANumber_KeepsItsScalarResult_AndKind(string expr, double expected)
    {
        var v = new Evaluator().Eval(expr, new Scope("t"));
        Assert.Equal(ValueKind.Real, v.Kind);
        Assert.Equal(expected, v.AsReal(), 14);
    }

    [Fact]
    public void AComplexNumber_KeepsItsComplexResult()
    {
        var v = new Evaluator().Eval("exp(j*0.5)", new Scope("t"));
        Assert.Equal(ValueKind.Complex, v.Kind);
        Assert.Equal(Complex.Exp(new Complex(0, 0.5)), v.AsComplex());
    }
}
