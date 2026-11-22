using System.Numerics;

namespace CircuitRF.Core.Systems;

/// <summary>
/// Which elliptic response. Type a is the classical Cauer response. Types b and c exist for EVEN
/// orders, where type a keeps a finite floor at infinite frequency that no LC ladder can reach:
/// b moves the highest transmission zero to infinity (unequal terminations), c also moves the
/// lowest reflection zero to DC (equal terminations).
/// </summary>
public enum EllipticType
{
    /// <summary>Type a at odd order, type c at even order.</summary>
    Auto,

    /// <summary>The classical response. At even order it has no LC ladder.</summary>
    A,

    /// <summary>Even order: the highest transmission zero sent to infinity. Unequal terminations.</summary>
    B,

    /// <summary>Even order: as b, and the lowest reflection zero sent to DC. Equal terminations.</summary>
    C,
}

/// <summary>Resolving <see cref="EllipticType.Auto"/> and the b/c-at-odd-order rule.</summary>
public static class EllipticTypes
{
    /// <summary>
    /// The type actually built at <paramref name="order"/>: Auto is a at odd order and c at even
    /// order; b and c only modify an even order, so at odd order they are a.
    /// </summary>
    public static EllipticType Resolve(EllipticType type, int order) => type switch
    {
        EllipticType.Auto => order % 2 == 1 ? EllipticType.A : EllipticType.C,
        EllipticType.B or EllipticType.C when order % 2 == 1 => EllipticType.A,
        _ => type,
    };

    /// <summary>The lower-case letter the parameter and the verb spell it with.</summary>
    public static string Letter(EllipticType type) => type switch
    {
        EllipticType.A => "a",
        EllipticType.B => "b",
        EllipticType.C => "c",
        _ => "auto",
    };

    /// <summary>Parses <c>auto</c>, <c>a</c>, <c>b</c>, <c>c</c> (any case).</summary>
    public static bool TryParse(string? text, out EllipticType type)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "auto": type = EllipticType.Auto; return true;
            case "a":    type = EllipticType.A;    return true;
            case "b":    type = EllipticType.B;    return true;
            case "c":    type = EllipticType.C;    return true;
            default:     type = EllipticType.Auto; return false;
        }
    }
}

/// <summary>
/// A response with zeros on the jΩ axis held as ROOTS rather than as expanded polynomials:
/// transmission zeros <c>±jΩ_z</c>, reflection zeros <c>±jΩ_r</c> (plus <see cref="M0"/> at the
/// origin) and the left-half-plane poles of <c>S21</c>.
/// </summary>
/// <remarks>
/// <para><b>Why roots.</b> The expanded coefficients of an order-11+ elliptic span many decades,
/// and every step that divides them needs a tolerance to accept the result. A product of
/// first- and second-order factors evaluates to full relative precision at any point, so both the
/// ideal response and the ladder synthesis that reads it (<see cref="FilterLadder"/>) work from
/// these products and never from coefficients.</para>
///
/// <para>With <c>E(s) = Π(s − p_i)</c> monic, <c>F̂(s) = s^M0 Π(s² + x_r)</c> and
/// <c>P̂(s) = Π(s² + x_z)</c>: <c>S11 = K_F F̂/E</c>, <c>S21 = K_P P̂/E</c>. The two scalars are
/// fixed at the passband edge, where the characteristic function is ±1 for every family held this
/// way, so <c>|S21(j)|² = 1/(1 + ε²)</c>.</para>
/// </remarks>
internal sealed class FilterRoots
{
    private FilterRoots(int order, double eps, double[] xz, double[] xr, int m0, DdComplex[] poles,
                        double stopEdge)
    {
        Order = order;
        Eps = eps;
        Xz = xz;
        Xr = xr;
        M0 = m0;
        PolesDd = poles;
        Poles = [.. poles.Select(p => p.ToComplex())];
        StopEdge = stopEdge;

        // The scalars from |S21(j)|² = 1/(1 + ε²) and |S11(j)|² = ε²/(1 + ε²), in double-double.
        var j = new DdComplex(0.0, 1.0);
        Dd e = Abs(E(j));
        Dd scale = Dd.Sqrt(1.0 + (Dd)eps * eps);
        KPd = e / (scale * Abs(PHat(j)));
        KFd = e * eps / (scale * Abs(FHat(j)));
        KP = KPd.Hi + KPd.Lo;
        KF = KFd.Hi + KFd.Lo;
    }

    private static Dd Abs(DdComplex z) => Dd.Sqrt(z.Re * z.Re + z.Im * z.Im);

    /// <summary>Prototype order — the number of poles.</summary>
    public int Order { get; }

    /// <summary>The ripple factor of the characteristic function.</summary>
    public double Eps { get; }

    /// <summary>Finite transmission zeros, <c>Ω_z²</c>, ascending.</summary>
    public double[] Xz { get; }

    /// <summary>Non-zero reflection zeros, <c>Ω_r²</c>.</summary>
    public double[] Xr { get; }

    /// <summary>The order of the reflection zero at the origin.</summary>
    public int M0 { get; }

    /// <summary>The left-half-plane poles.</summary>
    public Complex[] Poles { get; }

    /// <summary>The same poles to double-double precision — what the synthesis reads.</summary>
    public DdComplex[] PolesDd { get; }

    /// <summary><see cref="KF"/> to double-double precision.</summary>
    public Dd KFd { get; }

    /// <summary><see cref="KP"/> to double-double precision.</summary>
    public Dd KPd { get; }

    /// <summary>The prototype stopband edge (where the floor is first reached), or NaN.</summary>
    public double StopEdge { get; }

    /// <summary>The scalar on <c>F̂/E</c>.</summary>
    public double KF { get; }

    /// <summary>The scalar on <c>P̂/E</c>.</summary>
    public double KP { get; }

    /// <summary>Transmission zeros at infinity: the order less two per finite pair.</summary>
    public int ZerosAtInfinity => Order - 2 * Xz.Length;

    /// <summary><c>Π(s − p_i)</c>.</summary>
    public Complex E(Complex s)
    {
        Complex v = Complex.One;
        foreach (var p in Poles) v *= s - p;
        return v;
    }

    /// <summary><c>s^M0 Π(s² + x_r)</c>.</summary>
    public Complex FHat(Complex s)
    {
        Complex v = Complex.One;
        for (int i = 0; i < M0; i++) v *= s;
        var s2 = s * s;
        foreach (double x in Xr) v *= s2 + x;
        return v;
    }

    /// <summary><c>Π(s² + x_z)</c>.</summary>
    public Complex PHat(Complex s)
    {
        Complex v = Complex.One;
        var s2 = s * s;
        foreach (double x in Xz) v *= s2 + x;
        return v;
    }

    /// <summary><c>E</c> in double-double.</summary>
    public DdComplex E(DdComplex s)
    {
        var v = DdComplex.One;
        foreach (var p in PolesDd) v *= s - p;
        return v;
    }

    /// <summary><c>F̂</c> in double-double.</summary>
    public DdComplex FHat(DdComplex s)
    {
        var v = DdComplex.One;
        for (int i = 0; i < M0; i++) v *= s;
        var s2 = s * s;
        foreach (double x in Xr) v *= s2 + x;
        return v;
    }

    /// <summary><c>P̂</c> in double-double.</summary>
    public DdComplex PHat(DdComplex s)
    {
        var v = DdComplex.One;
        var s2 = s * s;
        foreach (double x in Xz) v *= s2 + x;
        return v;
    }

    /// <summary><c>S21</c> in double-double.</summary>
    public DdComplex S21(DdComplex s) => KPd * PHat(s) / E(s);

    /// <summary><c>S11</c> and its derivative in double-double. <paramref name="derivative"/> false
    /// skips the log-derivative, which is singular at the origin when <see cref="M0"/> &gt; 0.</summary>
    public (DdComplex Rho, DdComplex DRho) Reflection(DdComplex s, bool derivative)
    {
        var rho = KFd * FHat(s) / E(s);
        if (!derivative) return (rho, DdComplex.Zero);
        var dlogE = DdComplex.Zero;
        foreach (var p in PolesDd) dlogE += DdComplex.One / (s - p);
        var dlogF = DdComplex.Zero;
        if (M0 > 0) dlogF += new DdComplex(M0, 0.0) / s;
        var s2 = s * s;
        foreach (double x in Xr) dlogF += s * 2.0 / (s2 + x);
        return (rho, rho * (dlogF - dlogE));
    }

    /// <summary><c>S11 = K_F F̂/E</c> and its derivative in <c>s</c>.</summary>
    public (Complex Rho, Complex DRho) Reflection(Complex s)
    {
        Complex e = E(s), f = FHat(s);
        Complex rho = KF * f / e;
        // d(F̂/E)/ds = (F̂/E)·(F̂'/F̂ − E'/E), both log-derivatives as sums.
        Complex dlogE = Complex.Zero;
        foreach (var p in Poles) dlogE += 1.0 / (s - p);
        Complex dlogF = Complex.Zero;
        if (M0 > 0) dlogF += M0 / s;
        foreach (double x in Xr) dlogF += 2.0 * s / (s * s + x);
        return (rho, rho * (dlogF - dlogE));
    }

    /// <summary><c>(S11, S21, S22)</c> at a real prototype frequency.</summary>
    public (Complex S11, Complex S21, Complex S22) At(double omega)
    {
        var s = new Complex(0.0, omega);
        Complex e = E(s);
        return (KF * FHat(s) / e, KP * PHat(s) / e, -KF * FHat(-s) / e);
    }

    /// <summary>
    /// The expanded polynomials, descending: <c>E</c> monic, <c>F̂</c> monic, <c>P̂</c> monic.
    /// For the callers that still read coefficients (the limits at infinity, the tests); nothing
    /// numerical divides them.
    /// </summary>
    public (double[] E, double[] F, double[] P) Expand()
    {
        Complex[] e = [Complex.One];
        foreach (var p in Poles) e = MulLinear(e, p);
        double[] er = [.. e.Select(c => c.Real)];

        double[] f = [1.0];
        for (int i = 0; i < M0; i++) f = [.. f, 0.0];
        foreach (double x in Xr) f = MulQuad(f, x);

        double[] pp = [1.0];
        foreach (double x in Xz) pp = MulQuad(pp, x);
        return (er, f, pp);
    }

    private static Complex[] MulLinear(Complex[] a, Complex root)
    {
        var r = new Complex[a.Length + 1];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] += a[i];
            r[i + 1] -= a[i] * root;
        }
        return r;
    }

    private static double[] MulQuad(double[] a, double x)
    {
        var r = new double[a.Length + 2];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] += a[i];
            r[i + 2] += a[i] * x;
        }
        return r;
    }

    // ── construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds from the zeros and a first guess at the poles, which is then polished by Newton on
    /// <c>D(x)² + ε²c² x^r N(x)² = 0</c> in <c>x = Ω² = −s²</c>, evaluated as products.
    /// </summary>
    internal static FilterRoots FromZeros(int order, double eps, double[] xz, double[] xr, int m0,
                                          IReadOnlyList<Complex> guess, double stopEdge)
    {
        if (guess.Count != order)
            throw new InvalidOperationException(
                $"The response of order {order} produced {guess.Count} poles.");
        xz = [.. xz.OrderBy(x => x)];

        // c pins the characteristic function to ±1 at Ω = 1.
        double c = 1.0;
        foreach (double x in xz) c *= x - 1.0;
        foreach (double x in xr) c /= 1.0 - x;
        double ec2 = eps * eps * c * c;

        Dd cd = 1.0;
        foreach (double x in xz) cd *= (Dd)x - 1.0;
        foreach (double x in xr) cd /= 1.0 - (Dd)x;
        Dd ec2d = (Dd)eps * eps * cd * cd;

        var poles = new DdComplex[order];
        for (int i = 0; i < order; i++)
        {
            var s = guess[i];
            var x = -(s * s);
            for (int it = 0; it < 6; it++)
            {
                var (h, dh) = Characteristic(x, xz, xr, m0, ec2);
                if (dh == Complex.Zero) break;
                var step = h / dh;
                if (!double.IsFinite(step.Real) || !double.IsFinite(step.Imaginary)) break;
                if (step.Magnitude > 1e-3 * (1.0 + x.Magnitude)) break;   // a guess this far off is not polished
                x -= step;
                if (step.Magnitude <= 1e-17 * (1.0 + x.Magnitude)) break;
            }
            // Then the same Newton in double-double, which is what the synthesis needs.
            DdComplex xd = x;
            for (int it = 0; it < 3; it++)
            {
                var (h, dh) = CharacteristicDd(xd, xz, xr, m0, ec2d);
                var step = h / dh;
                if (!step.IsFinite || step.Magnitude > 1e-6 * (1.0 + xd.Magnitude)) break;
                xd -= step;
            }
            var root = DdComplex.Sqrt(-xd);
            if (root.Re.Hi > 0) root = -root;
            // Keep the conjugate symmetry the guess had: a pole on the real axis stays real.
            if (Math.Abs(guess[i].Imaginary) == 0.0) root = new DdComplex(root.Re, 0.0);
            else if (Math.Sign(root.Im.Hi) != Math.Sign(guess[i].Imaginary)) root = new DdComplex(root.Re, -root.Im);
            poles[i] = root;
        }
        return new FilterRoots(order, eps, xz, xr, m0, poles, stopEdge);
    }

    /// <summary><c>h(x) = D² + ε²c² x^r N²</c> and <c>dh/dx</c>, from products.</summary>
    private static (Complex H, Complex DH) Characteristic(
        Complex x, double[] xz, double[] xr, int m0, double ec2)
    {
        Complex d = Complex.One, dd = Complex.Zero;
        foreach (double z in xz) { dd = dd * (x - z) + d; d *= x - z; }
        Complex n = Complex.One, dn = Complex.Zero;
        foreach (double r in xr) { dn = dn * (x - r) + n; n *= x - r; }
        // |F̂(jΩ)|² carries Ω^(2·m0) = x^m0.
        Complex xr0 = Complex.Pow(x, m0);
        Complex dxr0 = m0 == 0 ? Complex.Zero : m0 * Complex.Pow(x, m0 - 1);
        Complex h = d * d + ec2 * xr0 * n * n;
        Complex dh = 2.0 * d * dd + ec2 * (dxr0 * n * n + 2.0 * xr0 * n * dn);
        return (h, dh);
    }

    private static (DdComplex H, DdComplex DH) CharacteristicDd(
        DdComplex x, double[] xz, double[] xr, int m0, Dd ec2)
    {
        DdComplex d = DdComplex.One, dd = DdComplex.Zero;
        foreach (double z in xz) { var f = x - z; dd = dd * f + d; d *= f; }
        DdComplex n = DdComplex.One, dn = DdComplex.Zero;
        foreach (double r in xr) { var f = x - r; dn = dn * f + n; n *= f; }
        DdComplex xm = DdComplex.One, dxm = DdComplex.Zero;
        for (int i = 0; i < m0; i++) { dxm = dxm * x + xm; xm *= x; }
        var h = d * d + ec2 * xm * n * n;
        var dh = d * dd * 2.0 + ec2 * (dxm * n * n + xm * n * dn * 2.0);
        return (h, dh);
    }

    /// <summary>
    /// The inverse-Chebyshev response with the stopband floor at <c>Ω = 1</c>:
    /// <c>|S21|² = T_n²(1/Ω)/(T_n²(1/Ω) + ε²)</c>, ε from the floor.
    /// </summary>
    internal static FilterRoots InverseChebyshev(int n, double eps)
    {
        var xz = new List<double>();
        for (int k = 1; k <= n / 2; k++)
        {
            double cz = Math.Cos((2.0 * k - 1.0) * Math.PI / (2.0 * n));
            xz.Add(1.0 / (cz * cz));
        }
        // Poles: T_n(1/Ω) = ±jε is a Chebyshev-I equation in w = 1/Ω with ripple factor 1/ε, so
        // s = 1/s' with s' a Chebyshev-I pole of a = asinh(ε)/n.
        double a = Math.Asinh(eps) / n;
        var guess = new List<Complex>();
        for (int k = 1; k <= n; k++)
        {
            double th = (2.0 * k - 1.0) * Math.PI / (2.0 * n);
            var sp = new Complex(-Math.Sinh(a) * Math.Sin(th), Math.Cosh(a) * Math.Cos(th));
            if (Math.Abs(sp.Imaginary) < 1e-14 * sp.Magnitude) sp = new Complex(sp.Real, 0.0);
            guess.Add(1.0 / sp);
        }
        return FromZeros(n, eps, [.. xz], [], n, guess, 1.0);
    }
}
