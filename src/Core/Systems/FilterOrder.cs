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
    /// <param name="fp1Hz">Lower passband edge (bandpass), or the passband edge below the rejected band (bandstop).</param>
    /// <param name="fp2Hz">Upper passband edge.</param>
    /// <param name="fs1Hz">Lower stopband edge.</param>
    /// <param name="fs2Hz">Upper stopband edge.</param>
    /// <param name="ellipticType">
    /// Read by Elliptic. <see cref="EllipticType.A"/> is the degree equation's own answer, which
    /// may be an even order with no LC ladder. Auto, b and c answer the smallest order whose type
    /// AS BUILT meets the mask: an even order is a type b or c, whose stop edge sits above type
    /// a's, so it is checked against the mask and the order steps up when it misses.
    /// </param>
    public static int? Minimum(
        FilterResponse response, NetworkForm form,
        double apDb, double asDb,
        double fpHz, double fsHz,
        double fp1Hz, double fp2Hz, double fs1Hz, double fs2Hz,
        EllipticType ellipticType = EllipticType.A)
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
            // |S21|² = 1/(1 + (εp Ω^n)²), so Ω_s^n ≥ εs/εp. (A stray /2 here once halved the order.)
            FilterResponse.Butterworth  => Ceil(Math.Log(epsS / epsP) / Math.Log(omegaS)),
            FilterResponse.Chebyshev    => Ceil(Acosh(epsS / epsP) / Acosh(omegaS)),
            FilterResponse.InvChebyshev => Ceil(Acosh(epsS / epsP) / Acosh(omegaS)),
            FilterResponse.Elliptic     => EllipticMinimum(epsP, epsS, omegaS) is { } n
                                           ? BuildableElliptic(n, apDb <= 0 ? 3.0102999566 : apDb,
                                                               asDb, omegaS, ellipticType)
                                           : null,
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
    /// The smallest order from <paramref name="n"/> up whose resolved type meets the stop edge.
    /// An odd order is type a, which meets it by the degree equation.
    /// </summary>
    private static int? BuildableElliptic(int n, double apDb, double asDb, double omegaS, EllipticType type)
    {
        if (type == EllipticType.A) return n;
        for (int m = n; m <= n + 2; m++)
        {
            var t = EllipticTypes.Resolve(type, m);
            if (t == EllipticType.A)
            {
                if (m % 2 == 1) return m;
                continue;
            }
            try
            {
                if (EllipticPrototype.Roots(m, apDb, asDb, t).StopEdge <= omegaS * (1 + 1e-12)) return m;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return null;
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
                // The prototype's Ω = 1 sits at the PASSBAND edges, which bound the rejected band
                // from outside; the stop edges sit between them, nearer the centre. Mapped through
                // Ω = BW·f / (f0² − f²) they land at |Ω| > 1 — the bandpass case, turned inside out.
                if (!(fp1 > 0) || !(fs1 > fp1) || !(fs2 > fs1) || !(fp2 > fs2))
                    return false;
                double w0 = Math.Sqrt(fp1 * fp2);
                double bw = fp2 - fp1;
                double Map(double f) => Math.Abs(f * bw / (w0 * w0 - f * f));
                omegaS = Math.Min(Map(fs1), Map(fs2));
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
