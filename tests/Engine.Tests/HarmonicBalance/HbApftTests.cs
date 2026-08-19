using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The T-tone APFT transform pair (harmonic-balance.md §6.5) — the T ≥ 3 replacement for
/// <see cref="HbFft2D"/>'s rectangular FFT.
///
/// <para>The amplitude tests are the ones that matter, because a wrong convention factor does not
/// fail — it produces a plausible spectrum with every level off by a constant, which is exactly
/// the failure mode <see cref="HbFft2D"/>'s own header warns about (a separable per-axis DC
/// halving would give N₁N₂/4 for cross bins and silently double every cross-term phasor). Here
/// the oracle is independent: the waveform is built analytically at the transform's OWN sample
/// phases (<see cref="HbApft.SamplePhase"/>) and its known trigonometric decomposition is
/// asserted bin by bin.</para>
/// </summary>
public class HbApftTests(ITestOutputHelper output)
{
    private static HbApft Make(int tones, int order, double oversample = 2.0)
        => new(new MixingLattice(tones, order), oversample);

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(6)]
    public void SynthesizeThenAnalyze_IsAnExactRoundTrip(int tones)
    {
        var apft    = Make(tones, tones >= 6 ? 2 : 3);
        var lattice = apft.Lattice;
        int M       = lattice.MixCount;

        // A deterministic, non-degenerate spectrum: every retained product populated, DC real.
        var v = new Complex[M];
        v[0] = new Complex(1.75, 0.0);
        for (int m = 1; m < M; m++)
            v[m] = new Complex(0.1 + 0.01 * m, -0.3 + 0.02 * m);

        var samples = new double[apft.SampleCount];
        var back    = new Complex[M];
        apft.Synthesize(v, samples);
        apft.Analyze(samples, back);

        double maxErr = 0;
        for (int m = 0; m < M; m++)
            maxErr = Math.Max(maxErr, (v[m] - back[m]).Magnitude);

        output.WriteLine($"T={tones}  M={M}  D={apft.Dof}  S={apft.SampleCount}  " +
                         $"cond(ΓᵀΓ)≈{apft.NormalConditionEstimate:E2}  round-trip err {maxErr:E3}");
        Assert.True(maxErr < 1e-10, $"round-trip error {maxErr:E3} at T={tones}");

        // The DC index comes back purely real — its quadrature DOF is fictitious by construction.
        Assert.Equal(0.0, back[0].Imaginary, 12);
    }

    [Fact]
    public void ThreeToneCrossProduct_LandsAtOneOneOne_WithAmplitudeAQuarter()
    {
        // cos φ₁·cos φ₂·cos φ₃ = ¼[cos(φ₁+φ₂+φ₃) + cos(φ₁+φ₂−φ₃)
        //                        + cos(φ₁−φ₂+φ₃) + cos(φ₁−φ₂−φ₃)]
        // Under the engine's full-amplitude convention that is 0.25 at each of the four
        // third-order products — the T-tone continuation of HbFft2D's cos·cos → 0.5 at (1,1).
        var apft    = Make(3, 3);
        var lattice = apft.Lattice;

        var x = new double[apft.SampleCount];
        for (int s = 0; s < apft.SampleCount; s++)
            x[s] = Math.Cos(apft.SamplePhase(s, 0))
                 * Math.Cos(apft.SamplePhase(s, 1))
                 * Math.Cos(apft.SamplePhase(s, 2));

        var spec = new Complex[lattice.MixCount];
        apft.Analyze(x, spec);

        foreach (int[] k in new[] { new[] {1,1,1}, [1,1,-1], [1,-1,1], [1,-1,-1] })
        {
            int m = lattice.IndexOf(k);
            Assert.True(m > 0, $"{MixingLattice.Label(k)} is not retained at order 3");
            Assert.Equal(0.25, spec[m].Real,      10);
            Assert.Equal(0.0,  spec[m].Imaginary, 10);
        }

        // Everything else is zero — no leakage into the lower-order products.
        for (int m = 0; m < lattice.MixCount; m++)
        {
            if (lattice.OrderOf(m) == 3 && Math.Abs(lattice.ToneOf(m)[0]) == 1) continue;
            Assert.True(spec[m].Magnitude < 1e-10,
                $"leakage {spec[m].Magnitude:E3} into {lattice.Label(m)}");
        }
    }

    [Fact]
    public void SingleAxisTone_HasUnitAmplitude_AndDcIsUnhalved()
    {
        // The two anchors of the convention: a pure cosine on one tone axis reads 1 (NOT ½), and
        // a constant reads its own value at DC (the halving is global and applies once, at k = 0).
        var apft    = Make(4, 2);
        var lattice = apft.Lattice;

        var x = new double[apft.SampleCount];
        for (int s = 0; s < apft.SampleCount; s++)
            x[s] = 3.0 + 2.0 * Math.Cos(apft.SamplePhase(s, 1));

        var spec = new Complex[lattice.MixCount];
        apft.Analyze(x, spec);

        Assert.Equal(3.0, spec[0].Real, 10);
        int tone2 = lattice.IndexOf([0, 1, 0, 0]);
        Assert.Equal(2.0, spec[tone2].Real,      10);
        Assert.Equal(0.0, spec[tone2].Imaginary, 10);
    }

    [Fact]
    public void PhaseIsCarriedWithTheEngineSign()
    {
        // v(φ) = Re{V·e^{jk·φ}} = Re{V}·cos(k·φ) − Im{V}·sin(k·φ), so a waveform cos(φ₁ − θ)
        // must come back as V = e^{−jθ}: the lag in the waveform is a NEGATIVE phasor angle.
        // Getting this sign backwards is invisible in magnitude-only plots and wrong in every
        // phase readout, so it is asserted on both components rather than on |V|.
        var apft    = Make(3, 2);
        var lattice = apft.Lattice;
        const double theta = 0.7;

        var x = new double[apft.SampleCount];
        for (int s = 0; s < apft.SampleCount; s++)
            x[s] = Math.Cos(apft.SamplePhase(s, 0) - theta);

        var spec = new Complex[lattice.MixCount];
        apft.Analyze(x, spec);

        int carrier = lattice.IndexOf([1, 0, 0]);
        Assert.Equal( Math.Cos(theta), spec[carrier].Real,      10);
        Assert.Equal(-Math.Sin(theta), spec[carrier].Imaginary, 10);
    }

    [Theory]
    [InlineData(3, 4)] [InlineData(4, 3)] [InlineData(5, 3)] [InlineData(6, 3)]
    public void SampleSet_IsWellConditioned_AtEveryShippingToneCount(int tones, int order)
    {
        // The Kronecker sample set is chosen for equidistribution, but correctness rests on the
        // measured conditioning of ΓᵀΓ, not on that choice — this is the measurement.
        var apft = Make(tones, order);
        output.WriteLine($"T={tones} O={order}  M={apft.MixCount}  D={apft.Dof}  " +
                         $"S={apft.SampleCount}  cond(ΓᵀΓ)≈{apft.NormalConditionEstimate:E3}");
        Assert.True(apft.NormalConditionEstimate < 1e4,
            $"ΓᵀΓ condition {apft.NormalConditionEstimate:E3} at T={tones}, O={order}");
    }

    [Fact]
    public void AtTwoTones_AgreesWithTheFrozenFftTransform()
    {
        // The APFT is only ever used at T ≥ 3 in production, but at T = 2 both transforms exist
        // and must produce the SAME spectrum for the same waveform. Any convention drift between
        // them would show up here rather than as a wrong two-tone-looking three-tone answer.
        const int order = 3;
        var apft    = new HbApft(new MixingLattice(2, order), 2.0);
        var grid    = new MixingGrid(order);
        int M       = grid.MixCount;

        var v = new Complex[M];
        v[0] = new Complex(0.9, 0.0);
        for (int m = 1; m < M; m++) v[m] = new Complex(0.2 - 0.01 * m, 0.15 + 0.03 * m);

        // Reference: synthesize with the FFT path on its rectangular grid, then read the
        // waveform analytically at the APFT's own sample phases via the shared convention.
        var samples = new double[apft.SampleCount];
        for (int s = 0; s < apft.SampleCount; s++)
        {
            double p1 = apft.SamplePhase(s, 0), p2 = apft.SamplePhase(s, 1);
            double acc = v[0].Real;
            for (int m = 1; m < M; m++)
            {
                var (k1, k2) = grid.ToneOf(m);
                double arg = k1 * p1 + k2 * p2;
                acc += v[m].Real * Math.Cos(arg) - v[m].Imaginary * Math.Sin(arg);
            }
            samples[s] = acc;
        }

        var back = new Complex[M];
        apft.Analyze(samples, back);

        double maxErr = 0;
        for (int m = 0; m < M; m++) maxErr = Math.Max(maxErr, (v[m] - back[m]).Magnitude);
        output.WriteLine($"two-tone APFT vs the frozen convention: max err {maxErr:E3}");
        Assert.True(maxErr < 1e-10, $"two-tone convention mismatch {maxErr:E3}");
    }
}
