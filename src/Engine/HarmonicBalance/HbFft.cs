using System.Numerics;
using FftFlat;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// HB FFT layer — frozen amplitude convention (harmonic-balance.md §5.1).
///
/// Frozen convention (recorded here; do NOT change silently):
///   forward (time → freq):
///     X[k] = rawFFT[k] / (N/2)  for k = 1..K   (positive harmonics)
///     X[0] = rawFFT[0] / N       (DC bin halved)
///   inverse (freq → time):
///     Scale by N/2, double DC, conjugate-mirror, IFFT real.
///
/// Grid: N = FFTOverSample × nextpow2(4K).  N must be even and a power of two.
/// The evaluation grid (N) is LARGER than the solution spectrum (K+1 bins);
/// FFTOverSample anti-aliases without growing the Newton unknowns.
///
/// Indexing: C# 0-based. k=0 is DC; k=1..K are the K positive harmonics.
/// The MATLAB pseudocode in harmonic-balance.md is 1-based — every formula
/// transcribed from it has been rebased to 0.
/// </summary>
public static class HbFft
{
    // ── Grid sizing ───────────────────────────────────────────────────────────

    public static int GridSize(int K, int oversample)
    {
        int minN = Math.Max(4, 4 * K);  // must resolve to harmonic 2K for Jacobian
        return Math.Max(1, oversample) * NextPow2(minN);
    }

    private static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    // ── Forward: real time-domain → HB phasors ───────────────────────────────

    /// <summary>
    /// Forward transform: real time-domain samples x (length N) →
    /// HB amplitude-convention spectrum X (length K+1: DC + K positive harmonics).
    /// Also returns the oversampled raw FFT output as rawSpectrum (length N/2+1)
    /// for use when building the Jacobian (needs harmonics 0..2K).
    /// </summary>
    public static void Forward(double[] x, int K, out Complex[] X, out Complex[] rawSpectrum)
    {
        int N = x.Length;
        // RealFourierTransform.Forward needs buffer of length N+2.
        var buf = new double[N + 2];
        x.CopyTo(buf, 0);

        var rft = new RealFourierTransform(N);
        var spec = rft.Forward(buf);   // spec is Span<Complex> of length N/2+1

        // Copy to a heap array so callers can keep it.
        rawSpectrum = new Complex[N / 2 + 1];
        for (int i = 0; i <= N / 2; i++)
            rawSpectrum[i] = spec[i];

        // Apply amplitude convention: X[k] = raw[k] / (N/2); X[0] /= 2 additionally (= raw[0]/N).
        double halfN = N / 2.0;
        X = new Complex[K + 1];
        X[0] = new Complex(rawSpectrum[0].Real / N, 0);  // DC: real only, halved
        for (int k = 1; k <= K && k <= N / 2; k++)
            X[k] = rawSpectrum[k] / halfN;
    }

    // ── Inverse: HB phasors → real time-domain ───────────────────────────────

    /// <summary>
    /// Inverse transform: HB spectrum X (length K+1) → real time-domain samples x (length N).
    /// N must equal the grid size matching K and the oversample factor.
    /// </summary>
    public static void Inverse(Complex[] X, int K, double[] x)
    {
        int N = x.Length;
        int halfPlusOne = N / 2 + 1;

        // Undo amplitude convention: scale by N/2, double DC.
        var spectrum = new Complex[halfPlusOne];
        spectrum[0] = new Complex(X[0].Real * N, 0);   // undo DC halving
        for (int k = 1; k <= K && k < halfPlusOne; k++)
            spectrum[k] = X[k] * (N / 2.0);
        // Bins K+1..N/2: left zero (no energy above K in our representation).

        var rft = new RealFourierTransform(N);
        var timeDomain = rft.Inverse(spectrum);  // Span<double> length N+2

        // Copy first N samples (real signal).
        for (int n = 0; n < N; n++)
            x[n] = timeDomain[n];
    }
}
