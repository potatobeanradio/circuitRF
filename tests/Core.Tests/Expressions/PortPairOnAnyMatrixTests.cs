// ================================================================
//  PortPairOnAnyMatrixTests.cs — X(i, j) by port number on any cube with port axes, not only S
//
//  Found writing the TRL and De-embedding example (DE-5): CAL.SA(2,1), a TRL fixture's S21, failed with "Expected 3
//  slice arguments, got 2", and so did SP1.Y(1,2) and SP1.Z(1,2), though SP1.S(2,1) worked. Each is a matrix over
//  port axes i and j, and (i, j) means the same thing on all of them.
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class PortPairOnAnyMatrixTests
{
    [Theory]
    [InlineData("SA")]
    [InlineData("Y")]
    public void APortPair_ReadsTheEntryByPortNumber_KeepingFrequency(string name)
    {
        var freq = new Axis("freq", [1e9, 2e9], "Hz");
        var ports = new[] { 1.0, 2.0 };
        // value = 10*i + j + 100*f, so each entry says where it came from
        var v = new Complex[2 * 2 * 2];
        for (int f = 0; f < 2; f++) for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
            v[(f * 2 + i) * 2 + j] = new Complex(10 * (i + 1) + (j + 1) + 100 * f, 0);
        var ds = new DataSet();
        ds.Add(name, new DataCube([freq, new Axis("i", ports, "port"), new Axis("j", ports, "port")], v));
        var ctx = new MeasurementContext(new Dictionary<string, DataSet> { ["CAL"] = ds });

        var r = new Evaluator(ctx).Eval($"CAL.{name}(2,1)", new Scope("t")).AsCube();

        Assert.Equal("freq", r.Axes[0].Name);
        Assert.Equal([new Complex(21, 0), new Complex(121, 0)], r.ComplexValues);
    }
}
