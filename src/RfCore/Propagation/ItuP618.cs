// ================================================================
//  ItuP618.cs  —  ITU-R P.618-14 (08/2023), Earth-space paths
//
//  Rain       §2.2.1.1, steps 1-10: the slant path below the rain height
//             (eqs (1), (2)), its horizontal projection, the specific
//             attenuation at R0.01 (P.838), the horizontal reduction and
//             vertical adjustment factors, the effective path length,
//             A0.01, and Ap for 0.001 % ≤ p ≤ 5 % (eq (8)). Up to 55 GHz.
//             The rain height hR and the rain rate R0.01 are inputs here
//             (the recommendation reads them from P.839's and P.837's
//             maps).
//  Scintillation
//             §2.4.1, steps 3-9: σref from the wet refractivity Nwet
//             (eq (42)), the effective path length (eq (43)), the antenna
//             averaging factor (eqs (44)-(46)), σ (eq (47)), and the fade
//             depth a(p)·σ for 0.01 % ≤ p ≤ 50 % (eqs (48), (49)).
//             4 to 55 GHz, θ ≥ 5°.
//  XPD        §4.1, steps 1-8: the cross-polarization discrimination not
//             exceeded for p % of the time from rain (Cf − CA + Cτ + Cθ + Cσ)
//             with the ice crystal term taken off, 6 to 55 GHz, θ ≤ 60°,
//             0.001 % ≤ p ≤ 1 %. A depolarization, not a loss: reported.
//  Total      §2.5: AT = AG + √((AR + AC)² + AS²) for 0.001 % ≤ p ≤ 5 %
//             (eq (65)), AG + √(AC² + AS²) for 5 % < p ≤ 50 % (eq (66)),
//             AC and AG held at their 5 % values below 5 % (eqs (67), (68)).
// ================================================================

using System;

namespace RfCore.Propagation
{
    public static class ItuP618
    {
        public const string Document = "ITU-R P.618-14 (08/2023)";
        private const string M = "P618";

        /// <summary>§4.1: the XPD (dB) not exceeded for <paramref name="pPercent"/> % of the time, from the rain attenuation
        /// <paramref name="apDb"/> exceeded for the same percentage; <paramref name="tiltDeg"/> the polarization's tilt from the
        /// horizontal (45° for circular). σ, the canting angle's spread, is 0, 5, 10 and 15° at 1, 0.1, 0.01 and 0.001 %: linear
        /// in log p, which is how it is read between them.</summary>
        public static double RainXpd(double fGhz, double elevationDeg, double tiltDeg, double pPercent, double apDb)
        {
            PropagationMath.Range(M, "the frequency (XPD)", fGhz, 6, 55, "GHz");
            PropagationMath.Range(M, "the elevation (XPD)", elevationDeg, 0, 60, "deg");
            PropagationMath.Range(M, "the time percentage (XPD)", pPercent, 0.001, 1, "%");
            PropagationMath.Positive(M, "the rain attenuation (XPD)", apDb, "dB");
            double lf = Math.Log10(fGhz);
            double cf = fGhz < 9 ? 60 * lf - 28.3 : fGhz < 36 ? 26 * lf + 4.1 : 35.9 * lf - 11.3;
            // eq (72)'s V(f) has its own four bands, not eq (71)'s three
            double v = fGhz < 9 ? 30.8 * Math.Pow(fGhz, -0.21) : fGhz < 20 ? 12.8 * Math.Pow(fGhz, 0.19) : fGhz < 40 ? 22.6 : 13.0 * Math.Pow(fGhz, 0.15);
            double ca = v * Math.Log10(apDb);
            double tau = tiltDeg * Math.PI / 180;
            double ct = -10 * Math.Log10(1 - 0.484 * (1 + Math.Cos(4 * tau)));
            double cth = -40 * Math.Log10(Math.Cos(elevationDeg * Math.PI / 180));
            double sigma = -5 * Math.Log10(pPercent);
            double cs = 0.0053 * sigma * sigma;
            double xpdRain = cf - ca + ct + cth + cs;
            double cIce = xpdRain * (0.3 + 0.1 * Math.Log10(pPercent)) / 2;
            return xpdRain - cIce;
        }

        /// <summary>§2.2.1.1: the rain attenuation exceeded for p % of an average year, dB.</summary>
        /// <param name="stationKm">hs, the station's height above sea level, km.</param>
        /// <param name="rainHeightKm">hR, the rain height, km.</param>
        /// <param name="r001">The rain rate exceeded for 0.01 % of an average year, mm/h.</param>
        /// <param name="latitudeDeg">The station's latitude, degrees.</param>
        public static double Rain(double fGhz, double elevationDeg, double tiltDeg, double pPercent, double stationKm,
                                  double rainHeightKm, double r001, double latitudeDeg)
        {
            PropagationMath.Range(M, "the frequency (rain)", fGhz, 1, 55, "GHz");
            PropagationMath.Range(M, "the time percentage (rain)", pPercent, 0.001, 5, "%");
            PropagationMath.Range(M, "the elevation (rain)", elevationDeg, 0, 90, "deg");
            if (r001 < 0) throw new PropagationRefusal($"{M}: the rain rate must not be negative; it is {PropagationMath.F(r001)} mm/h.");
            double dh = rainHeightKm - stationKm;
            if (dh <= 0 || r001 == 0) return 0;
            const double re = 8500;
            double th = elevationDeg * Math.PI / 180, s = Math.Sin(th);
            double ls = elevationDeg >= 5 ? dh / s : 2 * dh / (Math.Sqrt(s * s + 2 * dh / re) + s);
            double lg = ls * Math.Cos(th);
            double gamma = ItuP838.Specific(fGhz, r001, elevationDeg, tiltDeg);
            double r = 1 / (1 + 0.78 * Math.Sqrt(lg * gamma / fGhz) - 0.38 * (1 - Math.Exp(-2 * lg)));
            double zeta = Math.Atan(dh / (lg * r)) * 180 / Math.PI;
            double lr = zeta > elevationDeg ? lg * r / Math.Cos(th) : dh / s;
            double chi = Math.Abs(latitudeDeg) < 36 ? 36 - Math.Abs(latitudeDeg) : 0;
            double v = 1 / (1 + Math.Sqrt(s) * (31 * (1 - Math.Exp(-(elevationDeg / (1 + chi)))) * Math.Sqrt(lr * gamma) / (fGhz * fGhz) - 0.45));
            double a001 = gamma * lr * v;
            double beta = pPercent >= 1 || Math.Abs(latitudeDeg) >= 36 ? 0
                        : elevationDeg >= 25 ? -0.005 * (Math.Abs(latitudeDeg) - 36)
                        : -0.005 * (Math.Abs(latitudeDeg) - 36) + 1.8 - 4.25 * s;
            return a001 * Math.Pow(pPercent / 0.01, -(0.655 + 0.033 * Math.Log(pPercent) - 0.045 * Math.Log(a001) - beta * (1 - pPercent) * s));
        }

        /// <summary>§2.4.1: the scintillation fade depth exceeded for p % of the time, dB.</summary>
        /// <param name="nwet">The wet term of the surface refractivity, N-units.</param>
        /// <param name="diameterM">The antenna's physical diameter, m.</param>
        /// <param name="efficiency">The antenna efficiency (0.5 is the recommendation's conservative value).</param>
        public static double Scintillation(double fGhz, double elevationDeg, double pPercent, double nwet, double diameterM, double efficiency)
        {
            PropagationMath.Range(M, "the frequency (scintillation)", fGhz, 4, 55, "GHz");
            PropagationMath.Range(M, "the elevation (scintillation)", elevationDeg, 5, 90, "deg");
            // The text says 0.01 % < p; the published validation examples evaluate p = 0.01 % itself, so the boundary is in.
            PropagationMath.Range(M, "the time percentage (scintillation)", pPercent, 0.01, 50, "%");
            PropagationMath.Positive(M, "the antenna diameter", diameterM, "m");
            PropagationMath.Range(M, "the antenna efficiency", efficiency, 1e-6, 1, "");
            double s = Math.Sin(elevationDeg * Math.PI / 180);
            double sref = 3.6e-3 + 1e-4 * nwet;
            double l = 2 * 1000 / (Math.Sqrt(s * s + 2.35e-4) + s);
            double deff = Math.Sqrt(efficiency) * diameterM;
            double x = 1.22 * deff * deff * (fGhz / l);
            double arg = 3.86 * Math.Pow(x * x + 1, 11.0 / 12) * Math.Sin(11.0 / 6 * Math.Atan(1 / x)) - 7.08 * Math.Pow(x, 5.0 / 6);
            if (arg < 0) return 0;
            double sigma = sref * Math.Pow(fGhz, 7.0 / 12) * Math.Sqrt(arg) / Math.Pow(s, 1.2);
            double lp = Math.Log10(pPercent);
            double ap = -0.061 * lp * lp * lp + 0.072 * lp * lp - 1.71 * lp + 3.0;
            return ap * sigma;
        }

        /// <summary>§2.5: the total attenuation from its parts at p %, dB.</summary>
        public static double Total(double pPercent, double gas, double cloud, double rain, double scintillation)
        {
            PropagationMath.Range(M, "the time percentage (total)", pPercent, 0.001, 50, "%");
            return pPercent <= 5
                ? gas + Math.Sqrt(Math.Pow(rain + cloud, 2) + scintillation * scintillation)
                : gas + Math.Sqrt(cloud * cloud + scintillation * scintillation);
        }
    }
}
