// ================================================================
//  ItuP1411.cs  —  ITU-R P.1411-13 (09/2025), short-range outdoor
//
//  belowroof  §4.1.1, Table 4: both stations below rooftop (street
//             canyons), Lb = 10·α·log10(d) + β + 10·γ·log10(f), d in m,
//             f in GHz, σ by row; the rows' footnotes cap the distance at
//             the highest frequencies.
//  aboveroof  §4.2.1, Table 8: one station above rooftop, the same form.
//  lowheight  §4.3.1: two terminals near street level, 300-3000 MHz, the
//             line-of-sight and non-line-of-sight losses at a location
//             percentage p and the corner distance dLoS(p) between them,
//             joined linearly over a 20 m transition.
//  corner     §4.1.3: around one street corner, both stations below
//             rooftop. 800-2000 MHz: the reflected and diffracted waves
//             of eqs (14)-(18). 2-38 GHz (corner angle 90°): the loss in
//             the line-of-sight street for x1 (§4.1.2) plus the corner
//             loss Lc and the attenuation Latt of eqs (19)-(22).
//
//  The line-of-sight street loss the 2-38 GHz corner model adds to is
//  §4.1.2's median: up to 15 GHz eq (6), with the breakpoint of eq (7)
//  (the effective road height hs; eq (3) when hs = 0) and Lbp of eq (8),
//  or eq (12) where a station is at or below hs and no breakpoint exists;
//  above 15 GHz eq (13) with L0 = 20·log10(f/MHz) − 28 and the stated
//  exponent n (2 by default, free space), gas and rain left to their own
//  terms. Which of §4.1.2's forms applies above 15 GHz is this
//  implementation's choice, said in the result's notes.
// ================================================================

using System;
using System.Collections.Generic;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public enum StreetEnvironment { UrbanHighRise, UrbanLowRise, Suburban, Residential }

    public enum UrbanCategory { Suburban, Urban, DenseUrban }

    public static class ItuP1411
    {
        public const string Document = "ITU-R P.1411-13 (09/2025)";
        private const string M = "P1411";

        private sealed record Row(double FLo, double FHi, double DLo, double DHi, double Alpha, double Beta, double Gamma, double Sigma);

        private static string Env(StreetEnvironment e) => e switch
        {
            StreetEnvironment.UrbanHighRise => "urban high-rise",
            StreetEnvironment.UrbanLowRise => "urban low-rise",
            StreetEnvironment.Suburban => "suburban",
            _ => "residential",
        };

        // ---------------------------------------------------------------- site-general (Tables 4 and 8)

        public static PathLossResult SiteGeneral(bool aboveRoof, StreetEnvironment env, bool los, double fHz, double distance3DM)
        {
            double f = fHz / 1e9, d = distance3DM;
            string where = $"{(aboveRoof ? "above rooftop" : "below rooftop")}, {Env(env)}, {(los ? "line of sight" : "non-line-of-sight")}";
            Row r;
            double cap = double.PositiveInfinity;
            if (!aboveRoof)
            {
                if (los)
                {
                    if (env == StreetEnvironment.Residential)
                        throw new PropagationRefusal($"{M}: Table 4 gives no line-of-sight row for a residential environment below rooftop.");
                    r = new(0.45, 300, 5, 660, 2.07, 31.23, 2.06, 4.91);
                    if (f > 255) cap = 155; else if (f > 159) cap = 250; else if (f > 82) cap = 500;
                }
                else if (env == StreetEnvironment.UrbanHighRise)
                {
                    r = new(0.8, 159, 20, 715, 3.73, 16.02, 2.26, 7.62);
                    if (f > 82) cap = 150;
                }
                else if (env == StreetEnvironment.Residential)
                    r = new(0.8, 73, 30, 170, 3.01, 18.8, 2.07, 3.07);
                else
                {
                    r = new(0.45, 255, 10, 250, 4.52, 6.04, 2.14, 8.02);
                    if (f > 159) cap = 80; else if (f > 73) cap = 150;
                }
            }
            else
            {
                if (los)
                {
                    if (env == StreetEnvironment.Residential)
                        throw new PropagationRefusal($"{M}: Table 8 gives no row for a residential environment above rooftop.");
                    r = new(2.2, 73, 55, 1200, 2.29, 28.6, 1.96, 3.48);
                }
                else if (env == StreetEnvironment.UrbanHighRise)
                    r = new(2.2, 66.5, 260, 1200, 4.39, -6.27, 2.30, 6.89);
                else
                    throw new PropagationRefusal($"{M}: Table 8 gives a non-line-of-sight row above rooftop for urban high-rise only, not {Env(env)}.");
            }
            PropagationMath.Range(M, $"the frequency ({where})", f, r.FLo, r.FHi, "GHz");
            PropagationMath.Range(M, $"the 3D distance ({where}, at {PropagationMath.Ghz(fHz)})", d, r.DLo, Math.Min(r.DHi, cap), "m");
            double lb = 10 * r.Alpha * Math.Log10(d) + r.Beta + 10 * r.Gamma * Math.Log10(f);
            double fs = PropagationMath.FreeSpaceDb(fHz, d);
            return new PathLossResult(M, Document, lb, new[] { new LossTerm("free space", fs), new LossTerm($"P.1411 {where} excess", lb - fs) }, r.Sigma);
        }

        // ---------------------------------------------------------------- terminals near street level (§4.3.1)

        /// <summary>The corner distance dLoS(p) of eq (64), m.</summary>
        public static double CornerDistance(double pPercent)
        {
            double q = pPercent / 100, lq = Math.Log10(q);
            return pPercent < 45 ? 212 * lq * lq - 64 * lq : 79.2 - 70 * q;
        }

        /// <param name="cornerDistanceM">The corner distance when it is known; NaN takes eq (64)'s.</param>
        public static PathLossResult LowHeight(UrbanCategory urban, double fHz, double distanceM, double pPercent, double cornerDistanceM = double.NaN)
        {
            double f = fHz / 1e6;
            PropagationMath.Range(M, "the frequency (terminals near street level)", f, 300, 3000, "MHz");
            PropagationMath.Range(M, "the distance (terminals near street level)", distanceM, 1e-3, 3000, "m");
            PropagationMath.Range(M, "the location percentage", pPercent, 1, 99, "%");
            const double sigma = 7, w = 20;
            double lUrban = urban switch { UrbanCategory.Urban => 6.8, UrbanCategory.DenseUrban => 2.3, _ => 0 };
            double LosAt(double d) => 32.45 + 20 * Math.Log10(f) + 20 * Math.Log10(d / 1000)
                                      + 1.5624 * sigma * (Math.Sqrt(-2 * Math.Log(1 - pPercent / 100)) - 1.1774);
            double NlosAt(double d) => 9.5 + 45 * Math.Log10(f) + 40 * Math.Log10(d / 1000) + lUrban
                                       + sigma * Normal.InverseCdf(pPercent / 100);
            double dLos = double.IsNaN(cornerDistanceM) ? CornerDistance(pPercent) : cornerDistanceM;
            double d0 = distanceM;
            double l = d0 < dLos ? LosAt(d0)
                     : d0 > dLos + w ? NlosAt(d0)
                     : LosAt(dLos) + (NlosAt(dLos + w) - LosAt(dLos)) * (d0 - dLos) / w;
            string region = d0 < dLos ? "line of sight" : d0 > dLos + w ? "non-line-of-sight" : "corner transition";
            double fs = PropagationMath.FreeSpaceDb(fHz, d0);
            var notes = new List<string> { $"Terminals near street level at {PropagationMath.F(pPercent)} % of locations: corner distance {PropagationMath.F(dLos)} m, {region}." };
            return new PathLossResult(M, Document, l, new[] { new LossTerm("free space", fs), new LossTerm($"P.1411 street level, {region}", l - fs) },
                                      sigma, Notes: notes);
        }

        // ---------------------------------------------------------------- around a corner (§4.1.2, §4.1.3)

        /// <summary>§4.1.2's median loss in a line-of-sight street at distance d (see the file header for the form
        /// used in each band).</summary>
        public static double LosStreetMedian(double fHz, double d, double h1, double h2, double hs, double exponentAbove15GHz)
        {
            double lambda = PropagationMath.C / fHz;
            if (fHz > 15e9)
                return 20 * Math.Log10(fHz / 1e6) - 28 + 10 * exponentAbove15GHz * Math.Log10(d / 1.0);
            if (h1 > hs && h2 > hs)
            {
                double rbp = 4 * (h1 - hs) * (h2 - hs) / lambda;
                double lbp = Math.Abs(20 * Math.Log10(lambda * lambda / (8 * Math.PI * (h1 - hs) * (h2 - hs))));
                return lbp + 6 + (d <= rbp ? 20 * Math.Log10(d / rbp) : 40 * Math.Log10(d / rbp));
            }
            const double rs = 20;
            double ls = Math.Abs(20 * Math.Log10(lambda / (2 * Math.PI * rs)));
            if (d < rs)
                throw new PropagationRefusal($"{M}: with a station at or below the effective road height the street loss is given from {rs} m; x1 is {PropagationMath.F(d)} m.");
            return ls + 6 + 30 * Math.Log10(d / rs);
        }

        /// <param name="x1">Distance along the line-of-sight street to the crossing's centre line, m.</param>
        /// <param name="x2">Distance from the crossing along the second street, m.</param>
        /// <param name="w1">Width of the line-of-sight street, m.</param>
        /// <param name="w2">Width of the second street, m.</param>
        /// <param name="cornerDeg">The corner angle, degrees.</param>
        public static PathLossResult Corner(double fHz, double x1, double x2, double w1, double w2, double cornerDeg,
                                            bool residential, bool chamfered, double h1, double h2, double hs, double exponentAbove15GHz)
        {
            double f = fHz / 1e6, lambda = PropagationMath.C / fHz;
            foreach (var (what, v) in new[] { ("x1", x1), ("x2", x2), ("w1", w1), ("w2", w2) })
                PropagationMath.Positive(M, what, v, "m");
            var notes = new List<string>();
            double l;
            string term;
            if (f >= 800 && f <= 2000)
            {
                double a = cornerDeg * Math.PI / 180;
                if (!(a > 0.6 && a < Math.PI))
                    throw new PropagationRefusal($"{M}: the corner angle must be between {PropagationMath.F(0.6 * 180 / Math.PI)}° and 180° (exclusive); it is {PropagationMath.F(cornerDeg)}°.");
                double fa = 3.86 / Math.Pow(a, 3.5);
                double lr = 20 * Math.Log10(x1 + x2) + x1 * x2 * fa / (w1 * w2) + 20 * Math.Log10(4 * Math.PI / lambda);
                double da = 40 / (2 * Math.PI) * (Math.Atan(x2 / w2) + Math.Atan(x1 / w1) - Math.PI / 2);
                double ld = 10 * Math.Log10(x1 * x2 * (x1 + x2)) + 2 * da - 0.1 * (90 - cornerDeg) + 20 * Math.Log10(4 * Math.PI / lambda);
                // Eq (14) as printed, 10·log10(10^(−Lr/10) + 10^(−Ld/10)), is the power sum of the two waves as a GAIN
                // (negative dB); the loss is its negative.
                l = -10 * Math.Log10(Math.Pow(10, -lr / 10) + Math.Pow(10, -ld / 10));
                term = "P.1411 corner, reflected and diffracted";
            }
            else if (f > 2000 && f <= 38000)
            {
                if (Math.Abs(cornerDeg - 90) > 1e-9)
                    throw new PropagationRefusal($"{M}: above 2 GHz the corner model is for a 90° corner; it is {PropagationMath.F(cornerDeg)}°.");
                if (!(x1 > 20)) throw new PropagationRefusal($"{M}: above 2 GHz the line-of-sight street distance x1 must exceed 20 m; it is {PropagationMath.F(x1)} m.");
                if (!(x2 > w1 / 2 + 1))
                    throw new PropagationRefusal($"{M}: above 2 GHz x2 must exceed w1/2 + 1 = {PropagationMath.F(w1 / 2 + 1)} m (beyond the start of the corner region); it is {PropagationMath.F(x2)} m.");
                const double dCorner = 30;
                double lCorner = residential ? 30 : 20;
                double beta = chamfered && !residential ? 4.2 + (1.4 * Math.Log10(f) - 7.8) * (0.8 * Math.Log10(x1) - 1.0) : 6;
                if (chamfered && residential) notes.Add("A chamfered corner is given for urban environments only; β = 6 is used.");
                double edge = w1 / 2 + 1 + dCorner;
                double lc = x2 <= edge ? lCorner / Math.Log10(1 + dCorner) * Math.Log10(x2 - w1 / 2) : lCorner;
                double latt = x2 > edge ? 10 * beta * Math.Log10((x1 + x2) / (x1 + w1 / 2 + dCorner)) : 0;
                double lLos = LosStreetMedian(fHz, x1, h1, h2, hs, exponentAbove15GHz);
                if (fHz > 15e9) notes.Add($"The line-of-sight street loss above 15 GHz is P.1411 eq (13) with exponent {PropagationMath.F(exponentAbove15GHz)}.");
                double fs1 = PropagationMath.FreeSpaceDb(fHz, x1 + x2);
                var terms2 = new[]
                {
                    new LossTerm("free space", fs1),
                    new LossTerm("P.1411 line-of-sight street excess", lLos - fs1),
                    new LossTerm("P.1411 corner loss", lc),
                    new LossTerm("P.1411 corner attenuation", latt),
                };
                return new PathLossResult(M, Document, lLos + lc + latt, terms2, 0, Notes: notes);
            }
            else
                throw new PropagationRefusal($"{M}: the corner model covers 800 to 38000 MHz; the frequency is {PropagationMath.F(f)} MHz.");
            double fs = PropagationMath.FreeSpaceDb(fHz, x1 + x2);
            return new PathLossResult(M, Document, l, new[] { new LossTerm("free space", fs), new LossTerm(term, l - fs) }, 0, Notes: notes);
        }
    }
}
