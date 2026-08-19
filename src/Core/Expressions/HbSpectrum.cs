namespace CircuitRF.Core.Expressions;

public static class HbSpectrum
{
    /// Single-tone: physical frequency of harmonic <paramref name="order"/> at fundamental <paramref name="f0"/>.
    public static double HarmonicFreqHz(int order, double f0) => order * f0;

    /// Two-tone: physical frequency of mixing product (k1,k2) at tones (f1,f2).
    public static double MixFreqHz(int k1, int k2, double f1, double f2) => k1 * f1 + k2 * f2;

    /// Multi-tone: signed physical frequency of mixing product k = (k1…kT) at tones f.
    /// Extra tone entries beyond k's length contribute nothing (k_t = 0), so a two-tone tag read
    /// against a longer tone vector still gives its own frequency.
    public static double MixFreqHz(int[] k, double[] f)
    {
        double hz = 0;
        for (int t = 0; t < k.Length && t < f.Length; t++) hz += k[t] * f[t];
        return hz;
    }
}
