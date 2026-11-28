// Owner: PCD-12 (brief-pcd-12 R-pcd12-1).

using CircuitRF.Core.Matching;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public class Pcd12PrototypeTests
{
    /// <summary>R-pcd12-1: the LC Filter Designer's and the Match Designer's g-values, now one body in LowpassPrototype,
    /// are the bodies they replaced, bit for bit; GValues' exact Chebyshev stays its own.</summary>
    [Fact]
    public void TheMergedRecurrences_GiveTheOldNumbers_BitForBit()
    {
        foreach (int n in Enumerable.Range(1, 40))
        {
            Assert.Equal(OldButterworth(n), FilterLadder.ButterworthG(n));
            Assert.Equal(OldButterworth(n), LowpassPrototype.GValues(FilterResponse.Butterworth, n));
            foreach (double ripple in new[] { -1.0, 0.0, 0.01, 0.1, 0.5, 1.0, 3.0 })
            {
                var old = OldTableChebyshev(n, ripple);
                Assert.Equal(old, FilterLadder.ChebyshevG(n, ripple));
                Assert.Equal(old, MatchSynthesis.RippleG(n, ripple));
            }
        }
        // The exact form is a different number: merging it would have moved one designer or the other.
        Assert.NotEqual(OldTableChebyshev(5, 0.1)[1], LowpassPrototype.GValues(FilterResponse.Chebyshev, 5, 0.1)[1]);
    }

    // The bodies as they were in FilterLadder (ButterworthG, ChebyshevG) and MatchSynthesis (RippleG) before PCD-12.
    private static double[] OldButterworth(int n)
    {
        var g = new double[n + 2];
        g[0] = 1.0;
        for (int k = 1; k <= n; k++) g[k] = 2.0 * Math.Sin((2.0 * k - 1.0) * Math.PI / (2.0 * n));
        g[n + 1] = 1.0;
        return g;
    }

    private static double[] OldTableChebyshev(int n, double rippleDb)
    {
        double lar = rippleDb > 0 ? rippleDb : 0.1;
        double bb = Math.Log(1.0 / Math.Tanh(lar / 17.37));
        double gamma = Math.Sinh(bb / (2 * n));
        double th = Math.PI / (2 * n);
        var a = new double[n + 1];
        var b = new double[n + 1];
        for (int k = 1; k <= n; k++)
        {
            a[k] = Math.Sin((2 * k - 1) * th);
            b[k] = gamma * gamma + Math.Sin(k * Math.PI / n) * Math.Sin(k * Math.PI / n);
        }
        var g = new double[n + 2];
        g[0] = 1.0;
        g[1] = 2.0 * a[1] / gamma;
        for (int k = 2; k <= n; k++) g[k] = 4.0 * a[k - 1] * a[k] / (b[k - 1] * g[k - 1]);
        double coth = 1.0 / Math.Tanh(bb / 4.0);
        g[n + 1] = n % 2 == 0 ? coth * coth : 1.0;
        return g;
    }
}
