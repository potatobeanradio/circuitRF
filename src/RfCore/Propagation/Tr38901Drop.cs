// ================================================================
//  Tr38901Drop.cs  —  a 3GPP TR 38.901 stochastic drop for one link (brief PCH-10 R-pch10-2, design note §8.3)
//
//  Transcribed from 3GPP TR 38.901 V18.0.0 (ETSI TR 138 901 V18.0.0, 2024-05), clause 7.5 Steps 4-10 and the
//  cluster-level part of Step 11, with Table 7.5-6 Parts 1-3 (cross-correlations, r_τ, XPR, N, M, cluster spreads,
//  ζ), Tables 7.5-2 and 7.5-4 (C_φ, C_θ), 7.5-3 (ray offsets, CdlProfiles.RayOffsets), 7.5-5 (sub-clusters) and
//  7.5-7 to 7.5-11 (the ZOD offset). The large-scale parameters' means and deviations, σSF and the line-of-sight
//  probability are Tr38901.cs' (Tables 7.4.1-1, 7.4.2-1, 7.5-6 to 7.5-11), shared rather than copied.
//
//  One link, no spatial consistency, no O2I, no blockage, no oxygen absorption: the autocorrelation distances play no
//  part, the cross-correlations do (Step 4: the lower Cholesky factor of the correlation matrix, order
//  [SF, K, DS, ASD, ASA, ZSD, ZSA], K left out in non-line-of-sight).
//
//  Readings this transcription makes, each said in the drop's notes (src/RfCore/RESOLVED.md):
//    · The −25 dB cluster removal (Step 6) compares the eq. 7.5-6 powers, not eq. 7.5-8's: the report says the 7.5-8
//      powers (the line-of-sight spike added) are used only in eqs. 7.5-9 and 7.5-14. Angles are generated for all N
//      clusters (eqs. 7.5-12/17 need cluster 1) and the removed ones then dropped; the powers are not renormalized.
//    · In line of sight the channel's delays are the scaled τ_n/C_τ (eq. 7.5-4): the report says they are not used in
//      the power generation, which leaves Step 11 as where they are used.
//    · The two strongest clusters (by eq. 7.5-6 power) are split into Table 7.5-5's sub-clusters, their rays coupled within
//      each sub-cluster (Step 8); c_DS N/A is 3.91 ns (Step 11).
//    · A zenith is taken modulo 360° and [180°, 360°) reflected (eq. 7.5-18's rule), ZOD as ZOA.
//
//  Draws: DropDraws.Uniform/Normal(seed, drop, stream, "scen:<quantity>", k), so a drop is a function of (seed, drop,
//  stream) alone.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    /// <summary>A scenario's Table 7.5-6 small-scale parameters for one propagation state: the cross-correlations (21, in the
    /// table's row order), r_τ, XPR, the cluster count, the cluster spreads (c_DS in ns, null for N/A) and ζ.</summary>
    public sealed record ScenarioSmallScale(double[] Correlations, double RTau, double XprMu, double XprSigma, int Clusters,
                                            double? CDsNs, double CAsdDeg, double CAsaDeg, double CZsaDeg, double ZetaDb);

    /// <summary>One cluster of a drop: its channel delay (s), its eq. 7.5-6 power, and its angles (degrees).</summary>
    public sealed record ScenarioCluster(int Index, double DelayS, double Power, double AodDeg, double AoaDeg, double ZodDeg, double ZoaDeg);

    /// <summary>A drop: its state, the large-scale parameters drawn (SF in dB, positive more received power; K in dB; spreads in s and
    /// degrees), its kept clusters and every ray (the line-of-sight ray first in line of sight), and what was done, in sentences.</summary>
    public sealed record ScenarioDropResult(Scenario38901 Scenario, bool Los, double SfDb, double? KDb, double Ds, double Asd, double Asa,
                                            double Zsd, double Zsa, double KR, IReadOnlyList<ScenarioCluster> Clusters,
                                            CdlRealization Rays, IReadOnlyList<string> Notes)
    {
        /// <summary>The RMS delay spread of the clusters as they enter the channel (cluster level; in line of sight the line-of-sight
        /// power K_R/(K_R + 1) at the first delay and the clusters' powers over K_R + 1).</summary>
        public double RealizedDelaySpread
        {
            get
            {
                var pts = Clusters.Select(c => (c.DelayS, Los ? c.Power / (KR + 1) : c.Power)).ToList();
                if (Los) pts.Add((Clusters.Min(c => c.DelayS), KR / (KR + 1)));
                return TdlProfiles.Rms(pts);
            }
        }
    }

    public static class Tr38901Drop
    {
        // Table 7.5-2 and 7.5-4: C_φ^NLOS and C_θ^NLOS by the number of clusters
        private static readonly Dictionary<int, double> CPhi = new()
        {
            [4] = 0.779, [5] = 0.860, [8] = 1.018, [10] = 1.090, [11] = 1.123, [12] = 1.146, [14] = 1.190, [15] = 1.211,
            [16] = 1.226, [19] = 1.273, [20] = 1.289, [25] = 1.358,
        };
        private static readonly Dictionary<int, double> CTheta = new()
        {
            [8] = 0.889, [10] = 0.957, [11] = 1.031, [12] = 1.104, [15] = 1.1088, [19] = 1.184, [20] = 1.178, [25] = 1.282,
        };

        // Table 7.5-5: the rays (0-based) of the three sub-clusters, their power shares and delay offsets in c_DS
        private static readonly int[][] SubClusters = [[0, 1, 2, 3, 4, 5, 6, 7, 18, 19], [8, 9, 10, 11, 16, 17], [12, 13, 14, 15]];
        private static readonly double[] SubDelay = [0, 1.28, 2.56];

        private const double NaN = double.NaN;

        /// <summary>Table 7.5-6: the small-scale parameters of <paramref name="s"/> in one state at <paramref name="fGhz"/>.
        /// Correlations in the table's order: ASD-DS, ASA-DS, ASA-SF, ASD-SF, DS-SF, ASD-ASA, ASD-K, ASA-K, DS-K, SF-K, ZSD-SF,
        /// ZSA-SF, ZSD-K, ZSA-K, ZSD-DS, ZSA-DS, ZSD-ASD, ZSA-ASD, ZSD-ASA, ZSA-ASA, ZSD-ZSA (NaN where the table says N/A).</summary>
        public static ScenarioSmallScale SmallScale(Scenario38901 s, bool los, double fGhz)
        {
            switch (s)
            {
                case Scenario38901.UMi:
                    return los
                        ? new([0.5, 0.8, -0.4, -0.5, -0.4, 0.4, -0.2, -0.3, -0.7, 0.5, 0, 0, 0, 0, 0, 0.2, 0.5, 0.3, 0, 0, 0], 3, 9, 3, 12, 5, 3, 17, 7, 3)
                        : new([0, 0.4, -0.4, 0, -0.7, 0, NaN, NaN, NaN, NaN, 0, 0, NaN, NaN, -0.5, 0, 0.5, 0.5, 0, 0.2, 0], 2.1, 8, 3, 19, 11, 10, 22, 7, 3);
                case Scenario38901.UMa:
                {
                    double cds = Math.Max(0.25, 6.5622 - 3.4084 * Math.Log10(Math.Max(fGhz, 6)));
                    return los
                        ? new([0.4, 0.8, -0.5, -0.5, -0.4, 0, 0, -0.2, -0.4, 0, 0, -0.8, 0, 0, -0.2, 0, 0.5, 0, -0.3, 0.4, 0], 2.5, 8, 4, 12, cds, 5, 11, 7, 3)
                        : new([0.4, 0.6, 0, -0.6, -0.4, 0.4, NaN, NaN, NaN, NaN, 0, -0.4, NaN, NaN, -0.5, 0, 0.5, -0.1, 0, 0, 0], 2.3, 7, 3, 20, cds, 2, 15, 7, 3);
                }
                case Scenario38901.RMa:
                    return los
                        ? new([0, 0, 0, 0, -0.5, 0, 0, 0, 0, 0, 0.01, -0.17, 0, -0.02, -0.05, 0.27, 0.73, -0.14, -0.20, 0.24, -0.07], 3.8, 12, 4, 11, null, 2, 3, 3, 3)
                        : new([-0.4, 0, 0, 0.6, -0.5, 0, NaN, NaN, NaN, NaN, -0.04, -0.25, NaN, NaN, -0.10, -0.40, 0.42, -0.27, -0.18, 0.26, -0.27], 1.7, 7, 3, 10, null, 2, 3, 3, 3);
                case Scenario38901.InHMixed:
                case Scenario38901.InHOpen:
                    return los
                        ? new([0.6, 0.8, -0.5, -0.4, -0.8, 0.4, 0, 0, -0.5, 0.5, 0.2, 0.3, 0, 0.1, 0.1, 0.2, 0.5, 0, 0, 0.5, 0], 3.6, 11, 4, 15, null, 5, 8, 9, 6)
                        : new([0.4, 0, -0.4, 0, -0.5, 0, NaN, NaN, NaN, NaN, 0, 0, NaN, NaN, -0.27, -0.06, 0.35, 0.23, -0.08, 0.43, 0.42], 3, 10, 4, 19, null, 5, 11, 9, 3);
                default:   // InF, every sub-scenario (Part 3)
                    return los || s == Scenario38901.InFHH
                        ? new([0, 0, 0, 0, 0, 0, -0.5, 0, -0.7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 2.7, 12, 6, 25, null, 5, 8, 9, 4)
                        : new([0, 0, 0, 0, 0, 0, NaN, NaN, NaN, NaN, 0, 0, NaN, NaN, 0, 0, 0, 0, 0, 0, 0], 3, 11, 6, 25, null, 5, 8, 9, 3);
            }
        }

        /// <summary>Tables 7.5-7 to 7.5-11: the ZOD offset µ_offset,ZOD (degrees; zero in line of sight).</summary>
        public static double ZodOffsetDeg(Scenario38901 s, bool los, double fGhz, double d2D, double hBS, double hUT)
        {
            if (los) return 0;
            switch (s)
            {
                case Scenario38901.UMa:
                {
                    double lf = Math.Log10(Math.Max(fGhz, 6));
                    double a = 0.208 * lf - 0.782, b = 25, c = -0.13 * lf + 2.03, e = 7.66 * lf - 5.96;
                    return e - Math.Pow(10, a * Math.Log10(Math.Max(b, d2D)) + c - 0.07 * (hUT - 1.5));
                }
                case Scenario38901.UMi:
                    return -Math.Pow(10, -1.5 * Math.Log10(Math.Max(10, d2D)) + 3.3);
                case Scenario38901.RMa:
                    return (Math.Atan((35 - 3.5) / d2D) - Math.Atan((35 - 1.5) / d2D)) * 180 / Math.PI;
                default:
                    return 0;
            }
        }

        /// <summary>The 7×7 (6×6 without K) correlation matrix in Step 4's order [SF, K, DS, ASD, ASA, ZSD, ZSA].</summary>
        public static double[,] CorrelationMatrix(ScenarioSmallScale p, bool los)
        {
            const int SF = 0, K = 1, DS = 2, ASD = 3, ASA = 4, ZSD = 5, ZSA = 6;
            var c = new double[7, 7];
            for (int i = 0; i < 7; i++) c[i, i] = 1;
            (int, int)[] order = [(ASD, DS), (ASA, DS), (ASA, SF), (ASD, SF), (DS, SF), (ASD, ASA), (ASD, K), (ASA, K), (DS, K), (SF, K),
                                  (ZSD, SF), (ZSA, SF), (ZSD, K), (ZSA, K), (ZSD, DS), (ZSA, DS), (ZSD, ASD), (ZSA, ASD), (ZSD, ASA), (ZSA, ASA), (ZSD, ZSA)];
            for (int i = 0; i < order.Length; i++)
            {
                var (a, b) = order[i];
                double v = double.IsNaN(p.Correlations[i]) ? 0 : p.Correlations[i];
                c[a, b] = c[b, a] = v;
            }
            if (los) return c;
            int[] keep = [SF, DS, ASD, ASA, ZSD, ZSA];
            var r = new double[6, 6];
            for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) r[i, j] = c[keep[i], keep[j]];
            return r;
        }

        /// <summary>The lower Cholesky factor of <paramref name="c"/>; refuses a matrix that is not positive definite.</summary>
        public static double[,] Cholesky(double[,] c, string what)
        {
            int n = c.GetLength(0);
            var l = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = c[i, j];
                    for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                    if (i == j)
                    {
                        if (!(sum > 0)) throw new PropagationRefusal($"38901 {what}: Table 7.5-6's cross-correlation matrix is not positive definite.");
                        l[i, i] = Math.Sqrt(sum);
                    }
                    else l[i, j] = sum / l[j, j];
                }
            return l;
        }

        private static double Wrap180(double deg)
        {
            double w = (deg + 180) % 360;
            if (w < 0) w += 360;
            return w - 180;
        }

        private static double Zenith(double deg)
        {
            double z = deg % 360;
            if (z < 0) z += 360;
            return z > 180 ? 360 - z : z;
        }

        /// <summary>
        /// One drop of scenario <paramref name="s"/> in state <paramref name="los"/> at <paramref name="fHz"/> for the geometry (d2D, heights),
        /// the line-of-sight direction at the two ends in degrees (azimuth from +x towards +y, zenith from +z). Refuses where the scenario
        /// gives no channel parameters (RMa above 7 GHz).
        /// </summary>
        public static ScenarioDropResult Draw(Scenario38901 s, bool los, double fHz, double d2D, double hBS, double hUT,
                                              double losAodDeg, double losAoaDeg, double losZodDeg, double losZoaDeg,
                                              ulong seed, long drop, string stream, InfClutter? clutter = null)
        {
            var inv = CultureInfo.InvariantCulture;
            string name = Tr38901.Name(s);
            double fGhz = fHz / 1e9;
            var spreads = Tr38901.Spreads(s, los, fHz, d2D, hBS, hUT, clutter)
                          ?? throw new PropagationRefusal($"38901 {name}: the report gives no channel parameters at {fGhz.ToString("0.###", inv)} GHz (RMa's stop at 7 GHz), so there is no scenario drop.");
            var (_, sigmaSf) = Tr38901.PathLoss(s, los, fHz, d2D, hBS, hUT);
            var p = SmallScale(s, los, fGhz);
            // each quantity's stream hashed once (a draw is then one mix of four words)
            var ids = new Dictionary<string, ulong>();
            ulong Id(string q)
            {
                if (!ids.TryGetValue(q, out ulong id)) ids[q] = id = StatDraws.Id(stream + ":scen:" + q);
                return id;
            }
            double U(string q, int k) => StatDraws.Uniform(seed, drop, Id(q), k);
            double N(string q, int k) => Statistics.Normal.InverseCdf(U(q, k));

            // Step 4: the correlated large-scale parameters
            var l = Cholesky(CorrelationMatrix(p, los), $"{name} {(los ? "LOS" : "NLOS")}");
            int n = l.GetLength(0);
            var z = new double[n];
            for (int i = 0; i < n; i++) z[i] = N("lsp", i);
            var x = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j <= i; j++) x[i] += l[i, j] * z[j];
            int o = los ? 1 : 0;   // the index shift past K
            double sf = sigmaSf * x[0];
            double? kDb = los ? spreads.KFactorDb!.Value.Mean + spreads.KFactorDb.Value.Std * x[1] : null;
            double ds = Math.Pow(10, spreads.DelaySpread.Mean + spreads.DelaySpread.Std * x[1 + o]);
            double asd = Math.Min(104, Math.Pow(10, spreads.AzimuthDeparture.Mean + spreads.AzimuthDeparture.Std * x[2 + o]));
            double asa = Math.Min(104, Math.Pow(10, spreads.AzimuthArrival.Mean + spreads.AzimuthArrival.Std * x[3 + o]));
            double zsd = Math.Min(52, Math.Pow(10, spreads.ZenithDeparture.Mean + spreads.ZenithDeparture.Std * x[4 + o]));
            double zsa = Math.Min(52, Math.Pow(10, spreads.ZenithArrival.Mean + spreads.ZenithArrival.Std * x[5 + o]));
            double k = kDb ?? 0, kr = los ? Math.Pow(10, k / 10) : 0;
            int nc = p.Clusters;

            // Step 5: cluster delays
            var tau = new double[nc];
            for (int i = 0; i < nc; i++) tau[i] = -p.RTau * ds * Math.Log(U("delay", i));
            double t0 = tau.Min();
            for (int i = 0; i < nc; i++) tau[i] -= t0;
            // the per-cluster shadowing goes with each cluster's own draw, so sort the (delay, ζ) pairs together
            var zeta = Enumerable.Range(0, nc).Select(i => p.ZetaDb * N("zeta", i)).ToArray();
            Array.Sort(tau, zeta);
            double ctau = los ? 0.7705 - 0.0433 * k + 0.0002 * k * k + 0.000017 * k * k * k : 1;

            // Step 6: cluster powers (eq. 7.5-5/6), and in line of sight eq. 7.5-8's for the angles
            var pw = new double[nc];
            for (int i = 0; i < nc; i++) pw[i] = Math.Exp(-tau[i] * (p.RTau - 1) / (p.RTau * ds)) * Math.Pow(10, -zeta[i] / 10);
            double sum = pw.Sum();
            for (int i = 0; i < nc; i++) pw[i] /= sum;
            var pa = pw.Select((v, i) => los ? v / (kr + 1) + (i == 0 ? kr / (kr + 1) : 0) : v).ToArray();
            double paMax = pa.Max();

            // Step 7: angles, per cluster
            double cphi = CPhi[nc] * (los ? 1.1035 - 0.028 * k - 0.002 * k * k + 0.0001 * k * k * k : 1);
            double cth = CTheta[nc] * (los ? 1.3086 + 0.0339 * k - 0.0077 * k * k + 0.0002 * k * k * k : 1);
            double zodOffset = ZodOffsetDeg(s, los, fGhz, d2D, hBS, hUT);
            double[] Azimuth(string q, double spread, double losDeg)
            {
                var a = new double[nc];
                for (int i = 0; i < nc; i++)
                {
                    double prime = 2 * (spread / 1.4) * Math.Sqrt(-Math.Log(pa[i] / paMax)) / cphi;
                    double xs = U(q + ":x", i) < 0.5 ? -1 : 1;
                    a[i] = xs * prime + spread / 7 * N(q + ":y", i);
                }
                double shift = los ? losDeg - a[0] : losDeg;
                return [.. a.Select(v => v + shift)];
            }
            double[] ZenithAngles(string q, double spread, double losDeg, double offset)
            {
                var a = new double[nc];
                for (int i = 0; i < nc; i++)
                {
                    double prime = -spread * Math.Log(pa[i] / paMax) / cth;
                    double xs = U(q + ":x", i) < 0.5 ? -1 : 1;
                    a[i] = xs * prime + spread / 7 * N(q + ":y", i);
                }
                double shift = los ? losDeg - a[0] : losDeg + offset;
                return [.. a.Select(v => v + shift)];
            }
            var aoa = Azimuth("aoa", asa, losAoaDeg);
            var aod = Azimuth("aod", asd, losAodDeg);
            var zoa = ZenithAngles("zoa", zsa, losZoaDeg, 0);
            var zod = ZenithAngles("zod", zsd, losZodDeg, zodOffset);

            // the −25 dB removal on the eq. 7.5-6 powers; the two strongest kept clusters split into sub-clusters
            double pMax = pw.Max();
            var kept = Enumerable.Range(0, nc).Where(i => pw[i] >= pMax * Math.Pow(10, -2.5)).ToList();
            var strongest = kept.OrderByDescending(i => pw[i]).Take(2).ToHashSet();
            double cdsS = (p.CDsNs ?? 3.91) * 1e-9;
            double zsdRay = 3.0 / 8.0 * Math.Pow(10, spreads.ZenithDeparture.Mean);
            var alpha = CdlProfiles.RayOffsets;
            var clusters = new List<ScenarioCluster>();
            var rays = new List<CdlRay>();
            double nlosScale = los ? 1 / (kr + 1) : 1;
            if (los)
                rays.Add(new CdlRay(1, 0, tau[0] / ctau, kr / (kr + 1), Wrap180(losAodDeg), Wrap180(losAoaDeg), losZodDeg, losZoaDeg, 1, 0, 0, -1, true));
            foreach (int c in kept)
            {
                double delay = tau[c] / ctau;
                clusters.Add(new ScenarioCluster(c + 1, delay, pw[c], Wrap180(aod[c]), Wrap180(aoa[c]), Zenith(zod[c]), Zenith(zoa[c])));
                // Step 8: within the cluster (or each sub-cluster), AOD, ZOA and ZOD offsets each coupled to the AOA offsets at random
                int[][] groups = strongest.Contains(c) ? SubClusters : [[.. Enumerable.Range(0, 20)]];
                var mapAod = new int[20]; var mapZoa = new int[20]; var mapZod = new int[20]; var sub = new int[20];
                for (int g = 0; g < groups.Length; g++)
                {
                    var rs = groups[g];
                    int[] Perm(string q)
                    {
                        var idx = rs.ToArray();
                        ulong pid = StatDraws.Id($"{stream}:scen:{q}:{c}:{g}");
                        for (int i = idx.Length - 1; i > 0; i--)
                        {
                            int j = (int)Math.Min(i, Math.Floor(StatDraws.Uniform(seed, drop, pid, i) * (i + 1)));
                            (idx[i], idx[j]) = (idx[j], idx[i]);
                        }
                        return idx;
                    }
                    var pAod = Perm("perm:aod"); var pZoa = Perm("perm:zoa"); var pZod = Perm("perm:zod");
                    for (int i = 0; i < rs.Length; i++) { mapAod[rs[i]] = pAod[i]; mapZoa[rs[i]] = pZoa[i]; mapZod[rs[i]] = pZod[i]; sub[rs[i]] = groups.Length > 1 ? g : 0; }
                }
                ulong xprId = Id($"xpr:{c}"), phaseId = Id($"phase:{c}");
                for (int m = 0; m < 20; m++)
                {
                    // Step 9 and 10: the ray's XPR and four initial phases
                    double kappa = Math.Pow(10, (p.XprMu + p.XprSigma * Statistics.Normal.InverseCdf(StatDraws.Uniform(seed, drop, xprId, m))) / 10);
                    double xk = 1 / Math.Sqrt(kappa);
                    Complex Ph(int q) => Complex.FromPolarCoordinates(q is 1 or 2 ? xk : 1, Math.PI * (2 * StatDraws.Uniform(seed, drop, phaseId, 4 * m + q) - 1));
                    rays.Add(new CdlRay(c + 1, m, delay + SubDelay[sub[m]] * cdsS * (strongest.Contains(c) ? 1 : 0), pw[c] / 20 * nlosScale,
                                        Wrap180(aod[c] + p.CAsdDeg * alpha[mapAod[m]]), Wrap180(aoa[c] + p.CAsaDeg * alpha[m]),
                                        Zenith(zod[c] + zsdRay * alpha[mapZod[m]]), Zenith(zoa[c] + p.CZsaDeg * alpha[mapZoa[m]]),
                                        Ph(0), Ph(1), Ph(2), Ph(3), false));
                }
            }

            var notes = new List<string>
            {
                string.Format(inv, "38901 {0} scenario drop ({1}), {2}: SF {3:0.##} dB, {4}DS {5:G4} s, ASD {6:0.##}°, ASA {7:0.##}°, ZSD {8:0.##}°, ZSA {9:0.##}°; "
                                   + "{10} of {11} clusters kept (−25 dB), {12} rays.",
                              name, Tr38901.Document, los ? "line of sight" : "non-line-of-sight", sf, kDb is double kk ? string.Format(inv, "K {0:0.##} dB, ", kk) : "",
                              ds, asd, asa, zsd, zsa, kept.Count, nc, rays.Count),
                "The −25 dB removal compares eq. 7.5-6's powers (the report uses eq. 7.5-8's only in eqs. 7.5-9 and 7.5-14); the powers are not renormalized after it"
                + (los ? "; in line of sight the delays are the scaled τ/C_τ (eq. 7.5-4)." : "."),
            };
            double kept_ = rays.Sum(r => r.Power);
            var profile = new CdlProfile($"38901 {name} drop", Tr38901.Document,
                [.. clusters.Select(c => new CdlCluster(c.DelayS / ds, 10 * Math.Log10(c.Power), c.AodDeg, c.AoaDeg, c.ZodDeg, c.ZoaDeg))],
                p.CAsdDeg, p.CAsaDeg, zsdRay, p.CZsaDeg, p.XprMu);
            if (kept_ < 0.999) notes.Add(string.Format(inv, "The kept rays carry {0:0.###} of the power (the removed clusters' share is not put back).", kept_));
            return new ScenarioDropResult(s, los, sf, kDb, ds, asd, asa, zsd, zsa, kr, clusters, new CdlRealization(profile, rays, 1, notes), notes);
        }
    }
}
