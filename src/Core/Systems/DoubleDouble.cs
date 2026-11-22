using System.Globalization;
using System.Numerics;

namespace CircuitRF.Core.Systems;

/// <summary>
/// Unevaluated double-double: <c>Hi + Lo</c> with <c>|Lo| ≤ ½ ulp(Hi)</c> once normalised — about
/// 32 significant digits from two doubles and the error-free transformations (Dekker / Knuth
/// two-sum, fused multiply-add two-product). Used where a filter synthesis subtracts nearly equal
/// numbers by construction.
/// </summary>
internal readonly struct Dd
{
    public readonly double Hi, Lo;

    public Dd(double hi, double lo = 0) { Hi = hi; Lo = lo; }

    public bool IsZero => Hi == 0 && Lo == 0;

    public static Dd From(double x) => new(x);

    public static implicit operator Dd(double x) => new(x);

    public static Dd operator -(Dd a) => new(-a.Hi, -a.Lo);

    public static Dd operator +(Dd a, Dd b) => Add(a.Hi, a.Lo, b.Hi, b.Lo);

    public static Dd operator -(Dd a, Dd b) => Add(a.Hi, a.Lo, -b.Hi, -b.Lo);

    public static Dd operator *(Dd a, Dd b)
    {
        var p = TwoProd(a.Hi, b.Hi);
        double t = a.Hi * b.Lo + a.Lo * b.Hi + p.Lo;
        return Renorm(p.Hi, t);
    }

    public static Dd operator /(Dd a, Dd b)
    {
        double q1 = a.Hi / b.Hi;
        var p = TwoProd(q1, b.Hi);
        double dh = a.Hi - p.Hi;
        double dl = a.Lo - p.Lo - q1 * b.Lo;
        var q = Renorm(q1, (dh + dl) / b.Hi);
        // One more correction step: the first quotient is good to about 1e-30, this to the last bit.
        var r = a - q * b;
        return q + new Dd(r.Hi / b.Hi);
    }

    /// <summary>The square root, by one Newton step from the double root.</summary>
    public static Dd Sqrt(Dd a)
    {
        if (a.Hi <= 0) return new Dd(0);
        double x = Math.Sqrt(a.Hi);
        var xx = TwoProd(x, x);
        var r = a - xx;
        return Renorm(x, r.Hi / (2.0 * x));
    }

    public static Dd Abs(Dd a) => a.Hi < 0 ? -a : a;

    private static Dd TwoProd(double a, double b)
    {
        double p = a * b;
        return new Dd(p, Math.FusedMultiplyAdd(a, b, -p));
    }

    private static Dd Add(double ah, double al, double bh, double bl)
    {
        double s = ah + bh;
        double v = s - ah;
        double t = (bh - v) + (ah - (s - v)) + al + bl;
        return Renorm(s, t);
    }

    private static Dd Renorm(double hi, double lo)
    {
        double s = hi + lo;
        return new Dd(s, lo - (s - hi));
    }

    public override string ToString() => Hi.ToString("G17", CultureInfo.InvariantCulture);
}

/// <summary>A complex number of two <see cref="Dd"/>s.</summary>
internal readonly struct DdComplex
{
    public readonly Dd Re, Im;

    public DdComplex(Dd re, Dd im) { Re = re; Im = im; }

    public static readonly DdComplex One = new(1.0, 0.0);
    public static readonly DdComplex Zero = new(0.0, 0.0);

    public static implicit operator DdComplex(double x) => new(x, 0.0);

    public static implicit operator DdComplex(Complex z) => new(z.Real, z.Imaginary);

    public Complex ToComplex() => new(Re.Hi + Re.Lo, Im.Hi + Im.Lo);

    public double Magnitude => ToComplex().Magnitude;

    public static DdComplex operator -(DdComplex a) => new(-a.Re, -a.Im);
    public static DdComplex operator +(DdComplex a, DdComplex b) => new(a.Re + b.Re, a.Im + b.Im);
    public static DdComplex operator -(DdComplex a, DdComplex b) => new(a.Re - b.Re, a.Im - b.Im);
    public static DdComplex operator *(DdComplex a, DdComplex b) =>
        new(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
    public static DdComplex operator *(DdComplex a, Dd k) => new(a.Re * k, a.Im * k);
    public static DdComplex operator *(Dd k, DdComplex a) => new(a.Re * k, a.Im * k);
    public static DdComplex operator *(DdComplex a, double k) => new(a.Re * k, a.Im * k);
    public static DdComplex operator *(double k, DdComplex a) => new(a.Re * k, a.Im * k);

    public static DdComplex operator /(DdComplex a, DdComplex b)
    {
        // Scaled by the larger part of b so the squared modulus cannot overflow; one reciprocal,
        // two products.
        double m = Math.Max(Math.Abs(b.Re.Hi), Math.Abs(b.Im.Hi));
        if (m == 0) return new DdComplex(double.NaN, double.NaN);
        Dd inv = 1.0 / m;
        var br = b.Re * inv;
        var bi = b.Im * inv;
        var k = inv / (br * br + bi * bi);
        return new DdComplex((a.Re * br + a.Im * bi) * k, (a.Im * br - a.Re * bi) * k);
    }

    /// <summary>The principal square root.</summary>
    public static DdComplex Sqrt(DdComplex z)
    {
        var mag = Dd.Sqrt(z.Re * z.Re + z.Im * z.Im);
        if (mag.IsZero) return Zero;
        if (z.Re.Hi >= 0)
        {
            var t = Dd.Sqrt((mag + z.Re) * 0.5);
            return new DdComplex(t, z.Im / (t * 2.0));
        }
        var u = Dd.Sqrt((mag - z.Re) * 0.5);
        if (z.Im.Hi < 0) u = -u;
        return new DdComplex(z.Im / (u * 2.0), u);
    }

    public bool IsFinite =>
        double.IsFinite(Re.Hi) && double.IsFinite(Im.Hi) && double.IsFinite(Re.Lo) && double.IsFinite(Im.Lo);
}
