using System.Numerics;
using FftFlat;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Two-tone HB multidimensional FFT — separable 1-D FFTs per axis (harmonic-balance.md §6.1).
///
/// FROZEN CONVENTIONS (never change silently; update HarmonicBalance/CLAUDE.md if you do):
///
///   Grid sizes:   N_t = FFTOverSample × nextpow2(4 × order_t)   per axis t ∈ {1,2}.
///
///   Amplitude convention (full-amplitude, matching HbFft §5.1):
///     For a retained (k1,k2), the 2-D HB phasor V_HB[k1,k2] satisfies:
///       v(φ1,φ2) = V_HB[0,0] + Σ_{retained (k1,k2)≠(0,0)} Re{V_HB[k1,k2]·e^{j(k1φ1+k2φ2)}}
///     Convention factor: V_HB[k1,k2] = X_raw2D[k1,k2] / D(k1,k2)
///       where D(0,0) = N1·N2  (global DC bin), and D = N1·N2/2 for every non-DC bin.
///     This is the 2-D analogue of HbFft §5.1 (raw/N at DC, raw/(N/2) for AC): the DC halving
///     applies once, at the global (0,0) bin — NOT separably per axis. A cross term such as
///     cos(φ1)cos(φ2) has full cosine amplitude 0.5 at the (1,1) mixing frequency, so
///     V_HB[1,1] = 0.5 (raw N1·N2/4 ÷ N1·N2/2), while a pure-axis cos(φ1) gives V_HB[1,0] = 1.
///
///   Spectrum storage (Complex[N1, N2/2+1]) — ONE half-plane only:
///     - First index:  k1 in [0 .. N1-1]  (full; negative k1 via periodic extension k1→N1+k1)
///     - Second index: k2 in [0 .. N2/2]  (non-negative only; negative k2 via conjugate symmetry)
///     For the k2=0 column the negative-k1 bins (k1 > N1/2) are redundant conjugates of the
///     stored k1≥0 representatives (real-signal symmetry) and are FOLDED OUT (zeroed) by Forward2D,
///     exactly as HbFft (1-D) keeps only bins 0..N/2. For k2>0 both k1 halves are kept — they are
///     the independent representatives of (k1,k2) and (k1,-k2).
///     Conjugate symmetry for real signal: X[k1,-k2] = conj(X[(N1-k1)%N1, k2]).  Use SpecGet().
///
///   ConversionWeight2D (for the Jacobian — extends single-tone ConversionWeight):
///     W((k1_row,k2_row), (dk1,dk2)) =
///       (dk1==0 ? 1.0 : 0.5) × (dk2==0 ? 1.0 : 0.5)    // per-axis DC-bin factors
///       × (k1_row==0 &amp;&amp; k2_row==0 ? 0.5 : 1.0)         // DC-row factor for (0,0) only
///
/// Implementation: row-wise real FFT (FftFlat.RealFourierTransform) + column-wise complex FFT
/// (FftFlat.FastFourierTransform). No new FFT kernel — composes the existing 1-D primitives.
/// </summary>
public static class HbFft2D
{
    // ── Grid sizing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Per-axis evaluation grid sizes.
    /// N_t = oversample × nextpow2(4 × order_t), minimum 4.
    /// </summary>
    public static (int N1, int N2) GridSizes(int order1, int order2, int oversample)
    {
        int os = Math.Max(1, oversample);
        int N1 = os * NextPow2(Math.Max(4, 4 * order1));
        int N2 = os * NextPow2(Math.Max(4, 4 * order2));
        return (N1, N2);
    }

    private static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }

    // ── Forward: real[N1,N2] → complex spectrum[N1, N2/2+1] ──────────────────

    /// <summary>
    /// Forward 2-D FFT: real time-domain samples x[N1,N2] → HB-convention spectrum[N1, N2/2+1].
    ///
    /// Algorithm:
    ///   1. Row-wise real FFT along axis 2 (N2-point) for each row i1 → raw complex rows.
    ///   2. Column-wise complex FFT along axis 1 (N1-point) for each column k2 → full 2-D DFT.
    ///   3. Apply HB amplitude convention: spec[k1,k2] = raw[k1,k2] / (C1(k1)·C2(k2)).
    ///
    /// Spectrum layout: k1 in [0..N1-1] (full), k2 in [0..N2/2] (non-negative only).
    /// Negative k2 → use SpecGet() which applies conjugate symmetry.
    /// </summary>
    public static void Forward2D(double[,] x, out Complex[,] spectrum)
    {
        int N1 = x.GetLength(0);
        int N2 = x.GetLength(1);
        int kMax2 = N2 / 2;

        // Intermediate: raw DFT along axis 2 (complex, N1 × (N2/2+1))
        var partial = new Complex[N1, kMax2 + 1];

        var rft2 = new RealFourierTransform(N2);
        var rowBuf = new double[N2 + 2];  // RealFourierTransform needs length N+2
        for (int i1 = 0; i1 < N1; i1++)
        {
            for (int i2 = 0; i2 < N2; i2++) rowBuf[i2] = x[i1, i2];
            for (int i2 = N2; i2 < rowBuf.Length; i2++) rowBuf[i2] = 0;

            var raw2 = rft2.Forward(rowBuf);   // Span<Complex>, length N2/2+1, unnormalized DFT
            for (int k2 = 0; k2 <= kMax2; k2++)
                partial[i1, k2] = raw2[k2];
        }

        // Column-wise complex FFT along axis 1 for each k2
        spectrum = new Complex[N1, kMax2 + 1];
        var cft1  = new FastFourierTransform(N1);
        var col   = new Complex[N1];

        for (int k2 = 0; k2 <= kMax2; k2++)
        {
            for (int i1 = 0; i1 < N1; i1++) col[i1] = partial[i1, k2];
            cft1.Forward(col);  // in-place, unnormalized DFT along axis 1
            for (int k1 = 0; k1 < N1; k1++) spectrum[k1, k2] = col[k1];
        }

        // Apply HB amplitude convention: spec[k1,k2] /= D(k1,k2).
        // D(0,0) = N1·N2 (global DC bin halved once); every non-DC bin = N1·N2/2.
        // This matches HbFft §5.1 (raw/N at DC, raw/(N/2) for AC) — the DC halving is
        // global, not separable per axis (a separable C1·C2 would wrongly give N1·N2/4 for
        // cross bins, doubling cos·cos cross-term phasors).
        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= kMax2; k2++)
                spectrum[k1, k2] /= Divisor(k1, k2, N1, N2);

        // Fold out the redundant half of the k2=0 column: for a real signal the negative-k1
        // bins (k1 > N1/2) are conjugates of the stored k1≥0 representatives. Only k1≥0 is a
        // retained half-plane rep — zero the rest, mirroring HbFft keeping only bins 0..N/2.
        for (int k1 = N1 / 2 + 1; k1 < N1; k1++)
            spectrum[k1, 0] = Complex.Zero;
    }

    /// <summary>
    /// HB amplitude-convention divisor: N1·N2 at the global DC bin (0,0), else N1·N2/2.
    /// 2-D analogue of HbFft §5.1 (raw/N at DC, raw/(N/2) for every AC bin).
    /// </summary>
    private static double Divisor(int k1, int k2, int N1, int N2)
        => (k1 == 0 && k2 == 0) ? (double)N1 * N2 : (double)N1 * N2 / 2.0;

    // ── Spectrum lookup with conjugate symmetry ───────────────────────────────

    /// <summary>
    /// Look up spectrum[dk1, dk2] with:
    ///   - dk1: periodic extension (mod N1)
    ///   - dk2 &lt; 0: conjugate symmetry — SpecGet(spec, dk1, dk2) = conj(SpecGet(spec, −dk1, −dk2))
    ///   - |dk2| > N2/2 or index out of range: returns Complex.Zero
    ///
    /// This is the safe accessor for Jacobian G/C lookups at arbitrary difference/sum indices.
    /// </summary>
    public static Complex SpecGet(Complex[,] spec, int dk1, int dk2)
    {
        int N1    = spec.GetLength(0);
        int kMax2 = spec.GetLength(1) - 1;   // N2/2

        // Conjugate symmetry for negative dk2
        if (dk2 < 0)
        {
            var conj = SpecGet(spec, -dk1, -dk2);
            return Complex.Conjugate(conj);
        }

        // Out-of-range in axis 2
        if (dk2 > kMax2) return Complex.Zero;

        // Periodic wrap for axis 1
        int k1w = ((dk1 % N1) + N1) % N1;
        return spec[k1w, dk2];
    }

    // ── Inverse: diamond spectrum → real time-domain ──────────────────────────

    /// <summary>
    /// Inverse 2-D FFT: given the HB-convention diamond spectrum V_HB[mixIdx]
    /// (one complex value per retained mixing product, for a single node),
    /// reconstruct the real time-domain samples x[i1,i2] on the N1×N2 grid.
    ///
    /// Algorithm:
    ///   1. Build full N1×N2 complex raw-DFT array from V_HB + conjugate filling.
    ///   2. Column-wise complex IFFT along axis 1 (N1-point, normalized) for each k2.
    ///   3. Row-wise real IFFT along axis 2 (N2-point, normalized) for each i1.
    /// </summary>
    public static void Inverse2D(
        MixingGrid     grid,
        Complex[]      vDiamond,   // V_HB[mixIdx], length = grid.MixCount
        int            N1,
        int            N2,
        double[,]      x)          // output: x[N1, N2], must be pre-allocated
    {
        int kMax2 = N2 / 2;

        // ── 1. Place each retained V_HB into the half-plane storage [N1, N2/2+1] ──
        // Mirror Forward2D's layout exactly (this is its inverse):
        //   k2 ≥ 0 reps  → stored directly at (k1 mod N1, k2).
        //   k2 < 0 reps  → conjugate-symmetry image at ((N1−k1)%N1, −k2) = conj(V_HB).
        // For the k2=0 column we additionally fill the negative-k1 conjugate that Forward2D
        // folds out, so the axis-1 IFFT below sees a Hermitian spectrum (→ real signal).
        var spec = new Complex[N1, kMax2 + 1];

        for (int m = 0; m < grid.MixCount; m++)
        {
            var (k1, k2) = grid.ToneOf(m);
            Complex vhb  = vDiamond[m];

            if (k2 >= 0)
            {
                int k1Idx = ((k1 % N1) + N1) % N1;
                spec[k1Idx, k2] = vhb;

                // k2=0 column: restore the conjugate at the negative-k1 bin (Forward2D zeroed it).
                if (k2 == 0 && k1 != 0)
                {
                    int k1Conj = ((N1 - k1) % N1 + N1) % N1;
                    spec[k1Conj, 0] = Complex.Conjugate(vhb);
                }
            }
            else
            {
                // Retained (k1>0, k2<0): conjugate image lands in the stored k2≥0 half-plane.
                int k1Conj = ((N1 - k1) % N1 + N1) % N1;
                spec[k1Conj, -k2] = Complex.Conjugate(vhb);
            }
        }

        // ── 2. Undo the HB amplitude convention → raw column-FFT spectrum ──────
        for (int k1 = 0; k1 < N1; k1++)
            for (int k2 = 0; k2 <= kMax2; k2++)
                spec[k1, k2] *= Divisor(k1, k2, N1, N2);

        // ── 3. Column-wise complex IFFT along axis 1 (normalized 1/N1) ─────────
        // FastFourierTransform.Inverse undoes the unnormalized forward column FFT exactly.
        var cft1    = new FastFourierTransform(N1);
        var col     = new Complex[N1];
        var partial = new Complex[N1, kMax2 + 1];  // raw per-row axis-2 spectrum after axis-1 IFFT

        for (int k2 = 0; k2 <= kMax2; k2++)
        {
            for (int i1 = 0; i1 < N1; i1++) col[i1] = spec[i1, k2];
            cft1.Inverse(col);   // in-place, normalized 1/N1
            for (int i1 = 0; i1 < N1; i1++) partial[i1, k2] = col[i1];
        }

        // ── 4. Row-wise real IFFT along axis 2 (normalized 1/N2) ──────────────
        // partial[i1, k2] is the unnormalized axis-2 spectrum (the forward's raw2 output);
        // RealFourierTransform.Inverse consumes it directly and applies the 1/N2 normalization,
        // exactly as HbFft.Inverse does for the 1-D case. No extra per-axis rescale.
        var rft2    = new RealFourierTransform(N2);
        var specBuf = new Complex[kMax2 + 1];

        for (int i1 = 0; i1 < N1; i1++)
        {
            for (int k2 = 0; k2 <= kMax2; k2++) specBuf[k2] = partial[i1, k2];

            var result = rft2.Inverse(specBuf.AsSpan());  // Span<double>, length N2(+2)
            for (int i2 = 0; i2 < N2; i2++)
                x[i1, i2] = result[i2];
        }
    }

    // ── ConversionWeight2D ────────────────────────────────────────────────────

    /// <summary>
    /// 2-D Jacobian conversion weight: the ratio between the full-amplitude convention spectrum
    /// G_HB[dk1,dk2] (this file's convention) and the two-sided exponential coefficient that the
    /// Maas conversion-matrix block formula expects (G_HB[j] = 2·c_g[j] for any non-DC bin,
    /// G_HB[0,0] = c_g[0,0] at the global DC bin).
    ///
    ///   W = ((dk1==0 &amp;&amp; dk2==0) ? 1.0 : 0.5)        ← bin factor: ½ for EVERY non-DC bin (GLOBAL)
    ///       × ((kRow1==0 &amp;&amp; kRow2==0) ? 0.5 : 1.0)  ← DC-row factor (only the (0,0) row)
    ///
    /// The bin factor is GLOBAL, not per-axis: a per-axis ½·½ would halve cross bins (both axes
    /// nonzero) twice — the same double-halving the Forward2D divisor avoids (N₁N₂/2 globally, not
    /// N₁N₂/4). The two-tone FD-Jacobian oracle (HbJacobian2DTests) pins this. For single-tone
    /// (k2≡0, dk2≡0) it reduces to the single-tone ConversionWeight(k1_row, dk1).
    /// </summary>
    public static double ConversionWeight2D(int kRow1, int kRow2, int dk1, int dk2)
    {
        double fBin = (dk1 == 0 && dk2 == 0) ? 1.0 : 0.5;      // GLOBAL DC-bin factor
        double fRow = (kRow1 == 0 && kRow2 == 0) ? 0.5 : 1.0;  // DC-row (0,0) only
        return fBin * fRow;
    }

    // ── Omega for a retained mixing product ───────────────────────────────────

    /// <summary>
    /// Angular frequency (rad/s) of (k1,k2): ω = k1·ω1 + k2·ω2 (signed).
    /// Used for the charge-current rotation factor jω in the Jacobian (§7.4).
    /// </summary>
    public static double MixingOmega(int k1, int k2, double omega1, double omega2)
        => k1 * omega1 + k2 * omega2;
}
