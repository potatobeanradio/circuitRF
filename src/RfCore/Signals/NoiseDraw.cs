// ================================================================
//  NoiseDraw.cs  —  seeded, counter-based Gaussian draws
//
//  A draw is a pure function of (seed, stream, counter): no state, so
//  a run split into parallel time segments draws, at any one sample,
//  exactly what one sequential run draws there, and two runs with the
//  same seed draw the same bits.  System.Random is neither.
//
//  The bits come from a 64-bit mixing function applied to the counter
//  and a per-stream key.  The Gaussian pair is two uniforms turned
//  into a radius sqrt(-2 ln u1) and an angle 2 pi u2, with its own
//  logarithm, sine and cosine (polynomials in +, -, *, / and the
//  IEEE-exact square root), so the draws do not depend on the
//  platform's maths library either.
// ================================================================

using System;
using System.Numerics;

namespace RfCore.Signals
{
    public static class NoiseDraw
    {
        /// <summary>A 64-bit finaliser: every input bit reaches every output bit.</summary>
        public static ulong Mix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        /// <summary>The key of one stream of draws under <paramref name="seed"/>.</summary>
        public static ulong Key(ulong seed, ulong stream) => Mix(Mix(seed) ^ Mix(stream ^ 0xD1B54A32D192ED03UL));

        /// <summary>A stable 64-bit hash of a name (FNV-1a over its UTF-16 code units), for a stream named after a part.</summary>
        public static ulong Hash(string s)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (char c in s) { h ^= c; h *= 0x100000001B3UL; }
            return h;
        }

        /// <summary>Uniform on (0, 1]: draw <paramref name="counter"/> of the stream <paramref name="key"/>.</summary>
        public static double Uniform(ulong key, long counter)
        {
            ulong b = Mix(key ^ Mix((ulong)counter));
            return ((b >> 11) + 1) * (1.0 / 9007199254740992.0);   // 2^-53
        }

        /// <summary>
        /// A complex Gaussian with E|z|² = 1 (real and imaginary parts independent, each of variance 1/2), from uniform
        /// draws 2·<paramref name="index"/> and 2·<paramref name="index"/> + 1, as a radius √(−ln u1) and an angle 2π·u2.
        /// </summary>
        public static Complex Complex(ulong key, long index)
        {
            double u1 = Uniform(key, 2 * index), u2 = Uniform(key, 2 * index + 1);
            double r = Math.Sqrt(-Ln(u1));                           // sqrt(-2 ln u1) / sqrt(2)
            var (c, s) = SinCosTurns(u2);
            return new Complex(r * c, r * s);
        }

        /// <summary>A real standard Gaussian (unit variance): the real part of <see cref="Complex"/> times √2.</summary>
        public static double Real(ulong key, long index) => Complex(key, index).Real * Math.Sqrt(2.0);

        private const double Ln2 = 0.693147180559945309417232121458;

        /// <summary>The natural logarithm of <paramref name="u"/> &gt; 0, from its exponent and a series on its mantissa.</summary>
        internal static double Ln(double u)
        {
            long bits = BitConverter.DoubleToInt64Bits(u);
            int e = (int)((bits >> 52) & 0x7FF) - 1023;
            double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);   // [1, 2)
            if (m > 1.4142135623730951) { m *= 0.5; e++; }
            double s = (m - 1) / (m + 1), s2 = s * s;                 // |s| <= 0.1716
            double sum = 1.0 / 25;
            for (int k = 11; k >= 0; k--) sum = 1.0 / (2 * k + 1) + s2 * sum;
            return e * Ln2 + 2 * s * sum;
        }

        /// <summary>cos and sin of 2π·<paramref name="turns"/>, <paramref name="turns"/> in [0, 1].</summary>
        internal static (double Cos, double Sin) SinCosTurns(double turns)
        {
            double t = turns * 4;
            int q = (int)Math.Floor(t);
            double a = (t - q) * (Math.PI / 2);                       // [0, π/2)
            double a2 = a * a;
            double sin = 0, cos = 0;
            // Taylor to a^21 and a^20: below 1e-18 on [0, π/2).
            double ts = a, tc = 1;
            for (int k = 1; k <= 11; k++)
            {
                sin += ts; cos += tc;
                ts *= -a2 / ((2 * k) * (2 * k + 1));
                tc *= -a2 / ((2 * k - 1) * (2 * k));
            }
            return (q & 3) switch
            {
                0 => (cos, sin),
                1 => (-sin, cos),
                2 => (-cos, -sin),
                _ => (sin, -cos),
            };
        }
    }
}
