// ================================================================
//  AddedLosses.cs  —  losses added to any model's, each its own term
//
//  partitions     a list of partition losses on the path, dB each
//                 (a same-floor indoor link through walls).
//  clutter        ITU-R P.2108-1 (09/2021):
//                   terminal     §3.1 height-gain terminal correction,
//                                0.03-3 GHz: a terminal below the
//                                representative clutter height R loses
//                                J(ν) − 6.03 (urban, suburban, trees,
//                                dense urban) or −Kh2·log10(h/R) (open,
//                                water); J(ν) the knife-edge
//                                approximation, ν = Knu·√(hdif·θclut).
//                   terrestrial  §3.2 statistical clutter loss, 0.5-67 GHz,
//                                d ≥ 0.25 km, at p % of locations, capped
//                                at its 2 km value (eq. (6)).
//                   earthspace   §3.3 for an elevated platform, 10-100 GHz,
//                                elevation 0-90°, p % of locations.
//  building entry ITU-R P.2109-2 (08/2023): the loss not exceeded with
//                 probability P, traditional or thermally efficient, at
//                 the elevation of the path at the façade.
//  vegetation     ITU-R P.833-10 (09/2021) §2.1: one terminal a depth d
//                 into woodland, Aev = Am·[1 − exp(−d·γ/Am)]; γ (dB/m) and
//                 Am (dB) stated, or Am = A1·f^α (f in MHz) with the
//                 recommendation's 900-2200 MHz woodland fit A1 = 1.15,
//                 α = 0.43, and γ interpolated (log-log) in its Table 1
//                 between 105.9 and 2117.5 MHz.
//  polarization   a stated mismatch loss, dB, for ends with no pattern.
//  pointing       a stated pointing loss, dB.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public enum TerminalClutter { WaterSea, OpenRural, Suburban, UrbanTreesForest, DenseUrban }

    public enum BuildingClass { Traditional, ThermallyEfficient }

    public static class ItuP2108
    {
        public const string Document = "ITU-R P.2108-1 (09/2021)";
        private const string M = "P2108";

        /// <summary>The knife-edge loss approximation J(ν) of eq. (1); 0 for ν ≤ −0.78.</summary>
        public static double J(double nu)
            => nu <= -0.78 ? 0 : 6.9 + 20 * Math.Log10(Math.Sqrt((nu - 0.1) * (nu - 0.1) + 1) + nu - 0.1);

        /// <summary>Table 3's representative clutter height, m.</summary>
        public static double DefaultClutterHeight(TerminalClutter c) => c switch
        {
            TerminalClutter.UrbanTreesForest => 15,
            TerminalClutter.DenseUrban => 20,
            _ => 10,
        };

        /// <summary>§3.1: the additional loss Ah of a terminal at height h (m) in clutter of height R (m).</summary>
        public static double TerminalCorrection(double fHz, double hM, TerminalClutter clutter, double rM, double streetWidthM = 27)
        {
            double f = fHz / 1e9;
            PropagationMath.Range(M, "the frequency (terminal correction)", f, 0.03, 3, "GHz");
            PropagationMath.Positive(M, "the terminal height", hM, "m");
            PropagationMath.Positive(M, "the representative clutter height", rM, "m");
            PropagationMath.Positive(M, "the street width", streetWidthM, "m");
            if (hM >= rM) return 0;
            if (clutter is TerminalClutter.WaterSea or TerminalClutter.OpenRural)
                return -(21.8 + 6.2 * Math.Log10(f)) * Math.Log10(hM / rM);
            double hdif = rM - hM;
            double thetaDeg = Math.Atan(hdif / streetWidthM) * 180 / Math.PI;
            double nu = 0.342 * Math.Sqrt(f) * Math.Sqrt(hdif * thetaDeg);
            return J(nu) - 6.03;
        }

        /// <summary>§3.2: the clutter loss not exceeded for p % of locations, terrestrial path of length d (m).</summary>
        public static double Terrestrial(double fHz, double distanceM, double pPercent)
        {
            double f = fHz / 1e9, d = distanceM / 1000;
            PropagationMath.Range(M, "the frequency (terrestrial clutter)", f, 0.5, 67, "GHz");
            PropagationMath.Range(M, "the path length (terrestrial clutter)", d, 0.25, double.PositiveInfinity, "km");
            if (!(pPercent > 0 && pPercent < 100))
                throw new PropagationRefusal($"{M}: the location percentage must be between 0 and 100 (exclusive); it is {PropagationMath.F(pPercent)} %.");
            double Lctt(double dk)
            {
                double ll = -2 * Math.Log10(Math.Pow(10, -5 * Math.Log10(f) - 12.5) + Math.Pow(10, -16.5));
                double ls = 32.98 + 23.9 * Math.Log10(dk) + 3 * Math.Log10(f);
                double a = Math.Pow(10, -0.2 * ll), b = Math.Pow(10, -0.2 * ls);
                double scb = Math.Sqrt((16 * a + 36 * b) / (a + b));
                return -5 * Math.Log10(a + b) - scb * Normal.InverseQ(pPercent / 100);
            }
            return Math.Min(Lctt(d), Lctt(2));
        }

        /// <summary>§3.3: the clutter loss not exceeded for p % of locations toward a platform at elevation θ (deg).</summary>
        public static double EarthSpace(double fHz, double elevationDeg, double pPercent)
        {
            double f = fHz / 1e9;
            PropagationMath.Range(M, "the frequency (Earth-space clutter)", f, 10, 100, "GHz");
            PropagationMath.Range(M, "the elevation (Earth-space clutter)", elevationDeg, 0, 90, "deg");
            if (!(pPercent > 0 && pPercent < 100))
                throw new PropagationRefusal($"{M}: the location percentage must be between 0 and 100 (exclusive); it is {PropagationMath.F(pPercent)} %.");
            double k1 = 93 * Math.Pow(f, 0.175), a1 = 0.05, th = elevationDeg;
            double inner = -k1 * Math.Log(1 - pPercent / 100) / Math.Tan(a1 * (1 - th / 90) + Math.PI * th / 180);
            return Math.Pow(inner, 0.5 * (90 - th) / 90) - 1 - 0.6 * Normal.InverseQ(pPercent / 100);
        }
    }

    public static class ItuP2109
    {
        public const string Document = "ITU-R P.2109-2 (08/2023)";

        /// <summary>The building entry loss not exceeded with probability P (0-1), dB.</summary>
        public static double Loss(double fHz, double probability, BuildingClass building, double elevationDeg)
        {
            const string m = "P2109";
            double f = fHz / 1e9;
            PropagationMath.Range(m, "the frequency", f, 0.08, 100, "GHz");
            if (!(probability > 0 && probability < 1))
                throw new PropagationRefusal($"{m}: the probability must be between 0 and 1 (exclusive); it is {PropagationMath.F(probability)}.");
            PropagationMath.Range(m, "the elevation at the façade", elevationDeg, -90, 90, "deg");
            (double r, double s, double t, double u, double v, double w, double x, double y, double z) =
                building == BuildingClass.Traditional
                    ? (12.64, 3.72, 0.96, 9.6, 2.0, 9.1, -3.0, 4.5, -2.0)
                    : (28.19, -3.00, 8.48, 13.5, 3.8, 27.8, -2.9, 9.4, -2.1);
            double lf = Math.Log10(f);
            double lh = r + s * lf + t * lf * lf, le = 0.212 * Math.Abs(elevationDeg);
            double mu1 = lh + le, mu2 = w + x * lf, s1 = u + v * lf, s2 = y + z * lf;
            double fi = Normal.InverseCdf(probability);
            double a = fi * s1 + mu1, b = fi * s2 + mu2;
            const double c = -3.0;
            return 10 * Math.Log10(Math.Pow(10, 0.1 * a) + Math.Pow(10, 0.1 * b) + Math.Pow(10, 0.1 * c));
        }
    }

    public static class ItuP833
    {
        public const string Document = "ITU-R P.833-10 (09/2021)";
        private static readonly double[] TableMhz = { 105.9, 466.475, 949.0, 1852.2, 2117.5 };
        private static readonly double[] TableGamma = { 0.04, 0.12, 0.17, 0.30, 0.34 };

        /// <summary>Table 1's specific attenuation, interpolated log-log, dB/m; refused outside its frequencies.</summary>
        public static double DefaultGamma(double fHz)
        {
            double f = fHz / 1e6;
            PropagationMath.Range("P833", "the frequency for the tabulated specific attenuation (state VegGamma otherwise)", f, TableMhz[0], TableMhz[^1], "MHz");
            int i = 0;
            while (i < TableMhz.Length - 2 && f > TableMhz[i + 1]) i++;
            double t = Math.Log(f / TableMhz[i]) / Math.Log(TableMhz[i + 1] / TableMhz[i]);
            return Math.Exp(Math.Log(TableGamma[i]) + t * Math.Log(TableGamma[i + 1] / TableGamma[i]));
        }

        /// <summary>Am = A1·f^α with the 900-2200 MHz woodland fit, dB.</summary>
        public static double DefaultMaxAttenuation(double fHz) => 1.15 * Math.Pow(fHz / 1e6, 0.43);

        /// <summary>Eq. (1): the excess attenuation for one terminal a depth d (m) into woodland.</summary>
        public static double Woodland(double depthM, double gammaDbPerM, double maxDb)
        {
            if (depthM < 0) throw new PropagationRefusal($"P833: the woodland depth must not be negative; it is {PropagationMath.F(depthM)} m.");
            PropagationMath.Positive("P833", "the specific attenuation γ", gammaDbPerM, "dB/m");
            PropagationMath.Positive("P833", "the maximum attenuation Am", maxDb, "dB");
            return maxDb * (1 - Math.Exp(-depthM * gammaDbPerM / maxDb));
        }
    }
}

namespace RfCore.Propagation
{
    /// <summary>The added losses every model takes as parameters (the "Added losses" group of the registry).</summary>
    public static class AddedLosses
    {
        private const string G = "Added losses";

        private static ModelParameter N(string name, string unit, string def, string help, double min, double max)
            => new(name, unit, def, help, ParameterKind.Number, min, max, Group: G);
        private static ModelParameter W(string name, string def, string help, params string[] words)
            => new(name, "", def, help, ParameterKind.Word, Words: words, Group: G);

        public static readonly IReadOnlyList<ModelParameter> Parameters = new[]
        {
            new ModelParameter("Partitions", "dB", "", "partition losses on the path, dB each, comma-separated", ParameterKind.List, Group: G),
            W("Clutter", "off", "ITU-R P.2108 clutter loss: terminal correction, terrestrial statistics, or toward an elevated platform",
              "off", "terminal", "terrestrial", "earthspace"),
            W("ClutterEnd", "rx", "which terminal is in clutter", "rx", "tx", "both"),
            W("ClutterType", "urban", "terminal: the clutter type", "water", "open", "suburban", "urban", "denseurban"),
            N("TerminalClutterHeight", "m", "", "terminal: the representative clutter height (blank: the recommendation's for the type)", 0.1, 200),
            N("ClutterStreetWidth", "m", "27", "terminal: the street width", 1, 200),
            N("ClutterLocations", "%", "50", "terrestrial, earthspace: the percentage of locations the loss is not exceeded at", 1e-6, 99.999999),
            N("ClutterElevation", "deg", "", "earthspace: the platform's elevation seen from the terminal", 0, 90),
            W("BuildingEntry", "off", "ITU-R P.2109 building entry loss", "off", "traditional", "efficient"),
            N("BuildingProbability", "", "0.5", "the probability the building entry loss is not exceeded", 1e-6, 0.999999),
            N("FacadeElevation", "deg", "0", "the path's elevation at the building façade", -90, 90),
            W("Vegetation", "off", "ITU-R P.833 woodland: one terminal inside it", "off", "on"),
            N("VegDepth", "m", "0", "the depth of the terminal inside the woodland", 0, 1e5),
            N("VegGamma", "dB/m", "", "the woodland's specific attenuation (blank: the recommendation's table)", 1e-6, 100),
            N("VegMax", "dB", "", "the maximum woodland attenuation (blank: the recommendation's fit A1·f^α)", 1e-6, 500),
            N("PolLoss", "dB", "0", "a polarization mismatch loss", 0, 100),
            N("PointingLoss", "dB", "0", "a pointing loss", 0, 100),

            N("Availability", "%", "99", "the percentage of time the link must be available: rain, clouds and scintillation are taken at 100 minus it", 50, 99.9999),
            W("Gas", "off", "ITU-R P.676 gaseous absorption: along the path, or through the atmosphere on a satellite path", "off", "on"),
            N("SurfaceTemperature", "degC", "15", "the surface air temperature", -80, 60),
            N("SurfacePressure", "hPa", "1013.25", "the surface total air pressure", 300, 1100),
            N("WaterVapour", "g/m3", "7.5", "the surface water vapour density", 0, 50),
            W("Rain", "off", "rain attenuation at the availability: ITU-R P.530 on a terrestrial path, P.618 on a satellite path; drawn is per drop", "off", "fixed", "drawn"),
            N("RainRate", "mm/h", "40", "the rain rate exceeded for 0.01 % of an average year", 0, 300),
            N("RainHeight", "km", "3.4", "satellite path: the rain height above sea level", 0, 8),
            N("RainTilt", "deg", "45", "the polarization tilt from horizontal for rain: 0 horizontal, 90 vertical, 45 circular", 0, 90),
            N("StationHeight", "km", "0", "satellite path: the earth station's height above sea level", 0, 8),
            N("StationLatitude", "deg", "45", "satellite path: the earth station's latitude", -90, 90),
            W("Clouds", "off", "satellite path: ITU-R P.840 cloud attenuation", "off", "on"),
            N("CloudWater", "kg/m2", "0.5", "satellite path: the integrated cloud liquid water content", 0, 10),
            W("Scintillation", "off", "satellite path: ITU-R P.618 tropospheric scintillation fade at the availability", "off", "on"),
            N("AntennaDiameter", "m", "1", "satellite path, scintillation: the earth station antenna's diameter", 0.01, 100),
            N("AntennaEfficiency", "", "0.5", "satellite path, scintillation: the antenna efficiency", 0.01, 1),
            W("Ionosphere", "off", "satellite path: ITU-R P.531 Faraday rotation and ionospheric scintillation", "off", "on"),
            N("Tec", "TECU", "50", "the total electron content along the path, 1 TECU = 1e16 el/m2", 0, 1000),
            N("MagneticField", "uT", "50", "the average geomagnetic field along the path", 0, 70),
            W("LinearPolarization", "yes", "a linear polarization fixed for no rotation (Faraday rotation is then a loss)", "yes", "no"),
            N("S4", "", "0", "the amplitude scintillation index at S4Frequency (0: none)", 0, 1.5),
            N("S4Frequency", "GHz", "1.5", "the frequency the S4 index is stated at", 0.1, 10),
        };

        internal static (List<LossTerm> Terms, List<string> Notes) Evaluate(ModelArgs a, PathLossResult baseResult)
        {
            var terms = new List<LossTerm>();
            var notes = new List<string>();
            double[] parts = a.List("Partitions");
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] < 0) throw new PropagationRefusal($"{a.Inputs.Model}: a partition loss must not be negative; partition {i + 1} is {PropagationMath.F(parts[i])} dB.");
                terms.Add(new LossTerm($"partition {i + 1}", parts[i]));
            }

            string clutter = a.Word("Clutter");
            if (clutter != "off")
            {
                string end = a.Word("ClutterEnd");
                bool tx = end is "tx" or "both", rx = end is "rx" or "both";
                switch (clutter)
                {
                    case "terminal":
                    {
                        var type = a.Word("ClutterType") switch
                        {
                            "water" => TerminalClutter.WaterSea,
                            "open" => TerminalClutter.OpenRural,
                            "suburban" => TerminalClutter.Suburban,
                            "denseurban" => TerminalClutter.DenseUrban,
                            _ => TerminalClutter.UrbanTreesForest,
                        };
                        double r = a.Given("TerminalClutterHeight") ? a.Number("TerminalClutterHeight") : ItuP2108.DefaultClutterHeight(type);
                        double ws = a.Number("ClutterStreetWidth");
                        if (tx) terms.Add(new("P.2108 terminal clutter (transmit)", ItuP2108.TerminalCorrection(a.F, a.Htx, type, r, ws)));
                        if (rx) terms.Add(new("P.2108 terminal clutter (receive)", ItuP2108.TerminalCorrection(a.F, a.Hrx, type, r, ws)));
                        break;
                    }
                    case "terrestrial":
                    {
                        if (end == "both" && a.D < 1000)
                            throw new PropagationRefusal($"P2108: clutter at both ends needs a path of at least 1 km; it is {PropagationMath.F(a.D / 1000)} km.");
                        double l = ItuP2108.Terrestrial(a.F, a.D, a.Number("ClutterLocations"));
                        if (tx) terms.Add(new("P.2108 terrestrial clutter (transmit)", l));
                        if (rx) terms.Add(new("P.2108 terrestrial clutter (receive)", l));
                        break;
                    }
                    default:
                    {
                        if (!a.Given("ClutterElevation")) throw new PropagationRefusal("P2108: the Earth-space clutter loss needs ClutterElevation.");
                        terms.Add(new("P.2108 Earth-space clutter", ItuP2108.EarthSpace(a.F, a.Number("ClutterElevation"), a.Number("ClutterLocations"))));
                        break;
                    }
                }
            }

            string bel = a.Word("BuildingEntry");
            if (bel != "off")
                terms.Add(new("P.2109 building entry", ItuP2109.Loss(a.F, a.Number("BuildingProbability"),
                    bel == "efficient" ? BuildingClass.ThermallyEfficient : BuildingClass.Traditional, a.Number("FacadeElevation"))));

            if (a.Word("Vegetation") == "on")
            {
                double gamma = a.Given("VegGamma") ? a.Number("VegGamma") : ItuP833.DefaultGamma(a.F);
                double am = a.Given("VegMax") ? a.Number("VegMax") : ItuP833.DefaultMaxAttenuation(a.F);
                if (!a.Given("VegMax")) notes.Add("Woodland maximum attenuation from P.833's 900-2200 MHz fit Am = 1.15·f^0.43.");
                terms.Add(new("P.833 woodland", ItuP833.Woodland(a.Number("VegDepth"), gamma, am)));
            }

            double pol = a.Number("PolLoss"), point = a.Number("PointingLoss");
            if (pol > 0) terms.Add(new("polarization mismatch", pol));
            if (point > 0) terms.Add(new("pointing", point));

            Atmosphere(a, baseResult, terms, notes);
            return (terms, notes);
        }

        /// <summary>Gas, rain, clouds, scintillation and the ionosphere, by the terrestrial or the Earth-space methods.</summary>
        private static void Atmosphere(ModelArgs a, PathLossResult baseResult, List<LossTerm> terms, List<string> notes)
        {
            string model = a.Inputs.Model;
            bool gas = a.Word("Gas") == "on", clouds = a.Word("Clouds") == "on", scint = a.Word("Scintillation") == "on", iono = a.Word("Ionosphere") == "on";
            string rain = a.Word("Rain");
            if (rain == "drawn")
                throw new PropagationRefusal($"{model}: Rain=drawn takes a rain attenuation per drop of a Monte Carlo; a single evaluation takes Rain=fixed (the attenuation at the availability).");
            bool rainOn = rain == "fixed";
            if (!(gas || rainOn || clouds || scint || iono)) return;

            double p = 100 - a.Number("Availability");          // the one conversion from availability to time percentage
            double fGhz = a.F / 1e9;
            double tK = a.Number("SurfaceTemperature") + 273.15, rho = a.Number("WaterVapour");
            var surface = new AtmosphereState(tK, a.Number("SurfacePressure"), rho);
            var geo = baseResult.Geometry;

            if (geo is null)
            {
                if (clouds || scint || iono)
                    throw new PropagationRefusal($"{model}: Clouds, Scintillation and Ionosphere are Earth-space effects (ITU-R P.840, P.618, P.531); this is a terrestrial path. Use the satellite model.");
                double dKm = a.D3 / 1000;
                if (gas) terms.Add(new("P.676 gas", ItuP676.Terrestrial(fGhz, dKm, surface)));
                if (rainOn) terms.Add(new($"P.530 rain at {PropagationMath.F(p)} % of the time",
                                           ItuP530.Rain(fGhz, dKm, a.Number("RainRate"), p, a.Number("RainTilt"))));
                return;
            }

            double el = geo.ElevationDeg, hs = a.Number("StationHeight");
            double ag = 0, ar = 0, ac = 0, aS = 0;
            if (gas)
            {
                ag = ItuP676.Slant(fGhz, hs, el, rho).AttenuationDb;
                terms.Add(new("P.676 gas (slant path)", ag));
                notes.Add("Slant-path gas through the P.835 reference atmosphere with the stated surface water vapour density; the free-space elevation is taken as the apparent one.");
            }
            if (rainOn)
            {
                if (p <= 5) ar = ItuP618.Rain(fGhz, el, a.Number("RainTilt"), p, hs, a.Number("RainHeight"), a.Number("RainRate"), a.Number("StationLatitude"));
                else notes.Add($"Rain is not part of P.618's total above 5 % of the time; at {PropagationMath.F(p)} % it adds nothing.");
            }
            if (clouds) ac = ItuP840.Cloud(fGhz, el, a.Number("CloudWater"));
            if (scint)
            {
                double nwet = ItuP453.WetRefractivity(surface.VapourHpa, tK);
                aS = ItuP618.Scintillation(fGhz, el, p, nwet, a.Number("AntennaDiameter"), a.Number("AntennaEfficiency"));
            }
            if (rainOn || clouds || scint)
            {
                double combined = ItuP618.Total(p, 0, ac, ar, aS);
                terms.Add(new($"P.618 rain, clouds and scintillation at {PropagationMath.F(p)} % of the time", combined));
                notes.Add($"P.618 §2.5 combines rain {PropagationMath.F(ar)} dB, clouds {PropagationMath.F(ac)} dB and scintillation {PropagationMath.F(aS)} dB as √((rain + clouds)² + scintillation²).");
            }
            if (iono)
            {
                double rot = ItuP531.FaradayRotation(fGhz, a.Number("Tec") * 1e16, a.Number("MagneticField") * 1e-6);
                if (a.Word("LinearPolarization") == "yes")
                    terms.Add(new("P.531 Faraday rotation", ItuP531.FaradayLoss(rot)));
                notes.Add($"Faraday rotation {PropagationMath.F(rot * 180 / Math.PI)}°.");
                double s4 = a.Number("S4");
                if (s4 > 0)
                {
                    double s4f = ItuP531.S4At(fGhz, s4, a.Number("S4Frequency"));
                    terms.Add(new($"P.531 ionospheric scintillation at {PropagationMath.F(p)} % of the time", ItuP531.FadeDepth(s4f, p)));
                    notes.Add($"S4 {PropagationMath.F(s4f)} at {PropagationMath.F(fGhz)} GHz: peak-to-peak {PropagationMath.F(ItuP531.PeakToPeak(Math.Min(s4f, 1)))} dB.");
                }
            }
        }
    }
}
