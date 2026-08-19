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
    /// Torus phase φ_s[t] (radians) of sample <paramref name="s"/> along tone
    /// <paramref name="t"/>. Public because it is part of the transform's contract: it is the
    /// only way a caller — or an independent oracle — can build a waveform on the same sample set
    /// this transform analyses.
    /// </summary>
    public double SamplePhase(int s, int t) => _phases[s * Lattice.ToneCount + t];

    public HbApft(MixingLattice lattice, double oversample)
    {
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
    /// Jacobian building block: <c>out += A · diag(weights) · Γ</c>, a D×D real matrix, written
    /// into <paramref name="block"/> (row-major, D×D) which the caller has zeroed.
    ///
    /// <para>This is the T-tone replacement for the two-tone difference/sum-frequency convolution
    /// (<c>HbFft2D.SpecGet(G, k−i)</c> / <c>(k+i)</c>). It is the EXACT derivative of the
    /// discretized residual — <c>i_nl = A·i(Γ·V)</c> differentiates to <c>A·diag(∂i/∂v)·Γ</c> by
    /// the chain rule — which is why it needs no spectrum of the derivative waveform at all, and
    /// therefore no order-2·MaxMixOrder reach in the transform.</para>
    /// </summary>
    internal void AccumulateTripleProduct(double[] weights, double[] block)
    {
        int D = Dof, S = SampleCount;
        for (int s = 0; s < S; s++)
        {
            double w = weights[s];
            if (w == 0.0) continue;
            int rowBase = s * D;
            for (int r = 0; r < D; r++)
            {
                double ar = _at[rowBase + r] * w;
                if (ar == 0.0) continue;
                int outBase = r * D;
                for (int c = 0; c < D; c++)
                    block[outBase + c] += ar * _gamma[rowBase + c];
            }
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
