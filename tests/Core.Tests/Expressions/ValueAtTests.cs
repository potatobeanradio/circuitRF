using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// <c>value_at(x, v [, "axis"])</c>: one point by its axis VALUE — a grid point read straight off, a value between
/// two points interpolated linearly, other axes kept, and a value off the axis refused with its extent.
/// </summary>
public sealed class ValueAtTests
{
    private static Evaluator Evaluator(out Scope scope)
    {
        var freq = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var pin  = new Axis("Pin", [-10, 0], "dBm");
        var ds = new DataSet();
        ds.Add("G",  new DataCube([freq], [10.0, 14.0, 12.0]) { Unit = "dB" });
        ds.Add("S",  new DataCube([freq], new Complex[] { new(1, 0), new(0, 1), new(-1, 0) }));
        // [Pin, freq]: Pin = −10 → 1, 2, 3; Pin = 0 → 4, 6, 8.
        ds.Add("Gp", new DataCube([pin, freq], [1.0, 2.0, 3.0, 4.0, 6.0, 8.0]));
        scope = new Scope("t");
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));
    }

    private static Value Eval(string expr) => Evaluator(out var s).Eval(expr, s);

    [Theory]
    [InlineData("value_at(SP1.G, 2GHz)",    14.0)]   // a grid point
    [InlineData("value_at(SP1.G, 1.5GHz)",  12.0)]   // halfway between 10 and 14
    [InlineData("value_at(SP1.G, 2.25GHz)", 13.5)]   // a quarter of the way from 14 to 12
    [InlineData("value_at(value_at(SP1.Gp, -5, \"Pin\"), 3GHz)", 5.5)]   // a named axis, then freq: halfway 3 → 8
    public void ReadsThePointByItsValue(string expr, double expected)
        => Assert.Equal(expected, Eval(expr).AsReal(), 12);

    [Fact]
    public void AComplexValue_IsInterpolatedAsAComplexNumber()
        => Assert.Equal(new Complex(0.5, 0.5), Eval("value_at(SP1.S, 1.5GHz)").AsComplex());

    [Fact]
    public void EveryOtherAxisIsKept()
    {
        var c = Eval("value_at(SP1.Gp, 1.5GHz)").AsCube();
        Assert.Equal("Pin", Assert.Single(c.Axes).Name);
        Assert.Equal([1.5, 5.0], c.RealValues);
    }

    [Fact]
    public void AValueOffTheAxis_IsRefusedWithItsExtent()
    {
        var ex = Assert.Throws<ExpressionException>(() => Eval("value_at(SP1.G, 5GHz)"));
        Assert.Contains("runs from 1E+09 to 3E+09 Hz", ex.Message);
    }
}
