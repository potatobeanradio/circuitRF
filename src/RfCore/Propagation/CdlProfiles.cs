// ================================================================
//  CdlProfiles.cs  —  the clustered-delay-line profiles a Propagation Channel embeds (brief PCH-8 R-pch8-5, note §4.2)
//
//  Each table is transcribed from its document, edition stated, every row as printed:
//
//    CDL-A … CDL-E        3GPP TR 38.901 V18.0.0 (2024-05), Tables 7.7.1-1 … 7.7.1-5: per cluster a normalized delay, a power
//                         and the cluster's AOD, AOA, ZOD and ZOA; per profile the cluster spreads c_ASD, c_ASA, c_ZSD, c_ZSA
//                         and the XPR. CDL-D and CDL-E carry a line-of-sight (specular) ray, K1 13.3 dB and 22 dB.
//    NTN-CDL-A … D        3GPP TR 38.811 V15.4.0 (2020-09), Tables 6.9.1-1 … 6.9.1-4, at the reference elevation of 50°
//                         (every ZOD 140°, every AOD 0°, departure spreads zero). NTN-CDL-C and NTN-CDL-D carry a
//                         line-of-sight ray.
//
//  Ray offsets: TR 38.901 Table 7.5-3, the 20 offsets α_m for a unit RMS spread; a ray's angle is φ_n + c·α_m (eq. 7.7-0a).
//
//  What a channel does with a table (TR 38.901 §7.7.1 steps 1-4, §7.7.3, §7.7.5.1, §7.7.6):
//
//   1. K (a line-of-sight profile, KFactor stated): the Laplacian clusters' powers move by K_model − K_desired dB,
//      K_model = P_LOS − 10 log10 Σ 10^(P_n/10) over the Laplacian clusters (eq. 7.7.6-1/-2).
//   2. Rays. A Laplacian cluster is 20 rays of power P_n/20 at the cluster delay, each angle the cluster angle plus
//      c·α_m; the specular ray is one ray at the cluster angles. Within a cluster the AOD, ZOA and ZOD offsets are each
//      coupled to the AOA offsets by an independent random permutation (step 2: every pair coupled at random).
//   3. The powers are divided by their sum, so the rays sum to 1: the large-scale model carries the mean power.
//   4. Delays are divided by their own RMS delay spread and multiplied by DelaySpread (§7.7.3 and the §7.7.6
//      renormalization), so the spread is DelaySpread exactly; the note says the factor.
//   5. Angle spreads (stated ones only): each angle set is scaled about its circular mean (eq. 7.7-5, the deviation
//      wrapped to ±180°). The spread is the circular one of Annex A, AS = √(−2 ln |Σ P e^(jφ)| / Σ P), which a linear
//      scale does not move linearly, so the factor 7.7-5 names is refined until Annex A's spread equals the stated one.
//      A zenith leaving [0°, 180°] is reflected back into it (§7.5's rule; a negative zenith is negated).
//   6. Translation to the ends' line: every AOD, AOA, ZOD and ZOA moves by the difference between the requested
//      line-of-sight angle and the profile's reference one (the specular ray's, or for an NLOS profile the table's
//      own frame: AOD 0°, AOA 180°, ZOD and ZOA 90° in TR 38.901, ZOD 140° and ZOA 40° in TR 38.811's 50° frame), as
//      TR 38.901 eq. 7.5-12 translates its clusters. The specular ray lands exactly on the requested angles. A zenith
//      pushed out of [0°, 180°] is reflected as in 5. This also does TR 38.811 eq. 6.9-1/-2's elevation change.
//   7. Polarization (step 3, §7.5 step 10): κ = 10^(XPR/10) per cluster, four initial phases per ray uniform on
//      (−π, π); a diffuse ray's matrix is [[e^jΦθθ, κ^−½ e^jΦθφ], [κ^−½ e^jΦφθ, e^jΦφφ]], the specular ray's [[1, 0], [0, −1]].
//
//  Draws: StatDraws.Uniform(seed, drop, Id(stream + ":cdl:<n>:<quantity>"), k), quantity aod|zoa|zod (the
//  permutations, Fisher-Yates) and phase (k = 4m + i), n the 1-based cluster. A realization is a function of
//  (seed, drop, stream) alone.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    /// <summary>One printed row of a CDL table: its normalized delay, power (dB), the cluster's AOD, AOA, ZOD and ZOA (degrees), and
    /// whether it is the specular (line-of-sight) entry.</summary>
    public sealed record CdlCluster(double Delay, double PowerDb, double AodDeg, double AoaDeg, double ZodDeg, double ZoaDeg, bool Los = false);

    /// <summary>A CDL profile as printed: its rows, its per-cluster spreads (degrees) and XPR (dB), the K1 its note states, and the
    /// reference line-of-sight angles the translation is measured from when it has no specular ray.</summary>
    public sealed record CdlProfile(string Name, string Document, IReadOnlyList<CdlCluster> Clusters, double CAsdDeg, double CAsaDeg,
                                    double CZsdDeg, double CZsaDeg, double XprDb, double? NoteK1Db = null,
                                    double RefAodDeg = 0, double RefAoaDeg = 180, double RefZodDeg = 90, double RefZoaDeg = 90)
    {
        public bool HasLos => Clusters.Any(c => c.Los);
    }

    /// <summary>
    /// One ray: its cluster (1-based) and index within it, delay (s), share of the power (the rays sum to 1), global-coordinate angles in
    /// degrees (azimuth from +x towards +y, zenith from +z), and its 2×2 polarization matrix in the (θ̂, φ̂) bases of its departure and
    /// arrival directions.
    /// </summary>
    public sealed record CdlRay(int Cluster, int Index, double DelayS, double Power, double AodDeg, double AoaDeg, double ZodDeg, double ZoaDeg,
                                Complex Mtt, Complex Mtp, Complex Mpt, Complex Mpp, bool Los);

    /// <summary>Desired RMS angle spreads (degrees, Annex A's definition); null keeps the table's.</summary>
    public sealed record CdlAngleSpreads(double? AsdDeg, double? AsaDeg, double? ZsdDeg, double? ZsaDeg);

    /// <summary>A resolved profile: its rays, the factor its normalized delays were divided by, and what was done, in sentences.</summary>
    public sealed record CdlRealization(CdlProfile Profile, IReadOnlyList<CdlRay> Rays, double DelayRescale, IReadOnlyList<string> Notes);

    public static class CdlProfiles
    {
        private const string D38901 = "3GPP TR 38.901 V18.0.0 (2024-05)";
        private const string D38811 = "3GPP TR 38.811 V15.4.0 (2020-09)";

        /// <summary>TR 38.901 Table 7.5-3: the ray offsets for a unit RMS spread, rays 1…20 (±, in pairs).</summary>
        public static readonly IReadOnlyList<double> RayOffsets =
        [
            0.0447, -0.0447, 0.1413, -0.1413, 0.2492, -0.2492, 0.3715, -0.3715, 0.5129, -0.5129,
            0.6797, -0.6797, 0.8844, -0.8844, 1.1481, -1.1481, 1.5195, -1.5195, 2.1551, -2.1551,
        ];

        private static CdlCluster[] Rows(params double[] v)
        {
            var r = new CdlCluster[v.Length / 6];
            for (int i = 0; i < r.Length; i++) r[i] = new CdlCluster(v[6 * i], v[6 * i + 1], v[6 * i + 2], v[6 * i + 3], v[6 * i + 4], v[6 * i + 5]);
            return r;
        }

        private static CdlCluster[] WithLos(double[] los, params double[] v)
            => [new CdlCluster(los[0], los[1], los[2], los[3], los[4], los[5], true), .. Rows(v)];

        /// <summary>Every embedded profile.</summary>
        public static readonly IReadOnlyList<CdlProfile> All =
        [
            new("CDL-A", D38901 + ", Table 7.7.1-1", Rows(
                0.0000, -13.4, -178.1, 51.3, 50.2, 125.4,
                0.3819, 0, -4.2, -152.7, 93.2, 91.3,
                0.4025, -2.2, -4.2, -152.7, 93.2, 91.3,
                0.5868, -4, -4.2, -152.7, 93.2, 91.3,
                0.4610, -6, 90.2, 76.6, 122, 94,
                0.5375, -8.2, 90.2, 76.6, 122, 94,
                0.6708, -9.9, 90.2, 76.6, 122, 94,
                0.5750, -10.5, 121.5, -1.8, 150.2, 47.1,
                0.7618, -7.5, -81.7, -41.9, 55.2, 56,
                1.5375, -15.9, 158.4, 94.2, 26.4, 30.1,
                1.8978, -6.6, -83, 51.9, 126.4, 58.8,
                2.2242, -16.7, 134.8, -115.9, 171.6, 26,
                2.1718, -12.4, -153, 26.6, 151.4, 49.2,
                2.4942, -15.2, -172, 76.6, 157.2, 143.1,
                2.5119, -10.8, -129.9, -7, 47.2, 117.4,
                3.0582, -11.3, -136, -23, 40.4, 122.7,
                4.0810, -12.7, 165.4, -47.2, 43.3, 123.2,
                4.4579, -16.2, 148.4, 110.4, 161.8, 32.6,
                4.5695, -18.3, 132.7, 144.5, 10.8, 27.2,
                4.7966, -18.9, -118.6, 155.3, 16.7, 15.2,
                5.0066, -16.6, -154.1, 102, 171.7, 146,
                5.3043, -19.9, 126.5, -151.8, 22.7, 150.7,
                9.6586, -29.7, -56.2, 55.2, 144.9, 156.1), 5, 11, 3, 3, 10),
            new("CDL-B", D38901 + ", Table 7.7.1-2", Rows(
                0.0000, 0, 9.3, -173.3, 105.8, 78.9,
                0.1072, -2.2, 9.3, -173.3, 105.8, 78.9,
                0.2155, -4, 9.3, -173.3, 105.8, 78.9,
                0.2095, -3.2, -34.1, 125.5, 115.3, 63.3,
                0.2870, -9.8, -65.4, -88.0, 119.3, 59.9,
                0.2986, -1.2, -11.4, 155.1, 103.2, 67.5,
                0.3752, -3.4, -11.4, 155.1, 103.2, 67.5,
                0.5055, -5.2, -11.4, 155.1, 103.2, 67.5,
                0.3681, -7.6, -67.2, -89.8, 118.2, 82.6,
                0.3697, -3, 52.5, 132.1, 102.0, 66.3,
                0.5700, -8.9, -72, -83.6, 100.4, 61.6,
                0.5283, -9, 74.3, 95.3, 98.3, 58.0,
                1.1021, -4.8, -52.2, 103.7, 103.4, 78.2,
                1.2756, -5.7, -50.5, -87.8, 102.5, 82.0,
                1.5474, -7.5, 61.4, -92.5, 101.4, 62.4,
                1.7842, -1.9, 30.6, -139.1, 103.0, 78.0,
                2.0169, -7.6, -72.5, -90.6, 100.0, 60.9,
                2.8294, -12.2, -90.6, 58.6, 115.2, 82.9,
                3.0219, -9.8, -77.6, -79.0, 100.5, 60.8,
                3.6187, -11.4, -82.6, 65.8, 119.6, 57.3,
                4.1067, -14.9, -103.6, 52.7, 118.7, 59.9,
                4.2790, -9.2, 75.6, 88.7, 117.8, 60.1,
                4.7834, -11.3, -77.6, -60.4, 115.7, 62.3), 10, 22, 3, 7, 8),
            new("CDL-C", D38901 + ", Table 7.7.1-3", Rows(
                0, -4.4, -46.6, -101, 97.2, 87.6,
                0.2099, -1.2, -22.8, 120, 98.6, 72.1,
                0.2219, -3.5, -22.8, 120, 98.6, 72.1,
                0.2329, -5.2, -22.8, 120, 98.6, 72.1,
                0.2176, -2.5, -40.7, -127.5, 100.6, 70.1,
                0.6366, 0, 0.3, 170.4, 99.2, 75.3,
                0.6448, -2.2, 0.3, 170.4, 99.2, 75.3,
                0.6560, -3.9, 0.3, 170.4, 99.2, 75.3,
                0.6584, -7.4, 73.1, 55.4, 105.2, 67.4,
                0.7935, -7.1, -64.5, 66.5, 95.3, 63.8,
                0.8213, -10.7, 80.2, -48.1, 106.1, 71.4,
                0.9336, -11.1, -97.1, 46.9, 93.5, 60.5,
                1.2285, -5.1, -55.3, 68.1, 103.7, 90.6,
                1.3083, -6.8, -64.3, -68.7, 104.2, 60.1,
                2.1704, -8.7, -78.5, 81.5, 93.0, 61.0,
                2.7105, -13.2, 102.7, 30.7, 104.2, 100.7,
                4.2589, -13.9, 99.2, -16.4, 94.9, 62.3,
                4.6003, -13.9, 88.8, 3.8, 93.1, 66.7,
                5.4902, -15.8, -101.9, -13.7, 92.2, 52.9,
                5.6077, -17.1, 92.2, 9.7, 106.7, 61.8,
                6.3065, -16, 93.3, 5.6, 93.0, 51.9,
                6.6374, -15.7, 106.6, 0.7, 92.9, 61.7,
                7.0427, -21.6, 119.5, -21.9, 105.2, 58,
                8.6523, -22.8, -123.8, 33.6, 107.8, 57), 2, 15, 3, 7, 7),
            new("CDL-D", D38901 + ", Table 7.7.1-4", WithLos([0, -0.2, 0, -180, 98.5, 81.5],
                0, -13.5, 0, -180, 98.5, 81.5,
                0.035, -18.8, 89.2, 89.2, 85.5, 86.9,
                0.612, -21, 89.2, 89.2, 85.5, 86.9,
                1.363, -22.8, 89.2, 89.2, 85.5, 86.9,
                1.405, -17.9, 13, 163, 97.5, 79.4,
                1.804, -20.1, 13, 163, 97.5, 79.4,
                2.596, -21.9, 13, 163, 97.5, 79.4,
                1.775, -22.9, 34.6, -137, 98.5, 78.2,
                4.042, -27.8, -64.5, 74.5, 88.4, 73.6,
                7.937, -23.6, -32.9, 127.7, 91.3, 78.3,
                9.424, -24.8, 52.6, -119.6, 103.8, 87,
                9.708, -30.0, -132.1, -9.1, 80.3, 70.6,
                12.525, -27.7, 77.2, -83.8, 86.5, 72.9), 5, 8, 3, 3, 11, 13.3),
            new("CDL-E", D38901 + ", Table 7.7.1-5", WithLos([0.000, -0.03, 0, -180, 99.6, 80.4],
                0.000, -22.03, 0, -180, 99.6, 80.4,
                0.5133, -15.8, 57.5, 18.2, 104.2, 80.4,
                0.5440, -18.1, 57.5, 18.2, 104.2, 80.4,
                0.5630, -19.8, 57.5, 18.2, 104.2, 80.4,
                0.5440, -22.9, -20.1, 101.8, 99.4, 80.8,
                0.7112, -22.4, 16.2, 112.9, 100.8, 86.3,
                1.9092, -18.6, 9.3, -155.5, 98.8, 82.7,
                1.9293, -20.8, 9.3, -155.5, 98.8, 82.7,
                1.9589, -22.6, 9.3, -155.5, 98.8, 82.7,
                2.6426, -22.3, 19, -143.3, 100.8, 82.9,
                3.7136, -25.6, 32.7, -94.7, 96.4, 88,
                5.4524, -20.2, 0.5, 147, 98.9, 81,
                12.0034, -29.8, 55.9, -36.2, 95.6, 88.6,
                20.6419, -29.2, 57.6, -26, 104.6, 78.3), 5, 11, 3, 7, 8, 22),

            new("NTN-CDL-A", D38811 + ", Table 6.9.1-1", Rows(
                0, 0, 0, 178.8, 140, 35.6,
                1.0811, -4.675, 0, -115.7, 140, 22.9,
                2.8416, -6.482, 0, 111.5, 140, 127.4), 0, 15, 0, 7, 10, null, 0, 180, 140, 40),
            new("NTN-CDL-B", D38811 + ", Table 6.9.1-2", Rows(
                0, 0, 0, -174.6, 140, 42.2,
                0.7249, -1.973, 0, 144.9, 140, 63.4,
                0.7410, -4.332, 0, -119.8, 140, 89.7,
                5.7392, -11.914, 0, -88.8, 140, 174.1), 0, 15, 0, 7, 10, null, 0, 180, 140, 40),
            new("NTN-CDL-C", D38811 + ", Table 6.9.1-3", WithLos([0, -0.394, 0, -180, 140, 40],
                0, -10.618, 0, -180, 140, 40,
                14.8124, -23.373, 0, -75.9, 140, 87.1), 0, 11, 0, 7, 16, 10.224),
            new("NTN-CDL-D", D38811 + ", Table 6.9.1-4", WithLos([0, -0.284, 0, -180, 140, 40],
                0, -11.991, 0, -180, 140, 40,
                0.5596, -9.887, 0, -135.4, 140, 146.2,
                7.3340, -16.771, 0, -121.5, 140, 136.0), 0, 11, 0, 7, 16, 11.707),
        ];

        public static CdlProfile? Find(string name)
            => All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The profile names, for a refusal and a picker.</summary>
        public static string Names => string.Join(", ", All.Select(p => p.Name));

        /// <summary>K_model of a line-of-sight table (dB): P_LOS − 10 log10 Σ 10^(P_n/10) over its Laplacian clusters (eq. 7.7.6-2).</summary>
        public static double ModelKDb(CdlProfile p)
        {
            double los = p.Clusters.First(c => c.Los).PowerDb;
            return los - 10 * Math.Log10(p.Clusters.Where(c => !c.Los).Sum(c => Math.Pow(10, c.PowerDb / 10)));
        }

        /// <summary>The power-weighted RMS delay spread of <paramref name="rays"/> (s).</summary>
        public static double RmsDelay(IEnumerable<CdlRay> rays) => TdlProfiles.Rms(rays.Select(r => (r.DelayS, r.Power)));

        /// <summary>The circular angle spread of TR 38.901 Annex A (eq. A-1), √(−2 ln |Σ P e^(jφ)| / Σ P), in degrees.</summary>
        public static double RmsAngle(IEnumerable<(double Deg, double Power)> a)
        {
            Complex s = 0; double p = 0;
            foreach (var (deg, w) in a) { s += w * Complex.FromPolarCoordinates(1, deg * Math.PI / 180); p += w; }
            double r = Math.Min(1, s.Magnitude / p);
            return Math.Sqrt(Math.Max(0, -2 * Math.Log(r))) * 180 / Math.PI;
        }

        /// <summary>The power-weighted circular mean angle of TR 38.901 Annex A (eq. A-2), degrees.</summary>
        public static double MeanAngle(IEnumerable<(double Deg, double Power)> a)
        {
            Complex s = 0;
            foreach (var (deg, w) in a) s += w * Complex.FromPolarCoordinates(1, deg * Math.PI / 180);
            return s.Phase * 180 / Math.PI;
        }

        private static double Wrap180(double deg)
        {
            double w = (deg + 180) % 360;
            if (w < 0) w += 360;
            return w - 180;
        }

        private static double Zenith(double deg)
        {
            // §7.5's rule: a zenith in (180°, 360°) is 360° − θ; a negative one is −θ
            double z = deg % 360;
            if (z < 0) z += 360;
            return z > 180 ? 360 - z : z;
        }

        private static int[] Permutation(ulong seed, long drop, ulong stream, int n)
        {
            var p = Enumerable.Range(0, n).ToArray();
            for (int i = n - 1; i > 0; i--)
            {
                int j = (int)Math.Min(i, Math.Floor(StatDraws.Uniform(seed, drop, stream, i) * (i + 1)));
                (p[i], p[j]) = (p[j], p[i]);
            }
            return p;
        }

        /// <summary>
        /// <paramref name="p"/> resolved for one drop: its K (dB, line-of-sight profiles only; null keeps the table's), delays at
        /// <paramref name="delaySpreadS"/>, angle spreads at <paramref name="spreads"/>' stated ones, translated so the line of sight is at the
        /// requested angles (degrees), its random couplings and phases drawn from (<paramref name="seed"/>, <paramref name="drop"/>,
        /// <paramref name="stream"/>). Refuses K on an NLOS profile, a non-positive spread, and a departure spread on a profile whose own is zero.
        /// </summary>
        public static CdlRealization Resolve(CdlProfile p, double delaySpreadS, CdlAngleSpreads? spreads, double? kFactorDb,
                                             double losAodDeg, double losAoaDeg, double losZodDeg, double losZoaDeg, ulong seed, long drop, string stream)
        {
            var inv = CultureInfo.InvariantCulture;
            var notes = new List<string>();
            if (!(delaySpreadS > 0)) throw new PropagationRefusal("DelaySpread must be a positive time.");
            var clusters = p.Clusters.ToList();
            if (kFactorDb is double kd)
            {
                if (!p.HasLos)
                    throw new PropagationRefusal($"{p.Name} has no line-of-sight ray, so KFactor does not apply to it; CDL-D, CDL-E, NTN-CDL-C and NTN-CDL-D have one.");
                double km = ModelKDb(p);
                clusters = [.. clusters.Select(c => c.Los ? c : c with { PowerDb = c.PowerDb - kd + km })];
                notes.Add(string.Format(inv, "{0}: K {1:0.###} dB, its Laplacian clusters moved by {2:0.###} dB from the table's K of {3:0.###} dB "
                                             + "(TR 38.901 §7.7.6).", p.Name, kd, km - kd, km));
            }

            // rays: a Laplacian cluster is 20 at the cluster spreads' offsets, coupled at random; the specular entry is one
            var rays = new List<(int Cluster, int Index, double Delay, double P, double Aod, double Aoa, double Zod, double Zoa, bool Los, double Xpr)>();
            int number = 0;
            foreach (var c in clusters)
            {
                if (!c.Los) number++;
                int n = c.Los ? 1 : number;
                double pw = Math.Pow(10, c.PowerDb / 10);
                if (c.Los) { rays.Add((1, 0, c.Delay, pw, c.AodDeg, c.AoaDeg, c.ZodDeg, c.ZoaDeg, true, p.XprDb)); continue; }
                string at = $"{stream}:cdl:{n}:";
                var pAod = Permutation(seed, drop, StatDraws.Id(at + "aod"), 20);
                var pZoa = Permutation(seed, drop, StatDraws.Id(at + "zoa"), 20);
                var pZod = Permutation(seed, drop, StatDraws.Id(at + "zod"), 20);
                for (int m = 0; m < 20; m++)
                    rays.Add((n, m, c.Delay, pw / 20,
                              c.AodDeg + p.CAsdDeg * RayOffsets[pAod[m]], c.AoaDeg + p.CAsaDeg * RayOffsets[m],
                              c.ZodDeg + p.CZsdDeg * RayOffsets[pZod[m]], c.ZoaDeg + p.CZsaDeg * RayOffsets[pZoa[m]], false, p.XprDb));
            }
            double sum = rays.Sum(r => r.P);
            var pow = rays.Select(r => r.P / sum).ToArray();
            if (Math.Abs(10 * Math.Log10(sum)) > 0.005)
                notes.Add(string.Format(inv, "{0}: the rays' powers sum to {1:0.###} dB and are normalized to 0 dB (the large-scale loss carries the mean).",
                                        p.Name, 10 * Math.Log10(sum)));

            double rms = TdlProfiles.Rms(rays.Select((r, i) => (r.Delay, pow[i])));
            if (Math.Abs(rms - 1) > 1e-6)
                notes.Add(string.Format(inv, "{0}: its normalized delays have an RMS spread of {1:0.0######}; divided by it, so the spread is "
                                             + "DelaySpread exactly (TR 38.901 §7.7.6's renormalization).", p.Name, rms));

            double[] aod = [.. rays.Select(r => r.Aod)], aoa = [.. rays.Select(r => r.Aoa)];
            double[] zod = [.. rays.Select(r => r.Zod)], zoa = [.. rays.Select(r => r.Zoa)];
            void Scale(double[] a, double? desired, bool zenith, string what)
            {
                if (desired is not double want) return;
                if (!(want >= 0)) throw new PropagationRefusal($"{p.Name}: the {what} spread must not be negative.");
                double model = RmsAngle(a.Select((x, i) => (x, pow[i])));
                if (want == model) return;
                if (model < 1e-9)
                    throw new PropagationRefusal($"{p.Name}'s {what} spread is zero (every ray at one angle), so it cannot be scaled to {want.ToString("0.###", inv)}°.");
                double mu = MeanAngle(a.Select((x, i) => (x, pow[i])));
                var dev = a.Select(x => Wrap180(x - mu)).ToArray();
                double[] At(double s) => [.. dev.Select(d => zenith ? Zenith(mu + s * d) : Wrap180(mu + s * d))];
                double Spread(double s) => RmsAngle(At(s).Select((x, i) => (x, pow[i])));
                // eq. 7.7-5's ratio, refined until Annex A's spread is the one asked (bracket, then bisection with secant steps)
                double s0 = want / model, lo = 0, hi = s0;
                int guard = 0;
                while (Spread(hi) < want)
                {
                    lo = hi; hi *= 1.5;
                    if (++guard > 60) throw new PropagationRefusal($"{p.Name}: a {what} of {want.ToString("0.###", inv)}° is beyond what scaling its angles reaches (a strong "
                                                                     + "line-of-sight ray holds the circular spread down: |Σ P e^(jφ)| cannot fall below its excess power).");
                }
                double flo = Spread(lo) - want, fhi = Spread(hi) - want;
                for (int i = 0; i < 200 && hi - lo > 1e-15 * hi; i++)
                {
                    double s = fhi != flo ? hi - fhi * (hi - lo) / (fhi - flo) : 0.5 * (lo + hi);
                    if (!(s > lo && s < hi)) s = 0.5 * (lo + hi);
                    double f = Spread(s) - want;
                    if (f == 0) { lo = hi = s; break; }
                    if (f < 0) { lo = s; flo = f; } else { hi = s; fhi = f; }
                    if (i % 3 == 2) { double mid = 0.5 * (lo + hi); double fm = Spread(mid) - want; if (fm < 0) { lo = mid; flo = fm; } else { hi = mid; fhi = fm; } }
                }
                double sol = Math.Abs(flo) < Math.Abs(fhi) ? lo : hi;
                Array.Copy(At(sol), a, a.Length);
                notes.Add(string.Format(inv, "{0}: {1} spread {2:0.###}° scaled to {3:0.###}° about its mean {4:0.###}° (factor {5:0.######}; eq. 7.7-5's ratio "
                                             + "{6:0.######}, refined for Annex A's circular spread).", p.Name, what, model, want, mu, sol, s0));
            }
            Scale(aod, spreads?.AsdDeg, false, "ASD");
            Scale(aoa, spreads?.AsaDeg, false, "ASA");
            Scale(zod, spreads?.ZsdDeg, true, "ZSD");
            Scale(zoa, spreads?.ZsaDeg, true, "ZSA");

            // translation to the ends' line: the specular ray's angles (or the table's frame) onto the requested ones
            int losRay = rays.FindIndex(r => r.Los);
            double rAod = losRay >= 0 ? aod[losRay] : p.RefAodDeg, rAoa = losRay >= 0 ? aoa[losRay] : p.RefAoaDeg;
            double rZod = losRay >= 0 ? zod[losRay] : p.RefZodDeg, rZoa = losRay >= 0 ? zoa[losRay] : p.RefZoaDeg;
            double dAod = losAodDeg - rAod, dAoa = losAoaDeg - rAoa, dZod = losZodDeg - rZod, dZoa = losZoaDeg - rZoa;
            if (losRay < 0)
                notes.Add(string.Format(inv, "{0} has no line-of-sight ray: its angles are translated from the table's frame (AOD {1:0.#}°, AOA {2:0.#}°, "
                                             + "ZOD {3:0.#}°, ZOA {4:0.#}° on the line between the ends) onto the ends' line.",
                                        p.Name, p.RefAodDeg, p.RefAoaDeg, p.RefZodDeg, p.RefZoaDeg));

            var result = new List<CdlRay>(rays.Count);
            for (int i = 0; i < rays.Count; i++)
            {
                var r = rays[i];
                double tAod = Wrap180(aod[i] + dAod), tAoa = Wrap180(aoa[i] + dAoa);
                double tZod = Zenith(zod[i] + dZod), tZoa = Zenith(zoa[i] + dZoa);
                if (i == losRay) { tAod = Wrap180(losAodDeg); tAoa = Wrap180(losAoaDeg); tZod = losZodDeg; tZoa = losZoaDeg; }
                Complex mtt, mtp, mpt, mpp;
                if (r.Los) { mtt = 1; mtp = 0; mpt = 0; mpp = -1; }
                else
                {
                    double kappa = Math.Pow(10, r.Xpr / 10), x = 1 / Math.Sqrt(kappa);
                    ulong ph = StatDraws.Id($"{stream}:cdl:{r.Cluster}:phase");
                    double Phi(int q) => Math.PI * (2 * StatDraws.Uniform(seed, drop, ph, 4 * r.Index + q) - 1);
                    mtt = Complex.FromPolarCoordinates(1, Phi(0));
                    mtp = Complex.FromPolarCoordinates(x, Phi(1));
                    mpt = Complex.FromPolarCoordinates(x, Phi(2));
                    mpp = Complex.FromPolarCoordinates(1, Phi(3));
                }
                result.Add(new CdlRay(r.Cluster, r.Index, r.Delay / rms * delaySpreadS, pow[i], tAod, tAoa, tZod, tZoa, mtt, mtp, mpt, mpp, r.Los));
            }
            notes.Insert(0, string.Format(inv, "{0} ({1}): {2} clusters, {3} rays, RMS delay spread {4:G4} s; ASD {5:0.##}°, ASA {6:0.##}°, ZSD {7:0.##}°, "
                                               + "ZSA {8:0.##}°.", p.Name, p.Document, clusters.Count(c => !c.Los), result.Count, RmsDelay(result),
                                          RmsAngle(result.Select(x => (x.AodDeg, x.Power))), RmsAngle(result.Select(x => (x.AoaDeg, x.Power))),
                                          RmsAngle(result.Select(x => (x.ZodDeg, x.Power))), RmsAngle(result.Select(x => (x.ZoaDeg, x.Power)))));
            return new CdlRealization(p, result, rms, notes);
        }
    }
}
