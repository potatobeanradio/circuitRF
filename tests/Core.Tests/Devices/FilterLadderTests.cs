using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The ladder underneath the ideal filter. Expectations are the closed-form g-values and the
/// denormalisation identities, never the continued fraction that produced the ladder.
/// </summary>
public class FilterLadderTests
{
    /// <summary><c>g_k = 2 sin((2k−1)π / 2n)</c>, and the two terminations are 1.</summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(7)]
    public void ButterworthGIsTheSineFormula(int n)
    {
        var proto = FilterPrototype.Create(FilterResponse.Butterworth, n);
        var g = FilterLadder.PrototypeG(proto);
        Assert.NotNull(g);
        Assert.Equal(n + 2, g!.Length);
        Assert.Equal(1.0, g[0], 9);
        for (int k = 1; k <= n; k++)
        {
            double expected = 2.0 * Math.Sin((2.0 * k - 1.0) * Math.PI / (2.0 * n));
            Assert.Equal(expected, g[k], 6);
        }
        // The raw last entry is a resistance after an even order and a conductance after an odd
        // one. Either way the termination is 1.
        double load = n % 2 == 0 ? g[^1] : 1.0 / g[^1];
        Assert.Equal(1.0, load, 6);
    }

    [Fact]
    public void ALowpassScalesByROverOmega()
    {
        const double fc = 1e9, r = 50;
        var built = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Lowpass, 3,
            fc, 0, 0, 0.1, r, FilterFirstElement.Series);
        Assert.True(built.Ok, built.Refusal?.Message);
        var el = built.Realization!.Elements;
        Assert.Equal(3, el.Count);

        double w = 2 * Math.PI * fc;
        // g = 1, 2, 1. Series L, shunt C, series L.
        Assert.True(el[0].Inductor && !el[0].Shunt);
        Assert.Equal(1.0 * r / w, el[0].Value, 6);
        Assert.True(!el[1].Inductor && el[1].Shunt);
        Assert.Equal(2.0 / (r * w), el[1].Value, 6);
        Assert.True(el[2].Inductor && !el[2].Shunt);
        Assert.Equal(1.0 * r / w, el[2].Value, 6);
        Assert.Equal(r, built.Realization.LoadOhms, 6);
        Assert.Equal(el[2].NodeB, FilterLadder.LoadNode(el));
        Assert.NotEqual("p1", FilterLadder.LoadNode(el));
    }

    [Fact]
    public void AHighpassExchangesLAndC()
    {
        const double fc = 1e9, r = 75;
        var lp = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Lowpass, 3,
            fc, 0, 0, 0, r, FilterFirstElement.Series);
        var hp = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Highpass, 3,
            fc, 0, 0, 0, r, FilterFirstElement.Series);
        Assert.True(lp.Ok && hp.Ok);
        // Each lowpass L becomes a highpass C of 1/(ω² L), and each lowpass C a highpass L of
        // 1/(ω² C). That is the element substitution, checked on the values rather than re-derived.
        double w = 2 * Math.PI * fc;
        for (int i = 0; i < 3; i++)
        {
            Assert.NotEqual(lp.Realization!.Elements[i].Inductor, hp.Realization!.Elements[i].Inductor);
            double a = lp.Realization.Elements[i].Value, b = hp.Realization.Elements[i].Value;
            Assert.Equal(1.0 / (w * w), a * b, 8);
        }
    }

    [Fact]
    public void ABandpassDoublesTheElementCountAndResonatesAtTheCentre()
    {
        const double f1 = 1e9, f2 = 2e9, r = 50;
        var built = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Bandpass, 3,
            0, f1, f2, 0, r, FilterFirstElement.Series);
        Assert.True(built.Ok, built.Refusal?.Message);
        Assert.Equal(6, built.Realization!.Elements.Count);
        double w0 = 2 * Math.PI * Math.Sqrt(f1 * f2);
        // Arms come in pairs that resonate at w0: L then C in series, then C and L in shunt.
        AssertResonant(built.Realization.Elements[0], built.Realization.Elements[1], w0);
        AssertResonant(built.Realization.Elements[2], built.Realization.Elements[3], w0);
        AssertResonant(built.Realization.Elements[4], built.Realization.Elements[5], w0);
    }

    [Fact]
    public void ABandstopStopsAtTheCentreAndPassesAtDc()
    {
        const double f1 = 1e9, f2 = 2e9;
        var ideal = FilterNetwork.Create(
            FilterResponse.Butterworth, NetworkForm.Bandstop, 3, 0, f1, f2, 0, 0, 0);
        double w0 = 2 * Math.PI * Math.Sqrt(f1 * f2);
        Assert.Equal(1.0, ideal.At(0).S21.Real, 9);
        Assert.True(ideal.At(w0).S21.Magnitude < 1e-9);
        // Geometric symmetry about the centre, the same identity the bandpass carries.
        foreach (double f in new[] { 0.4e9, 0.8e9, 1.2e9, 3e9 })
        {
            double mirror = (f1 * f2) / f;
            Assert.Equal(ideal.At(2 * Math.PI * f).S21.Magnitude,
                         ideal.At(2 * Math.PI * mirror).S21.Magnitude, 9);
        }

        var built = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Bandstop, 2,
            0, f1, f2, 0, 50, FilterFirstElement.Series);
        Assert.True(built.Ok, built.Refusal?.Message);
        Assert.Equal(4, built.Realization!.Elements.Count);
    }

    [Fact]
    public void ParallelFirstStartsWithAShunt()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Butterworth, NetworkForm.Lowpass, 3,
            1e9, 0, 0, 0, 50, FilterFirstElement.Parallel);
        Assert.True(built.Ok, built.Refusal?.Message);
        Assert.True(built.Realization!.Elements[0].Shunt);
        Assert.False(built.Realization.Elements[0].Inductor);
    }

    [Fact]
    public void AnAxisZeroFamilyIsRefusedByName()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Lowpass, 3,
            1e9, 0, 0, 0.1, 50, FilterFirstElement.Series);
        Assert.False(built.Ok);
        Assert.Contains("Elliptic", built.Refusal!.Message);
    }

    [Fact]
    public void GaussianIsThreeDbAtTheCutoffAndAThroughAtDc()
    {
        foreach (int n in new[] { 2, 4, 6 })
        {
            var p = FilterPrototype.Create(FilterResponse.Gaussian, n);
            var (s11, s21, _) = p.At(0);
            Assert.Equal(1.0, s21.Real, 9);
            Assert.Equal(0.0, s11.Magnitude, 9);
            Assert.Equal(-10.0 * Math.Log10(2.0), 20.0 * Math.Log10(p.At(1).S21.Magnitude), 6);
        }
    }

    private static void AssertResonant(FilterElement a, FilterElement b, double w0)
    {
        Assert.NotEqual(a.Inductor, b.Inductor);
        double l = a.Inductor ? a.Value : b.Value;
        double c = a.Inductor ? b.Value : a.Value;
        double w = 1.0 / Math.Sqrt(l * c);
        Assert.True(Math.Abs(w - w0) / w0 < 1e-6, $"resonant at {w:G6}, centre {w0:G6}");
    }
}
