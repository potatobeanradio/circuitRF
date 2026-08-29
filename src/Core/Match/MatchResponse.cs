using System.Numerics;

namespace CircuitRF.Core.Matching;

/// <summary>
/// The ladder's two-port response, by ABCD cascade against the two real port resistances.
/// </summary>
/// <remarks>
/// This exists in production because match.md §6.2 step 5 scores a response family by the worst
/// in-band return loss of the resulting bandpass ladder, so the constrained search needs a response
/// evaluator of its own; MN-3's plots read the same one. <c>tests/Core.Tests/Match</c> carries a
/// SEPARATE cascade written independently, so the golden numbers are not checked by the code that
/// produced them.
/// </remarks>
public static class MatchResponse
{
    /// <summary>S11 and S21 at one frequency, referenced to the network's own port resistances.</summary>
    public static (Complex S11, Complex S21) At(MatchNetwork network, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(network);
        double om = 2.0 * Math.PI * frequencyHz;

        // ABCD of the cascade, left to right.
        Complex a = Complex.One, b = Complex.Zero, c = Complex.Zero, d = Complex.One;
        foreach (var e in network.Elements)
        {
            Complex z = e.Type == ElementType.L
                ? new Complex(0.0, om * e.Value)
                : Complex.One / new Complex(0.0, om * e.Value);

            if (e.IsShunt)
            {
                Complex y = Complex.One / z;
                a += b * y;
                c += d * y;
            }
            else
            {
                b += a * z;
                d += c * z;
            }
        }

        double z1 = network.R1, z2 = network.R2;
        Complex den = a * z2 + b + c * z1 * z2 + d * z1;
        Complex s11 = (a * z2 + b - c * z1 * z2 - d * z1) / den;
        Complex s21 = 2.0 * Math.Sqrt(z1 * z2) / den;
        return (s11, s21);
    }

    /// <summary>Worst in-band |S11| in dB (a number near 0 is a bad match; -20 is a good one).</summary>
    public static double WorstReturnLossDb(MatchNetwork network, double f1, double f2, int points = 201)
    {
        double worst = 0.0;
        for (int i = 0; i < points; i++)
        {
            double f = f1 + (f2 - f1) * i / (points - 1.0);
            double m = At(network, f).S11.Magnitude;
            if (m > worst) worst = m;
        }
        return 20.0 * Math.Log10(worst);
    }

    /// <summary>
    /// Worst |S11| in dB over a SET of bands — match.md §18's dual-band figure, and the single-band
    /// one when the set has one member.
    /// </summary>
    /// <remarks>
    /// <b>Points are per band, not shared across them</b>, so a dual-band figure samples each band as
    /// densely as the single-band overload samples its one. The gap between bands is deliberately not
    /// sampled: it is where the Fano budget is being SAVED (§18.1), and including it would report the
    /// design working as though it were failing.
    /// </remarks>
    public static double WorstReturnLossDb(
        MatchNetwork network, IReadOnlyList<(double Lo, double Hi)> bands, int points = 201)
    {
        ArgumentNullException.ThrowIfNull(bands);
        double worst = 0.0;
        foreach (var (lo, hi) in bands)
        {
            for (int i = 0; i < points; i++)
            {
                double f = lo + (hi - lo) * i / (points - 1.0);
                double m = At(network, f).S11.Magnitude;
                if (m > worst) worst = m;
            }
        }
        return 20.0 * Math.Log10(worst);
    }

    /// <summary>The largest |S11| between two bands — the gap mismatch of match.md §18.4.</summary>
    /// <remarks>
    /// <b>Reported as a number rather than hidden</b>, because it is the mechanism working: a finite
    /// ladder buys its in-band return loss by leaving the gap unmatched, and the gap maximum RISING
    /// with order is the reclaim happening (§18.1).
    /// </remarks>
    public static double GapMaxS11(MatchNetwork network, double f2, double f3, int points = 201)
    {
        if (!(f3 > f2)) return 0.0;
        double worst = 0.0;
        for (int i = 0; i < points; i++)
        {
            double f = f2 + (f3 - f2) * i / (points - 1.0);
            double m = At(network, f).S11.Magnitude;
            if (m > worst) worst = m;
        }
        return worst;
    }

    /// <summary>Insertion loss (dB, positive) and its in-band ripple.</summary>
    public static (double LossDb, double RippleDb) InsertionLoss(
        MatchNetwork network, double f1, double f2, int points = 401)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < points; i++)
        {
            double f = f1 + (f2 - f1) * i / (points - 1.0);
            double db = 20.0 * Math.Log10(At(network, f).S21.Magnitude);
            lo = Math.Min(lo, db);
            hi = Math.Max(hi, db);
        }
        return (-lo, hi - lo);
    }
}
