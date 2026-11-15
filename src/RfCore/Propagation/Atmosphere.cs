// ================================================================
//  Atmosphere.cs  —  ITU-R P.835 reference atmosphere and P.453
//  refractivity
//
//  ItuP835   Annex 1, the ITU-R reference atmosphere: temperature and
//            total pressure against geometric height to 100 km (the
//            geopotential regime below 86 km, eqs (1)-(3); the geometric
//            regime above, eqs (4)-(5)), and the water vapour density
//            ρ(Z) = ρ0·exp(−Z/2) up to the height where the mixing ratio
//            e/P reaches 2·10⁻⁶, constant mixing ratio above (eqs (6)-(8)).
//            ρ0 is the recommendation's 7.5 g/m³ unless stated.
//  ItuP453   the radio refractivity N (eq (2)), its wet term (eq (4)),
//            the saturation vapour pressure over water (eq (9)), and
//            e = ρT/216.7 (eq (10)).
// ================================================================

using System;

namespace RfCore.Propagation
{
    /// <summary>The state of the atmosphere at one height: temperature (K), total pressure (hPa), water vapour density
    /// (g/m³), and the partial pressures derived from them.</summary>
    public readonly record struct AtmosphereState(double TemperatureK, double PressureHpa, double WaterVapourGm3)
    {
        /// <summary>Water vapour partial pressure e = ρT/216.7, hPa.</summary>
        public double VapourHpa => WaterVapourGm3 * TemperatureK / 216.7;

        /// <summary>Dry air pressure p = P − e, hPa.</summary>
        public double DryHpa => PressureHpa - VapourHpa;
    }

    public static class ItuP835
    {
        public const string Document = "ITU-R P.835-7 (08/2024)";

        /// <summary>The reference atmosphere at geometric height z (km), with surface water vapour density ρ0 (g/m³).</summary>
        public static AtmosphereState At(double zKm, double rho0 = 7.5)
        {
            double t, p;
            if (zKm < 86)
            {
                double h = 6356.766 * zKm / (6356.766 + zKm);
                if (h <= 11) { t = 288.15 - 6.5 * h; p = 1013.25 * Math.Pow(288.15 / (288.15 - 6.5 * h), -34.1632 / 6.5); }
                else if (h <= 20) { t = 216.65; p = 226.3226 * Math.Exp(-34.1632 * (h - 11) / 216.65); }
                else if (h <= 32) { t = 216.65 + (h - 20); p = 54.74980 * Math.Pow(216.65 / (216.65 + (h - 20)), 34.1632); }
                else if (h <= 47) { t = 228.65 + 2.8 * (h - 32); p = 8.680422 * Math.Pow(228.65 / (228.65 + 2.8 * (h - 32)), 34.1632 / 2.8); }
                else if (h <= 51) { t = 270.65; p = 1.109106 * Math.Exp(-34.1632 * (h - 47) / 270.65); }
                else if (h <= 71) { t = 270.65 - 2.8 * (h - 51); p = 0.6694167 * Math.Pow(270.65 / (270.65 - 2.8 * (h - 51)), -34.1632 / 2.8); }
                else { t = 214.65 - 2.0 * (h - 71); p = 0.03956649 * Math.Pow(214.65 / (214.65 - 2.0 * (h - 71)), -34.1632 / 2.0); }
            }
            else
            {
                t = zKm <= 91 ? 186.8673 : 263.1905 - 76.3232 * Math.Sqrt(1 - Math.Pow((zKm - 91) / 19.9429, 2));
                p = Math.Exp(95.571899 - 4.011801 * zKm + 6.424731e-2 * zKm * zKm - 4.789660e-4 * Math.Pow(zKm, 3) + 1.340543e-6 * Math.Pow(zKm, 4));
            }
            double rho = rho0 * Math.Exp(-zKm / 2);
            if (rho * t / 216.7 / p < 2e-6) rho = 2e-6 * p * 216.7 / t;
            return new AtmosphereState(t, p, rho);
        }
    }

    public static class ItuP453
    {
        public const string Document = "ITU-R P.453-14 (08/2019)";

        /// <summary>The radio refractivity N (N-units), eq (2): dry pressure p, vapour pressure e (hPa), temperature T (K).</summary>
        public static double Refractivity(double dryHpa, double vapourHpa, double temperatureK)
            => 77.6 * dryHpa / temperatureK + 72 * vapourHpa / temperatureK + 3.75e5 * vapourHpa / (temperatureK * temperatureK);

        /// <summary>The wet term Nwet (N-units), eq (4).</summary>
        public static double WetRefractivity(double vapourHpa, double temperatureK)
            => 72 * vapourHpa / temperatureK + 3.75e5 * vapourHpa / (temperatureK * temperatureK);

        /// <summary>The saturation vapour pressure over water (hPa), eq (9), at t (°C) and total pressure P (hPa).</summary>
        public static double SaturationVapour(double tCelsius, double pressureHpa)
        {
            double ef = 1 + 1e-4 * (7.2 + pressureHpa * (0.0320 + 5.9e-6 * tCelsius * tCelsius));
            return ef * 6.1121 * Math.Exp((18.678 - tCelsius / 234.5) * tCelsius / (tCelsius + 257.14));
        }
    }
}
