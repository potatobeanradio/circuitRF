// ================================================================
//  Diffraction.cs  —  the parts ITU-R P.526 and P.1812 share
//
//  Fresnel     the complex Fresnel integral Fc(ν) = C(ν) + jS(ν) by the
//              twelve-term coefficient expansions P.526-16 §2.7 gives
//              (eqs (8a), (8b)), and the exact knife-edge loss of eq (30).
//  J(ν)        the knife-edge approximation of P.526 eq (31) / P.1812
//              eq (12): 6.9 + 20·log10(√((ν − 0.1)² + 1) + ν − 0.1),
//              0 for ν ≤ −0.78.
//  Bullington  P.526 §4.5.1 / P.1812 §4.3.1: slopes in m/km relative to
//              the sea-level baseline, the Bullington point, its knife-edge
//              loss Luc and the path's Lb = Luc + (1 − e^(−Luc/6))·(10 + 0.02d).
//              Distances km, heights m above sea level, ap km, λ m.
//  SmoothSurface
//              P.526 eqs (58)-(63) / P.1812 eqs (83)-(89): the least-squares
//              line fitted to the profile, lowered by the highest obstruction
//              above the terminal line shared in proportion to the horizon
//              angles, never above the terminal ground.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Propagation
{
    public static class Diffraction
    {
        private static readonly double[] A =
        {
            +1.595769140, -0.000001702, -6.808568854, -0.000576361, +6.920691902, -0.016898657,
            -3.050485660, -0.075752419, +0.850663781, -0.025639041, -0.150230960, +0.034404779,
        };
        private static readonly double[] B =
        {
            -0.000000033, +4.255387524, -0.000092810, -7.780020400, -0.009520895, +5.075161298,
            -0.138341947, -1.363729124, -0.403349276, +0.702222016, -0.216195929, +0.019547031,
        };
        private static readonly double[] Cc =
        {
            +0.000000000, -0.024933975, +0.000003936, +0.005770956, +0.000689892, -0.009497136,
            +0.011948809, -0.006748873, +0.000246420, +0.002102967, -0.001217930, +0.000233939,
        };
        private static readonly double[] D =
        {
            +0.199471140, +0.000000023, -0.009351341, +0.000023006, +0.004851466, +0.001903218,
            -0.017122914, +0.029064067, -0.027928955, +0.016497308, -0.005598515, +0.000838386,
        };

        /// <summary>The complex Fresnel integral Fc(ν) = ∫₀^ν exp(jπs²/2) ds = C(ν) + jS(ν).</summary>
        public static Complex Fresnel(double nu)
        {
            if (nu < 0) return -Fresnel(-nu);
            double x = 0.5 * Math.PI * nu * nu;
            Complex sum = Complex.Zero, rot = Complex.FromPolarCoordinates(1, x);
            if (x < 4)
            {
                double t = x / 4, p = 1;
                for (int n = 0; n < 12; n++) { sum += new Complex(A[n], -B[n]) * p; p *= t; }
                return rot * Math.Sqrt(x / 4) * sum;
            }
            else
            {
                double t = 4 / x, p = 1;
                for (int n = 0; n < 12; n++) { sum += new Complex(Cc[n], -D[n]) * p; p *= t; }
                return new Complex(0.5, 0.5) + rot * Math.Sqrt(4 / x) * sum;
            }
        }

        /// <summary>The knife-edge loss J(ν) of P.526 eq (30), from the Fresnel integrals, dB.</summary>
        public static double KnifeEdgeExact(double nu)
        {
            Complex f = Fresnel(nu);
            double c = f.Real, s = f.Imaginary;
            return -20 * Math.Log10(Math.Sqrt((1 - c - s) * (1 - c - s) + (c - s) * (c - s)) / 2);
        }

        /// <summary>The knife-edge approximation J(ν) of P.526 eq (31) / P.1812 eq (12); 0 for ν ≤ −0.78.</summary>
        public static double J(double nu)
            => nu <= -0.78 ? 0 : 6.9 + 20 * Math.Log10(Math.Sqrt((nu - 0.1) * (nu - 0.1) + 1) + nu - 0.1);

        /// <summary>The Bullington diffraction loss Lb (dB) over the profile heights <paramref name="g"/> between antennas
        /// at <paramref name="hts"/>, <paramref name="hrs"/> m above sea level.</summary>
        public static double Bullington(IReadOnlyList<double> dKm, IReadOnlyList<double> g, double hts, double hrs, double apKm, double lambdaM)
        {
            int n = dKm.Count;
            double d = dKm[n - 1], ce = 1 / apKm;
            double stim = double.NegativeInfinity;
            for (int i = 1; i < n - 1; i++)
                stim = Math.Max(stim, (g[i] + 500 * ce * dKm[i] * (d - dKm[i]) - hts) / dKm[i]);
            double str = (hrs - hts) / d;
            double luc;
            if (stim < str)
            {
                double numax = double.NegativeInfinity;
                for (int i = 1; i < n - 1; i++)
                {
                    double di = dKm[i];
                    double nu = (g[i] + 500 * ce * di * (d - di) - (hts * (d - di) + hrs * di) / d)
                                * Math.Sqrt(0.002 * d / (lambdaM * di * (d - di)));
                    numax = Math.Max(numax, nu);
                }
                luc = J(numax);
            }
            else
            {
                double srim = double.NegativeInfinity;
                for (int i = 1; i < n - 1; i++)
                    srim = Math.Max(srim, (g[i] + 500 * ce * dKm[i] * (d - dKm[i]) - hrs) / (d - dKm[i]));
                double dbp = (hrs - hts + srim * d) / (stim + srim);
                double nub = (hts + stim * dbp - (hts * (d - dbp) + hrs * dbp) / d)
                             * Math.Sqrt(0.002 * d / (lambdaM * dbp * (d - dbp)));
                luc = J(nub);
            }
            return luc + (1 - Math.Exp(-luc / 6)) * (10 + 0.02 * d);
        }

        /// <summary>The smooth surface's heights at the terminals: the least-squares values (hst, hsr) and those the
        /// diffraction model uses (hstd, hsrd).</summary>
        public readonly record struct SmoothHeights(double Hst, double Hsr, double Hstd, double Hsrd);

        public static SmoothHeights SmoothSurface(IReadOnlyList<double> dKm, IReadOnlyList<double> h, double hts, double hrs)
        {
            int n = dKm.Count;
            double d = dKm[n - 1], v1 = 0, v2 = 0;
            for (int i = 1; i < n; i++)
            {
                double dd = dKm[i] - dKm[i - 1];
                v1 += dd * (h[i] + h[i - 1]);
                v2 += dd * (h[i] * (2 * dKm[i] + dKm[i - 1]) + h[i - 1] * (dKm[i] + 2 * dKm[i - 1]));
            }
            double hst = (2 * v1 * d - v2) / (d * d), hsr = (v2 - v1 * d) / (d * d);

            double hobs = double.NegativeInfinity, aobt = double.NegativeInfinity, aobr = double.NegativeInfinity;
            for (int i = 1; i < n - 1; i++)
            {
                double hi = h[i] - (hts * (d - dKm[i]) + hrs * dKm[i]) / d;
                hobs = Math.Max(hobs, hi);
                aobt = Math.Max(aobt, hi / dKm[i]);
                aobr = Math.Max(aobr, hi / (d - dKm[i]));
            }
            double hstp = hst, hsrp = hsr;
            if (hobs > 0)
            {
                double gt = aobt / (aobt + aobr), gr = aobr / (aobt + aobr);
                hstp = hst - hobs * gt;
                hsrp = hsr - hobs * gr;
            }
            return new SmoothHeights(hst, hsr, Math.Min(hstp, h[0]), Math.Min(hsrp, h[n - 1]));
        }
    }
}
