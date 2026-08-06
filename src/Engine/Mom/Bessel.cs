using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Bessel and Hankel functions of the first and second kind, orders 0 and 1, for <b>complex</b>
/// argument. Kernel B needs J₀ for every Sommerfeld/Hankel transform and H₀⁽²⁾ for the closed-form
/// surface-wave term; kernel A needed neither, and nothing else in this repository provides them.
///
/// <para><b>Written, not depended on — and this was a decision to surface (brief §2, §8.3).</b> The
/// root <c>CLAUDE.md</c> reserves dependency additions to the owner, so no package was added. It is
/// also not transcribed: <i>Numerical Recipes</i>-style coefficient tables are copyrighted and
/// their licence does not permit it, and Abramowitz &amp; Stegun §9.4's polynomial fits are public domain
/// but real-argument only and good to ~1e-8, which is not enough for a function that has to serve
/// as an <i>oracle</i>. What is implemented instead is the pair of <i>defining series</i>, which
/// are mathematics rather than anyone's code:</para>
/// <list type="bullet">
///   <item><b>|z| &lt; 13</b> — the ascending power series (A&amp;S 9.1.10 / 9.1.11 in form, but
///     generated term-by-term from the recurrence here, with no tabulated coefficients).</item>
///   <item><b>|z| ≥ 13</b> — the Hankel asymptotic expansion, whose coefficients
///     <c>a_m(ν) = Π_{k=1..m}(4ν² − (2k−1)²) / (m!·8^m)</c> are <b>computed</b> from that product,
///     not written down. Truncated at the smallest term, which is where a divergent asymptotic
///     series is closest.</item>
/// </list>
///
/// <para><b>Measured accuracy, not asserted</b> (see <c>BesselTests</c>): J₀/J₁ agree with the
/// integral representation <c>J₀(x) = (1/π)∫₀^π cos(x sin θ)dθ</c> — an independent formula
/// integrated by Gauss-Legendre to round-off — to <b>2.9e-13 / 1.1e-13 absolute</b> over
/// 0.05 ≤ x ≤ 200, worst right at the series/asymptotic crossover, as expected. Y₀/Y₁ are pinned by
/// the Wronskian <c>J₁(z)Y₀(z) − J₀(z)Y₁(z) = 2/(πz)</c> — <b>5.6e-11 relative</b>, worst at
/// z = 7 − 6j — which holds for complex z and would catch a sign or normalisation error in either
/// function.</para>
///
/// <para><b>Branch cuts.</b> The Y/H series carry <c>Log(z/2)</c>, so the negative real axis is a
/// branch cut. Every call site here keeps <c>Re z &gt; 0</c> (a Hankel transform of a real radial
/// distance), and nothing tries to be clever about continuing across it.</para>
///
/// <para><b>Large |Im z| is bounded by the caller, deliberately.</b> J₀(z) grows like
/// e^{|Im z|}/√|z|, so an integration contour deformed far off the real axis makes an O(1) integral
/// out of enormous cancelling terms. The Sommerfeld contour in <see cref="SommerfeldIntegral"/>
/// therefore stays on the real k_ρ axis; the only complex arguments reaching here have modest
/// imaginary parts.</para>
/// </summary>
public static class Bessel
{
    /// <summary>Euler-Mascheroni γ.</summary>
    public const double EulerGamma = 0.57721566490153286060651209008240243104;

    /// <summary>
    /// |z| at which the ascending series hands over to the asymptotic expansion. Chosen where the
    /// two error mechanisms cross: the series loses ~e^{|z|}·ε to cancellation, the asymptotic
    /// series bottoms out near e^{−2|z|}. Both are ~1e-10 here, which is the worst case anywhere.
    /// </summary>
    private const double SeriesRadius = 13.0;

    private const int MaxSeriesTerms = 200;
    private const int MaxAsymptoticTerms = 40;

    // ---------------------------------------------------------------------------------------
    // Public entry points
    // ---------------------------------------------------------------------------------------

    public static Complex J0(Complex z) =>
        z.Magnitude < SeriesRadius ? J0Series(z) : 0.5 * (H0(z, +1) + H0(z, -1));

    public static Complex J1(Complex z) =>
        z.Magnitude < SeriesRadius ? J1Series(z) : 0.5 * (H1(z, +1) + H1(z, -1));

    public static Complex Y0(Complex z) =>
        z.Magnitude < SeriesRadius ? Y0Series(z) : (H0(z, +1) - H0(z, -1)) / new Complex(0, 2);

    public static Complex Y1(Complex z) =>
        z.Magnitude < SeriesRadius ? Y1Series(z) : (H1(z, +1) - H1(z, -1)) / new Complex(0, 2);

    /// <summary>
    /// H₀⁽²⁾(z) = J₀(z) − j·Y₀(z) — the outgoing-wave Hankel function under the e^{jωt} convention
    /// this repository uses throughout. It is what the extracted surface-wave pole inverts to in
    /// closed form (<see cref="Dcim"/>), so it is the one Hankel function kernel B actually needs.
    /// Evaluated from its <i>own</i> asymptotic series for large |z| rather than as J₀ − jY₀, which
    /// would cancel two O(1) numbers to produce an exponentially small one when Im z &lt; 0.
    /// </summary>
    public static Complex H02(Complex z) =>
        z.Magnitude < SeriesRadius ? J0Series(z) - Complex.ImaginaryOne * Y0Series(z) : H0(z, -1);

    /// <summary>
    /// H₁⁽²⁾(z) = J₁(z) − j·Y₁(z). <b>L9c — needed only because <c>d/dz H₀⁽²⁾ = −H₁⁽²⁾</c></b>, and the
    /// mixed dyadic component enters the fill through a ∂/∂x rather than as a value. Same construction
    /// as <see cref="H02"/> and for the same reason: its own asymptotic series for large |z|, never
    /// J₁ − jY₁, which cancels two O(1) numbers where the answer is exponentially small.
    /// </summary>
    public static Complex H12(Complex z) =>
        z.Magnitude < SeriesRadius ? J1Series(z) - Complex.ImaginaryOne * Y1Series(z) : H1(z, -1);

    /// <summary>H₀⁽¹⁾(z) = J₀(z) + j·Y₀(z) — present for the Wronskian/round-trip gates.</summary>
    public static Complex H01(Complex z) =>
        z.Magnitude < SeriesRadius ? J0Series(z) + Complex.ImaginaryOne * Y0Series(z) : H0(z, +1);

    /// <summary>
    /// The Wronskian residual <c>J₁(z)Y₀(z) − J₀(z)Y₁(z) − 2/(πz)</c>. Zero identically; exposed so
    /// the tests can assert it rather than reasoning about the series coefficients.
    /// </summary>
    public static Complex WronskianResidual(Complex z) =>
        J1(z) * Y0(z) - J0(z) * Y1(z) - 2.0 / (Math.PI * z);

    // ---------------------------------------------------------------------------------------
    // Ascending series.  J₀(z) = Σ (−1)^k (z²/4)^k / (k!)²  and its companions; every coefficient
    // comes from the previous one by a multiplication, so nothing is tabulated.
    // ---------------------------------------------------------------------------------------

    private static Complex J0Series(Complex z)
    {
        Complex q = -0.25 * z * z;          // −z²/4
        Complex term = Complex.One, sum = Complex.One;
        for (int k = 1; k <= MaxSeriesTerms; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term.Magnitude <= 1e-18 * sum.Magnitude) break;
        }
        return sum;
    }

    private static Complex J1Series(Complex z)
    {
        Complex q = -0.25 * z * z;
        Complex term = Complex.One, sum = Complex.One;   // Σ q^k / (k!(k+1)!) · (k+1)!/1 … see below
        // J₁(z) = (z/2) Σ_k q^k / (k! (k+1)!) with q = −z²/4.
        for (int k = 1; k <= MaxSeriesTerms; k++)
        {
            term *= q / (k * (double)(k + 1));
            sum += term;
            if (term.Magnitude <= 1e-18 * sum.Magnitude) break;
        }
        return 0.5 * z * sum;
    }

    private static Complex Y0Series(Complex z)
    {
        // Y₀(z) = (2/π)[ln(z/2) + γ]J₀(z) + (2/π) Σ_{k≥1} (−1)^{k+1} H_k (z²/4)^k / (k!)²
        //       = (2/π)[ln(z/2) + γ]J₀(z) − (2/π) Σ_{k≥1} H_k q^k / (k!)²      with q = −z²/4.
        Complex q = -0.25 * z * z;
        Complex term = Complex.One, sum = Complex.Zero;
        double h = 0;
        for (int k = 1; k <= MaxSeriesTerms; k++)
        {
            term *= q / (k * (double)k);
            h += 1.0 / k;
            Complex contrib = h * term;
            sum += contrib;
            if (contrib.Magnitude <= 1e-18 * (sum.Magnitude + 1e-300)) break;
        }
        return (2.0 / Math.PI) * ((Complex.Log(0.5 * z) + EulerGamma) * J0Series(z) - sum);
    }

    private static Complex Y1Series(Complex z)
    {
        // Y₁(z) = (2/π)[ln(z/2)+γ]J₁(z) − 2/(πz) − (z/2π) Σ_{k≥0} (H_k + H_{k+1}) q^k / (k!(k+1)!)
        Complex q = -0.25 * z * z;
        Complex term = Complex.One;                 // q^k/(k!(k+1)!) at k = 0
        double hk = 0, hk1 = 1;                     // H₀ = 0, H₁ = 1
        Complex sum = (hk + hk1) * term;
        for (int k = 1; k <= MaxSeriesTerms; k++)
        {
            term *= q / (k * (double)(k + 1));
            hk = hk1;
            hk1 += 1.0 / (k + 1);
            Complex contrib = (hk + hk1) * term;
            sum += contrib;
            if (contrib.Magnitude <= 1e-18 * (sum.Magnitude + 1e-300)) break;
        }
        return (2.0 / Math.PI) * (Complex.Log(0.5 * z) + EulerGamma) * J1Series(z)
             - 2.0 / (Math.PI * z)
             - z * sum / (2.0 * Math.PI);
    }

    // ---------------------------------------------------------------------------------------
    // Hankel asymptotic expansion.
    //
    //   H_ν^{(1,2)}(z) ~ √(2/(πz)) · e^{±j(z − νπ/2 − π/4)} · Σ_m (±j)^m a_m(ν) / z^m
    //   a_m(ν) = Π_{k=1..m} (4ν² − (2k−1)²) / (m! 8^m)
    //
    // The product is evaluated here, so there are no magic constants to mistype or to have taken
    // from a copyrighted table.  The series is divergent; it is truncated at its smallest term,
    // which is the standard and is where its error is minimised (~e^{−2|z|}).
    // ---------------------------------------------------------------------------------------

    private static Complex H0(Complex z, int sign) => HankelAsymptotic(z, nu: 0, sign);
    private static Complex H1(Complex z, int sign) => HankelAsymptotic(z, nu: 1, sign);

    private static Complex HankelAsymptotic(Complex z, int nu, int sign)
    {
        double fourNuSq = 4.0 * nu * nu;
        Complex jSign = sign > 0 ? Complex.ImaginaryOne : -Complex.ImaginaryOne;

        Complex a = Complex.One;      // a_m(ν)
        Complex power = Complex.One;  // (±j)^m / z^m
        Complex sum = Complex.One;
        double bestMag = double.MaxValue;
        Complex best = sum;

        for (int m = 1; m <= MaxAsymptoticTerms; m++)
        {
            double f = fourNuSq - (2 * m - 1) * (double)(2 * m - 1);
            a *= f / (m * 8.0);
            power *= jSign / z;
            Complex term = a * power;
            double mag = term.Magnitude;
            if (mag > bestMag) break;   // the asymptotic series has started to diverge
            bestMag = mag;
            sum += term;
            best = sum;
            if (mag <= 1e-18 * sum.Magnitude) break;
        }

        Complex phase = z - (nu * Math.PI / 2.0) - (Math.PI / 4.0);
        Complex prefactor = Complex.Sqrt(2.0 / (Math.PI * z)) * Complex.Exp(jSign * phase);
        return prefactor * best;
    }
}
