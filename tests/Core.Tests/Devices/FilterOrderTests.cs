using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

public class FilterOrderTests
{
    [Fact]
    public void AButterworthLowpassOrderMatchesTheClosedForm()
    {
        // Ap ≈ 3.01 dB (ε=1), As = 40 dB, Fs/Fp = 2 → n = log(εs/εp)/(2 log 2) ≈ 3.32 → 4.
        int? n = FilterOrder.Minimum(FilterResponse.Butterworth, NetworkForm.Lowpass,
                                     3.0102999566, 40, fpHz: 1e9, fsHz: 2e9,
                                     0, 0, 0, 0);
        Assert.Equal(4, n);
    }

    [Fact]
    public void AnEllipticLowpassNeedsFewerSectionsThanButterworth()
    {
        int? butter = FilterOrder.Minimum(FilterResponse.Butterworth, NetworkForm.Lowpass,
                                          0.1, 40, 1e9, 2e9, 0, 0, 0, 0);
        int? ellip = FilterOrder.Minimum(FilterResponse.Elliptic, NetworkForm.Lowpass,
                                         0.1, 40, 1e9, 2e9, 0, 0, 0, 0);
        Assert.NotNull(butter);
        Assert.NotNull(ellip);
        Assert.True(ellip < butter, $"elliptic {ellip} should beat Butterworth {butter}");
    }

    [Fact]
    public void BesselHasNoMagnitudeOrderFormula()
    {
        Assert.Null(FilterOrder.Minimum(FilterResponse.Bessel, NetworkForm.Lowpass,
                                        3, 40, 1e9, 2e9, 0, 0, 0, 0));
    }

    [Fact]
    public void APassbandBeyondTheStopbandIsRefused()
    {
        Assert.Null(FilterOrder.Minimum(FilterResponse.Chebyshev, NetworkForm.Lowpass,
                                        0.1, 40, fpHz: 2e9, fsHz: 1e9, 0, 0, 0, 0));
    }

    /// <summary>
    /// Auto picks the smallest order whose type AS BUILT meets the mask — an even order is type c,
    /// whose stop edge sits above type a's — and that order is a ladder that meets it.
    /// </summary>
    [Fact]
    public void AnAutoEllipticMinimumIsTheSmallestLadderThatMeetsTheMask()
    {
        var rng = new Random(20261121);
        for (int trial = 0; trial < 50; trial++)
        {
            double ap = new[] { 0.01, 0.1, 0.25, 0.5, 1.0 }[rng.Next(5)];
            double astop = 20 + 60 * rng.NextDouble();
            double ws = 1.05 + 1.5 * rng.NextDouble();
            int? n = FilterOrder.Minimum(FilterResponse.Elliptic, NetworkForm.Lowpass, ap, astop,
                                         1e9, ws * 1e9, 0, 0, 0, 0, EllipticType.Auto);
            Assert.NotNull(n);
            string spec = $"Ap {ap} dB, As {astop:F2} dB, Ωs {ws:F4}: order {n}";

            var built = FilterLadder.Synthesize(FilterResponse.Elliptic, NetworkForm.Lowpass, n!.Value,
                                                1e9, 0, 0, ap, 50, FilterFirstElement.Parallel, astop);
            Assert.True(built.Ok, $"{spec}: {built.Refusal?.Message}");
            Assert.True(WorstStopDb(n.Value, ap, astop, ws) >= astop - 1e-6, $"{spec} misses the mask");
            if (n > 1)
                Assert.True(WorstStopDb(n.Value - 1, ap, astop, ws) < astop - 1e-6,
                            $"{spec}: order {n - 1} already meets the mask");
        }
    }

    /// <summary>The least attenuation over the stopband of the Auto-type ideal response, dB.</summary>
    private static double WorstStopDb(int n, double ap, double astop, double ws)
    {
        var ideal = FilterNetwork.Create(FilterResponse.Elliptic, NetworkForm.Lowpass, n, 1e9, 0, 0,
                                         ap, astop, 0, EllipticTypes.Resolve(EllipticType.Auto, n));
        double worst = double.PositiveInfinity;
        for (int i = 0; i <= 2000; i++)
        {
            double w = ws * Math.Pow(100.0, i / 2000.0);
            double db = -20 * Math.Log10(ideal.At(2 * Math.PI * 1e9 * w).S21.Magnitude);
            worst = Math.Min(worst, db);
        }
        return worst;
    }
}
