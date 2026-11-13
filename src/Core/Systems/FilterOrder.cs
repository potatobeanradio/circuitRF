using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// The smallest prototype order that meets a passband / stopband mask for a family that has one.
/// Bessel and Gaussian have no magnitude mask of this form — they stay a typed order.
/// </summary>
public static class FilterOrder
{
    /// <summary>
    /// Ceiling of the closed-form order for <paramref name="response"/> against the mask edges.
    /// Returns null when the family has no magnitude order formula, or when the edges do not form a
    /// transition (Fs must sit outside the passband).
    /// </summary>
    /// <param name="fpHz">Passband edge for lowpass / highpass; unused for band forms.</param>
    /// <param name="fsHz">Stopband edge for lowpass / highpass; unused for band forms.</param>
    /// <param name="fp1Hz">Lower passband edge (bandpass) or lower passband (bandstop).</param>
    /// <param name="fp2Hz">Upper passband edge.</param>
    /// <param name="fs1Hz">Lower stopband edge.</param>
    /// <param name="fs2Hz">Upper stopband edge.</param>
    public static int? Minimum(
        FilterResponse response, NetworkForm form,
        double apDb, double asDb,
        double fpHz, double fsHz,
        double fp1Hz, double fp2Hz, double fs1Hz, double fs2Hz)
    {
        if (!(asDb > 0) || !double.IsFinite(asDb)) return null;
        if (!(apDb >= 0) || !double.IsFinite(apDb)) return null;

        if (!PrototypeStop(form, fpHz, fsHz, fp1Hz, fp2Hz, fs1Hz, fs2Hz, out double omegaS))
            return null;
        if (!(omegaS > 1.0) || !double.IsFinite(omegaS)) return null;

        double epsP = Eps(apDb <= 0 ? 3.0102999566 : apDb); // −3.01 dB when Ap is left at 0
        double epsS = Eps(asDb);
        if (!(epsS > epsP)) return null;

        return response switch
        {
            FilterResponse.Butterworth  => Ceil(Math.Log(epsS / epsP) / Math.Log(omegaS) / 2.0),
            FilterResponse.Chebyshev    => Ceil(Acosh(epsS / epsP) / Acosh(omegaS)),
            FilterResponse.InvChebyshev => Ceil(Acosh(epsS / epsP) / Acosh(omegaS)),
            FilterResponse.Elliptic     => EllipticMinimum(epsP, epsS, omegaS),
            _                           => null,
        };
    }

    private static int? EllipticMinimum(double epsP, double epsS, double omegaS)
    {
        double k1 = epsP / epsS;
        double k  = 1.0 / omegaS;
        if (!(k > 0) || k >= 1 || !(k1 > 0) || k1 >= 1) return null;
        double ratio = (EllipticPrototype.EllipK(Math.Sqrt(1 - k1 * k1)) / EllipticPrototype.EllipK(k1))
                     * (EllipticPrototype.EllipK(k) / EllipticPrototype.EllipK(Math.Sqrt(1 - k * k)));
        return Ceil(ratio);
    }

    /// <summary>
    /// The lowpass-prototype stopband frequency for the mask. Greater than 1 when the stop sits
    /// outside the passband.
    /// </summary>
    private static bool PrototypeStop(
        NetworkForm form, double fp, double fs, double fp1, double fp2, double fs1, double fs2,
        out double omegaS)
    {
        omegaS = 0;
        switch (form)
        {
            case NetworkForm.Lowpass:
                if (!(fp > 0) || !(fs > fp)) return false;
                omegaS = fs / fp;
                return true;
            case NetworkForm.Highpass:
                if (!(fs > 0) || !(fp > fs)) return false;
                omegaS = fp / fs;
                return true;
            case NetworkForm.Bandpass:
            {
                if (!(fp1 > 0) || !(fp2 > fp1) || !(fs1 > 0) || !(fs2 > fp2) || !(fp1 > fs1))
                    return false;
                double w0 = Math.Sqrt(fp1 * fp2);
                double bw = fp2 - fp1;
                double Map(double f) => Math.Abs(f * f - w0 * w0) / (f * bw);
                omegaS = Math.Min(Map(fs1), Map(fs2));
                return true;
            }
            case NetworkForm.Bandstop:
            {
                // F1/F2 on the ladder are the stopband edges; the passband edges sit outside.
                if (!(fs1 > 0) || !(fs2 > fs1) || !(fp1 > 0) || !(fp2 > fs2) || !(fs1 > fp1))
                    return false;
                double w0 = Math.Sqrt(fs1 * fs2);
                double bw = fs2 - fs1;
                double Map(double f) => Math.Abs(f * bw / (f * f - w0 * w0));
                omegaS = Math.Min(Map(fp1), Map(fp2));
                return true;
            }
            default:
                return false;
        }
    }

    private static double Eps(double db) => Math.Sqrt(Math.Pow(10.0, db / 10.0) - 1.0);

    private static double Acosh(double x) => Math.Log(x + Math.Sqrt(x * x - 1.0));

    private static int Ceil(double x)
    {
        if (!(x > 0) || !double.IsFinite(x)) return 1;
        int n = (int)Math.Ceiling(x - 1e-12);
        return Math.Max(1, n);
    }
}
