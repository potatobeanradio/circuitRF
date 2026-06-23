namespace CircuitRF.Core.Expressions;

public static class HbSpectrum
{
    /// Single-tone: physical frequency of harmonic <paramref name="order"/> at fundamental <paramref name="f0"/>.
    public static double HarmonicFreqHz(int order, double f0) => order * f0;

    /// Two-tone: physical frequency of mixing product (k1,k2) at tones (f1,f2).
    public static double MixFreqHz(int k1, int k2, double f1, double f2) => k1 * f1 + k2 * f2;
}
