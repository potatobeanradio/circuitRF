// ================================================================
//  TdlProfiles.cs  —  the tapped-delay-line profiles a Propagation Channel embeds (brief PCH-7 R-pch7-4, note §4.1)
//
//  Each table is transcribed from its document, edition stated, every row as printed:
//
//    TDL-A … TDL-E        3GPP TR 38.901 V18.0.0 (2024-05), Tables 7.7.2-1 … 7.7.2-5: normalized delays, scaled by
//                         DelaySpread (§7.7.3); TDL-D and TDL-E carry a line-of-sight tap (K1 13.3 dB and 22 dB)
//    EPA, EVA, ETU        3GPP TS 36.104 V18.6.0 (2025-01), Annex B.2, Tables B.2-1 … B.2-3: delays in ns
//    PedA, PedB, VehA,    Rec. ITU-R M.1225 (02/1997), Tables 4 and 5 (outdoor-to-indoor and pedestrian, vehicular
//    VehB                 high antenna), channels A and B: delays in ns
//    NTN-TDL-A … D        3GPP TR 38.811 V15.4.0 (2020-09), Tables 6.9.2-1 … 6.9.2-4: normalized delays; NTN-TDL-C and
//                         NTN-TDL-D carry a line-of-sight tap (K1 10.224 dB and 11.707 dB)
//
//  Every tap's Doppler spectrum is classical in all four documents (38.811 refers to 38.901 §7.7.2).
//
//  What a channel does with a table:
//
//   1. K (a line-of-sight profile, KFactor stated): the Rayleigh entries' powers move by K_model − K_desired dB,
//      K_model = P_LOS − 10 log10 Σ 10^(P_n/10) over the Rayleigh entries (TR 38.901 §7.7.6, eq. 7.7.6-1/-2).
//   2. The line-of-sight entry and the Rayleigh entry at its delay become ONE Rician tap: power their sum, K their ratio
//      (the tables' note: "the first tap follows a Ricean distribution with a K-factor of K1 and a mean power of 0 dB").
//   3. The powers are divided by their sum, so they sum to 1: the large-scale model carries the mean power.
//   4. Delays. A normalized table is divided by its own RMS delay spread and multiplied by DelaySpread, so the spread is
//      DelaySpread exactly. That is §7.7.6's renormalization ("divide the delays by that value to obtain DS = 1"); the
//      printed tables' rounding leaves their own spread a few parts in 10⁴ off 1, and the note says by how much. A table
//      in ns is used as printed, unless DelaySpread is stated, when it is scaled the same way.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RfCore.Propagation
{
    /// <summary>One printed row of a profile: its delay (normalized, or ns), its power (dB) and whether it is the line-of-sight
    /// entry.</summary>
    public readonly record struct TdlEntry(double Delay, double PowerDb, bool Los = false);

    /// <summary>A profile as printed: its name, its document and table, whether its delays are normalized, its rows, and the
    /// K1 its note states (dB) for a line-of-sight profile.</summary>
    public sealed record TdlProfile(string Name, string Document, bool Normalized, IReadOnlyList<TdlEntry> Entries, double? NoteK1Db = null)
    {
        public bool HasLos => Entries.Any(e => e.Los);
    }

    /// <summary>One tap of a resolved profile: its delay (s), its share of the power (they sum to 1) and its Rician K (linear;
    /// null for a Rayleigh tap).</summary>
    public readonly record struct TdlTap(double DelayS, double Power, double? K);

    /// <summary>A resolved profile: its taps, the factor its delays were divided by (1 for an absolute table used as printed),
    /// the sum its powers had before they were normalized, and what was done, in sentences.</summary>
    public sealed record TdlRealization(TdlProfile Profile, IReadOnlyList<TdlTap> Taps, double DelayRescale, double PowerSum,
                                        IReadOnlyList<string> Notes);

    public static class TdlProfiles
    {
        private const string D38901 = "3GPP TR 38.901 V18.0.0 (2024-05)";
        private const string D36104 = "3GPP TS 36.104 V18.6.0 (2025-01)";
        private const string DM1225 = "Rec. ITU-R M.1225 (02/1997)";
        private const string D38811 = "3GPP TR 38.811 V15.4.0 (2020-09)";

        private static TdlEntry[] Rows(params double[] dp)
        {
            var r = new TdlEntry[dp.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new TdlEntry(dp[2 * i], dp[2 * i + 1]);
            return r;
        }

        private static TdlEntry[] WithLos(double losDb, params double[] dp)
            => [new TdlEntry(0, losDb, true), .. Rows(dp)];

        /// <summary>Every embedded profile.</summary>
        public static readonly IReadOnlyList<TdlProfile> All =
        [
            new("TDL-A", D38901 + ", Table 7.7.2-1", true, Rows(
                0.0000, -13.4, 0.3819, 0, 0.4025, -2.2, 0.5868, -4, 0.4610, -6, 0.5375, -8.2, 0.6708, -9.9, 0.5750, -10.5,
                0.7618, -7.5, 1.5375, -15.9, 1.8978, -6.6, 2.2242, -16.7, 2.1718, -12.4, 2.4942, -15.2, 2.5119, -10.8,
                3.0582, -11.3, 4.0810, -12.7, 4.4579, -16.2, 4.5695, -18.3, 4.7966, -18.9, 5.0066, -16.6, 5.3043, -19.9,
                9.6586, -29.7)),
            new("TDL-B", D38901 + ", Table 7.7.2-2", true, Rows(
                0.0000, 0, 0.1072, -2.2, 0.2155, -4, 0.2095, -3.2, 0.2870, -9.8, 0.2986, -1.2, 0.3752, -3.4, 0.5055, -5.2,
                0.3681, -7.6, 0.3697, -3, 0.5700, -8.9, 0.5283, -9, 1.1021, -4.8, 1.2756, -5.7, 1.5474, -7.5, 1.7842, -1.9,
                2.0169, -7.6, 2.8294, -12.2, 3.0219, -9.8, 3.6187, -11.4, 4.1067, -14.9, 4.2790, -9.2, 4.7834, -11.3)),
            new("TDL-C", D38901 + ", Table 7.7.2-3", true, Rows(
                0, -4.4, 0.2099, -1.2, 0.2219, -3.5, 0.2329, -5.2, 0.2176, -2.5, 0.6366, 0, 0.6448, -2.2, 0.6560, -3.9,
                0.6584, -7.4, 0.7935, -7.1, 0.8213, -10.7, 0.9336, -11.1, 1.2285, -5.1, 1.3083, -6.8, 2.1704, -8.7,
                2.7105, -13.2, 4.2589, -13.9, 4.6003, -13.9, 5.4902, -15.8, 5.6077, -17.1, 6.3065, -16, 6.6374, -15.7,
                7.0427, -21.6, 8.6523, -22.8)),
            new("TDL-D", D38901 + ", Table 7.7.2-4", true, WithLos(-0.2,
                0, -13.5, 0.035, -18.8, 0.612, -21, 1.363, -22.8, 1.405, -17.9, 1.804, -20.1, 2.596, -21.9, 1.775, -22.9,
                4.042, -27.8, 7.937, -23.6, 9.424, -24.8, 9.708, -30.0, 12.525, -27.7), 13.3),
            new("TDL-E", D38901 + ", Table 7.7.2-5", true, WithLos(-0.03,
                0, -22.03, 0.5133, -15.8, 0.5440, -18.1, 0.5630, -19.8, 0.5440, -22.9, 0.7112, -22.4, 1.9092, -18.6,
                1.9293, -20.8, 1.9589, -22.6, 2.6426, -22.3, 3.7136, -25.6, 5.4524, -20.2, 12.0034, -29.8, 20.6519, -29.2), 22),

            new("EPA", D36104 + ", Table B.2-1", false, Rows(0, 0.0, 30, -1.0, 70, -2.0, 90, -3.0, 110, -8.0, 190, -17.2, 410, -20.8)),
            new("EVA", D36104 + ", Table B.2-2", false, Rows(
                0, 0.0, 30, -1.5, 150, -1.4, 310, -3.6, 370, -0.6, 710, -9.1, 1090, -7.0, 1730, -12.0, 2510, -16.9)),
            new("ETU", D36104 + ", Table B.2-3", false, Rows(
                0, -1.0, 50, -1.0, 120, -1.0, 200, 0.0, 230, 0.0, 500, 0.0, 1600, -3.0, 2300, -5.0, 5000, -7.0)),

            new("PedA", DM1225 + ", Table 4, channel A", false, Rows(0, 0, 110, -9.7, 190, -19.2, 410, -22.8)),
            new("PedB", DM1225 + ", Table 4, channel B", false, Rows(0, 0, 200, -0.9, 800, -4.9, 1200, -8.0, 2300, -7.8, 3700, -23.9)),
            new("VehA", DM1225 + ", Table 5, channel A", false, Rows(0, 0.0, 310, -1.0, 710, -9.0, 1090, -10.0, 1730, -15.0, 2510, -20.0)),
            new("VehB", DM1225 + ", Table 5, channel B", false, Rows(0, -2.5, 300, 0, 8900, -12.8, 12900, -10.0, 17100, -25.2, 20000, -16.0)),

            new("NTN-TDL-A", D38811 + ", Table 6.9.2-1", true, Rows(0, 0, 1.0811, -4.675, 2.8416, -6.482)),
            new("NTN-TDL-B", D38811 + ", Table 6.9.2-2", true, Rows(0, 0, 0.7249, -1.973, 0.7410, -4.332, 5.7392, -11.914)),
            new("NTN-TDL-C", D38811 + ", Table 6.9.2-3", true, WithLos(-0.394, 0, -10.618, 14.8124, -23.373), 10.224),
            new("NTN-TDL-D", D38811 + ", Table 6.9.2-4", true, WithLos(-0.284, 0, -11.991, 0.5596, -9.887, 7.3340, -16.771), 11.707),
        ];

        public static TdlProfile? Find(string name)
            => All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The profile names, for a refusal and a picker.</summary>
        public static string Names => string.Join(", ", All.Select(p => p.Name));

        /// <summary>K_model of a line-of-sight table (dB): P_LOS − 10 log10 Σ 10^(P_n/10) over its Rayleigh entries
        /// (TR 38.901 eq. 7.7.6-2).</summary>
        public static double ModelKDb(TdlProfile p)
        {
            double los = p.Entries.First(e => e.Los).PowerDb;
            return los - 10 * Math.Log10(p.Entries.Where(e => !e.Los).Sum(e => Math.Pow(10, e.PowerDb / 10)));
        }

        /// <summary>
        /// <paramref name="profile"/> resolved: with the channel's K (dB, line-of-sight profiles only; null keeps the table's),
        /// and its delays at <paramref name="delaySpreadS"/> (required for a normalized table; for one in ns, null keeps it as
        /// printed). Refuses a K on a profile with no line-of-sight tap, and a normalized table with no spread.
        /// </summary>
        public static TdlRealization Resolve(TdlProfile profile, double? delaySpreadS, double? kFactorDb)
        {
            var inv = CultureInfo.InvariantCulture;
            var notes = new List<string>();
            var entries = profile.Entries.ToList();
            if (kFactorDb is double kd)
            {
                if (!profile.HasLos)
                    throw new PropagationRefusal($"{profile.Name} has no line-of-sight tap, so KFactor does not apply to it; "
                                                 + "TDL-D, TDL-E, NTN-TDL-C and NTN-TDL-D have one.");
                double km = ModelKDb(profile);
                entries = [.. entries.Select(e => e.Los ? e : e with { PowerDb = e.PowerDb - kd + km })];
                notes.Add(string.Format(inv, "{0}: K {1:0.###} dB, its Rayleigh taps moved by {2:0.###} dB from the table's K of {3:0.###} dB "
                                             + "(TR 38.901 §7.7.6).", profile.Name, kd, km - kd, km));
            }
            if (profile.Normalized && delaySpreadS is null)
                throw new PropagationRefusal($"{profile.Name}'s delays are normalized: DelaySpread states the RMS delay spread they are "
                                             + "scaled to (TR 38.901 Table 7.7.3-1 gives 10, 30, 100, 300 and 1000 ns as examples).");
            if (delaySpreadS is double ds && !(ds > 0))
                throw new PropagationRefusal("DelaySpread must be a positive time.");

            // the line-of-sight entry and the Rayleigh entry at its delay become one Rician tap
            var taps = new List<(double Delay, double P, double? K)>();
            var los = entries.FirstOrDefault(e => e.Los);
            bool merged = false;
            foreach (var e in entries)
            {
                if (e.Los) continue;
                double p = Math.Pow(10, e.PowerDb / 10);
                if (profile.HasLos && !merged && e.Delay == los.Delay)
                {
                    double pl = Math.Pow(10, los.PowerDb / 10);
                    taps.Add((e.Delay, pl + p, pl / p));
                    merged = true;
                }
                else taps.Add((e.Delay, p, null));
            }
            double sum = taps.Sum(t => t.P);
            taps = [.. taps.Select(t => (t.Delay, t.P / sum, t.K))];
            if (Math.Abs(10 * Math.Log10(sum)) > 0.005)
                notes.Add(string.Format(inv, "{0}: the taps' powers sum to {1:0.###} dB and are normalized to 0 dB (the large-scale loss "
                                             + "carries the mean).", profile.Name, 10 * Math.Log10(sum)));

            double rms = Rms(taps.Select(t => (t.Delay, t.P)));
            double scale, rescale = 1;
            if (profile.Normalized)
            {
                rescale = rms;
                scale = delaySpreadS!.Value / rms;
                if (Math.Abs(rms - 1) > 1e-6)
                    notes.Add(string.Format(inv, "{0}: its normalized delays have an RMS spread of {1:0.0######}; divided by it, so the spread is "
                                                 + "DelaySpread exactly (TR 38.901 §7.7.6's renormalization).", profile.Name, rms));
            }
            else if (delaySpreadS is double target)
            {
                rescale = rms * 1e-9 / target;
                scale = 1e-9 * target / (rms * 1e-9);
                notes.Add(string.Format(inv, "{0}: its RMS delay spread of {1:0.#} ns scaled to DelaySpread, {2:0.###} ns.",
                                        profile.Name, rms, target * 1e9));
            }
            else scale = 1e-9;
            var result = taps.Select(t => new TdlTap(t.Delay * scale, t.P, t.K)).ToList();
            notes.Insert(0, string.Format(inv, "{0} ({1}): {2} taps, RMS delay spread {3:G4} s.", profile.Name, profile.Document, result.Count,
                                          Rms(result.Select(t => (t.DelayS, t.Power)))));
            return new TdlRealization(profile, result, rescale, sum, notes);
        }

        /// <summary>The power-weighted RMS spread of (delay, power) pairs, in the delays' unit.</summary>
        public static double Rms(IEnumerable<(double Delay, double Power)> taps)
        {
            double p = 0, m1 = 0, m2 = 0;
            foreach (var (d, w) in taps) { p += w; m1 += w * d; m2 += w * d * d; }
            if (!(p > 0)) return 0;
            m1 /= p; m2 /= p;
            return Math.Sqrt(Math.Max(0, m2 - m1 * m1));
        }
    }
}
