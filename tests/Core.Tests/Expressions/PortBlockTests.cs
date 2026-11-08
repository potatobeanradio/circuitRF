// ================================================================
//  PortBlockTests.cs — SP1.S(lo:hi, lo:hi) by port number, and the bracket range it replaces
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class PortBlockTests
{
    /// <summary>An S cube over <paramref name="ports"/>, each entry 10*i + j by port number.</summary>
    private static Evaluator With(double[] ports)
    {
        int n = ports.Length;
        var v = new Complex[n * n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) v[i * n + j] = 10 * ports[i] + ports[j];
        var ds = new DataSet();
        ds.Add("S", new DataCube([new Axis("freq", [1e9], "Hz"), new Axis("i", ports, "port"), new Axis("j", ports, "port")], v));
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));
    }

    [Fact]
    public void ARange_TakesThePortsInsideIt_WhenTheBenchSkipsNumbers()
    {
        var r = With([1, 2, 9, 10]).Eval("SP1.S(9:10, 9:10)", new Scope("t")).AsCube();

        Assert.Equal([1.0, 2.0], r.Axes[1].Values);
        Assert.Equal([new Complex(99, 0), new Complex(100, 0), new Complex(109, 0), new Complex(110, 0)], r.ComplexValues);
    }

    [Fact]
    public void ABracketRangeOnAPortAxis_IsRefused_NamingTheParenthesesForm()
    {
        var ex = Assert.Throws<ExpressionException>(() => With([1, 2, 3, 4]).Eval("SP1.S[:, 1:2, 1:2]", new Scope("t")));

        Assert.Equal("measure.ports.bracket-range", ex.Diagnostic!.Id);
        Assert.Contains("SP1.S(1:2, 1:2)", ex.Message);
    }

    [Fact]
    public void ARangeOutsideAPortMatrix_IsRefused()
    {
        var ex = Assert.Throws<ExpressionException>(() => With([1, 2]).Eval("max(1:2, 3)", new Scope("t")));

        Assert.Equal("measure.ports.range-misplaced", ex.Diagnostic!.Id);
    }
}
