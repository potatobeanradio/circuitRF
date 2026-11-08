// ================================================================
//  AbsOnACubeTests.cs — abs() over a swept quantity
//
//  Found writing the TRL and De-embedding example (DE-5): abs(SP1.S(2,1) - SP1.S(4,3)) failed with
//  "Value is Cube, not Complex", while mag() of the same cube worked. The expression reference lists
//  abs and mag as one function, so abs takes the cube the same way.
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class AbsOnACubeTests
{
    private static readonly Axis Freq = new("freq", [1e9, 2e9], "Hz");

    private static DataCube Eval(string expr, DataCube x)
    {
        var ds = new DataSet();
        ds.Add("x", x);
        var ctx = new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds });
        return new Evaluator(ctx).Eval(expr, new Scope("t")).AsCube();
    }

    [Fact]
    public void AbsOfACube_IsMagOfIt_ComplexAndReal()
    {
        var c = new DataCube([Freq], new Complex[] { new(3, 4), new(0, -2) });
        Assert.Equal(Eval("mag(SP1.x)", c).RealValues, Eval("abs(SP1.x)", c).RealValues);
        Assert.Equal([5.0, 2.0], Eval("abs(SP1.x)", c).RealValues);

        var r = new DataCube([Freq], new[] { -1.5, 2.0 });
        Assert.Equal([1.5, 2.0], Eval("abs(SP1.x)", r).RealValues);
    }
}
