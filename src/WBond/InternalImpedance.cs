using System.Numerics;

namespace CircuitRF.WBond;

/// <summary>
/// Skin effect in a round wire: R(f) and the internal inductance L_int(f), from the exact Bessel
/// solution (wbond.md §3.5, R-wb-8).
///
/// <h3>The one insight that makes this cheap</h3>
/// <para>The exact internal impedance per unit length is</para>
/// <code>
/// Z_int(ω) = R_dc · (γa/2) · I₀(γa)/I₁(γa),   γ = √(jωμσ),   R_dc = 1/(σπa²)
/// </code>
/// <para>and since γa = (1+j)·q with <b>q = a/δ</b> and δ = √(2/ωμσ), the ratio
/// <b>Z_int/R_dc is a function of the single dimensionless parameter q</b> — not of radius, metal or
/// frequency separately. That is what turns a per-wire-per-frequency complex Bessel evaluation into
/// a one-dimensional problem (WB10).</para>
///
/// <h3>Three regimes, each exact where it is used</h3>
/// <list type="bullet">
/// <item><b>q &lt; 0.4</b> — the ascending series <c>Z/R_dc = 1 + z²/8 − z⁴/192 + …</c>, which with
///   z = (1+j)q gives <c>R/R_dc = 1 + q⁴/48</c> and <c>X/R_dc = q²/4</c> directly. Those are exactly
///   the two small-q asymptotes tier 6 gates against, so the series and the oracle share an
///   origin — which is why the oracle for this regime is the <i>independent</i> continued
///   fraction.</item>
/// <item><b>0.4 ≤ q ≤ 25</b> — a continued fraction for I₁/I₀. Chosen over the ascending series
///   because the series suffers real cancellation for complex argument: the loss goes as
///   e^(|z|−Re z) = e^(0.414q), which is ~5 digits by q = 30 and ~11 by q = 60. The continued
///   fraction has no such cancellation.</item>
/// <item><b>q &gt; 25</b> — the asymptotic expansion <c>I₀/I₁ ≈ 1 + 1/(2z) + 3/(8z²)</c>, giving
///   <c>Z/R_dc ≈ z/2 + 1/4 + 3/(16z)</c> whose real part is the familiar
///   <c>q/2 + ¼ + 3/(32q)</c>.</item>
/// </list>
///
/// <h3>The gap the brief warned about</h3>
/// <para>Neither <i>asymptotic series</i> is usable for q ≈ 1–4: at q = 2 the exact value is
/// 1.264643 while the small-q form gives 1.333 (+5.4 %) and the large-q form 1.296875 (+2.5 %).
/// That band is roughly 100 MHz–1 GHz for a 0.5 mil gold wire — the low end of the tool's own
/// range, not an exotic corner. The continued fraction covers it exactly, which is why it exists
/// rather than a fit stretched across the gap.</para>
/// </summary>
public static class InternalImpedance
{
    /// <summary>Magnetic permeability of free space. Bond-wire metals are all non-magnetic (μr = 1).</summary>
    public const double Mu0 = 1.25663706127e-6;

    /// <summary>Below this q the ascending series is exact to machine precision.</summary>
    private const double SeriesLimit = 0.4;

    /// <summary>Above this q the asymptotic expansion beats the continued fraction on cost, at ~1e-7.</summary>
    private const double AsymptoticLimit = 25.0;

    /// <summary>Skin depth δ = √(2/ωμσ), metres.</summary>
    public static double SkinDepth(double frequencyHz, double sigma)
    {
        if (frequencyHz <= 0.0) return double.PositiveInfinity;
        return Math.Sqrt(2.0 / (2.0 * Math.PI * frequencyHz * Mu0 * sigma));
    }

    /// <summary>The dimensionless parameter q = a/δ that the whole calculation depends on.</summary>
    public static double QParameter(double frequencyHz, double radiusM, double sigma)
    {
        double delta = SkinDepth(frequencyHz, sigma);
        return double.IsPositiveInfinity(delta) ? 0.0 : radiusM / delta;
    }

    /// <summary>DC resistance per unit length, Ω/m.</summary>
    public static double DcResistancePerMetre(double radiusM, double sigma) =>
        1.0 / (sigma * Math.PI * radiusM * radiusM);

    /// <summary>
    /// <c>Z_int/R_dc</c> as a function of q alone — the whole of the physics, in one dimensionless
    /// complex number.
    /// </summary>
    public static Complex NormalizedZ(double q)
    {
        if (q < 0.0) throw new ArgumentOutOfRangeException(nameof(q), q, "q = a/δ cannot be negative.");
        if (q == 0.0) return Complex.One;

        var z = new Complex(q, q);   // γa = (1+j)q

        if (q < SeriesLimit)
        {
            // Z/R_dc = 1 + u/8 − u²/192 + u³/3072, with u = z². Obtained by dividing the ascending
            // series for I₀ by that for I₁ — every term is real in u, which is what makes the two
            // familiar asymptotes fall straight out at u = 2jq²: R/R_dc = 1 + q⁴/48 and X/R_dc = q²/4.
            //
            // The u³ term matters: without it the REACTANCE is wrong by 1.0e-6 relative at q = 0.1,
            // which is above the 5e-7 the reference table is gated to. The resistance would not have
            // noticed — the same asymmetry as the large-q z⁻² term in LargeQ().
            Complex u = z * z;
            return Complex.One + u / 8.0 - u * u / 192.0 + u * u * u / 3072.0;
        }

        if (q > AsymptoticLimit)
            return LargeQ(z);

        // Z/R_dc = (z/2)·I₀/I₁ = (z/2) / (I₁/I₀)
        return z / 2.0 / RatioI1OverI0(z);
    }

    /// <summary>
    /// The asymptotic expansion, from <c>I₀/I₁ ~ 1 + 1/(2z) + 3/(8z²) + 3/(8z³)</c>:
    /// <code>Z/R_dc ~ z/2 + 1/4 + 3/(16z) + 3/(16z²)</code>
    ///
    /// <para><b>The z⁻² term is not decoration.</b> For z = (1+j)q it is purely imaginary, so it
    /// leaves R(f) untouched and improves L_int(f) by an order of magnitude — measured, the complex
    /// error at the q = 25 crossover falls from 8.6e-6 to 3.2e-7, and at q = 30 from 5.0e-6 to
    /// 1.6e-7. Dropping it because "the resistance does not change" would quietly degrade the
    /// inductance the whole tool exists to compute.</para>
    /// </summary>
    private static Complex LargeQ(Complex z) =>
        z / 2.0 + 0.25 + 3.0 / (16.0 * z) + 3.0 / (16.0 * z * z);

    /// <summary>
    /// The same quantity by the continued fraction <b>at every q</b>, with no regime switching.
    ///
    /// <para>This is the slow reference path. It exists so the fast <see cref="NormalizedZ"/> can be
    /// gated against an evaluation that shares none of its branches — otherwise the large-q tier
    /// would be comparing the asymptotic expansion against itself, which is vacuous. Accurate
    /// everywhere because the continued fraction has no cancellation, unlike the ascending series.</para>
    /// </summary>
    public static Complex NormalizedZExact(double q)
    {
        if (q < 0.0) throw new ArgumentOutOfRangeException(nameof(q), q, "q = a/δ cannot be negative.");
        if (q == 0.0) return Complex.One;

        var z = new Complex(q, q);
        return z / 2.0 / RatioI1OverI0(z);
    }

    /// <summary>
    /// <c>I₁(z)/I₀(z)</c> by backward recurrence on the continued fraction
    /// <c>I_{ν+1}/I_ν = 1/( 2(ν+1)/z + I_{ν+2}/I_{ν+1} )</c>.
    ///
    /// <para>Derived from the standard recurrence I_{ν−1} − I_{ν+1} = (2ν/z)·I_ν. Started far enough
    /// above |z| that the seed is irrelevant, and evaluated downward, which is the direction that is
    /// numerically stable for modified Bessel ratios.</para>
    /// </summary>
    private static Complex RatioI1OverI0(Complex z)
    {
        int terms = (int)(2.0 * z.Magnitude) + 40;

        Complex r = Complex.Zero;
        for (int v = terms; v >= 0; v--)
            r = Complex.One / (2.0 * (v + 1) / z + r);

        return r;
    }

    /// <summary>
    /// Resistance and internal inductance per unit length at one frequency.
    /// </summary>
    /// <returns>
    /// <c>ResistancePerMetre</c> in Ω/m and <c>InternalInductancePerMetre</c> in H/m. The internal
    /// inductance is the <b>only</b> inductive term this class produces — the external inductance
    /// comes from <see cref="Grover"/> with GMD = a, so the whole frequency dependence lives here
    /// and nowhere else (D4 / WB8).
    /// </returns>
    public static (double ResistancePerMetre, double InternalInductancePerMetre) PerMetre(
        double frequencyHz, double radiusM, double sigma)
    {
        double rdc = DcResistancePerMetre(radiusM, sigma);

        if (frequencyHz <= 0.0)
            return (rdc, Mu0 / (8.0 * Math.PI));   // the DC limit of the internal inductance

        double q = QParameter(frequencyHz, radiusM, sigma);
        var normalized = NormalizedZ(q);

        double omega = 2.0 * Math.PI * frequencyHz;
        return (normalized.Real * rdc, normalized.Imaginary * rdc / omega);
    }

    /// <summary>
    /// The small-q asymptote <c>R/R_dc = 1 + q⁴/48</c>. Public because tier 6 gates against it and a
    /// test that re-derives its own oracle proves nothing.
    /// </summary>
    public static double SmallQResistanceAsymptote(double q) => 1.0 + q * q * q * q / 48.0;

    /// <summary>The large-q asymptote <c>R/R_dc = q/2 + ¼ + 3/(32q)</c>.</summary>
    public static double LargeQResistanceAsymptote(double q) => q / 2.0 + 0.25 + 3.0 / (32.0 * q);
}
