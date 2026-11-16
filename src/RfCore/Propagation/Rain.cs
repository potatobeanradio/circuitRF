// ================================================================
//  Rain.cs  —  ITU-R P.838-3 specific attenuation, ITU-R P.530-19
//  terrestrial rain attenuation, ITU-R P.840-9 cloud attenuation
//
//  P.838   γR = k·R^α (eq (1)), with log10 k and α the fitted sums of
//          eqs (2) and (3) (Tables 1-4) for horizontal and vertical
//          polarization, combined for a path elevation θ and a
//          polarization tilt τ by eqs (4) and (5) (τ = 45° circular).
//          1 to 1000 GHz.
//  P.530   §2.4.1: the effective path length factor r (eq (32), with f
//          held at no less than 6 GHz, d at no more than 30 km and R0.01
//          at no less than 3 mm/h inside it), A0.01 = γR·d·r (eq (33)),
//          and Ap for 0.001 % ≤ p ≤ 1 % (eqs (34)-(36)). Up to 175 GHz and
//          60 km.
//  P.840   §2, §3.1: the cloud liquid water specific attenuation
//          coefficient from the double-Debye permittivity of water
//          (eqs (2)-(10)), the mass absorption coefficient KL(f)
//          (eq (12), Kl at 273.75 K), and AC = KL·L/sin θ (eq (11)) for
//          an integrated cloud liquid water content L (kg/m²), 1 to
//          200 GHz, 5° ≤ θ ≤ 90°.
// ================================================================

using System;

namespace RfCore.Propagation
{
    public static class ItuP838
    {
        public const string Document = "ITU-R P.838-3 (03/2005)";

        private static readonly double[,] KH = { { -5.33980, -0.10008, 1.13098 }, { -0.35351, 1.26970, 0.45400 }, { -0.23789, 0.86036, 0.15354 }, { -0.94158, 0.64552, 0.16817 } };
        private static readonly double[,] KV = { { -3.80595, 0.56934, 0.81061 }, { -3.44965, -0.22911, 0.51059 }, { -0.39902, 0.73042, 0.11899 }, { 0.50167, 1.07319, 0.27195 } };
        private static readonly double[,] AH = { { -0.14318, 1.82442, -0.55187 }, { 0.29591, 0.77564, 0.19822 }, { 0.32177, 0.63773, 0.13164 }, { -5.37610, -0.96230, 1.47828 }, { 16.1721, -3.29980, 3.43990 } };
        private static readonly double[,] AV = { { -0.07771, 2.33840, -0.76284 }, { 0.56727, 0.95545, 0.54039 }, { -0.20238, 1.14520, 0.26809 }, { -48.2991, 0.791669, 0.116226 }, { 48.5833, 0.791459, 0.116479 } };

        private static double Sum(double[,] t, double lf, double m, double c)
        {
            double s = 0;
            for (int j = 0; j < t.GetLength(0); j++) s += t[j, 0] * Math.Exp(-Math.Pow((lf - t[j, 1]) / t[j, 2], 2));
            return s + m * lf + c;
        }

        /// <summary>kH, kV, αH, αV at f (GHz).</summary>
        public static (double Kh, double Kv, double Ah, double Av) Coefficients(double fGhz)
        {
            PropagationMath.Range("P838", "the frequency", fGhz, 1, 1000, "GHz");
            double lf = Math.Log10(fGhz);
            return (Math.Pow(10, Sum(KH, lf, -0.18961, 0.71147)), Math.Pow(10, Sum(KV, lf, -0.16398, 0.63297)),
                    Sum(AH, lf, 0.67849, -1.95537), Sum(AV, lf, -0.053739, 0.83433));
        }

        /// <summary>k and α for elevation θ and polarization tilt τ (degrees; 0 horizontal, 90 vertical, 45 circular).</summary>
        public static (double K, double Alpha) Coefficients(double fGhz, double elevationDeg, double tiltDeg)
        {
            var (kh, kv, ah, av) = Coefficients(fGhz);
            double c = Math.Pow(Math.Cos(elevationDeg * Math.PI / 180), 2) * Math.Cos(2 * tiltDeg * Math.PI / 180);
            double k = (kh + kv + (kh - kv) * c) / 2;
            double a = (kh * ah + kv * av + (kh * ah - kv * av) * c) / (2 * k);
            return (k, a);
        }

        /// <summary>The specific rain attenuation γR = k·R^α (dB/km) for a rain rate R (mm/h).</summary>
        public static double Specific(double fGhz, double rainMmH, double elevationDeg, double tiltDeg)
        {
            var (k, a) = Coefficients(fGhz, elevationDeg, tiltDeg);
            return k * Math.Pow(rainMmH, a);
        }
    }

    public static class ItuP530
    {
        public const string Document = "ITU-R P.530-19 (09/2025)";
        private const string M = "P530";

        /// <summary>§2.4.1: the rain attenuation exceeded for p % of the time on a terrestrial path of length d (km).</summary>
        public static double Rain(double fGhz, double dKm, double r001, double pPercent, double tiltDeg)
        {
            PropagationMath.Range(M, "the frequency", fGhz, 1, 175, "GHz");
            PropagationMath.Range(M, "the path length", dKm, 0, 60, "km");
            PropagationMath.Range(M, "the time percentage", pPercent, 0.001, 1, "%");
            if (r001 < 0) throw new PropagationRefusal($"{M}: the rain rate must not be negative; it is {PropagationMath.F(r001)} mm/h.");
            if (r001 == 0 || dKm == 0) return 0;
            var (k, alpha) = ItuP838.Coefficients(fGhz, 0, tiltDeg);
            double gamma = k * Math.Pow(r001, alpha);
            double fr = Math.Max(fGhz, 6), dr = Math.Min(dKm, 30), rr = Math.Max(r001, 3);
            // eq (32) with P.530-19's own limits on f, d and R0.01 (above). An earlier edition capped r at 2.5; -19 does not, and a
            // sub-km path's r of 5-8 is what its equation gives.
            double r = 1 / (0.477 * Math.Pow(dr, 0.633) * Math.Pow(rr, 0.073 * alpha) * Math.Pow(fr, 0.123) - 10.579 * (1 - Math.Exp(-0.024 * dr)));
            double a001 = gamma * dKm * r;
            double c0 = fGhz >= 10 ? 0.12 + 0.4 * Math.Pow(Math.Log10(fGhz / 10), 0.8) : 0.12;
            double c1 = Math.Pow(0.07, c0) * Math.Pow(0.12, 1 - c0);
            double c2 = 0.855 * c0 + 0.546 * (1 - c0);
            double c3 = 0.139 * c0 + 0.043 * (1 - c0);
            return a001 * c1 * Math.Pow(pPercent, -(c2 + c3 * Math.Log10(pPercent)));
        }
    }

    public static class ItuP840
    {
        public const string Document = "ITU-R P.840-9 (08/2023)";

        /// <summary>The double-Debye permittivity of water, ε′ and ε″, at f (GHz) and T (K).</summary>
        public static (double Real, double Imag) Permittivity(double fGhz, double tK)
        {
            double th = 300 / tK;
            double e0 = 77.66 + 103.3 * (th - 1), e1 = 0.0671 * e0, e2 = 3.52;
            double fp = 20.20 - 146 * (th - 1) + 316 * (th - 1) * (th - 1), fs = 39.8 * fp;
            double re = (e0 - e1) / (1 + (fGhz / fp) * (fGhz / fp)) + (e1 - e2) / (1 + (fGhz / fs) * (fGhz / fs)) + e2;
            double im = fGhz * (e0 - e1) / (fp * (1 + (fGhz / fp) * (fGhz / fp))) + fGhz * (e1 - e2) / (fs * (1 + (fGhz / fs) * (fGhz / fs)));
            return (re, im);
        }

        /// <summary>Eq (2): the cloud liquid water specific attenuation coefficient Kl, (dB/km)/(g/m³).</summary>
        public static double Kl(double fGhz, double tK)
        {
            var (re, im) = Permittivity(fGhz, tK);
            double eta = (2 + re) / im;
            return 0.819 * fGhz / (im * (1 + eta * eta));
        }

        /// <summary>Eq (12): the cloud liquid mass absorption coefficient KL, dB/(kg/m²).</summary>
        public static double KL(double fGhz)
            => Kl(fGhz, 273.75) * (0.1522 * Math.Exp(-Math.Pow(fGhz + 23.9589, 2) / 3.2991e3)
                                   + 11.51 * Math.Exp(-Math.Pow(fGhz - 219.2096, 2) / 2.7595e6) - 10.4912);

        /// <summary>Eq (11): the slant-path cloud attenuation for integrated cloud liquid water L (kg/m²), dB.</summary>
        public static double Cloud(double fGhz, double elevationDeg, double lKgM2)
        {
            PropagationMath.Range("P840", "the frequency", fGhz, 1, 200, "GHz");
            PropagationMath.Range("P840", "the elevation", elevationDeg, 5, 90, "deg");
            if (lKgM2 < 0) throw new PropagationRefusal($"P840: the cloud liquid water content must not be negative; it is {PropagationMath.F(lKgM2)} kg/m².");
            return KL(fGhz) * lKgM2 / Math.Sin(elevationDeg * Math.PI / 180);
        }
    }
}
