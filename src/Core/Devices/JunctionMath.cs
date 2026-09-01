namespace CircuitRF.Core.Devices;

/// <summary>
/// The two closed forms every junction in this directory is built out of: the saturation-current
/// exponential, and the depletion charge with its capacitance.
///
/// <para><b>Why this exists.</b> <see cref="DiodeModel"/>, <see cref="BjtModel"/> and
/// <see cref="Fet.FetModelBase"/> each wrote their own copy of both, and the copies agree — but
/// they are three answers to one question, which is the arrangement <see cref="Temperature"/> was
/// pulled out to end for the temperature relations. Every model added after that one shares these,
/// so a fix to the tangent continuation or to the M = 1 special case lands once.</para>
///
/// <para><b>Both are continued by their TANGENT, never clamped.</b> Above the exponential's
/// argument limit, and above <c>Fc·Vj</c> for the depletion charge, the published expression runs
/// away and a Newton iterate that overshoots there cannot come back. A clamp keeps the value finite
/// and puts a kink in the Jacobian, which stalls the solve in a way that reads as a bad circuit;
/// continuing along the tangent keeps value AND slope continuous, which is what keeps Newton
/// convergent. This is standard practice, not a shortcut.</para>
/// </summary>
public static class JunctionMath
{
    /// <summary>
    /// Where the exponential is replaced by its tangent. Above this it is astronomically large and
    /// Newton cannot recover from a single overshoot, so the model continues linearly from the last
    /// sane point.
    /// </summary>
    public const double ExpArgLimit = 40.0;

    /// <summary>
    /// One saturation-current exponential and its derivative:
    /// <c>I = Isat·(exp(V/Vte) − 1)</c>, <c>G = dI/dV</c>.
    /// </summary>
    /// <param name="v">Junction voltage, V.</param>
    /// <param name="isat">Saturation current, A. Zero or negative means the branch is not modelled
    /// and returns exactly zero — never a small current, which would be a leak nothing asked for.</param>
    /// <param name="vte">The exponential's scale, <c>N·Vt</c>.</param>
    public static (double I, double G) Exponential(double v, double isat, double vte)
    {
        if (isat <= 0 || vte <= 0) return (0.0, 0.0);

        double x = v / vte;
        if (x > ExpArgLimit)
        {
            double eL = System.Math.Exp(ExpArgLimit);
            double iL = isat * (eL - 1.0);
            double gL = isat * eL / vte;
            return (iL + gL * (v - ExpArgLimit * vte), gL);
        }

        double ex = System.Math.Exp(x);
        return (isat * (ex - 1.0), isat * ex / vte);
    }

    /// <summary>
    /// Depletion (junction) charge and its derivative, the small-signal junction capacitance:
    /// <c>Q(V) = Cj0·Vj/(1−M)·(1 − (1 − V/Vj)^(1−M))</c> below <c>Fc·Vj</c>, continued by its
    /// tangent above. <c>M == 1</c> is the logarithmic special case and is handled exactly rather
    /// than by nudging M off 1.
    /// </summary>
    /// <param name="v">Junction voltage, V.</param>
    /// <param name="cj0">Zero-bias capacitance, F. Zero or negative means no junction charge.</param>
    /// <param name="vj">Junction (built-in) potential, V — already at the device temperature.</param>
    /// <param name="m">Grading coefficient.</param>
    /// <param name="fc">Forward-bias changeover coefficient; must stay below 1 or the expression
    /// divides by zero at the changeover, which is why every caller sanitises it once at
    /// construction rather than per evaluation.</param>
    public static (double Q, double C) Depletion(double v, double cj0, double vj, double m, double fc)
    {
        if (cj0 <= 0 || vj <= 0) return (0.0, 0.0);

        double fcvj = fc * vj;
        if (v <= fcvj)
        {
            double u = 1.0 - v / vj;
            double c = cj0 * System.Math.Pow(u, -m);
            double q = System.Math.Abs(1.0 - m) < 1e-12
                ? -cj0 * vj * System.Math.Log(u)
                : cj0 * vj / (1.0 - m) * (1.0 - System.Math.Pow(u, 1.0 - m));
            return (q, c);
        }

        double u0  = 1.0 - fc;
        double c0  = cj0 * System.Math.Pow(u0, -m);
        double dc0 = cj0 * m * System.Math.Pow(u0, -m - 1.0) / vj;   // dC/dV at the changeover
        double q0  = System.Math.Abs(1.0 - m) < 1e-12
            ? -cj0 * vj * System.Math.Log(u0)
            : cj0 * vj / (1.0 - m) * (1.0 - System.Math.Pow(u0, 1.0 - m));
        double dv = v - fcvj;
        return (q0 + c0 * dv + 0.5 * dc0 * dv * dv, c0 + dc0 * dv);
    }

    /// <summary>
    /// Sanitises a forward-bias changeover coefficient. Outside (0, 0.95) the depletion expression
    /// either divides by zero at the changeover or continues from a point past where it means
    /// anything, so a value there is read as "not stated" and takes the conventional 0.5.
    /// </summary>
    public static double SanitiseFc(double fc) => fc is > 0 and < 0.95 ? fc : 0.5;
}
