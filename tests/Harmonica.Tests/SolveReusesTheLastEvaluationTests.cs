using System;
using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// <see cref="HarmonicaContext.Solve"/> reports the Newton solve's own last evaluation instead of evaluating the devices a
/// second time at the same iterate (LP-1). The gate is the claim itself: what the removed second evaluation returned —
/// the conduction spectrum, and the total it was folded into — equals what is reported, bit for bit, on a converged solve
/// and on one stopped at its iteration cap.
/// </summary>
public sealed class SolveReusesTheLastEvaluationTests
{
    [Theory]
    [InlineData("sdd", 100)]
    [InlineData("native", 100)]
    [InlineData("composed", 100)]
    [InlineData("sdd", 2)]
    public void TheReportedSpectraAreTheDevicesAtTheReportedIterate(string kind, int maxIter)
    {
        var model = HarmonicaCostTests.Model(kind);
        model = model with { Settings = model.Settings with { MaxIter = maxIter } };
        var ctx = HarmonicaContext.Create(model);
        int K = model.Settings.HarmonicCount, N = ctx.Interface.InterfaceCount;
        var terms = new TerminationSet(K);
        terms.Set(TerminationSide.Source, 1, new Complex(50, 0));
        double pin = model.Settings.PinStartDbm + 5;
        var seed = ctx.Solve(terms, pin - 1);
        var pt = ctx.Solve(terms, pin, seed.V);
        Assert.Equal(maxIter > 2, pt.Converged);

        int gridN = HbFft.GridSize(K, model.Settings.FftOverSample);
        var (iNl, qNl, _, _, buckets) = HbNewton.EvaluateNonlinear(pt.V, N, K, gridN, ctx.Netlist, ctx.Interface.DeviceNodes);
        double omega0 = 2.0 * Math.PI * model.Settings.FrequencyHz;
        for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
            {
                Complex t = iNl[n, k];
                if (k > 0) t += new Complex(0, k * omega0) * qNl[n, k];
                foreach (var b in buckets) t += b.Model.Weight(b.W, k * omega0) * b.WNl[n, k];
                Same(iNl[n, k], pt.INl[n, k]);
                Same(t, pt.INlTotal[n, k]);
            }
    }

    private static void Same(Complex expected, Complex actual)
    {
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Real), BitConverter.DoubleToInt64Bits(actual.Real));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Imaginary), BitConverter.DoubleToInt64Bits(actual.Imaginary));
    }
}
