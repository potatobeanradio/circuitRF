using System.Numerics;

namespace CircuitRF.Core.Systems;

/// <summary>
/// The elliptic (Cauer) response, built as ROOTS (<see cref="FilterRoots"/>): Jacobi elliptic
/// functions, the complete and incomplete elliptic integrals, and the degree equation that ties
/// order, selectivity and the two ripple figures together.
///
/// <para><b>The degree equation.</b> With <c>ε_p</c> from the passband ripple and <c>ε_s</c> from
/// the stopband floor, <c>k1 = ε_p/ε_s</c> is the discrimination modulus and <c>k = 1/Ω_s</c> the
/// selectivity modulus, tied by <c>n·K'(k)/K(k) = K'(k1)/K(k1)</c>. Through the elliptic nome that
/// is one exponential, <c>q = exp(−π K'(k1)/(n K(k1)))</c>, and the modulus comes back from the
/// theta series. Of <c>q</c> and its complement <c>q' = exp(−π n K(k1)/K'(k1))</c> the SMALLER is
/// used — <c>ln q · ln q' = π²</c>, so one of them is always below <c>e^−π</c> — and the modulus
/// on the other side is <c>√((1−k)(1+k))</c>. Computing <c>k'</c> as <c>√(1 − k²)</c> from a
/// <c>k</c> near 1 would lose most of its digits exactly at the high-order, narrow-transition end
/// the family is used for, so every routine here takes a modulus AND its complement.</para>
///
/// <para><b>Zeros and poles in closed form</b> (the standard parametrisation, as in Orfanidis'
/// lecture notes on elliptic filter design): with <c>u_i = (2i−1)/n</c>, the reflection zeros are
/// <c>ζ_i = cd(u_i K, k)</c>, the transmission zeros <c>1/(k ζ_i)</c>, and the poles
/// <c>p_i = j·cd((u_i − j v0) K, k)</c> plus, at odd order, the real pole
/// <c>p_0 = j·sn(j v0 K, k)</c>, where <c>sn(j v0 n K1, k1) = j/ε_p</c>. No polynomial root finder
/// is involved; <see cref="FilterRoots.FromZeros"/> polishes each pole by Newton on the
/// characteristic equation evaluated as products.</para>
///
/// <para><b>Types b and c</b> are a monotone Möbius map in <c>x = Ω²</c>,
/// <c>x_old = (a x + b)/(c x + 1)</c>, with <c>g(1) = 1</c> (the passband edge stays) and
/// <c>g(∞) = x_p</c>, the largest transmission zero (now a double zero at infinity). Type b has
/// <c>b = 0</c>; type c sends the smallest reflection zero to DC as well, so the terminations come
/// out equal. Every zero and pole maps in closed form; the floor is preserved and the stop edge
/// moves to <c>√g⁻¹(Ω_s²)</c>.</para>
/// </summary>
internal static class EllipticPrototype
{
    /// <summary>The zeros, poles and scalars of the elliptic response.</summary>
    internal static FilterRoots Roots(int n, double rippleDb, double astopDb, EllipticType type)
    {
        double epsP = FilterPrototype.EpsFromDb(rippleDb, nameof(rippleDb));
        double epsS = FilterPrototype.EpsFromDb(astopDb, nameof(astopDb));

        if (!(epsS > epsP))
            throw new ArgumentOutOfRangeException(nameof(astopDb),
                $"An elliptic filter's stopband floor must be deeper than its passband ripple; got " +
                $"Astop = {astopDb:G6} dB against Ripple = {rippleDb:G6} dB. Below that the two " +
                "bands cross and there is no transition band to design.");

        type = EllipticTypes.Resolve(type, n);
        if (type is EllipticType.B or EllipticType.C && n < 2)
            type = EllipticType.A;

        double k1 = epsP / epsS;
        double k1p = Math.Sqrt((1.0 - k1) * (1.0 + k1));
        var (k, kp) = SelectivityModuli(n, k1, k1p);
        if (!(kp > 0.0) || !(k > 0.0))
            throw new ArgumentOutOfRangeException(nameof(n),
                $"An elliptic filter of order {n} at {rippleDb:G6} dB / {astopDb:G6} dB has a " +
                "transition band narrower than double precision can place a zero in. A lower " +
                "order or a deeper floor widens it.");

        double bigK = Kc(kp);
        int l = n / 2;
        var xr = new List<double>(l);
        var xz = new List<double>(l);
        for (int i = 1; i <= l; i++)
        {
            var (_, cn, dn) = Jacobi((2.0 * i - 1.0) / n * bigK, k, kp);
            double zeta = cn / dn;
            xr.Add(zeta * zeta);
            double z = 1.0 / (k * zeta);
            xz.Add(z * z);
        }

        // v0: sn(j t K1, k1) = j sc(t K1, k1') = j/ε_p, so t K1 = F(atan(1/ε_p) | k1').
        double bigK1 = Kc(k1p);
        double t = IncompleteF(Math.Atan(1.0 / epsP), k1p, k1) / bigK1;
        double v0 = t / n;

        var poles = new List<Complex>(n);
        for (int i = 1; i <= l; i++)
        {
            double u = (2.0 * i - 1.0) / n;
            var p = Complex.ImaginaryOne * Cd(u * bigK, -v0 * bigK, k, kp);
            if (p.Real > 0) p = -p;
            poles.Add(p);
            poles.Add(Complex.Conjugate(p));
        }
        if (n % 2 == 1)
        {
            var (s1, c1, _) = Jacobi(v0 * bigK, kp, k);
            poles.Add(new Complex(-Math.Abs(s1 / c1), 0.0));
        }

        double stop = 1.0 / k;
        int m0 = n % 2;
        if (type is EllipticType.B or EllipticType.C)
        {
            double xp = xz.Max();
            double b = type == EllipticType.C ? xr.Min() : 0.0;
            double c = (1.0 - b) / (xp - 1.0);
            double a = xp * c;
            double Inv(double x) => (x - b) / (a - c * x);
            Complex InvC(Complex x) => (x - b) / (a - c * x);

            int drop = xz.IndexOf(xp);
            xz.RemoveAt(drop);
            xz = [.. xz.Select(Inv)];
            if (type == EllipticType.C)
            {
                int low = xr.IndexOf(b);
                xr.RemoveAt(low);
                m0 = 2;
            }
            xr = [.. xr.Select(Inv)];
            poles = [.. poles.Select(p =>
            {
                var x = InvC(-(p * p));
                var s = Complex.Sqrt(-x);
                if (s.Real > 0) s = -s;
                return Math.Sign(s.Imaginary) == Math.Sign(p.Imaginary) ? s : Complex.Conjugate(s);
            })];
            stop = Math.Sqrt(Inv(stop * stop));
        }

        return FilterRoots.FromZeros(n, epsP, [.. xz], [.. xr], m0, poles, stop);
    }

    /// <summary>
    /// The selectivity modulus and its complement satisfying the degree equation, via whichever
    /// nome is smaller.
    /// </summary>
    internal static (double K, double Kp) SelectivityModuli(int n, double k1, double k1p)
    {
        double bigK1 = Kc(k1p), bigK1p = Kc(k1);
        double lnq  = -Math.PI * bigK1p / (n * bigK1);
        double lnqp = -Math.PI * n * bigK1 / bigK1p;
        if (lnq <= lnqp)
        {
            double k = ModulusFromNome(Math.Exp(lnq));
            return (k, Math.Sqrt((1.0 - k) * (1.0 + k)));
        }
        double kp = ModulusFromNome(Math.Exp(lnqp));
        return (Math.Sqrt((1.0 - kp) * (1.0 + kp)), kp);
    }

    /// <summary>
    /// <c>k = (θ₂(0,q)/θ₃(0,q))²</c>. Called only with <c>q ≤ e^−π</c>, where a handful of terms
    /// are past double precision; the loop stops on a term that no longer moves the sum.
    /// </summary>
    private static double ModulusFromNome(double q)
    {
        if (!(q > 0.0)) return 0.0;
        double num = 0.0, den = 0.0;
        for (int m = 1; m <= 64; m++)
        {
            double a = Math.Pow(q, m * (m + 1.0));
            double b = Math.Pow(q, (double)m * m);
            num += a;
            den += b;
            if (a < 1e-18 * (1.0 + num) && b < 1e-18 * (1.0 + den)) break;
        }
        double t = (1.0 + num) / (1.0 + 2.0 * den);
        return 4.0 * Math.Sqrt(q) * t * t;
    }

    /// <summary><c>K(k)</c> from the modulus — used by the order estimate.</summary>
    internal static double EllipK(double k) => Kc(Math.Sqrt(Math.Max(0.0, (1.0 - k) * (1.0 + k))));

    /// <summary>
    /// <c>K</c> from the COMPLEMENTARY modulus: <c>π/(2·AGM(1, k'))</c>. Quadratically
    /// convergent.
    /// </summary>
    internal static double Kc(double kp)
    {
        double a = 1.0, b = Math.Max(0.0, kp);
        for (int i = 0; i < 64 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (a + b);
    }

    /// <summary>
    /// <c>sn, cn, dn</c> at real <paramref name="u"/>, modulus <paramref name="k"/> with complement
    /// <paramref name="kp"/>, by the descending Landen (AGM) transformation (A&amp;S 16.4). It
    /// drives the modulus towards zero, so its accuracy improves at the selective end where an
    /// ascending series would fail. <c>dn = cos φ₀ / cos(φ₁ − φ₀)</c> avoids <c>√(1 − k² sn²)</c>,
    /// which cancels when both <c>k</c> and <c>sn</c> approach 1.
    /// </summary>
    internal static (double Sn, double Cn, double Dn) Jacobi(double u, double k, double kp)
    {
        const int Max = 48;
        Span<double> a = stackalloc double[Max + 1];
        Span<double> c = stackalloc double[Max + 1];
        Span<double> phi = stackalloc double[Max + 1];
        a[0] = 1.0;
        c[0] = k;
        double b = kp;
        int n = 0;
        while (n < Max && Math.Abs(c[n]) > 1e-17 * a[n])
        {
            a[n + 1] = 0.5 * (a[n] + b);
            c[n + 1] = 0.5 * (a[n] - b);
            b = Math.Sqrt(a[n] * b);
            n++;
        }
        phi[n] = Math.ScaleB(1.0, n) * a[n] * u;
        for (int i = n; i >= 1; i--)
            phi[i - 1] = 0.5 * (phi[i] + Math.Asin(Math.Clamp(c[i] / a[i] * Math.Sin(phi[i]), -1.0, 1.0)));

        double sn = Math.Sin(phi[0]), cn = Math.Cos(phi[0]);
        double dn = n == 0 ? 1.0 : cn / Math.Cos(phi[1] - phi[0]);
        return (sn, cn, dn);
    }

    /// <summary>
    /// <c>cd(x + jy, k) = cn/dn</c> by the addition formulas (A&amp;S 16.21), real parts with
    /// modulus <c>k</c> and imaginary parts with the complement.
    /// </summary>
    internal static Complex Cd(double x, double y, double k, double kp)
    {
        var (s, c, d) = Jacobi(x, k, kp);
        var (s1, c1, d1) = Jacobi(y, kp, k);
        var cn = new Complex(c * c1, -s * d * s1 * d1);
        var dn = new Complex(d * c1 * d1, -k * k * s * c * s1);
        return cn / dn;   // the shared denominator c1² + k² s² s1² cancels
    }

    /// <summary>
    /// The incomplete integral <c>F(φ | κ)</c> by Carlson's <c>R_F</c>, given the modulus AND its
    /// complement so <c>1 − κ² sin²φ = cos²φ + κ'² sin²φ</c> never cancels.
    /// </summary>
    internal static double IncompleteF(double phi, double kappa, double kappaP)
    {
        double s = Math.Sin(phi), c = Math.Cos(phi);
        _ = kappa;
        return s * CarlsonRf(c * c, c * c + kappaP * kappaP * s * s, 1.0);
    }

    /// <summary>Carlson's symmetric <c>R_F(x, y, z)</c> by the duplication theorem.</summary>
    private static double CarlsonRf(double x, double y, double z)
    {
        for (int i = 0; i < 64; i++)
        {
            double sx = Math.Sqrt(x), sy = Math.Sqrt(y), sz = Math.Sqrt(z);
            double lambda = sx * (sy + sz) + sy * sz;
            x = 0.25 * (x + lambda);
            y = 0.25 * (y + lambda);
            z = 0.25 * (z + lambda);
            double mean = (x + y + z) / 3.0;
            double dx = (mean - x) / mean, dy = (mean - y) / mean, dz = (mean - z) / mean;
            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) < 1e-4)
            {
                double e2 = dx * dy - dz * dz, e3 = dx * dy * dz;
                return (1.0 - e2 / 10.0 + e3 / 14.0 + e2 * e2 / 24.0 - 3.0 * e2 * e3 / 44.0)
                       / Math.Sqrt(mean);
            }
        }
        return double.NaN;
    }
}
