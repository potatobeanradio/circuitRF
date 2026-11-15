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
            return (terms, notes);
        }
    }
}
