using System.Globalization;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// Double-double continued-fraction extraction for all-pole prototypes whose ordinary
/// <see cref="double"/> Cauer step loses the leading-ratio sign around order 8+. Used only as a
/// fallback after <see cref="MatchPrototypes.ExtractScaled"/> fails.
/// </summary>
internal static class FilterLadderHighPrecision
{
    /// <summary>
    /// Cauer extract of <paramref name="num"/>/<paramref name="den"/> in double-double arithmetic.
    /// Same contract as <see cref="MatchPrototypes.Extract"/>: null when a removal is non-positive.
    /// </summary>
    public static double[]? Extract(double[] num, double[] den, int n)
    {
        var a = DdPoly.From(num);
        var b = DdPoly.From(den);
        var g = new List<double>(n + 2) { 1.0 };
        for (int k = 0; k < n; k++)
        {
            DdPoly.Renorm(ref a, ref b);
            if (b.Length == 0 || a.Length != b.Length + 1) return null;
            var gk = a.Coef[0] / b.Coef[0];
            if (!(gk.Hi > 0) || double.IsNaN(gk.Hi) || double.IsInfinity(gk.Hi)) return null;
            // Same forced-degree rule as MatchPrototypes.ExtractCore: a − g·s·b drops two degrees.
            var rem = DdPoly.Sub(a, DdPoly.MulByS(b, gk));
            int expectLen = Math.Max(1, a.Length - 2);
            rem = DdPoly.ForceLength(rem, expectLen);
            a = b;
            b = rem;
            g.Add(gk.Hi);
        }
        DdPoly.Renorm(ref a, ref b);
        if (a.Length != 1 || b.Length != 1 || b.Coef[0].IsZero) return null;
        var last = a.Coef[0] / b.Coef[0];
        if (!(last.Hi > 0) || double.IsNaN(last.Hi) || double.IsInfinity(last.Hi)) return null;
        g.Add(last.Hi);
        return [.. g];
    }

    /// <summary>Unevaluated double-double: hi + lo with |lo| ≤½ ulp(hi) when normalised.</summary>
    private readonly struct Dd
    {
        public readonly double Hi, Lo;
        public Dd(double hi, double lo = 0) { Hi = hi; Lo = lo; }
        public bool IsZero => Hi == 0 && Lo == 0;
        public static Dd From(double x) => new(x);
        public static Dd operator +(Dd a, Dd b) => Add(a.Hi, a.Lo, b.Hi, b.Lo);
        public static Dd operator -(Dd a, Dd b) => Add(a.Hi, a.Lo, -b.Hi, -b.Lo);
        public static Dd operator *(Dd a, Dd b)
        {
            var p = Mul(a.Hi, b.Hi);
            double t = a.Hi * b.Lo + a.Lo * b.Hi + p.Lo;
            return Renorm(p.Hi, t);
        }
        public static Dd operator /(Dd a, Dd b)
        {
            double q1 = a.Hi / b.Hi;
            var p = Mul(q1, b.Hi);
            double dh = a.Hi - p.Hi;
            double dl = a.Lo - p.Lo - q1 * b.Lo;
            return Renorm(q1, (dh + dl) / b.Hi);
        }
        private static Dd Mul(double a, double b)
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

    private sealed class DdPoly
    {
        public Dd[] Coef; // descending
        public int Length => Coef.Length;
        private DdPoly(Dd[] c) { Coef = Trim(c); }
        public static DdPoly From(double[] a)
        {
            var c = new Dd[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = Dd.From(a[i]);
            return new DdPoly(c);
        }
        public static void Renorm(ref DdPoly a, ref DdPoly b)
        {
            double m = 0;
            foreach (var c in a.Coef) m = Math.Max(m, Math.Abs(c.Hi));
            foreach (var c in b.Coef) m = Math.Max(m, Math.Abs(c.Hi));
            if (!(m > 0) || Math.Abs(m - 1) < 1e-15) return;
            var inv = Dd.From(1.0 / m);
            for (int i = 0; i < a.Coef.Length; i++) a.Coef[i] *= inv;
            for (int i = 0; i < b.Coef.Length; i++) b.Coef[i] *= inv;
        }
        public static DdPoly Sub(DdPoly a, DdPoly b)
        {
            int n = Math.Max(a.Length, b.Length);
            var r = new Dd[n];
            for (int i = 0; i < n; i++)
            {
                var av = i < n - a.Length ? default : a.Coef[i - (n - a.Length)];
                var bv = i < n - b.Length ? default : b.Coef[i - (n - b.Length)];
                r[i] = av - bv;
            }
            return new DdPoly(r);
        }
        public static DdPoly MulByS(DdPoly b, Dd gk)
        {
            var r = new Dd[b.Length + 1];
            for (int i = 0; i < b.Length; i++) r[i] = b.Coef[i] * gk;
            return new DdPoly(r);
        }
        public static DdPoly ForceLength(DdPoly p, int len)
        {
            var c = Trim(p.Coef);
            if (c.Length <= len) return new DdPoly(c.Length == 0 ? [default] : c);
            return new DdPoly(c[(c.Length - len)..]);
        }

        private static Dd[] Trim(Dd[] p)
        {
            int i = 0;
            while (i < p.Length - 1 && Math.Abs(p[i].Hi) < 1e-30 && Math.Abs(p[i].Lo) < 1e-30) i++;
            return i == 0 ? p : p[i..];
        }
    }
}
