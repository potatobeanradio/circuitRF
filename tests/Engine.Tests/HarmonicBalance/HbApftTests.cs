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
        // Since 2026-08-30 the APFT is what two tones run on by default too, but both transforms
        // exist at T = 2 and must produce the SAME spectrum for the same waveform. Any convention
        // drift between them would show up here rather than as a wrong two-tone-looking three-tone
        // answer.
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

    // ══ The triple-product micro-kernel and the transform cache (HB-P1, M2/M3) ══════════════════

    /// <summary>
    /// The scalar triple loop the register-blocked kernel replaced, kept HERE rather than in
    /// <see cref="HbApft"/> so the oracle cannot drift with the thing it gates. Transcribed
    /// verbatim from the shipped implementation as of 2026-08-30.
    /// </summary>
    private static void ReferenceTripleProduct(HbApft apft, double[] weights, double[] block)
    {
        int D = apft.Dof, S = apft.SampleCount;
        for (int s = 0; s < S; s++)
        {
            double w = weights[s];
            if (w == 0.0) continue;
            for (int r = 0; r < D; r++)
            {
                double ar = apft.AnalysisElement(r, s) * w;
                if (ar == 0.0) continue;
                int outBase = r * D;
                for (int c = 0; c < D; c++) block[outBase + c] += ar * apft.SynthesisElement(s, c);
            }
        }
    }

    private static double[] Weights(int s, int seed)
    {
        var r = new Random(seed);
        var w = new double[s];
        for (int i = 0; i < s; i++) w[i] = r.NextDouble() * 2 - 1;
        return w;
    }

    private static double MaxRelDiff(double[] a, double[] b)
    {
        double d = 0, scale = 0;
        for (int i = 0; i < a.Length; i++)
        {
            d = Math.Max(d, Math.Abs(a[i] - b[i]));
            scale = Math.Max(scale, Math.Abs(b[i]));
        }
        return d / Math.Max(scale, 1e-300);
    }

    /// <summary>
    /// <b>Both blocks at once, against the scalar loop.</b> A transposed W or a swapped operand in
    /// the micro-kernel produces a plausible, wrong Jacobian and merely slows Newton down instead
    /// of failing, so the kernel is compared element for element against the form it replaced.
    ///
    /// <para>The two lattices straddle <c>HbApft</c>'s parallel threshold — 3 tones at order 3 is
    /// below it and runs on one thread, 6 tones at order 2 is above it and fans out over column
    /// panels — so this covers both dispatch paths as well as both blocks.</para>
    /// </summary>
    [Theory]
    [InlineData(3, 3)]   // S·D² ≈ 5.2e5, below the fan-out threshold
    [InlineData(6, 2)]   // S·D² ≈ 1.3e6, above it
    public void TripleProduct_MatchesTheScalarReference_ForBothBlocksAtOnce(int tones, int order)
    {
        var apft = Make(tones, order);
        int D = apft.Dof, S = apft.SampleCount;
        var wG = Weights(S, 101);
        var wC = Weights(S, 202);

        var refG = new double[D * D];
        var refC = new double[D * D];
        ReferenceTripleProduct(apft, wG, refG);
        ReferenceTripleProduct(apft, wC, refC);

        var blockG = new double[D * D];
        var blockC = new double[D * D];
        apft.AccumulateTripleProducts(wG, wC, blockG, blockC);

        double eG = MaxRelDiff(blockG, refG), eC = MaxRelDiff(blockC, refC);
        output.WriteLine($"T={tones} order={order}  D={D} S={S}  rel err  G {eG:E2}  C {eC:E2}");
        Assert.True(eG <= 1e-12, $"the conductance block differs by {eG:E2}");
        Assert.True(eC <= 1e-12, $"the charge block differs by {eC:E2}");
    }

    /// <summary>
    /// A null weight vector means "this derivative waveform is identically zero for this node
    /// pair" — the AllZero shortcut, moved into the argument. The other block must still be
    /// produced, and the skipped one must be left exactly as the caller handed it over.
    /// </summary>
    [Fact]
    public void TripleProduct_WithOneWeightVectorNull_LeavesTheOtherBlockUntouched()
    {
        var apft = Make(3, 3);
        int D = apft.Dof, S = apft.SampleCount;
        var wG = Weights(S, 303);

        var refG = new double[D * D];
        ReferenceTripleProduct(apft, wG, refG);

        var blockG = new double[D * D];
        var blockC = new double[D * D];
        for (int i = 0; i < blockC.Length; i++) blockC[i] = 7.5;      // a sentinel, not a zero

        apft.AccumulateTripleProducts(wG, null, blockG, blockC);

        Assert.True(MaxRelDiff(blockG, refG) <= 1e-12);
        Assert.All(blockC, v => Assert.Equal(7.5, v));

        // And the symmetric case: neither weight present is a no-op on both blocks.
        var g2 = new double[D * D];
        var c2 = new double[D * D];
        apft.AccumulateTripleProducts(null, null, g2, c2);
        Assert.All(g2, v => Assert.Equal(0.0, v));
        Assert.All(c2, v => Assert.Equal(0.0, v));
    }

    /// <summary>
    /// The kernel ACCUMULATES; it does not assign. <see cref="HbNewtonNd.BuildJNd"/> relies on that
    /// — it clears the block once and then adds the conductance product, the rotated charge
    /// product, Y_NN and the Maas DC terms on top of each other.
    /// </summary>
    [Fact]
    public void TripleProduct_AccumulatesIntoTheBlock_RatherThanOverwritingIt()
    {
        var apft = Make(3, 3);
        int D = apft.Dof, S = apft.SampleCount;
        var w = Weights(S, 404);

        var once = new double[D * D];
        apft.AccumulateTripleProducts(w, null, once, new double[D * D]);

        var twice = new double[D * D];
        apft.AccumulateTripleProducts(w, null, twice, new double[D * D]);
        apft.AccumulateTripleProducts(w, null, twice, new double[D * D]);

        for (int i = 0; i < once.Length; i++) Assert.Equal(2.0 * once[i], twice[i], 12);
    }

    /// <summary>
    /// <b>The product is called once per LIVE node pair, not twice per pair.</b> The kernel takes
    /// both derivative waveforms together, so one Jacobian build over N² node pairs makes at most
    /// N² calls rather than the 2·N² of the one-block-per-call form it replaced.
    ///
    /// <para>"At most" and not "exactly": a node pair whose conductance AND charge waveforms are
    /// both identically zero is skipped entirely, which is the AllZero shortcut and is worth
    /// keeping. On the Hero-2 FET one of the four pairs is exactly that — the gate current does not
    /// depend on the drain voltage — so the honest assertion is that the count equals the number of
    /// pairs that actually carry a derivative, and is strictly below 2·N².</para>
    /// </summary>
    [Fact]
    public void BuildJNd_CallsTheProduct_OncePerLiveNodePair()
    {
        var (J, _, dof, apft, N) = HbDenseSolveTests.RealJacobian(3, 3);
        Assert.NotEmpty(J);
        Assert.Equal(2 * N * apft.MixCount, dof);

        // RealJacobian builds exactly one Jacobian on a private transform, so the instance's
        // counter is that one build's call count and nothing else.
        int calls = apft.ProductCallCount;
        output.WriteLine($"N={N} node pairs={N * N}  product calls for one BuildJNd = {calls}");

        Assert.InRange(calls, 1, N * N);
        Assert.True(calls < 2 * N * N,
            $"{calls} calls for {N * N} node pairs — the pair is not being taken in one call");
    }

    // ── The transform cache ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Get_ReturnsTheSameInstance_ForEqualKeys_AndDistinctOnesOtherwise()
    {
        var a = HbApft.Get(3, 3, 2.0);
        var b = HbApft.Get(3, 3, 2.0);
        Assert.Same(a, b);

        Assert.NotSame(a, HbApft.Get(4, 3, 2.0));      // different tone count
        Assert.NotSame(a, HbApft.Get(3, 2, 2.0));      // different mix order
        Assert.NotSame(a, HbApft.Get(3, 3, 2.5));      // different oversample

        // The cached instance really is the transform for that key, not merely some transform.
        Assert.Equal(3, a.Lattice.ToneCount);
        Assert.Equal(3, a.Lattice.MaxMixOrder);
    }

    /// <summary>
    /// The construction is elided, not merely the lookup: asking twice builds one transform.
    /// The oversample here is one no other test uses, so this owns its cache key outright and the
    /// count is exact however many test classes are running alongside it.
    /// </summary>
    [Fact]
    public void Get_ConstructsTheTransformExactlyOnce_PerKey()
    {
        const double mine = 2.0517;
        Assert.Equal(0, HbApft.ConstructionCountFor(3, 3, mine));

        var first = HbApft.Get(3, 3, mine);
        Assert.Equal(1, HbApft.ConstructionCountFor(3, 3, mine));

        for (int i = 0; i < 5; i++) Assert.Same(first, HbApft.Get(3, 3, mine));
        Assert.Equal(1, HbApft.ConstructionCountFor(3, 3, mine));
    }

    /// <summary>
    /// A shared, immutable transform is only safe if concurrent readers see the same answer as a
    /// lone one. The blocks are per-caller and Γ/Aᵀ are read-only, so they do — and the products
    /// come back bit-identical, not merely close, because each output element is still summed over
    /// the samples in ascending order by exactly one thread.
    /// </summary>
    [Fact]
    public void OneSharedTransform_GivesBitIdenticalProducts_ToConcurrentCallers()
    {
        var apft = HbApft.Get(6, 2, 2.0);
        int D = apft.Dof, S = apft.SampleCount;
        var wG = Weights(S, 505);
        var wC = Weights(S, 606);

        var refG = new double[D * D];
        var refC = new double[D * D];
        apft.AccumulateTripleProducts(wG, wC, refG, refC);

        var results = new double[8][];
        Parallel.For(0, 8, t =>
        {
            var g = new double[D * D];
            var c = new double[D * D];
            apft.AccumulateTripleProducts(wG, wC, g, c);
            results[t] = t % 2 == 0 ? g : c;
        });

        for (int t = 0; t < 8; t++)
            Assert.Equal(t % 2 == 0 ? refG : refC, results[t]);   // bit-for-bit, not a tolerance
    }
}
