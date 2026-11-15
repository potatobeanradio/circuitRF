// ================================================================
//  Tr38901.cs  —  3GPP TR 38.901 scenario path loss, line-of-sight
//  probability and large-scale parameter statistics
//
//  Transcribed from 3GPP TR 38.901 version 18.0.0 (ETSI TR 138 901
//  V18.0.0, 2024-05): Table 7.4.1-1 (path loss and shadow fading),
//  Table 7.4.2-1 (line-of-sight probability), Table 7.5-6 parts 1-3 and
//  Tables 7.5-7 to 7.5-11 (delay, angle and zenith spreads, K), Tables
//  7.2-4 and 7.8-7 (the InF clutter and hall defaults).
//
//  In the report fc is in GHz and distances are in metres; so here.
//
//  Choices this transcription makes, each said in a result's notes:
//    · UMa's effective environment height hE is 1 m. The report draws it
//      per link (1 m with probability 1/(1 + C(d2D, hUT)), otherwise from
//      12, 15, …, hUT − 1.5); a median model takes the 1 m branch, and
//      drawing it is the drop's business.
//    · Los=auto gives the loss of the more probable state, with the
//      probability; drawing the state is the drop's business.
//    · The InF line-of-sight σSF is 4.3 dB: the cell is clipped to "4."
//      in this edition's table and reads 4.3 in the report's earlier
//      editions (src/RfCore/RESOLVED.md).
//    · O2I (outdoor-to-indoor) columns are not transcribed: building
//      entry is ITU-R P.2109's, an added loss.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Propagation
{
    public enum Scenario38901 { RMa, UMa, UMi, InHMixed, InHOpen, InFSL, InFDL, InFSH, InFDH, InFHH }

    public enum LosState { Auto, Los, Nlos }

    /// <summary>The InF scenario's clutter and hall (Table 7.2-4; defaults from Table 7.8-7).</summary>
    public sealed record InfClutter(double Density, double HeightM, double SizeM, double HallLengthM, double HallWidthM, double HallHeightM)
    {
        /// <summary>V/S: hall volume over its total surface (walls, floor, ceiling), m.</summary>
        public double VolumeToSurface
        {
            get
            {
                double l = HallLengthM, w = HallWidthM, h = HallHeightM;
                return l * w * h / (2 * (l * w + l * h + w * h));
            }
        }

        /// <summary>The report's calibration values for a sub-scenario: low clutter density (SL, SH, and HH here) 20 %,
        /// 2 m high, 10 m in size; high (DL, DH) 60 %, 6 m, 2 m; halls 120 × 60 m (SL, DH, HH) or 300 × 150 m (DL, SH),
        /// 10 m high.</summary>
        public static InfClutter Default(Scenario38901 s) => s switch
        {
            Scenario38901.InFDL => new(0.6, 6, 2, 300, 150, 10),
            Scenario38901.InFDH => new(0.6, 6, 2, 120, 60, 10),
            Scenario38901.InFSH => new(0.2, 2, 10, 300, 150, 10),
            _ => new(0.2, 2, 10, 120, 60, 10),
        };
    }

    public static class Tr38901
    {
        public const string Document = "3GPP TR 38.901 V18.0.0 (2024-05)";
        private const double C = 3.0e8;   // the report's c for breakpoint distances

        public static string Name(Scenario38901 s) => s switch
        {
            Scenario38901.InHMixed => "InH-Mixed",
            Scenario38901.InHOpen => "InH-Open",
            Scenario38901.InFSL => "InF-SL",
            Scenario38901.InFDL => "InF-DL",
            Scenario38901.InFSH => "InF-SH",
            Scenario38901.InFDH => "InF-DH",
            Scenario38901.InFHH => "InF-HH",
            _ => s.ToString(),
        };

        public static bool TryParse(string text, out Scenario38901 s)
        {
            foreach (Scenario38901 v in Enum.GetValues<Scenario38901>())
                if (string.Equals(Name(v), text?.Trim(), StringComparison.OrdinalIgnoreCase)) { s = v; return true; }
            s = default;
            return false;
        }

        private static bool IsInF(Scenario38901 s) => s is Scenario38901.InFSL or Scenario38901.InFDL or Scenario38901.InFSH
                                                         or Scenario38901.InFDH or Scenario38901.InFHH;
        private static bool IsInH(Scenario38901 s) => s is Scenario38901.InHMixed or Scenario38901.InHOpen;

        // ---------------------------------------------------------------- path loss (Table 7.4.1-1)

        /// <summary>Path loss and σSF for one propagation state. d2D is the horizontal distance; hBS, hUT the antenna
        /// heights; <paramref name="w"/> and <paramref name="h"/> RMa's street width and building height.</summary>
        public static (double Pl, double Sigma) PathLoss(Scenario38901 s, bool los, double fHz, double d2D, double hBS, double hUT,
                                                         double w = 20, double h = 5)
        {
            string m = "38901 " + Name(s);
            double fc = fHz / 1e9;
            double d3D = Math.Sqrt(d2D * d2D + (hBS - hUT) * (hBS - hUT));
            PropagationMath.Range(m, "the frequency", fc, 0.5, s == Scenario38901.RMa ? 30 : 100, "GHz");

            switch (s)
            {
                case Scenario38901.RMa:
                {
                    PropagationMath.Range(m, "the street width W", w, 5, 50, "m");
                    PropagationMath.Range(m, "the building height h", h, 5, 50, "m");
                    PropagationMath.Range(m, "the BS height", hBS, 10, 150, "m");
                    PropagationMath.Range(m, "the UT height", hUT, 1, 10, "m");
                    PropagationMath.Range(m, los ? "the 2D distance (line of sight)" : "the 2D distance (non-line-of-sight)",
                                          d2D, 10, los ? 10_000 : 5_000, "m");
                    double dBP = 2 * Math.PI * hBS * hUT * fHz / C;
                    double Pl1(double d) => 20 * Math.Log10(40 * Math.PI * d * fc / 3) + Math.Min(0.03 * Math.Pow(h, 1.72), 10) * Math.Log10(d)
                                            - Math.Min(0.044 * Math.Pow(h, 1.72), 14.77) + 0.002 * Math.Log10(h) * d;
                    double plLos = d2D <= dBP ? Pl1(d3D) : Pl1(dBP) + 40 * Math.Log10(d3D / dBP);
                    double sigLos = d2D <= dBP ? 4 : 6;
                    if (los) return (plLos, sigLos);
                    double plN = 161.04 - 7.1 * Math.Log10(w) + 7.5 * Math.Log10(h)
                                 - (24.37 - 3.7 * (h / hBS) * (h / hBS)) * Math.Log10(hBS)
                                 + (43.42 - 3.1 * Math.Log10(hBS)) * (Math.Log10(d3D) - 3)
                                 + 20 * Math.Log10(fc) - (3.2 * Math.Pow(Math.Log10(11.75 * hUT), 2) - 4.97);
                    return (Math.Max(plLos, plN), 8);
                }
                case Scenario38901.UMa:
                case Scenario38901.UMi:
                {
                    bool uma = s == Scenario38901.UMa;
                    PropagationMath.Range(m, "the UT height", hUT, 1.5, 22.5, "m");
                    PropagationMath.Range(m, "the 2D distance", d2D, 10, 5_000, "m");
                    const double hE = 1.0;
                    double dBP = 4 * (hBS - hE) * (hUT - hE) * fHz / C;
                    double plLos;
                    if (d2D <= dBP)
                        plLos = uma ? 28.0 + 22 * Math.Log10(d3D) + 20 * Math.Log10(fc)
                                    : 32.4 + 21 * Math.Log10(d3D) + 20 * Math.Log10(fc);
                    else
                        plLos = uma ? 28.0 + 40 * Math.Log10(d3D) + 20 * Math.Log10(fc) - 9 * Math.Log10(dBP * dBP + (hBS - hUT) * (hBS - hUT))
                                    : 32.4 + 40 * Math.Log10(d3D) + 20 * Math.Log10(fc) - 9.5 * Math.Log10(dBP * dBP + (hBS - hUT) * (hBS - hUT));
                    if (los) return (plLos, 4);
                    double plN = uma ? 13.54 + 39.08 * Math.Log10(d3D) + 20 * Math.Log10(fc) - 0.6 * (hUT - 1.5)
                                     : 35.3 * Math.Log10(d3D) + 22.4 + 21.3 * Math.Log10(fc) - 0.3 * (hUT - 1.5);
                    return (Math.Max(plLos, plN), uma ? 6 : 7.82);
                }
                case Scenario38901.InHMixed:
                case Scenario38901.InHOpen:
                {
                    PropagationMath.Range(m, "the 3D distance", d3D, 1, 150, "m");
                    double plLos = 32.4 + 17.3 * Math.Log10(d3D) + 20 * Math.Log10(fc);
                    if (los) return (plLos, 3);
                    return (Math.Max(plLos, 38.3 * Math.Log10(d3D) + 17.30 + 24.9 * Math.Log10(fc)), 8.03);
                }
                default:
                {
                    PropagationMath.Range(m, "the 3D distance", d3D, 1, 600, "m");
                    double plLos = 31.84 + 21.50 * Math.Log10(d3D) + 19.00 * Math.Log10(fc);
                    if (los || s == Scenario38901.InFHH) return (plLos, 4.3);
                    double lf = 20 * Math.Log10(fc), ld = Math.Log10(d3D);
                    double sl = 33 + 25.5 * ld + lf;
                    return s switch
                    {
                        Scenario38901.InFSL => (Math.Max(sl, plLos), 5.7),
                        Scenario38901.InFDL => (Math.Max(Math.Max(18.6 + 35.7 * ld + lf, plLos), sl), 7.2),
                        Scenario38901.InFSH => (Math.Max(32.4 + 23.0 * ld + lf, plLos), 5.9),
                        _ => (Math.Max(33.63 + 21.9 * ld + lf, plLos), 4.0),
                    };
                }
            }
        }

        // ---------------------------------------------------------------- line of sight (Table 7.4.2-1)

        /// <summary>The probability of line of sight at horizontal distance d2D.</summary>
        public static double LosProbability(Scenario38901 s, double d2D, double hBS, double hUT, InfClutter? clutter = null)
        {
            switch (s)
            {
                case Scenario38901.RMa:
                    return d2D <= 10 ? 1 : Math.Exp(-(d2D - 10) / 1000);
                case Scenario38901.UMi:
                    return d2D <= 18 ? 1 : 18 / d2D + Math.Exp(-d2D / 36) * (1 - 18 / d2D);
                case Scenario38901.UMa:
                {
                    if (d2D <= 18) return 1;
                    double cp = hUT <= 13 ? 0 : Math.Pow((hUT - 13) / 10, 1.5);
                    return (18 / d2D + Math.Exp(-d2D / 63) * (1 - 18 / d2D))
                           * (1 + cp * 5.0 / 4.0 * Math.Pow(d2D / 100, 3) * Math.Exp(-d2D / 150));
                }
                case Scenario38901.InHMixed:
                    if (d2D <= 1.2) return 1;
                    return d2D < 6.5 ? Math.Exp(-(d2D - 1.2) / 4.7) : Math.Exp(-(d2D - 6.5) / 32.6) * 0.32;
                case Scenario38901.InHOpen:
                    if (d2D <= 5) return 1;
                    return d2D <= 49 ? Math.Exp(-(d2D - 5) / 70.8) : Math.Exp(-(d2D - 49) / 211.7) * 0.54;
                case Scenario38901.InFHH:
                    return 1;
                default:
                {
                    var c = clutter ?? InfClutter.Default(s);
                    string m = "38901 " + Name(s);
                    if (!(c.Density > 0 && c.Density < 1))
                        throw new PropagationRefusal($"{m}: the clutter density must be between 0 and 1 (exclusive); it is {PropagationMath.F(c.Density)}.");
                    PropagationMath.Positive(m, "the clutter size", c.SizeM, "m");
                    double k = -c.SizeM / Math.Log(1 - c.Density);
                    if (s is Scenario38901.InFSH or Scenario38901.InFDH)
                    {
                        if (!(c.HeightM > hUT))
                            throw new PropagationRefusal(
                                $"{m}: the clutter height ({PropagationMath.F(c.HeightM)} m) must exceed the UT height ({PropagationMath.F(hUT)} m): the UT is clutter-embedded in this sub-scenario.");
                        k *= (hBS - hUT) / (c.HeightM - hUT);
                    }
                    return Math.Exp(-d2D / k);
                }
            }
        }

        // ---------------------------------------------------------------- spreads (Tables 7.5-6 to 7.5-11)

        /// <summary>The large-scale parameter statistics for one state, or null where the report gives none at this
        /// frequency (RMa's channel parameters stop at 7 GHz).</summary>
        public static SpreadStatistics? Spreads(Scenario38901 s, bool los, double fHz, double d2D, double hBS, double hUT, InfClutter? clutter = null)
        {
            double fc = fHz / 1e9;
            static Statistic S(double mu, double sigma) => new(mu, sigma);
            switch (s)
            {
                case Scenario38901.UMa:
                {
                    double f = Math.Max(fc, 6), lf = Math.Log10(f);
                    return los
                        ? new(S(-6.955 - 0.0963 * lf, 0.66), S(1.06 + 0.1114 * lf, 0.28), S(1.81, 0.20), S(0.95, 0.16),
                              S(Math.Max(-0.5, -2.1 * (d2D / 1000) - 0.01 * (hUT - 1.5) + 0.75), 0.40), S(9, 3.5))
                        : new(S(-6.28 - 0.204 * lf, 0.39), S(1.5 - 0.1144 * lf, 0.28), S(2.08 - 0.27 * lf, 0.11), S(-0.3236 * lf + 1.512, 0.16),
                              S(Math.Max(-0.5, -2.1 * (d2D / 1000) - 0.01 * (hUT - 1.5) + 0.9), 0.49), null);
                }
                case Scenario38901.UMi:
                {
                    double l1 = Math.Log10(1 + Math.Max(fc, 2));
                    return los
                        ? new(S(-0.24 * l1 - 7.14, 0.38), S(-0.05 * l1 + 1.21, 0.41), S(-0.08 * l1 + 1.73, 0.014 * l1 + 0.28),
                              S(-0.1 * l1 + 0.73, -0.04 * l1 + 0.34),
                              S(Math.Max(-0.21, -14.8 * (d2D / 1000) + 0.01 * Math.Abs(hUT - hBS) + 0.83), 0.35), S(9, 5))
                        : new(S(-0.24 * l1 - 6.83, 0.16 * l1 + 0.28), S(-0.23 * l1 + 1.53, 0.11 * l1 + 0.33), S(-0.08 * l1 + 1.81, 0.05 * l1 + 0.3),
                              S(-0.04 * l1 + 0.92, -0.07 * l1 + 0.41),
                              S(Math.Max(-0.5, -3.1 * (d2D / 1000) + 0.01 * Math.Max(hUT - hBS, 0) + 0.2), 0.35), null);
                }
                case Scenario38901.RMa:
                    if (fc > 7) return null;
                    return los
                        ? new(S(-7.49, 0.55), S(0.90, 0.38), S(1.52, 0.24), S(0.47, 0.40),
                              S(Math.Max(-1, -0.17 * (d2D / 1000) - 0.01 * (hUT - 1.5) + 0.22), 0.34), S(7, 4))
                        : new(S(-7.43, 0.48), S(0.95, 0.45), S(1.52, 0.13), S(0.58, 0.37),
                              S(Math.Max(-1, -0.19 * (d2D / 1000) - 0.01 * (hUT - 1.5) + 0.28), 0.30), null);
                case Scenario38901.InHMixed:
                case Scenario38901.InHOpen:
                {
                    double l1 = Math.Log10(1 + Math.Max(fc, 6));
                    return los
                        ? new(S(-0.01 * l1 - 7.692, 0.18), S(1.60, 0.18), S(-0.19 * l1 + 1.781, 0.12 * l1 + 0.119),
                              S(-0.26 * l1 + 1.44, -0.04 * l1 + 0.264), S(-1.43 * l1 + 2.228, 0.13 * l1 + 0.30), S(7, 4))
                        : new(S(-0.28 * l1 - 7.173, 0.10 * l1 + 0.055), S(1.62, 0.25), S(-0.11 * l1 + 1.863, 0.12 * l1 + 0.059),
                              S(-0.15 * l1 + 1.387, -0.09 * l1 + 0.746), S(1.08, 0.36), null);
                }
                default:
                {
                    double vs = (clutter ?? InfClutter.Default(s)).VolumeToSurface, l1 = Math.Log10(1 + fc);
                    return los || s == Scenario38901.InFHH
                        ? new(S(Math.Log10(26 * vs + 14) - 9.35, 0.15), S(1.56, 0.25), S(-0.18 * l1 + 1.78, 0.12 * l1 + 0.2),
                              S(-0.2 * l1 + 1.5, 0.35), S(1.35, 0.35), S(7, 8))
                        : new(S(Math.Log10(30 * vs + 32) - 9.44, 0.19), S(1.57, 0.2), S(1.72, 0.3),
                              S(-0.13 * l1 + 1.45, 0.45), S(1.2, 0.55), null);
                }
            }
        }

        // ---------------------------------------------------------------- the model

        /// <summary>The scenario's loss, σ, line-of-sight probability and spreads. <paramref name="state"/> Auto takes the
        /// more probable state.</summary>
        public static PathLossResult Evaluate(Scenario38901 s, LosState state, double fHz, double d2D, double hBS, double hUT,
                                              double w = 20, double h = 5, InfClutter? clutter = null)
        {
            var notes = new List<string>();
            double pLos = LosProbability(s, d2D, hBS, hUT, clutter);
            bool los = state switch
            {
                LosState.Los => true,
                LosState.Nlos => false,
                _ => pLos >= 0.5,
            };
            if (s == Scenario38901.InFHH && !los)
                throw new PropagationRefusal("38901 InF-HH: the sub-scenario is line of sight only (its probability is 1); Los=nlos has no model.");
            if (state == LosState.Auto)
                notes.Add($"Los=auto: the line-of-sight probability is {PropagationMath.F(pLos)}, so the {(los ? "line-of-sight" : "non-line-of-sight")} loss is given.");
            if (s == Scenario38901.UMa)
                notes.Add("UMa's effective environment height hE is taken as 1 m (the report draws it per link).");
            if (s is Scenario38901.UMa && Math.Abs(hBS - 25) > 1e-9 || s is Scenario38901.UMi && Math.Abs(hBS - 10) > 1e-9)
                notes.Add($"The report's {Name(s)} model was derived for a BS height of {(s == Scenario38901.UMa ? 25 : 10)} m; this BS is {PropagationMath.F(hBS)} m.");

            var (pl, sigma) = PathLoss(s, los, fHz, d2D, hBS, hUT, w, h);
            var spreads = Spreads(s, los, fHz, d2D, hBS, hUT, clutter);
            if (spreads is null) notes.Add("The report gives RMa's channel parameters up to 7 GHz only: no spreads above it.");

            double d3D = Math.Sqrt(d2D * d2D + (hBS - hUT) * (hBS - hUT));
            double fs = PropagationMath.FreeSpaceDb(fHz, d3D);
            string label = $"38901 {Name(s)} {(los ? "line of sight" : "non-line-of-sight")}";
            return new PathLossResult("38901", Document, pl,
                new[] { new LossTerm("free space", fs), new LossTerm(label + " excess", pl - fs) }, sigma, pLos, spreads, notes);
        }
    }
}
