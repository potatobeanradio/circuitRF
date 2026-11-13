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
}
