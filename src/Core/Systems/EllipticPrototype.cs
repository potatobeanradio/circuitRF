using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// The elliptic (Cauer) response — the one family in this series needing mathematics the repository
/// did not have: Jacobi elliptic functions, the complete elliptic integral, and the degree equation
/// that ties order, selectivity and the two ripple figures together.
///
/// <para><b>What makes it different from the other four.</b> Butterworth, Chebyshev and inverse
/// Chebyshev each name their characteristic function in closed form. The elliptic one does not: its
/// <c>R_n(ξ, Ω)</c> is defined by where its zeros are, those sit at Jacobi <c>cd</c> values, and the
/// selectivity <c>ξ</c> that places them is itself the solution of a transcendental equation in the
/// two dB figures the user typed. So the construction is: solve for <c>ξ</c>, place the zeros, and
/// hand the resulting rational characteristic function to
/// <see cref="FilterPrototype.FromCharacteristic"/>, which is the same road every other family
/// takes from there on.</para>
///
/// <para><b>The degree equation.</b> With <c>ε_p</c> from the passband ripple and <c>ε_s</c> from
/// the stopband floor, write <c>k1 = ε_p/ε_s</c> (the DISCRIMINATION modulus) and <c>k = 1/ξ</c>
/// (the SELECTIVITY modulus). The two are tied by</para>
/// <code>
///   n · K(k')/K(k)  =  K(k1')/K(k1)          k' = sqrt(1 − k²)
/// </code>
/// <para>which is solved not by iteration but through the elliptic NOME: <c>q = exp(−π K'/K)</c>
/// turns the equation into <c>q = q1^(1/n)</c> — one exponential — and <c>k</c> comes back from
/// <c>q</c> through the theta-function series. Solving the equation directly by bisection on
/// <c>K'/K</c> would work and would be slower and less accurate at exactly the selective end where
/// the family is used.</para>
///
/// <para><b>Why the order is not clamped.</b> <c>n</c>, <c>Ripple</c> and <c>Astop</c> are three
/// numbers and the degree equation determines the fourth — the transition width. Every combination
/// of the three is realisable; a demanding one simply produces a wide transition, and saying so
/// with a plot is more use than a refusal. The only genuine refusal here is a stopband floor at or
/// below the passband ripple, which is not a filter.</para>
/// </summary>
internal static class EllipticPrototype
{
    /// <summary>Builds the elliptic prototype of order <paramref name="n"/>.</summary>
    internal static FilterPrototype Build(int n, double rippleDb, double astopDb)
    {
        double epsP = FilterPrototype.EpsFromDb(rippleDb, nameof(rippleDb));
        double epsS = FilterPrototype.EpsFromDb(astopDb,  nameof(astopDb));

        if (!(epsS > epsP))
            throw new ArgumentOutOfRangeException(nameof(astopDb),
                $"An elliptic filter's stopband floor must be deeper than its passband ripple; got " +
                $"Astop = {astopDb:G6} dB against Ripple = {rippleDb:G6} dB. Below that the two " +
                "bands cross and there is no transition band to design.");

        int    l  = n / 2;                       // conjugate zero pairs
        int    r  = n % 2;                       // the zero at the origin, for odd n
        double k1 = epsP / epsS;
        double k  = SelectivityModulus(n, k1);
        double xi = 1.0 / k;

        // Zeros of R_n on the Ω axis: ±ζ_m, plus the origin when n is odd. Poles: ±ξ/ζ_m, which is
        // the inversion relation R_n(ξ, ξ/x) = L_n/R_n(ξ, x) written as a factorisation.
        double kk = k * k;
        double bigK = EllipK(k);

        double[] num = FilterPrototype.Monomial(r);        // Ω^r  (or [1] when n is even)
        double[] den = [1.0];
        for (int m = 1; m <= l; m++)
        {
            double zeta = JacobiCd((2.0 * m - 1.0) / n * bigK, kk);
            num = MatchPoly.Mul(num, [1.0, 0.0, -zeta * zeta]);
            double pole = xi / zeta;
            den = MatchPoly.Mul(den, [1.0, 0.0, -pole * pole]);
        }

        // r0 pins the passband edge: C_n(1) = 1, so |S21|² there is exactly 1/(1 + ε_p²) — the
        // stated ripple, at the stated edge, by construction rather than by fit.
        double r0 = MatchPoly.Eval(den, 1.0).Real / MatchPoly.Eval(num, 1.0).Real;
        num = [.. num.Select(c => c * r0)];

        return FilterPrototype.FromCharacteristic(FilterResponse.Elliptic, n, den, num, epsP);
    }

    /// <summary>
    /// The selectivity modulus <c>k = 1/ξ</c> satisfying the degree equation, via the nome.
    /// </summary>
    private static double SelectivityModulus(int n, double k1)
    {
        double bigK  = EllipK(k1);
        double bigKp = EllipK(Math.Sqrt(1.0 - k1 * k1));

        // q = q1^(1/n) written as one exponential rather than a Pow of an underflowed q1: a
        // selective specification puts q1 near 1e-17 and a demanding one below the smallest double,
        // and q1 = 0 would return k = 0 — an infinitely selective filter — in perfect silence.
        double q = Math.Exp(-Math.PI * bigKp / (n * bigK));
        return ModulusFromNome(q);
    }

    /// <summary>
    /// <c>k = (θ₂(0,q)/θ₃(0,q))²</c>, the theta-function series for the modulus of a given nome.
    /// </summary>
    /// <remarks>
    /// The series converge geometrically in <c>q</c>, and <c>q &lt; 0.05</c> for anything a filter
    /// user would type, so a dozen terms are far past double precision. The loop stops early on a
    /// term that no longer moves the sum rather than trusting that.
    /// </remarks>
    private static double ModulusFromNome(double q)
    {
        if (!(q > 0.0)) return 0.0;

        double num = 0.0, den = 0.0;
        for (int m = 1; m <= 32; m++)
        {
            double a = Math.Pow(q, m * (m + 1));
            double b = Math.Pow(q, m * m);
            num += a;
            den += b;
            if (a == 0.0 && b == 0.0) break;
        }
        double t = (1.0 + num) / (1.0 + 2.0 * den);
        return 4.0 * Math.Sqrt(q) * t * t;
    }

    /// <summary>
    /// The complete elliptic integral of the first kind <c>K(k)</c>, by the arithmetic-geometric
    /// mean: <c>K = π/(2·AGM(1, k'))</c>. Quadratically convergent, so five or six iterations reach
    /// double precision at every modulus this file uses.
    /// </summary>
    internal static double EllipK(double k)
    {
        double a = 1.0, b = Math.Sqrt(Math.Max(0.0, 1.0 - k * k));
        for (int i = 0; i < 64 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (a + b);
    }

    /// <summary>
    /// The Jacobi elliptic function <c>cd(u, k) = cn/dn</c> at real <paramref name="u"/>, with
    /// <paramref name="m"/> = <c>k²</c>, by the descending Landen (AGM) transformation.
    /// </summary>
    /// <remarks>
    /// The ascending series would be the obvious route and is the wrong one: it loses accuracy
    /// exactly as <c>k</c> approaches 1, which is the selective end of every elliptic design. The
    /// descending transformation drives the modulus TOWARDS zero, so its accuracy improves where
    /// the series' would fail.
    /// </remarks>
    internal static double JacobiCd(double u, double m)
    {
        const int Max = 24;
        var a = new double[Max + 1];
        var c = new double[Max + 1];

        a[0] = 1.0;
        c[0] = Math.Sqrt(m);
        double b = Math.Sqrt(Math.Max(0.0, 1.0 - m));

        int n = 0;
        while (n < Max && Math.Abs(c[n]) > 1e-16)
        {
            a[n + 1] = 0.5 * (a[n] + b);
            c[n + 1] = 0.5 * (a[n] - b);
            b = Math.Sqrt(a[n] * b);
            n++;
        }

        double phi = Math.ScaleB(1.0, n) * a[n] * u;
        for (int i = n; i >= 1; i--)
        {
            double arg = c[i] / a[i] * Math.Sin(phi);
            phi = 0.5 * (phi + Math.Asin(Math.Clamp(arg, -1.0, 1.0)));
        }

        double sn = Math.Sin(phi), cn = Math.Cos(phi);
        double dn = Math.Sqrt(Math.Max(0.0, 1.0 - m * sn * sn));
        return cn / dn;
    }
}
