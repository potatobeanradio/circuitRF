// ================================================================
//  RfHelpers.cs  —  Pure RF math and formatting utilities
//
//  Extracted from splotRF/Models/Misc.cs.
//  No UI, no Avalonia, no SkiaSharp dependencies.
// ================================================================

using System;
using System.Numerics;

namespace RfCore
{
    public static class RfHelpers
    {
        /// <summary>Normalised impedance → reflection coefficient.</summary>
        public static Complex Z2G(Complex z) => (z - Complex.One) / (z + Complex.One);

        /// <summary>Reflection coefficient → normalised impedance.</summary>
        public static Complex G2Z(Complex g) => (Complex.One + g) / (1 - g);

        /// <summary>VSWR between two reflection coefficients.</summary>
        public static double VswrFromGamma(Complex g1, Complex g2)
        {
            var z1    = G2Z(g1);
            var z2    = G2Z(g2);
            var gamma = (z2 - z1) / (z2 + Complex.Conjugate(z1));
            return (1 + gamma.Magnitude) / (1 - gamma.Magnitude);
        }

        /// <summary>VSWR between two normalised impedances.</summary>
        public static double VswrFromZ(Complex z1, Complex z2)
        {
            var gamma = (z2 - z1) / (z2 + Complex.Conjugate(z1));
            return (1 + gamma.Magnitude) / (1 - gamma.Magnitude);
        }

        /// <summary>Formats a complex number as "a.bb ± j c.dd".</summary>
        public static string ComplexToString(Complex val)
        {
            string sign = val.Imaginary < 0 ? " - j" : " + j";
            return $"{val.Real:F2}{sign}{Math.Abs(val.Imaginary):F2}";
        }

        /// <summary>Rounds <paramref name="value"/> to the nearest multiple of <paramref name="nearest"/>.</summary>
        public static double RoundNearest(double value, double nearest) =>
            nearest == 0 ? value : Math.Round(value / nearest) * nearest;

        /// <summary>
        /// "Nice number" rounding for axis label intervals (Wilkinson variant).
        /// </summary>
        public static double Nicenum(double value, bool round)
        {
            if (value == 0) return 0;
            double mag = Math.Abs(value);
            double exp = Math.Floor(Math.Log10(mag));
            double f   = mag / Math.Pow(10, exp);
            double nf;
            if (round)
                nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
            else
                nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return value > 0 ? nf * Math.Pow(10, exp) : -nf * Math.Pow(10, exp);
        }

        /// <summary>Snaps a tick interval to a human-friendly value.</summary>
        public static double RoundTick(double value)
        {
            if (value > 0.5)  return RoundNearest(Math.Round(value), 1);
            if (value > 0.3)  return 0.5;
            if (value >= 0.2) return 0.25;
            if (value >= 0.05) return 0.1;
            if (value > 0.01)  return 0.05;
            return value;
        }
    }
}
