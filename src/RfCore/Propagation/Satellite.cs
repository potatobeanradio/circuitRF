// ================================================================
//  Satellite.cs  —  ITU-R P.531-16 ionospheric effects, 3GPP TR 38.811
//  large-scale parameters, and the geometry of a satellite link
//
//  ItuP531    Faraday rotation θF = 2.36·10⁻¹⁴·Bav·NT/f² (eq (4); f in
//             GHz, NT the total electron content in el/m², Bav in T), the
//             cross-polar discrimination −20·log10(tan θF) (eq (5)), and
//             the loss a linear receive polarization fixed for no rotation
//             sees, −20·log10|cos θF|. Amplitude scintillation from the
//             S4 index: S4 scaled from a reference frequency as f^−1.5
//             (§5.1), the peak-to-peak fluctuation 27.5·S4^1.26 (eq (8)),
//             the Nakagami m of the intensity (eq (10)), and the fade depth
//             exceeded for p % of the time from the Nakagami intensity
//             distribution (eq (11)). 0.1 ≤ S4 ≤ 1 (weaker: no fade).
//  Tr38811    TR 38.811 V15.4.0 Table 6.6.1-1 (line-of-sight probability)
//             and Tables 6.6.2-1 to 6.6.2-3 (shadow fading σ, and clutter
//             loss for a terminal out of line of sight), for dense urban,
//             urban, suburban and rural, S band and Ka band, at the
//             reference elevation nearest the link's (10° to 90°).
//  SatelliteGeometry
//             A circular orbit of altitude h seen at elevation ε from a
//             station on a sphere of radius 6371 km: the slant range
//             d = √((R+h)² − (R·cos ε)²) − R·sin ε, the latency d/c, and,
//             for a pass through the station's zenith, the range rate
//             ḋ = v·R·sin λ/d (λ the central angle between station and
//             sub-satellite point, v = √(μ/(R+h))), its rate, and the
//             Doppler −f·ḋ/c and its drift. Earth's rotation is not
//             included. A geostationary satellite has no range rate.
// ================================================================

using System;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public static class ItuP531
    {
        public const string Document = "ITU-R P.531-16 (09/2025)";

        /// <summary>The Faraday rotation (rad) for a total electron content NT (el/m²) and mean field B (T) at f (GHz).</summary>
        public static double FaradayRotation(double fGhz, double tecElPerM2, double fieldT)
            => 2.36e-14 * fieldT * tecElPerM2 / (fGhz * fGhz);

        /// <summary>The loss (dB) a linear polarization aligned for no rotation sees after a rotation θ (rad).</summary>
        public static double FaradayLoss(double rotationRad)
            => -20 * Math.Log10(Math.Max(Math.Abs(Math.Cos(rotationRad)), 1e-12));

        /// <summary>The cross-polar discrimination (dB) after a rotation θ (rad), eq (5).</summary>
        public static double Xpd(double rotationRad) => -20 * Math.Log10(Math.Abs(Math.Tan(rotationRad)));

        /// <summary>S4 at f from S4 at a reference frequency, the f^−1.5 dependence of weak and moderate scintillation.</summary>
        public static double S4At(double fGhz, double s4Ref, double fRefGhz) => s4Ref * Math.Pow(fGhz / fRefGhz, -1.5);

        /// <summary>Eq (8): the peak-to-peak intensity fluctuation (dB).</summary>
        public static double PeakToPeak(double s4) => 27.5 * Math.Pow(s4, 1.26);

        /// <summary>Eq (10): the Nakagami m of the intensity.</summary>
        public static double NakagamiM(double s4) => Math.Exp(5.69 * Math.Exp(-3.055 * s4) + 0.292 * Math.Exp(0.344 * s4));

        /// <summary>The scintillation fade depth (dB) exceeded for p % of the time: the intensity I (mean 1) below which the
        /// Nakagami distribution of eq (11) lies with probability p/100, as −10·log10 I. 0 for S4 below 0.1.</summary>
        public static double FadeDepth(double s4, double pPercent)
        {
            if (s4 < 0.1) return 0;
            PropagationMath.Range("P531", "the scintillation index S4 at this frequency", s4, 0, 1, "");
            PropagationMath.Range("P531", "the time percentage (scintillation)", pPercent, 1e-6, 50, "%");
            double m = NakagamiM(s4);
            double i = GammaFunction.InverseP(m, pPercent / 100) / m;
            return -10 * Math.Log10(i);
        }
    }

    public enum NtnScenario { DenseUrban, Urban, Suburban, Rural }

    public static class Tr38811
    {
        public const string Document = "3GPP TR 38.811 V15.4.0 (2020-09)";

        private static readonly double[,] Los =
        {
            { 28.2, 33.1, 39.8, 46.8, 53.7, 61.2, 73.8, 82.0, 98.1 },
            { 24.6, 38.6, 49.3, 61.3, 72.6, 80.5, 91.9, 96.8, 99.2 },
            { 78.2, 86.9, 91.9, 92.9, 93.5, 94.0, 94.9, 95.2, 99.8 },
        };

        // per scenario family, per reference elevation: S-band σLOS, σNLOS, CL; Ka-band σLOS, σNLOS, CL
        private static readonly double[][,] Sf =
        {
            new double[,]
            {
                { 3.5, 15.5, 34.3, 2.9, 17.1, 44.3 }, { 3.4, 13.9, 30.9, 2.4, 17.1, 39.9 }, { 2.9, 12.4, 29.0, 2.7, 15.6, 37.5 },
                { 3.0, 11.7, 27.7, 2.4, 14.6, 35.8 }, { 3.1, 10.6, 26.8, 2.4, 14.2, 34.6 }, { 2.7, 10.5, 26.2, 2.7, 12.6, 33.8 },
                { 2.5, 10.1, 25.8, 2.6, 12.1, 33.3 }, { 2.3, 9.2, 25.5, 2.8, 12.3, 33.0 }, { 1.2, 9.2, 25.5, 0.6, 12.3, 32.9 },
            },
            new double[,]
            {
                { 4, 6, 34.3, 4, 6, 44.3 }, { 4, 6, 30.9, 4, 6, 39.9 }, { 4, 6, 29.0, 4, 6, 37.5 }, { 4, 6, 27.7, 4, 6, 35.8 },
                { 4, 6, 26.8, 4, 6, 34.6 }, { 4, 6, 26.2, 4, 6, 33.8 }, { 4, 6, 25.8, 4, 6, 33.3 }, { 4, 6, 25.5, 4, 6, 33.0 },
                { 4, 6, 25.5, 4, 6, 32.9 },
            },
            new double[,]
            {
                { 1.79, 8.93, 19.52, 1.9, 10.7, 29.5 }, { 1.14, 9.08, 18.17, 1.6, 10.0, 24.6 }, { 1.14, 8.78, 18.42, 1.9, 11.2, 21.9 },
                { 0.92, 10.25, 18.28, 2.3, 11.6, 20.0 }, { 1.42, 10.56, 18.63, 2.7, 11.8, 18.7 }, { 1.56, 10.74, 17.68, 3.1, 10.8, 17.8 },
                { 0.85, 10.17, 16.50, 3.0, 10.8, 17.2 }, { 0.72, 11.52, 16.30, 3.6, 10.8, 16.9 }, { 0.72, 11.52, 16.30, 0.4, 10.8, 16.8 },
            },
        };

        private static int Family(NtnScenario s) => s switch { NtnScenario.DenseUrban => 0, NtnScenario.Urban => 1, _ => 2 };

        /// <summary>The index of the reference elevation nearest ε (10°, 20°, … 90°), refusing outside 10°-90°.</summary>
        public static int ReferenceIndex(double elevationDeg)
        {
            PropagationMath.Range("38811", "the elevation", elevationDeg, 10, 90, "deg");
            return (int)Math.Clamp(Math.Floor(elevationDeg / 10 + 0.5) - 1, 0, 8);
        }

        /// <summary>The band the report's tables give: S band (2-4 GHz) or Ka band (17.7-40 GHz).</summary>
        public static bool IsKa(double fGhz)
        {
            if (fGhz >= 2 && fGhz <= 4) return false;
            if (fGhz >= 17.7 && fGhz <= 40) return true;
            throw new PropagationRefusal(
                $"38811: the report's shadow fading and clutter loss are for S band (2 to 4 GHz) and Ka band (17.7 to 40 GHz); the frequency is {PropagationMath.Ghz(fGhz * 1e9)}.");
        }

        public static double LosProbability(NtnScenario s, double elevationDeg) => Los[Family(s), ReferenceIndex(elevationDeg)] / 100;

        /// <summary>The shadow fading σ (dB) and the clutter loss (dB, 0 in line of sight).</summary>
        public static (double Sigma, double ClutterLoss) LargeScale(NtnScenario s, bool los, double fGhz, double elevationDeg)
        {
            int k = ReferenceIndex(elevationDeg), o = IsKa(fGhz) ? 3 : 0;
            var t = Sf[Family(s)];
            return los ? (t[k, o], 0) : (t[k, o + 1], t[k, o + 2]);
        }
    }

    /// <summary>The geometry of a satellite link at one instant.</summary>
    /// <param name="RangeRateMps">ḋ, positive receding.</param>
    /// <param name="DopplerHz">The common Doppler shift at the carrier: positive approaching.</param>
    public sealed record SlantGeometry(double AltitudeKm, double ElevationDeg, double SlantRangeKm, double LatencyS,
                                       double RangeRateMps, double RangeAccelMps2, double DopplerHz, double DopplerRateHzps);

    public static class SatelliteGeometry
    {
        public const double EarthRadiusKm = 6371;
        public const double Mu = 3.986004418e14;            // m³/s²
        public const double GeostationaryKm = 35786;

        public static double SlantRangeKm(double altitudeKm, double elevationDeg)
        {
            double r = EarthRadiusKm, e = elevationDeg * Math.PI / 180;
            return Math.Sqrt(Math.Pow(r + altitudeKm, 2) - Math.Pow(r * Math.Cos(e), 2)) - r * Math.Sin(e);
        }

        /// <summary>The geometry at elevation ε of a pass through the station's zenith, rising (approaching) or setting
        /// (receding); a geostationary orbit (altitude 35786 km) is stationary.</summary>
        public static SlantGeometry At(double altitudeKm, double elevationDeg, double carrierHz, bool rising)
        {
            PropagationMath.Range("satellite", "the altitude", altitudeKm, 1, 1e6, "km");
            PropagationMath.Range("satellite", "the elevation", elevationDeg, 0, 90, "deg");
            double d = SlantRangeKm(altitudeKm, elevationDeg);
            double latency = d * 1000 / PropagationMath.C;
            if (Math.Abs(altitudeKm - GeostationaryKm) < 1)
                return new SlantGeometry(altitudeKm, elevationDeg, d, latency, 0, 0, 0, 0);
            double r = EarthRadiusKm * 1000, a = r + altitudeKm * 1000, dm = d * 1000;
            double v = Math.Sqrt(Mu / a), w = v / a;
            double e = elevationDeg * Math.PI / 180;
            double eta = Math.Asin(r * Math.Cos(e) / a);
            double lambda = Math.PI / 2 - e - eta;
            double sign = rising ? -1 : 1;
            double rate = sign * v * r * Math.Sin(lambda) / dm;
            // d̈ = (v·R/d²)·(ω·cos λ·d − sin λ·ḋ), with λ shrinking while rising and growing while setting
            double accel = v * r / (dm * dm) * (w * Math.Cos(lambda) * dm - Math.Sin(lambda) * Math.Abs(rate));
            double doppler = -carrierHz * rate / PropagationMath.C;
            double drift = -carrierHz * accel / PropagationMath.C;
            return new SlantGeometry(altitudeKm, elevationDeg, d, latency, rate, accel, doppler, drift);
        }
    }
}
