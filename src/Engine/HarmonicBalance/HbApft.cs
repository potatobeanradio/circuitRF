using System.Collections.Concurrent;
using System.Numerics;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Almost-Periodic Fourier Transform for the T-tone HB lattice (harmonic-balance.md §6.5) —
/// the T ≥ 3 replacement for <see cref="HbFft2D"/>'s rectangular multidimensional FFT.
///
/// <para><b>Why not a separable N₁×…×N_T real FFT.</b> The rectangular grid is
/// <c>nextpow2(4·order)^T</c> samples, which does not reach the tone counts this engine must
/// support: 6 tones at order 3 is 16⁶ = 16.7M samples PER waveform, and one Newton iteration
/// holds ~14 such arrays (v/i/q per node, dg/dc per node pair) ≈ 1.9 GB — 5 tones at order 3 is
/// already 117 MB. The rectangle is also nearly all waste at high T: it computes a full box to
/// retain a diamond. The APFT's sample count scales with the RETAINED set M instead, so the same
/// 6-tone order-3 case needs 1,512 samples.</para>
///
/// <para><b>FROZEN CONVENTIONS</b> (never change silently; update
/// <c>docs/design/harmonic-balance.md</c> §6.5 if you do):</para>
///
/// <para><i>Amplitude convention</i> — identical to <see cref="HbFft"/> §5.1 and
/// <see cref="HbFft2D"/>, written as a matrix:
/// <code>
///   v(φ) = V_HB[0] + Σ_{retained k ≠ 0} Re{ V_HB[k] · e^{j k·φ} }
/// </code>
/// so a pure cos(φ₁) gives V_HB[(1,0,…)] = 1, and a product cos φ₁·cos φ₂·cos φ₃ gives
/// V_HB[(1,1,1)] = 0.25 — the T-tone continuation of the 2-D rule that cos φ₁·cos φ₂ gives 0.5
/// at (1,1). The DC halving is GLOBAL (it applies once, at k = 0), not per axis.</para>
///
/// <para><i>Real-DOF layout</i> — D = 2M reals, local index <c>2·mixIdx + (isIm ? 1 : 0)</c>.
/// This is exactly <see cref="HbNewton2D"/>'s <c>Idx(n, mix, isIm, M) = n·D + local</c>, so the
/// per-node blocks stay contiguous and the Jacobian is assembled node-pair by node-pair.
/// The Im DOF of the DC index (local index 1) is a FICTITIOUS dummy — its Γ column is
/// identically zero (a real signal has no DC quadrature). It is kept rather than removed so the
/// Maas §7.3 DC special cases, which supply its diagonal in J, carry over verbatim from the
/// two-tone path.</para>
///
/// <para><i>Synthesis matrix</i> Γ (S×D, real): <c>Γ[s, 2m]   = cos(k_m·φ_s)</c>,
/// <c>Γ[s, 2m+1] = −sin(k_m·φ_s)</c>. <i>Analysis matrix</i> A = Γ⁺ = (ΓᵀΓ)⁻¹Γᵀ, factored once
/// and cached. On the retained lattice A·Γ = I exactly (up to round-off) on the non-dummy DOFs,
/// so synthesize→analyze is a round trip; out-of-band content is least-squares projected rather
/// than sharply aliased.</para>
///
/// <para><i>Sample phases</i> come from the deterministic R_T Kronecker (Weyl) low-discrepancy
/// sequence — <c>φ_s[t] = 2π·frac(0.5 + (s+1)·g^-(t+1))</c> with g the positive root of
/// <c>x^(T+1) = x + 1</c>. No RNG is involved, so a run is bit-reproducible. Equidistribution
/// makes ΓᵀΓ near-diagonal, but correctness does not rest on the sequence choice: the
/// constructor gates on the actual conditioning of ΓᵀΓ and throws rather than returning a
/// silently rank-deficient transform.</para>
/// </summary>
public sealed class HbApft
{
    // ── The process-wide transform cache (M3) ─────────────────────────────────────────────────
    //
    // An HbApft depends on NOTHING that changes between sweep points: (tone count, MaxMixOrder,
    // oversample) fix the lattice, the sample phases, Γ, the normal matrix, its Cholesky and the
    // S back-solves that produce Aᵀ. RunMultiTone used to build one per CALL, which is per sweep
    // point — 93 ms at 6 tones / order 3, so a 20-point parametric sweep spent 1.9 s constructing
    // twenty identical transforms.
    //
    // Sharing one instance across points AND across threads is safe because the object is
    // immutable after construction: _gamma, _at and _phases are written only in the constructor
    // and are read-only by contract thereafter (the Jacobian reads them directly, and the
    // micro-kernel writes only into the caller's own block buffers).
    //
    // The cache is UNBOUNDED on purpose. A key is (tones, order, oversample), so a session holds
    // one entry per distinct analysis shape — a handful, not a stream — and a sweep, a loadpull or
    // a drive ladder all reuse the same one. An eviction policy would add a way to get this wrong
    // and buy nothing. The ceiling check in HbEngine.CheckMultiToneCeiling still runs BEFORE Get
    // is called, so an over-cap request is refused without allocating anything.

    private static readonly ConcurrentDictionary<(int Tones, int Order, double Oversample), HbApft>
        Cache = new();

    private static readonly ConcurrentDictionary<(int Tones, int Order, double Oversample), int>
        Constructions = new();

    /// <summary>
    /// The shared transform for this analysis shape, built on first use and reused thereafter.
    /// A duplicate construction under a race is harmless — the two are identical and immutable,
    /// and one is simply discarded.
    /// </summary>
    public static HbApft Get(int toneCount, int maxMixOrder, double oversample)
        => Cache.GetOrAdd((toneCount, maxMixOrder, oversample),
                          k => new HbApft(new MixingLattice(k.Tones, k.Order), k.Oversample));

    /// <summary>
    /// How many <see cref="HbApft"/> instances this process has constructed for one cache key. A
    /// diagnostic counter, public so <c>HbApftTests</c> can assert that the cache actually elides
    /// the rebuild and that the multi-tone ceiling refuses before anything is constructed.
    ///
    /// <para>It is counted PER KEY, not process-wide, on purpose: xUnit runs test classes
    /// concurrently, and several of them build transforms, so a single global counter would be
    /// perturbed by whatever else happened to be running. A test that picks an oversample no other
    /// test uses owns its key outright and can assert an exact count.</para>
    /// </summary>
    public static int ConstructionCountFor(int toneCount, int maxMixOrder, double oversample)
        => Constructions.TryGetValue((toneCount, maxMixOrder, oversample), out int c) ? c : 0;

    private int _productCalls;

    /// <summary>
    /// How many times <see cref="AccumulateTripleProducts"/> has been entered on THIS transform.
    /// A diagnostic counter, public so <c>HbApftTests</c> can assert that a Jacobian build calls
    /// the product once per live node pair rather than the twice-per-pair of the one-block-per-call
    /// form this replaced. Per instance rather than static, for the concurrency reason above.
    /// </summary>
    public int ProductCallCount => Volatile.Read(ref _productCalls);

    /// <summary>Ratio of samples to real DOF. Below ~1 the transform is under-determined.</summary>
    public const double MinOversample = 1.2;

    /// <summary>Conditioning gate on ΓᵀΓ: the Cholesky pivot spread that trips a refusal.</summary>
    private const double MinPivotRatio = 1e-10;

    private readonly double[] _gamma;   // S×D row-major: Γ[s·D + j]
    private readonly double[] _at;      // S×D row-major: Aᵀ[s·D + r] = A[r, s]
    private readonly double[] _phases;  // S×T row-major: φ_s[t]

    /// <summary>The lattice this transform is built for.</summary>
    public MixingLattice Lattice { get; }

    /// <summary>Number of retained mixing products M.</summary>
    public int MixCount => Lattice.MixCount;

    /// <summary>Real degrees of freedom D = 2M (Re/Im interleaved per mix index).</summary>
    public int Dof { get; }

    /// <summary>Number of torus sample points S.</summary>
    public int SampleCount { get; }

    /// <summary>Estimated 2-norm condition number of ΓᵀΓ (from the Cholesky pivot spread).</summary>
    public double NormalConditionEstimate { get; }

    /// <summary>Γ in S×D row-major order. Read-only by contract — the Jacobian reads it directly.</summary>
    internal double[] Gamma => _gamma;

    /// <summary>Aᵀ in S×D row-major order (<c>_at[s·D + r] = A[r, s]</c>), so both matrices are
    /// walked contiguously by sample in the Jacobian's triple product.</summary>
    internal double[] AnalysisT => _at;

    /// <summary>
    /// Γ[s, j] — the synthesis matrix element for sample <paramref name="sample"/> and real DOF
    /// <paramref name="dof"/>. Public for the same reason as <see cref="SamplePhase"/>: an
    /// independent oracle cannot re-derive the triple product without the two matrices that go
    /// into it. Nothing in the engine reads it elementwise; the kernel walks the arrays directly.
    /// </summary>
    public double SynthesisElement(int sample, int dof) => _gamma[sample * Dof + dof];

    /// <summary>
    /// A[r, s] — the analysis (pseudo-inverse) matrix element for real DOF <paramref name="dof"/>
    /// and sample <paramref name="sample"/>. The companion to <see cref="SynthesisElement"/>.
    /// </summary>
    public double AnalysisElement(int dof, int sample) => _at[sample * Dof + dof];

    /// <summary>
    /// Torus phase φ_s[t] (radians) of sample <paramref name="s"/> along tone
    /// <paramref name="t"/>. Public because it is part of the transform's contract: it is the
    /// only way a caller — or an independent oracle — can build a waveform on the same sample set
    /// this transform analyses.
    /// </summary>
    public double SamplePhase(int s, int t) => _phases[s * Lattice.ToneCount + t];

    public HbApft(MixingLattice lattice, double oversample)
    {
        Constructions.AddOrUpdate((lattice.ToneCount, lattice.MaxMixOrder, oversample), 1, (_, c) => c + 1);
        Lattice = lattice;
        int M   = lattice.MixCount;
        int T   = lattice.ToneCount;
        int D   = 2 * M;
        Dof     = D;

        double os = Math.Max(MinOversample, oversample);
        int    S  = (int)Math.Ceiling(os * D);
        SampleCount = S;

        // ── Sample phases: R_T Kronecker sequence (deterministic, no RNG) ─────
        var alpha = KroneckerAlphas(T);
        var phase = new double[T];

        if ((long)S * D > int.MaxValue)
            throw new InvalidOperationException(
                $"HB (multi-tone): the APFT matrix would need {(long)S * D:N0} entries " +
                $"({S} samples × {D} unknowns) — lower MaxMixOrder or the tone count.");
        _gamma  = new double[S * D];
        _phases = new double[S * T];

        for (int s = 0; s < S; s++)
        {
            for (int t = 0; t < T; t++)
            {
                phase[t] = 2.0 * Math.PI * Frac(0.5 + (s + 1) * alpha[t]);
                _phases[s * T + t] = phase[t];
            }

            int rowBase = s * D;
            for (int m = 0; m < M; m++)
            {
                var k = lattice.ToneOf(m);
                double arg = 0;
                for (int t = 0; t < T; t++) arg += k[t] * phase[t];
                _gamma[rowBase + 2 * m]     =  Math.Cos(arg);
                _gamma[rowBase + 2 * m + 1] = -Math.Sin(arg);
            }
        }

        // ── Normal matrix ΓᵀΓ (symmetric, D×D) ────────────────────────────────
        var nm = new double[D * D];
        for (int s = 0; s < S; s++)
        {
            int rowBase = s * D;
            for (int i = 0; i < D; i++)
            {
                double gi = _gamma[rowBase + i];
                if (gi == 0.0) continue;
                int ni = i * D;
                for (int j = i; j < D; j++) nm[ni + j] += gi * _gamma[rowBase + j];
            }
        }
        for (int i = 0; i < D; i++)
            for (int j = 0; j < i; j++) nm[i * D + j] = nm[j * D + i];

        // Ridge the fictitious DC-Im DOF, whose Γ column is identically zero. Its row and column
        // of ΓᵀΓ are zero too, so a unit diagonal decouples it entirely: Aᵀ[:, that DOF] comes
        // back zero, which is exactly what "this DOF carries no information" should produce.
        var isDummy = new bool[D];
        for (int i = 0; i < D; i++)
            if (nm[i * D + i] == 0.0) { nm[i * D + i] = 1.0; isDummy[i] = true; }

        // ── Cholesky, with the conditioning gate ──────────────────────────────
        NormalConditionEstimate = CholeskyInPlace(nm, D, isDummy, out double pivotRatio);
        if (pivotRatio < MinPivotRatio)
            throw new InvalidOperationException(
                $"HB (multi-tone): the {T}-tone APFT sample set is rank-deficient " +
                $"(ΓᵀΓ pivot ratio {pivotRatio:E2}, condition ≈ {NormalConditionEstimate:E2}) at " +
                $"{S} samples for {D} unknowns. Raise the APFT oversample " +
                $"(AnalysisSettings.HbApftOversample, currently {oversample:G3}) or lower MaxMixOrder.");

        // ── Aᵀ = Γ·(ΓᵀΓ)⁻¹ : solve the Cholesky system once per SAMPLE ROW ────
        // (A = (ΓᵀΓ)⁻¹Γᵀ and (ΓᵀΓ)⁻¹ is symmetric, so Aᵀ's rows are Γ's rows solved against it.)
        _at = new double[S * D];
        var rhs = new double[D];
        for (int s = 0; s < S; s++)
        {
            Array.Copy(_gamma, s * D, rhs, 0, D);
            CholeskySolveInPlace(nm, D, rhs);
            Array.Copy(rhs, 0, _at, s * D, D);
        }
    }

    // ── Transform pair ────────────────────────────────────────────────────────

    /// <summary>
    /// Spectrum → time samples (the analogue of <see cref="HbFft2D.Inverse2D"/>).
    /// The DC index's imaginary part is ignored by construction (its Γ column is zero), which is
    /// the matrix form of the two-tone path's explicit "force DC real" step.
    /// </summary>
    public void Synthesize(Complex[] diamond, double[] samples)
    {
        int D = Dof, S = SampleCount, M = MixCount;
        for (int s = 0; s < S; s++)
        {
            int rowBase = s * D;
            double acc = 0;
            for (int m = 0; m < M; m++)
                acc += _gamma[rowBase + 2 * m]     * diamond[m].Real
                     + _gamma[rowBase + 2 * m + 1] * diamond[m].Imaginary;
            samples[s] = acc;
        }
    }

    /// <summary>
    /// Time samples → spectrum (the analogue of <see cref="HbFft2D"/>'s forward transform).
    /// Least-squares projection onto the retained lattice; the DC index comes back purely real.
    /// </summary>
    public void Analyze(double[] samples, Complex[] diamond)
    {
        int D = Dof, S = SampleCount, M = MixCount;
        var acc = new double[D];
        for (int s = 0; s < S; s++)
        {
            double xs = samples[s];
            if (xs == 0.0) continue;
            int rowBase = s * D;
            for (int r = 0; r < D; r++) acc[r] += _at[rowBase + r] * xs;
        }
        for (int m = 0; m < M; m++)
            diamond[m] = new Complex(acc[2 * m], acc[2 * m + 1]);
    }

    /// <summary>
    /// Jacobian building blocks for one node pair: <c>blockG += A·diag(wG)·Γ</c> and
    /// <c>blockC += A·diag(wC)·Γ</c>, each a D×D real matrix written row-major into a buffer the
    /// caller has zeroed. Either weight vector may be <c>null</c>, meaning "that derivative
    /// waveform is identically zero for this node pair" — the corresponding block is left alone.
    ///
    /// <para>This is the T-tone replacement for the two-tone difference/sum-frequency convolution
    /// (<c>HbFft2D.SpecGet(G, k−i)</c> / <c>(k+i)</c>). It is the EXACT derivative of the
    /// discretized residual — <c>i_nl = A·i(Γ·V)</c> differentiates to <c>A·diag(∂i/∂v)·Γ</c> by
    /// the chain rule — which is why it needs no spectrum of the derivative waveform at all, and
    /// therefore no order-2·MaxMixOrder reach in the transform.</para>
    ///
    /// <para><b>Both blocks are taken together</b> because they share A and Γ: a thread that has
    /// just walked a column panel of Γ for the conductance block finds it hot in cache for the
    /// charge block. That is the whole reason for the paired signature — the arithmetic is
    /// unchanged and, being accumulated in the same order, the result is bit-identical to two
    /// separate passes.</para>
    ///
    /// <para>Public rather than internal so <c>HbApftTests</c> can compare it against the scalar
    /// triple loop it replaced; <c>CircuitRF.Engine</c> exposes no internals to its test
    /// project.</para>
    /// </summary>
    public void AccumulateTripleProducts(double[]? wG, double[]? wC, double[] blockG, double[] blockC)
    {
        Interlocked.Increment(ref _productCalls);
        if (wG is null && wC is null) return;

        int D = Dof, S = SampleCount;
        int nr = 4 * Vector<double>.Count;                  // columns handled by one kernel pass
        int panels = (D + nr - 1) / nr;

        // Column panels are independent — each owns a disjoint set of output COLUMNS in both
        // blocks — so the split needs no locking, no per-thread buffer and no reduction, and every
        // output element is still summed over s in ascending order by exactly one thread. The
        // result is therefore bit-identical however many threads run it, which is what
        // HbApftTests pins; a thread count is never a numerical input here.
        int threads = 1;
        if ((long)S * D * D >= ParallelThreshold)
            threads = Math.Min(Environment.ProcessorCount, panels);

        if (threads <= 1)
        {
            Kernel(wG, wC, blockG, blockC, 0, D);
            return;
        }

        // Round the panel split first and derive the worker count from it, so a run never asks for
        // workers that would find no panel left (11 panels across 10 cores is 6 workers of 2, not
        // 10 of whom 4 are empty).
        int panelsPer = (panels + threads - 1) / threads;
        threads = (panels + panelsPer - 1) / panelsPer;

        Parallel.For(0, threads, t =>
        {
            int c0 = t * panelsPer * nr;
            int c1 = Math.Min(D, (t + 1) * panelsPer * nr);
            if (c0 < c1) Kernel(wG, wC, blockG, blockC, c0, c1);
        });
    }

    /// <summary>
    /// Work below this many multiply-adds (S·D²) runs single-threaded: measured on the 3-tone
    /// order-3 lattice (S·D² ≈ 5.2e5) the fan-out costs more than it saves, while at 6 tones
    /// order 2 (≈1.3e6) it is already a 2.7× win.
    /// </summary>
    private const long ParallelThreshold = 1_000_000;

    /// <summary>
    /// The triple product over one range of output columns — a 4-row × 4-vector register-blocked
    /// GEMM micro-kernel.
    ///
    /// <para><b>Why this shape.</b> The scalar form this replaced ran s outermost and touched the
    /// whole D×D output block on every one of the S samples: 862 MB of read-modify-write traffic
    /// at 6 tones / order 3, for 1.08e8 multiply-adds. Here the 16 accumulators for a 4×(4·W)
    /// output tile live in vector registers for the whole s loop, so each output element is
    /// written exactly once. The two operand rows a tile needs at sample s — <c>_at[s·D + r0…r0+3]</c>
    /// and <c>_gamma[s·D + c0…]</c> — are both CONTIGUOUS, which is why no packed copy of either
    /// matrix is needed and the method allocates nothing.</para>
    ///
    /// <para>MEASURED per D×D block at 6 tones / order 3 (D = 378, S = 756; Release, Apple M4):
    /// scalar triple loop 56.5 ms → 9.9 ms single-threaded → 4.0 ms on four column panels.
    /// A variant that pre-transposes the weighted analysis rows into a (2D×S) scratch buffer was
    /// 4% faster and needs 4.6 MB per call; it was not worth the allocation.</para>
    /// </summary>
    private void Kernel(double[]? wG, double[]? wC, double[] blockG, double[] blockC, int c0, int c1)
    {
        int D = Dof, S = SampleCount;
        int w  = Vector<double>.Count;
        int nr = 4 * w;

        for (int cb = c0; cb < c1; cb += nr)
        {
            int width = Math.Min(nr, c1 - cb);
            if (width != nr)                                  // ragged tail panel
            {
                if (wG is not null) Edge(wG, blockG, 0, D, cb, cb + width);
                if (wC is not null) Edge(wC, blockC, 0, D, cb, cb + width);
                continue;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                var weights = pass == 0 ? wG : wC;
                if (weights is null) continue;
                var block = pass == 0 ? blockG : blockC;

                int r0 = 0;
                for (; r0 + 4 <= D; r0 += 4)
                {
                    Vector<double> a00 = default, a01 = default, a02 = default, a03 = default;
                    Vector<double> a10 = default, a11 = default, a12 = default, a13 = default;
                    Vector<double> a20 = default, a21 = default, a22 = default, a23 = default;
                    Vector<double> a30 = default, a31 = default, a32 = default, a33 = default;

                    for (int s = 0; s < S; s++)
                    {
                        int ab = s * D + r0, gb = s * D + cb;
                        double wv = weights[s];
                        var g0 = new Vector<double>(_gamma.AsSpan(gb,         w));
                        var g1 = new Vector<double>(_gamma.AsSpan(gb + w,     w));
                        var g2 = new Vector<double>(_gamma.AsSpan(gb + 2 * w, w));
                        var g3 = new Vector<double>(_gamma.AsSpan(gb + 3 * w, w));
                        var v0 = new Vector<double>(_at[ab]     * wv);
                        var v1 = new Vector<double>(_at[ab + 1] * wv);
                        var v2 = new Vector<double>(_at[ab + 2] * wv);
                        var v3 = new Vector<double>(_at[ab + 3] * wv);
                        a00 += v0 * g0; a01 += v0 * g1; a02 += v0 * g2; a03 += v0 * g3;
                        a10 += v1 * g0; a11 += v1 * g1; a12 += v1 * g2; a13 += v1 * g3;
                        a20 += v2 * g0; a21 += v2 * g1; a22 += v2 * g2; a23 += v2 * g3;
                        a30 += v3 * g0; a31 += v3 * g1; a32 += v3 * g2; a33 += v3 * g3;
                    }

                    int o = r0 * D + cb;
                    Store(block, o, w, a00, a01, a02, a03); o += D;
                    Store(block, o, w, a10, a11, a12, a13); o += D;
                    Store(block, o, w, a20, a21, a22, a23); o += D;
                    Store(block, o, w, a30, a31, a32, a33);
                }
                if (r0 < D) Edge(weights, block, r0, D, cb, cb + nr);      // ragged row remainder
            }
        }
    }

    private static void Store(double[] block, int o, int w,
        Vector<double> a0, Vector<double> a1, Vector<double> a2, Vector<double> a3)
    {
        (a0 + new Vector<double>(block.AsSpan(o,         w))).CopyTo(block.AsSpan(o,         w));
        (a1 + new Vector<double>(block.AsSpan(o + w,     w))).CopyTo(block.AsSpan(o + w,     w));
        (a2 + new Vector<double>(block.AsSpan(o + 2 * w, w))).CopyTo(block.AsSpan(o + 2 * w, w));
        (a3 + new Vector<double>(block.AsSpan(o + 3 * w, w))).CopyTo(block.AsSpan(o + 3 * w, w));
    }

    /// <summary>Scalar fallback for the rows and columns a whole 4×(4·W) tile does not cover.
    /// Accumulates over s in the same ascending order, so the tail matches the kernel.</summary>
    private void Edge(double[] weights, double[] block, int r0, int r1, int c0, int c1)
    {
        int D = Dof, S = SampleCount;
        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            double acc = 0;
            for (int s = 0; s < S; s++) acc += _at[s * D + r] * weights[s] * _gamma[s * D + c];
            block[r * D + c] += acc;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static double Frac(double x) => x - Math.Floor(x);

    /// <summary>
    /// R_T low-discrepancy generator: α_t = g^-(t+1) with g the positive root of x^(T+1) = x + 1,
    /// found by the fixed point g ← (1+g)^(1/(T+1)) (monotone, converges in a few iterations).
    /// </summary>
    private static double[] KroneckerAlphas(int T)
    {
        double g = 1.2;
        for (int i = 0; i < 64; i++) g = Math.Pow(1.0 + g, 1.0 / (T + 1));

        var alpha = new double[T];
        double p = 1.0;
        for (int t = 0; t < T; t++) { p /= g; alpha[t] = p; }
        return alpha;
    }

    /// <summary>
    /// In-place Cholesky A = L·Lᵀ storing L in the lower triangle. Returns the condition estimate
    /// (max/min pivot)², and reports the raw pivot ratio so the caller can refuse. Dummy DOFs
    /// (unit-ridged, carrying no information) are excluded from the spread.
    /// </summary>
    private static double CholeskyInPlace(double[] a, int n, bool[] isDummy, out double pivotRatio)
    {
        double minP = double.MaxValue, maxP = 0.0;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i * n + j];
                for (int k = 0; k < j; k++) sum -= a[i * n + k] * a[j * n + k];

                if (i == j)
                {
                    if (sum <= 0.0) { pivotRatio = 0.0; return double.PositiveInfinity; }
                    double d = Math.Sqrt(sum);
                    a[i * n + i] = d;
                    if (!isDummy[i]) { if (d < minP) minP = d; if (d > maxP) maxP = d; }
                }
                else
                {
                    a[i * n + j] = sum / a[j * n + j];
                }
            }
            for (int j = i + 1; j < n; j++) a[i * n + j] = 0.0;   // clear the upper triangle
        }

        if (maxP <= 0.0) { pivotRatio = 0.0; return double.PositiveInfinity; }
        pivotRatio = minP / maxP;
        return 1.0 / (pivotRatio * pivotRatio);
    }

    /// <summary>Solves L·Lᵀ·x = b in place on <paramref name="b"/>, given L from
    /// <see cref="CholeskyInPlace"/>.</summary>
    private static void CholeskySolveInPlace(double[] l, int n, double[] b)
    {
        for (int i = 0; i < n; i++)
        {
            double sum = b[i];
            int rowBase = i * n;
            for (int k = 0; k < i; k++) sum -= l[rowBase + k] * b[k];
            b[i] = sum / l[rowBase + i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int k = i + 1; k < n; k++) sum -= l[k * n + i] * b[k];
            b[i] = sum / l[i * n + i];
        }
    }
}
