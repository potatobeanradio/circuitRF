using System.Collections.Generic;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Brief NZ-9 — <c>deriv()</c>, the slope of a result along one of its axes (a tuning curve's Kvco). A parabola on an uneven
/// grid is differentiated exactly at every interior point, along the named axis only, with the other axis kept.
/// </summary>
public class DerivTests
{
    [Fact]
    public void AParabolaOnAnUnevenGrid_IsExactInside_AndAlongTheNamedAxisOnly()
    {
        double[] x = [1.0, 1.5, 3.0, 3.25, 5.0];
        var bias = new Axis("Vb", x, "V");
        var harm = new Axis("harmonic", [0.0, 1.0]);
        var y = new double[x.Length * 2];
        for (int i = 0; i < x.Length; i++) { y[2 * i] = 2 * x[i] * x[i] - 3 * x[i]; y[2 * i + 1] = 7; }
        var ds = new DataSet();
        ds.Add("Fosc", new DataCube([bias, harm], y));
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SW1"] = ds }));

        var d = ev.Eval("deriv(SW1.Fosc, \"Vb\")", new Scope("t")).AsCube().RealValues;
        for (int i = 1; i < x.Length - 1; i++)
        {
            Assert.Equal(4 * x[i] - 3, d[2 * i], 12);   // d/dx (2x² − 3x)
            Assert.Equal(0, d[2 * i + 1], 12);          // the constant column stays flat
        }
        Assert.Equal((y[2] - y[0]) / (x[1] - x[0]), d[0], 12);   // one-sided at the ends
    }
}
