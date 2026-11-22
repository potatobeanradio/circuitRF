using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

public class FilterOrderTests
{
    [Fact]
    public void AButterworthLowpassOrderMatchesTheClosedForm()
    {
        // Ap ≈ 3.01 dB (ε=1), As = 40 dB, Fs/Fp = 2.
        int? n = FilterOrder.Minimum(FilterResponse.Butterworth, NetworkForm.Lowpass,
                                     3.0102999566, 40, fpHz: 1e9, fsHz: 2e9,
                                     0, 0, 0, 0);
        // Ω_s^n ≥ εs/εp = √(10⁴ − 1): n ≥ log(99.995)/log 2 = 6.64.
        Assert.Equal(7, n);
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

    /// <summary>
    /// A bandstop mask — Fp1 &lt; Fs1 &lt; Fs2 &lt; Fp2 — has an order, and that order built the way the
    /// Designer builds it (at Fp1/Fp2; inverse Chebyshev at Fs1/Fs2) meets the mask at all four
    /// edges, while one order fewer misses it. The mapping used to take its centre from the stop
    /// edges and returned no order for any bandstop at all.
    /// </summary>
    [Theory]
    [InlineData(FilterResponse.Butterworth)]
    [InlineData(FilterResponse.Chebyshev)]
    [InlineData(FilterResponse.Elliptic)]
    [InlineData(FilterResponse.InvChebyshev)]
    public void ABandstopMaskHasAnOrderThatMeetsIt(FilterResponse response)
    {
        // A Butterworth edge is its 3.01 dB point, which is what Ap has to say for the order to
        // be about the filter the Designer builds.
        double ap = response == FilterResponse.Butterworth ? 3.0103 : 0.5;
        const double astop = 40;
        // Geometrically symmetric about 1.5 GHz, so the stop and the pass edges share a centre.
        double f0 = 1.5e9, fs1 = 1.3e9, fs2 = f0 * f0 / fs1, fp1 = 1.0e9, fp2 = f0 * f0 / fp1;
        int? n = FilterOrder.Minimum(response, NetworkForm.Bandstop, ap, astop, 0, 0,
                                     fp1, fp2, fs1, fs2, EllipticType.Auto);
        Assert.NotNull(n);

        double Db(int order, double f)
        {
            bool inv = response == FilterResponse.InvChebyshev;
            var net = FilterNetwork.Create(response, NetworkForm.Bandstop, order, 0,
                                           inv ? fs1 : fp1, inv ? fs2 : fp2, ap, astop, 0,
                                           EllipticTypes.Resolve(EllipticType.Auto, order));
            return -20 * Math.Log10(net.At(2 * Math.PI * f).S21.Magnitude);
        }
        double passLimit = ap;
        foreach (double f in new[] { fp1, fp2 })
            Assert.True(Db(n!.Value, f) <= passLimit + 1e-6, $"{response} n={n}: {Db(n.Value, f):F3} dB at Fp {f:G4}");
        foreach (double f in new[] { fs1, fs2 })
            Assert.True(Db(n!.Value, f) >= astop - 1e-6, $"{response} n={n}: {Db(n.Value, f):F3} dB at Fs {f:G4}");
        // One order fewer misses: at Fs for the families built at Fp, at Fp for inverse Chebyshev,
        // whose floor sits at Fs by construction.
        if (n > 1 && response != FilterResponse.Elliptic)
            Assert.True(response == FilterResponse.InvChebyshev
                            ? Db(n.Value - 1, fp1) > ap + 1e-6
                            : Db(n.Value - 1, fs1) < astop - 1e-6,
                        $"{response}: order {n - 1} already meets the mask");
    }

    [Fact]
    public void ABandstopWhoseEdgesAreOutOfOrderHasNoOrder()
    {
        Assert.Null(FilterOrder.Minimum(FilterResponse.Chebyshev, NetworkForm.Bandstop, 0.5, 40, 0, 0,
                                        fp1Hz: 1.4e9, fp2Hz: 2.5e9, fs1Hz: 1.0e9, fs2Hz: 2e9));
    }

}
