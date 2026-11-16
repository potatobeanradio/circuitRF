using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Synthesis;

/// <summary>The closed-form prototypes against the tabulated values (Matthaei, Young and Jones, tables 4.05-1 and
/// 4.05-2(a)).</summary>
public sealed class LowpassPrototypeTests
{
    [Fact]
    public void Butterworth_Order3_Is1_2_1()
        => Assert.Equal([1.0, 1.0, 2.0, 1.0, 1.0], LowpassPrototype.GValues(FilterResponse.Butterworth, 3), new Tol(1e-12));

    [Fact]
    public void Chebyshev_0p1dB_MatchesTheTables()
    {
        Assert.Equal([1.0, 1.0316, 1.1474, 1.0316, 1.0], LowpassPrototype.GValues(FilterResponse.Chebyshev, 3, 0.1), new Tol(1e-4));
        Assert.Equal([1.0, 1.1468, 1.3712, 1.9750, 1.3712, 1.1468, 1.0],
            LowpassPrototype.GValues(FilterResponse.Chebyshev, 5, 0.1), new Tol(1e-4));
        // An even order ends on coth²(β/4): 1.3554 for 0.1 dB.
        Assert.Equal(1.3554, LowpassPrototype.GValues(FilterResponse.Chebyshev, 4, 0.1)[5], 4);
    }

    [Fact]
    public void ABandSpec_HasItsGeometricCentre()
    {
        var s = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 2.28e9, 2.52e9);
        Assert.Equal(Math.Sqrt(2.28e9 * 2.52e9), s.CentreHz, 1);
        Assert.Equal(0.24e9 / s.CentreHz, s.FractionalBandwidth, 12);
    }

    private sealed class Tol(double eps) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= eps;
        public int GetHashCode(double x) => 0;
    }
}
