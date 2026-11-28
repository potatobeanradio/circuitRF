// ================================================================
//  Ofdm3gpp.cs  —  LTE / NR past the full-load downlink: uplink
//                  DFT-s-OFDM and SC-FDMA, RB allocation, the 3GPP
//                  test models, TDD gating, NB-IoT and LTE-M uplink
//
//  Everything is built as an OfdmGrid on the carrier's own numerology
//  (OfdmGenerator.Numerology) and synthesised by it; with no option
//  set OfdmGenerator keeps its own full-load path untouched.
//
//  NR uplink (TS 38.211 V19.5.0): PUSCH with transform precoding
//    (cl. 6.3.1.4: y = DFT(x)/sqrt(M) per symbol, M = 12 x N_RB, N_RB =
//    2^a 3^b 5^c), pi/2-BPSK (cl. 5.1.1) or QPSK..256QAM, DM-RS type 1
//    at symbols 2 and 11 (mapping type A, pos2, dmrs-AdditionalPosition
//    pos1, table 6.4.1.1.3-3), low-PAPR sequence type 1 (cl. 5.2.2,
//    u = N_ID mod 30, v = 0, alpha = 0) on the even subcarriers of the
//    allocation, 3 dB above the data (two CDM groups without data);
//    without transform precoding (CP-OFDM PUSCH) the DM-RS is the Gold
//    QPSK of cl. 6.4.1.1.1 on even subcarriers, data on the odd.
//  LTE uplink (TS 36.211 V19.3.0): SC-FDMA (cl. 5.3.3, 5.6) with the
//    half-subcarrier shift, DM-RS on symbol 3 of each slot (cl. 5.5.2),
//    base sequence of cl. 5.5.1 (u = N_ID mod 30, v = 0, alpha = 0).
//  Test models: NR-FR1-TM1.1..3.3 (TS 38.141-1 V19.6.0 cl. 4.9.2.2,
//    4.9.2.3) and E-TM1.1..3.3 (TS 36.141 V19.1.0 cl. 6.1.1, 6.1.2),
//    FDD and TDD; see the builders for what each carries.
//  TDD: a slot pattern "DDDSU:10,2" — D, S, U per slot (LTE: per
//    subframe), and per S slot the DL and UL symbol counts; the rest of
//    an S slot is guard.  Symbols of the other link are gated off.
//
//  Simplifications: data are seeded random constellation points (no
//  channel coding, scrambling or PN23); no PT-RS, SRS, PRACH,
//  frequency hopping or sequence/group hopping; the E-TM control
//  region carries QPSK REGs at the table's PCFICH / PHICH / PDCCH
//  EPREs spread evenly over the REGs rather than placed by the
//  PCFICH / PHICH / CCE mapping; the NR-TM PDCCH carries QPSK with its
//  DM-RS; PDSCH DM-RS symbols carry data on the other comb.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>LTE / NR options beyond the full-load downlink; all unset = the plain carrier.</summary>
    public sealed record OfdmOptions
    {
        /// <summary>Uplink: NR PUSCH (CP-OFDM, or DFT-s-OFDM with <see cref="TransformPrecoding"/>); LTE is always SC-FDMA.</summary>
        public bool Uplink { get; init; }
        /// <summary>NR uplink DFT-s-OFDM (TS 38.211 cl. 6.3.1.4).</summary>
        public bool TransformPrecoding { get; init; }
        /// <summary>pi/2-BPSK in place of the carrier's modulation (NR DFT-s-OFDM only).</summary>
        public bool Pi2Bpsk { get; init; }
        /// <summary>Contiguous allocation: first RB and RB count (null = from 0 / to the last RB).</summary>
        public int? RbStart { get; init; }
        public int? RbCount { get; init; }
        /// <summary>"NR-FR1-TM1.1" .. "NR-FR1-TM3.3", "E-TM1.1" .. "E-TM3.3" (with "2a", "2b", "3.1a", "3.1b").</summary>
        public string? TestModel { get; init; }
        /// <summary>TDD slot pattern, e.g. "DDDSU:10,2"; "TM" = the test model's own (TS 38.141-1 table 4.9.2.2-1,
        /// TS 36.141 table 6.1.1-1).</summary>
        public string? Tdd { get; init; }
        /// <summary>Physical cell ID: the reference signals' sequences (1 = the test models' lowest carrier).</summary>
        public int CellId { get; init; } = 1;

        public bool IsDefault => Equals(Default);
        private static readonly OfdmOptions Default = new();
    }

    public static class Ofdm3gpp
    {
        public static readonly string[] NrTestModels =
            ["NR-FR1-TM1.1", "NR-FR1-TM1.2", "NR-FR1-TM2", "NR-FR1-TM2a", "NR-FR1-TM2b", "NR-FR1-TM3.1", "NR-FR1-TM3.1a",
             "NR-FR1-TM3.1b", "NR-FR1-TM3.2", "NR-FR1-TM3.3"];
        public static readonly string[] LteTestModels =
            ["E-TM1.1", "E-TM1.2", "E-TM2", "E-TM2a", "E-TM2b", "E-TM3.1", "E-TM3.1a", "E-TM3.1b", "E-TM3.2", "E-TM3.3"];

        /// <summary>The grid of carrier <paramref name="c"/> with options <paramref name="o"/>, <paramref name="halfMs"/> x 0.5 ms.</summary>
        public static OfdmGrid Grid(CarrierSpec c, OfdmOptions o, int halfMs)
        {
            if (c.Standard is not (CarrierStandard.Lte or CarrierStandard.Nr))
                throw new ArgumentException("uplink, allocation, test-model and TDD options apply to LTE and NR carriers only");
            var nm = OfdmGenerator.Numerology(c);
            if (o.TestModel is not null)
            {
                if (o.Uplink) throw new ArgumentException($"{o.TestModel} is a downlink test model; turn Uplink off");
                return c.Standard == CarrierStandard.Lte ? Etm(c, nm, o, halfMs) : NrTm(c, nm, o, halfMs);
            }
            if (o.Pi2Bpsk && !(o.Uplink && c.Standard == CarrierStandard.Nr && o.TransformPrecoding))
                throw new ArgumentException("pi/2-BPSK is defined for NR uplink DFT-s-OFDM only (TS 38.211 cl. 6.3.1.4)");
            var (rb0, nrb) = Allocation(o, nm.NRb);
            if (o.Uplink) return c.Standard == CarrierStandard.Lte ? LteUplink(c, nm, o, rb0, nrb, halfMs) : NrUplink(c, nm, o, rb0, nrb, halfMs);
            return Downlink(c, nm, o, rb0, nrb, halfMs);
        }

        private static (int start, int count) Allocation(OfdmOptions o, int nRb)
        {
            int s = o.RbStart ?? 0, n = o.RbCount ?? nRb - s;
            if (s < 0 || n < 1 || s + n > nRb) throw new ArgumentException($"RB allocation {s}+{n} does not fit the carrier's {nRb} RB");
            return (s, n);
        }

        // ── shared pieces ───────────────────────────────────────────────────────────────────────────────────────────

        private sealed class Builder
        {
            public readonly Complex[][] X; public readonly ReRole[][] R; public readonly bool[] On; public readonly int[] Cp;
            public readonly Complex[]?[] Pre; public readonly int[] PreStart; public bool AnyPre;
            public Builder(int symbols, int cols, CarrierNumerology nm)
            {
                X = new Complex[symbols][]; R = new ReRole[symbols][]; On = new bool[symbols]; Cp = new int[symbols];
                Pre = new Complex[]?[symbols]; PreStart = new int[symbols];
                for (int s = 0; s < symbols; s++)
                {
                    X[s] = new Complex[cols]; R[s] = new ReRole[cols];
                    Cp[s] = s % nm.SymbolsPerHalfMs == 0 ? nm.CpLong : nm.CpNormal;
                }
            }
            public void Set(int s, int col, Complex v, ReRole r) { X[s][col] = v; R[s][col] = r; }
            public OfdmGrid Done(string label, CarrierNumerology nm, int[] bins, double shift = 0) => new()
            {
                Label = label, Nfft = nm.Nfft, SampleRateHz = nm.SampleRateHz, Cp = Cp, Bins = bins, X = X, Role = R, On = On,
                Shift = shift, Pre = AnyPre ? Pre : null, PreStart = AnyPre ? PreStart : null,
            };
        }

        private static int[] DlBins(CarrierNumerology nm)
        {
            var b = new int[nm.NSc];
            for (int i = 0; i < b.Length; i++) b[i] = OfdmGenerator.BinOf(nm, i);
            return b;
        }

        /// <summary>A QAM point of unit mean power, from the generator's own stream (the full-load path's formula).</summary>
        private static Complex Qam(Random rng, QamOrder m)
        {
            int lv = 1 << ((int)m / 2);
            double qn = 1.0 / Math.Sqrt(2.0 * (lv * lv - 1) / 3.0);
            return new Complex((2 * rng.Next(lv) - (lv - 1)) * qn, (2 * rng.Next(lv) - (lv - 1)) * qn);
        }

        /// <summary>pi/2-BPSK (TS 38.211 cl. 5.1.1): d(i) = e^(j pi/2 (i mod 2)) / sqrt(2) [(1 - 2b) + j (1 - 2b)].</summary>
        public static Complex Pi2Bpsk(int i, int bit)
        {
            double a = (1 - 2 * bit) / Math.Sqrt(2);
            var d = new Complex(a, a);
            return (i & 1) == 1 ? d * Complex.ImaginaryOne : d;
        }

        /// <summary>The length-31 Gold sequence c(0..n-1) of TS 38.211 cl. 5.2.1 / TS 36.211 cl. 7.2 (N_C = 1600).</summary>
        public static byte[] Gold(long cInit, int n)
        {
            const int nc = 1600;
            var x1 = new byte[nc + n + 31];
            var x2 = new byte[nc + n + 31];
            x1[0] = 1;
            for (int i = 0; i < 31; i++) x2[i] = (byte)((cInit >> i) & 1);
            for (int i = 0; i < nc + n; i++)
            {
                x1[i + 31] = (byte)((x1[i + 3] + x1[i]) & 1);
                x2[i + 31] = (byte)((x2[i + 3] + x2[i + 2] + x2[i + 1] + x2[i]) & 1);
            }
            var c = new byte[n];
            for (int i = 0; i < n; i++) c[i] = (byte)((x1[i + nc] + x2[i + nc]) & 1);
            return c;
        }

        /// <summary>QPSK reference symbols r(m) = ((1 - 2c(2m)) + j (1 - 2c(2m+1))) / sqrt 2, m = 0..n-1.</summary>
        private static Complex[] QpskRs(long cInit, int n)
        {
            var c = Gold(cInit, 2 * n);
            var r = new Complex[n];
            double a = 1 / Math.Sqrt(2);
            for (int m = 0; m < n; m++) r[m] = new Complex(a * (1 - 2 * c[2 * m]), a * (1 - 2 * c[2 * m + 1]));
            return r;
        }

        /// <summary>NR DM-RS Gold init (TS 38.211 cl. 7.4.1.1.1 / 6.4.1.1.1 / 7.4.1.3.1, n_SCID = 0, lambda = 0).</summary>
        private static long NrDmrsInit(int slot, int l, int nId) => ((1L << 17) * (14L * slot + l + 1) * (2L * nId + 1) + 2L * nId) % (1L << 31);

        private static bool IsPrime(int n) { if (n < 2) return false; for (int d = 2; d * d <= n; d++) if (n % d == 0) return false; return true; }

        /// <summary>Zadoff-Chu base sequence of length <paramref name="m"/> >= 36 (TS 38.211 cl. 5.2.2.1, TS 36.211 cl. 5.5.1.1).</summary>
        private static Complex[] ZcBase(int m, int u, int v)
        {
            int nzc = m - 1;
            while (!IsPrime(nzc)) nzc--;
            double qb = nzc * (u + 1) / 31.0;
            int q = (int)Math.Floor(qb + 0.5) + v * (((int)Math.Floor(2 * qb) & 1) == 0 ? 1 : -1);
            var r = new Complex[m];
            for (int n = 0; n < m; n++) { long k = n % nzc, e = q * k * (k + 1) % (2L * nzc); r[n] = Complex.FromPolarCoordinates(1, -Math.PI * e / nzc); }
            return r;
        }

        private static Complex[] PhiBase(int[] phi)
        {
            var r = new Complex[phi.Length];
            for (int n = 0; n < r.Length; n++) r[n] = Complex.FromPolarCoordinates(1, phi[n] * Math.PI / 4);
            return r;
        }

        /// <summary>NR low-PAPR sequence type 1 base (TS 38.211 cl. 5.2.2), alpha = 0.</summary>
        public static Complex[] LowPaprNr(int m, int u, int v = 0) => m switch
        {
            6 => PhiBase(Ofdm3gppTables.NrPhi6[u]),
            12 => PhiBase(Ofdm3gppTables.NrPhi12[u]),
            18 => PhiBase(Ofdm3gppTables.NrPhi18[u]),
            24 => PhiBase(Ofdm3gppTables.NrPhi24[u]),
            30 => Zc30(u),
            >= 36 => ZcBase(m, u, v),
            _ => throw new ArgumentException($"no low-PAPR base sequence of length {m}"),
        };

        private static Complex[] Zc30(int u)
        {
            var r = new Complex[30];
            for (int n = 0; n < 30; n++) r[n] = Complex.FromPolarCoordinates(1, -Math.PI * (u + 1) * (n + 1) * (n + 2) / 31.0);
            return r;
        }

        /// <summary>LTE uplink reference-signal base sequence (TS 36.211 cl. 5.5.1), alpha = 0.</summary>
        public static Complex[] LowPaprLte(int m, int u, int v = 0) => m switch
        {
            12 => PhiBase(Ofdm3gppTables.LtePhi12[u]),
            24 => PhiBase(Ofdm3gppTables.LtePhi24[u]),
            >= 36 => ZcBase(m, u, v),
            _ => throw new ArgumentException($"no LTE base sequence of length {m}"),
        };

        /// <summary>M = 2^a 3^b 5^c (the DFT sizes TS 38.211 cl. 6.3.1.4 and TS 36.211 cl. 5.3.3 allow).</summary>
        public static bool IsDftSize(int nrb)
        {
            foreach (int p in new[] { 2, 3, 5 }) while (nrb % p == 0) nrb /= p;
            return nrb == 1;
        }

        private static void RequireDftSize(int nrb)
        {
            if (IsDftSize(nrb)) return;
            int lo = nrb, hi = nrb;
            while (!IsDftSize(lo)) lo--;
            while (!IsDftSize(hi)) hi++;
            throw new ArgumentException($"a DFT-spread allocation of {nrb} RB is not 2^a 3^b 5^c RB; use {lo} or {hi}");
        }

        /// <summary>y = DFT(x) / sqrt(M).</summary>
        private static Complex[] Spread(Complex[] x)
        {
            var y = Fft.Forward(x);
            double s = 1 / Math.Sqrt(x.Length);
            for (int i = 0; i < y.Length; i++) y[i] *= s;
            return y;
        }

        /// <summary>Per symbol: transmitted on the link (<paramref name="uplink"/>) under a TDD pattern of
        /// <paramref name="perSlot"/>-symbol slots; null pattern = every symbol.</summary>
        public static bool[] TddMask(string? pattern, int perSlot, int symbols, bool uplink)
        {
            var on = new bool[symbols];
            if (pattern is null) { Array.Fill(on, true); return on; }
            var parts = pattern.Split(':');
            string slots = parts[0].Trim().ToUpperInvariant();
            if (slots.Length == 0 || slots.Trim('D', 'S', 'U').Length > 0)
                throw new ArgumentException($"TDD pattern \"{pattern}\": slots are D, S or U");
            var special = new List<(int dl, int ul)>();
            if (parts.Length > 1)
                foreach (var p in parts[1].Split(';'))
                {
                    var v = p.Split(',');
                    if (v.Length != 2 || !int.TryParse(v[0], out int d) || !int.TryParse(v[1], out int u) || d < 0 || u < 0 || d + u > perSlot)
                        throw new ArgumentException($"TDD pattern \"{pattern}\": an S slot is \"DL,UL\" symbols, at most {perSlot} in all");
                    special.Add((d, u));
                }
            if (slots.Contains('S') && special.Count == 0) throw new ArgumentException($"TDD pattern \"{pattern}\": give the S slot's DL,UL symbols after ':'");
            var sIndex = new int[slots.Length];
            for (int i = 0, j = 0; i < slots.Length; i++) if (slots[i] == 'S') sIndex[i] = j++;
            for (int s = 0; s < symbols; s++)
            {
                int slot = s / perSlot % slots.Length, l = s % perSlot;
                on[s] = slots[slot] switch
                {
                    'D' => !uplink,
                    'U' => uplink,
                    _ => special[Math.Min(sIndex[slot], special.Count - 1)] is var (d, u) && (uplink ? l >= perSlot - u : l < d),
                };
            }
            return on;
        }

        /// <summary>DL symbols of each slot under the mask (14 for a D slot, the DL count of an S slot, 0 otherwise).</summary>
        private static int DlSymbols(bool[] on, int slot)
        {
            int n = 0;
            while (n < 14 && on[slot * 14 + n]) n++;
            return n;
        }

        // ── downlink with an allocation (no reference signals, as the full-load path) ─────────────────────────────

        private static OfdmGrid Downlink(CarrierSpec c, CarrierNumerology nm, OfdmOptions o, int rb0, int nrb, int halfMs)
        {
            int symbols = nm.SymbolsPerHalfMs * halfMs;
            var b = new Builder(symbols, nm.NSc, nm);
            var on = TddMask(o.Tdd, 14, symbols, uplink: false);
            var rng = new Random(c.Seed);
            for (int s = 0; s < symbols; s++)
            {
                if (!(b.On[s] = on[s])) continue;
                for (int k = 12 * rb0; k < 12 * (rb0 + nrb); k++) b.Set(s, k, Qam(rng, c.Modulation), ReRole.Data);
            }
            return b.Done($"{c.Label} DL RB {rb0}+{nrb}", nm, DlBins(nm));
        }

        // ── NR uplink ───────────────────────────────────────────────────────────────────────────────────────────

        private static OfdmGrid NrUplink(CarrierSpec c, CarrierNumerology nm, OfdmOptions o, int rb0, int nrb, int halfMs)
        {
            bool tp = o.TransformPrecoding;
            if (tp) RequireDftSize(nrb);
            int symbols = nm.SymbolsPerHalfMs * halfMs, m = 12 * nrb, k0 = 12 * rb0;
            int mu = (int)Math.Round(Math.Log2(nm.ScsHz / 15e3)), slotsPerFrame = 10 << mu;
            var b = new Builder(symbols, nm.NSc, nm);
            var on = TddMask(o.Tdd, 14, symbols, uplink: true);
            var rng = new Random(c.Seed);
            var dmrsSeq = tp ? LowPaprNr(m / 2, o.CellId % 30) : null;
            double beta = tp ? Math.Sqrt(2) : 1;                 // two CDM groups without data: DM-RS 3 dB above PUSCH
            for (int s = 0; s < symbols; s++)
            {
                if (!(b.On[s] = on[s])) continue;
                int l = s % 14, slot = s / 14 % slotsPerFrame;
                if (l == 2 || l == 11)
                {
                    if (tp)
                        for (int n = 0; n < m / 2; n++) b.Set(s, k0 + 2 * n, beta * dmrsSeq![n], ReRole.Pilot);
                    else
                    {
                        // reference point: subcarrier 0 of CRB 0 (the carrier's lowest subcarrier), r(k/2) on even k
                        var r = QpskRs(NrDmrsInit(slot, l, o.CellId), (k0 + m) / 2 + 1);
                        for (int k = k0; k < k0 + m; k++)
                            if ((k & 1) == 0) b.Set(s, k, r[k / 2], ReRole.Pilot);
                            else b.Set(s, k, Qam(rng, c.Modulation), ReRole.Data);
                    }
                    continue;
                }
                if (tp)
                {
                    var x = new Complex[m];
                    for (int i = 0; i < m; i++) x[i] = o.Pi2Bpsk ? Pi2Bpsk(i, rng.Next(2)) : Qam(rng, c.Modulation);
                    var y = Spread(x);
                    for (int i = 0; i < m; i++) b.Set(s, k0 + i, y[i], ReRole.Data);
                    b.Pre[s] = x; b.PreStart[s] = k0; b.AnyPre = true;
                }
                else
                    for (int k = k0; k < k0 + m; k++) b.Set(s, k, Qam(rng, c.Modulation), ReRole.Data);
            }
            string mod = o.Pi2Bpsk ? "pi/2-BPSK" : c.Modulation.ToString();
            return b.Done($"{c.Label} UL {(tp ? "DFT-s-OFDM" : "CP-OFDM")} {mod} RB {rb0}+{nrb}", nm, DlBins(nm));
        }

        // ── LTE uplink SC-FDMA ──────────────────────────────────────────────────────────────────────────────────

        private static OfdmGrid LteUplink(CarrierSpec c, CarrierNumerology nm, OfdmOptions o, int rb0, int nrb, int halfMs)
        {
            RequireDftSize(nrb);
            int symbols = nm.SymbolsPerHalfMs * halfMs, m = 12 * nrb, k0 = 12 * rb0, nsc = nm.NSc;
            var b = new Builder(symbols, nsc, nm);
            var on = TddMask(o.Tdd, 14, symbols, uplink: true);
            var rng = new Random(c.Seed);
            var dmrs = LowPaprLte(m, o.CellId % 30);
            for (int s = 0; s < symbols; s++)
            {
                if (!(b.On[s] = on[s])) continue;
                if (s % 7 == 3) { for (int n = 0; n < m; n++) b.Set(s, k0 + n, dmrs[n], ReRole.Pilot); continue; }
                var x = new Complex[m];
                for (int i = 0; i < m; i++) x[i] = Qam(rng, c.Modulation);
                var y = Spread(x);
                for (int i = 0; i < m; i++) b.Set(s, k0 + i, y[i], ReRole.Data);
                b.Pre[s] = x; b.PreStart[s] = k0; b.AnyPre = true;
            }
            // TS 36.211 cl. 5.6: subcarrier k at (k + 1/2) df, k = -N_sc/2 .. N_sc/2 - 1; no DC hole
            var bins = new int[nsc];
            for (int i = 0; i < nsc; i++) bins[i] = ((i - nsc / 2) % nm.Nfft + nm.Nfft) % nm.Nfft;
            return b.Done($"{c.Label} UL SC-FDMA {c.Modulation} RB {rb0}+{nrb}", nm, bins, shift: 0.5);
        }

        /// <summary>LTE-M (eMTC) uplink: the 6 PRBs of narrowband <paramref name="narrowband"/> of an LTE uplink carrier of
        /// <paramref name="nRb"/> RB (TS 36.211 cl. 5.2.4: N_NB = floor(N_RB / 6), PRB 6 n + i0 + i, i0 = floor(N_RB / 2) - 3 N_NB).</summary>
        public static OfdmOptions LteM(int nRb, int narrowband)
        {
            int nnb = nRb / 6;
            if (narrowband < 0 || narrowband >= nnb) throw new ArgumentException($"{nRb} RB has narrowbands 0..{nnb - 1}");
            return new OfdmOptions { Uplink = true, RbStart = 6 * narrowband + nRb / 2 - 6 * nnb / 2, RbCount = 6 };
        }

        // ── NR-FR1 test models (TS 38.141-1 V19.6.0 cl. 4.9.2.2) ─────────────────────────────────────────────────

        /// <summary>RBG size P for a bandwidth part of <paramref name="size"/> RB, TS 38.214 table 5.1.2.2.1-1 configuration 1.</summary>
        private static int RbgSize(int size) => size <= 36 ? 2 : size <= 72 ? 4 : size <= 144 ? 8 : 16;

        /// <summary>Per RB of an NR-FR1 test model: modulation (null = not allocated in slot <paramref name="slot"/>),
        /// level (dB) and the first PDSCH symbol.</summary>
        public static (QamOrder? mod, double db, int start)[] NrTmAllocation(string tm, int nRb, int slot)
        {
            var a = new (QamOrder? mod, double db, int start)[nRb];
            QamOrder hi = tm.EndsWith('a') ? QamOrder.Qam256 : tm.EndsWith('b') ? QamOrder.Qam1024 : QamOrder.Qam64;
            switch (tm)
            {
                case "NR-FR1-TM1.1":
                case "NR-FR1-TM3.1" or "NR-FR1-TM3.1a" or "NR-FR1-TM3.1b":
                {
                    var m = tm == "NR-FR1-TM1.1" ? QamOrder.Qpsk : hi;
                    for (int r = 0; r < nRb; r++) a[r] = (m, 0, r < 3 ? 2 : 0);
                    return a;
                }
                case "NR-FR1-TM2" or "NR-FR1-TM2a" or "NR-FR1-TM2b":
                {
                    // table 4.9.2.2.3-1: slot 3n -> RB 0, 3n+1 -> floor(N_RB/2), 3n+2 -> N_RB - 1; n_RNTI = 2, from symbol 2
                    int rb = (slot % 3) switch { 0 => 0, 1 => nRb / 2, _ => nRb - 1 };
                    a[rb] = (hi, 0, 2);
                    return a;
                }
                case "NR-FR1-TM1.2" or "NR-FR1-TM3.2" or "NR-FR1-TM3.3":
                {
                    // tables 4.9.2.2.2-1, 4.9.2.2.7-1, 4.9.2.2.8-1 (RBGs of the BWP from RB 3, P from TS 38.214)
                    double x = tm == "NR-FR1-TM1.2" ? 0.4 : tm == "NR-FR1-TM3.2" ? 0.6 : 0.5;
                    double lvl = tm == "NR-FR1-TM1.2" ? 3 : tm == "NR-FR1-TM3.2" ? -3 : -6;
                    QamOrder listed = tm == "NR-FR1-TM3.2" ? QamOrder.Qam16 : QamOrder.Qpsk;
                    int size = nRb - 3, p = RbgSize(size), f = (size + 3 % p - p) / p;
                    int nRbg = Math.Min((int)Math.Floor(x * size / p), (f - f % 2) / 2 + 1);
                    var rbgs = new List<int> { (size + 3 % p) / p - 1 };
                    for (int j = 1; j <= 2 * (nRbg - 2) + 1 && nRbg > 1; j += 2) rbgs.Add(j);
                    double other = 10 * Math.Log10((size - Math.Pow(10, lvl / 10) * p * nRbg) / (size - p * nRbg));
                    for (int r = 0; r < nRb; r++) a[r] = r < 3 ? (QamOrder.Qpsk, 0, 2) : (QamOrder.Qpsk, other, 0);
                    foreach (int j in rbgs)
                    {
                        // RBG j of the BWP starting at RB 3: CRBs [max(3, (3/P + j) P), min(N_RB, (3/P + j + 1) P))
                        int lo = Math.Max(3, (3 / p + j) * p), hiRb = Math.Min(nRb, (3 / p + j + 1) * p);
                        for (int r = lo; r < hiRb; r++) a[r] = (listed, lvl, 0);
                    }
                    return a;
                }
                default: throw new ArgumentException($"unknown NR test model \"{tm}\"; one of {string.Join(", ", NrTestModels)}");
            }
        }

        private static OfdmGrid NrTm(CarrierSpec c, CarrierNumerology nm, OfdmOptions o, int halfMs)
        {
            string tm = o.TestModel!;
            int scsK = (int)Math.Round(nm.ScsHz / 1e3);
            if (scsK is not (15 or 30 or 60)) throw new ArgumentException($"{tm} is an FR1 test model (15, 30 or 60 kHz), not {scsK} kHz");
            if (Array.IndexOf(NrTestModels, tm) < 0) throw new ArgumentException($"unknown NR test model \"{tm}\"; one of {string.Join(", ", NrTestModels)}");
            int mu = (scsK / 15) switch { 1 => 0, 2 => 1, _ => 2 }, nRb = nm.NRb, slotsPerFrame = 10 << mu;
            int symbols = nm.SymbolsPerHalfMs * halfMs;
            // TS 38.141-1 table 4.9.2.2-1 (TDD-UL-DL-ConfigCommon, 5 ms)
            string? tdd = o.Tdd == "TM" ? mu switch
            {
                0 => "DDDSU:10,2",
                1 => "DDDDDDDSUU:6,4",
                _ => "DDDDDDDDDDDDDDSSUUUU:12,0;0,8",
            } : o.Tdd;
            var on = TddMask(tdd, 14, symbols, uplink: false);
            var b = new Builder(symbols, nm.NSc, nm);
            var rng = new Random(c.Seed);
            for (int s0 = 0; s0 < symbols; s0 += 14)
            {
                int slot = s0 / 14 % slotsPerFrame, ld = DlSymbols(on, s0 / 14);
                var alloc = NrTmAllocation(tm, nRb, slot);
                // PDSCH DM-RS, mapping type A, pos2, dmrs-AdditionalPosition pos1 (TS 38.211 table 7.4.1.1.2-3)
                int extra = ld >= 13 ? 11 : ld >= 10 ? 9 : ld >= 8 ? 7 : -1;
                for (int l = 0; l < 14; l++)
                {
                    int s = s0 + l;
                    if (!(b.On[s] = on[s])) continue;
                    bool dmrs = l == 2 || l == extra;
                    var rs = dmrs ? QpskRs(NrDmrsInit(slot, l, o.CellId), 6 * nRb) : null;
                    var pdcchRs = l < 2 ? QpskRs(NrDmrsInit(slot, l, o.CellId), 9) : null;
                    for (int rb = 0; rb < nRb; rb++)
                    {
                        if (l < 2 && rb < 3)
                        {
                            // PDCCH: 1 CCE = 6 REGs over RB 0-2, symbols 0-1 (table 4.9.2.2-2); DM-RS at k = 1, 5, 9 (cl. 7.4.1.3.2)
                            for (int kk = 0; kk < 12; kk++)
                            {
                                int k = 12 * rb + kk;
                                if (kk % 4 == 1) b.Set(s, k, pdcchRs![3 * rb + kk / 4], ReRole.Pilot);
                                else b.Set(s, k, Qam(rng, QamOrder.Qpsk), ReRole.Other);
                            }
                            continue;
                        }
                        var (mod, db, start) = alloc[rb];
                        if (mod is not QamOrder q || l < start) continue;
                        double amp = Math.Pow(10, db / 20);
                        for (int kk = 0; kk < 12; kk++)
                        {
                            int k = 12 * rb + kk;
                            if (dmrs && (k & 1) == 0) b.Set(s, k, amp * rs![k / 2], ReRole.Pilot);   // EPRE ratio PDSCH:DM-RS 0 dB
                            else b.Set(s, k, amp * Qam(rng, q), ReRole.Data);
                        }
                    }
                }
            }
            return b.Done($"{c.Label} {tm}{(tdd is null ? "" : " TDD " + tdd)}", nm, DlBins(nm));
        }

        // ── E-UTRA test models (TS 36.141 V19.1.0 cl. 6.1.1, 6.1.2) ───────────────────────────────────────────────

        private static readonly int[] LteNrb = [6, 15, 25, 50, 75, 100];
        private static readonly int[] TddSubframes = [0, 1, 5, 6, 7, 8, 9];
        public const string EtmTdd = "DSUUUDDDDD:11,1";       // table 6.1.1-1: UL/DL configuration 3, special subframe configuration 8

        /// <summary>The E-TM's table and the modulations of its listed and other PRBs.</summary>
        private static (Ofdm3gppTables.Etm t, QamOrder listed, QamOrder other) EtmOf(string tm) => tm switch
        {
            "E-TM1.1" => (Ofdm3gppTables.Etm11, QamOrder.Qpsk, QamOrder.Qpsk),
            "E-TM1.2" => (Ofdm3gppTables.Etm12, QamOrder.Qpsk, QamOrder.Qpsk),
            "E-TM2" => (Ofdm3gppTables.Etm2, QamOrder.Qam64, QamOrder.Qam64),
            "E-TM2a" => (Ofdm3gppTables.Etm2, QamOrder.Qam256, QamOrder.Qam256),
            "E-TM2b" => (Ofdm3gppTables.Etm2, QamOrder.Qam1024, QamOrder.Qam1024),
            "E-TM3.1" => (Ofdm3gppTables.Etm31, QamOrder.Qam64, QamOrder.Qam64),
            "E-TM3.1a" => (Ofdm3gppTables.Etm31, QamOrder.Qam256, QamOrder.Qam256),
            "E-TM3.1b" => (Ofdm3gppTables.Etm31, QamOrder.Qam1024, QamOrder.Qam1024),
            "E-TM3.2" => (Ofdm3gppTables.Etm32, QamOrder.Qam16, QamOrder.Qpsk),
            "E-TM3.3" => (Ofdm3gppTables.Etm33, QamOrder.Qpsk, QamOrder.Qam16),
            _ => throw new ArgumentException($"unknown E-UTRA test model \"{tm}\"; one of {string.Join(", ", LteTestModels)}"),
        };

        /// <summary>Per PRB of an E-TM in subframe <paramref name="sf"/> (of frame <paramref name="frame"/>, TDD): modulation
        /// (null = not allocated) and PA = E_A / E_RS (dB).</summary>
        public static (QamOrder? mod, double paDb)[] EtmAllocation(string tm, int nRb, int sf, bool tdd, int frame)
        {
            var (t, mListed, mOther) = EtmOf(tm);
            int bw = Array.IndexOf(LteNrb, nRb);
            if (bw < 0) throw new ArgumentException($"E-TMs are defined for 1.4..20 MHz, not {nRb} RB");
            int[]? list;
            if (t.Fdd is null) list = null;                                      // every PRB is "listed"
            else if (!tdd) list = t.Fdd[bw][sf] ?? [];
            else
            {
                int i = Array.IndexOf(TddSubframes, sf);
                list = i < 0 ? [] : t.Tdd![bw][7 * (frame % 2) + i] ?? [];
            }
            // Note 1 (1.4 MHz): no boosting / deboosting in subframes with PBCH or synchronisation signals
            bool noBoost = bw == 0 && (tdd ? sf is 0 or 1 or 5 or 6 : sf is 0 or 5);
            var a = new (QamOrder? mod, double paDb)[nRb];
            for (int r = 0; r < nRb; r++)
            {
                bool listed = list is null || Array.IndexOf(list, r) >= 0;
                if (listed) a[r] = (mListed, noBoost ? 0 : t.ListedPaDb[bw]);
                else if (t.OtherCount[bw] > 0 && !double.IsNaN(t.OtherPaDb[bw])) a[r] = (mOther, noBoost ? 0 : t.OtherPaDb[bw]);
                else a[r] = (null, double.NegativeInfinity);
            }
            return a;
        }

        private static OfdmGrid Etm(CarrierSpec c, CarrierNumerology nm, OfdmOptions o, int halfMs)
        {
            string tm = o.TestModel!;
            var (t, _, _) = EtmOf(tm);
            int nRb = nm.NRb, bw = Array.IndexOf(LteNrb, nRb), nsc = nm.NSc, cell = o.CellId;
            bool tdd = o.Tdd is not null;
            if (tdd && o.Tdd != "TM" && o.Tdd != EtmTdd)
                throw new ArgumentException($"a TDD E-TM uses UL/DL configuration 3 with special subframe 8 (\"{EtmTdd}\" or \"TM\")");
            int symbols = nm.SymbolsPerHalfMs * halfMs;
            var on = TddMask(tdd ? EtmTdd : null, 14, symbols, uplink: false);
            var b = new Builder(symbols, nsc, nm);
            var rng = new Random(c.Seed);
            int vshift = cell % 6, cfi = t.Cfi[bw];
            var pss = Pss(cell % 3);
            var sss0 = Sss(cell, subframe5: false);
            var sss5 = Sss(cell, subframe5: true);
            double ampSync = Math.Pow(10, t.SyncDb[bw] / 20), ampPbch = Math.Pow(10, t.PbchDb[bw] / 20);
            var used = new bool[nsc];
            for (int s0 = 0; s0 < symbols; s0 += 14)
            {
                int sfAll = s0 / 14, sf = sfAll % 10, frame = sfAll / 10;
                var alloc = EtmAllocation(tm, nRb, sf, tdd, frame);
                int ndl = DlSymbols(on, sfAll);
                for (int l = 0; l < 14; l++)
                {
                    int s = s0 + l;
                    if (!(b.On[s] = on[s])) continue;
                    Array.Clear(used);
                    int ns = 2 * sf + l / 7, l7 = l % 7;
                    // CRS, antenna port 0 (cl. 6.10.1): l = 0 and N_symb - 3 of each slot, k = 6m + (v + v_shift) mod 6
                    if (l7 == 0 || l7 == 4)
                    {
                        var r = QpskRs((1L << 10) * (7 * (ns + 1) + l7 + 1) * (2 * cell + 1) + 2 * cell + 1, 220);
                        int v = l7 == 0 ? 0 : 3;
                        for (int m = 0; m < 2 * nRb; m++)
                        {
                            int k = 6 * m + (v + vshift) % 6;
                            b.Set(s, k, r[m + 110 - nRb], ReRole.Pilot);
                            used[k] = true;
                        }
                    }
                    // PSS / SSS (cl. 6.11): FDD last two symbols of slots 0 and 10; TDD PSS in symbol 2 of subframes 1, 6 and SSS
                    // in the last symbol of slots 1 and 11; the 5 subcarriers either side are reserved
                    bool isPss = tdd ? (sf is 1 or 6) && l == 2 : (sf is 0 or 5) && l == 6;
                    bool isSss = tdd ? (sf is 0 or 5) && l == 13 : (sf is 0 or 5) && l == 5;
                    if (isPss || isSss)
                    {
                        var d = isPss ? pss : sf == 0 ? sss0 : sss5;
                        for (int n = -5; n < 67; n++)
                        {
                            int k = n - 31 + 6 * nRb;
                            used[k] = true;
                            if (n >= 0 && n < 62) b.Set(s, k, ampSync * d[n], ReRole.Other);
                        }
                    }
                    // PBCH (cl. 6.6.4): subframe 0, slot 1, symbols 0-3, the 72 centre subcarriers, skipping the four-port CRS
                    if (sf == 0 && l >= 7 && l <= 10)
                        for (int kp = 0; kp < 72; kp++)
                        {
                            int k = 6 * nRb - 36 + kp;
                            if (used[k]) continue;
                            used[k] = true;
                            if (l7 <= 1 && k % 3 == vshift % 3) continue;
                            b.Set(s, k, ampPbch * Qam(rng, QamOrder.Qpsk), ReRole.Other);
                        }
                    if (l < cfi) { Control(b, s, l, nRb, vshift, t, bw, used, rng); continue; }
                    if (l >= ndl) continue;
                    // PDSCH: E_B / E_A = P_B = 1, so the same level in CRS symbols
                    for (int rb = 0; rb < nRb; rb++)
                    {
                        if (alloc[rb].mod is not QamOrder q) continue;
                        double amp = Math.Pow(10, alloc[rb].paDb / 20);
                        for (int k = 12 * rb; k < 12 * rb + 12; k++)
                            if (!used[k]) b.Set(s, k, amp * Qam(rng, q), ReRole.Data);
                    }
                }
            }
            return b.Done($"{c.Label} {tm}{(tdd ? " TDD" : "")}", nm, DlBins(nm));
        }

        /// <summary>The control region of symbol <paramref name="l"/> (&lt; CFI): REGs of four free REs (cl. 6.2.4; in symbol 0
        /// the two- port CRS positions are excluded), of which PCFICH (4 REGs), PHICH (3 per group) and PDCCH (the table's
        /// count) are filled with QPSK at their EPREs, spread evenly over the REGs of both symbols; the rest are NIL.</summary>
        private static void Control(Builder b, int s, int l, int nRb, int vshift, Ofdm3gppTables.Etm t, int bw, bool[] used, Random rng)
        {
            int cfi = t.Cfi[bw];
            int Regs(int sym) => sym == 0 ? 2 * nRb : 3 * nRb;             // per RB: 2 REGs with CRS, 3 without (1-2 ports)
            int total = 0;
            for (int j = 0; j < cfi; j++) total += Regs(j);
            int pcfich = 4, phich = 3 * t.PhichGroups[bw], pdcch = t.PdcchRegs[bw], use = Math.Min(total, pcfich + phich + pdcch);
            // REG index across the region: symbol 0's first, then symbol 1's; REG g of the region carries channel g' when
            // g = floor(g' total / use)
            int first = 0;
            for (int j = 0; j < l; j++) first += Regs(j);
            var free = new List<int>();
            for (int k = 0; k < 12 * nRb; k++)
                if (!used[k] && !(l == 0 && k % 3 == vshift % 3)) free.Add(k);
            for (int g = 0; g < Regs(l); g++)
            {
                int gg = first + g;
                int idx = -1;
                for (int u = 0; u < use; u++) if ((long)u * total / use == gg) { idx = u; break; }
                if (idx < 0) continue;
                double db = idx < pcfich ? t.PcfichDb[bw] : idx < pcfich + phich ? 0 : t.PdcchDb[bw];
                double amp = Math.Pow(10, db / 20);
                for (int e = 0; e < 4; e++) b.Set(s, free[4 * g + e], amp * Qam(rng, QamOrder.Qpsk), ReRole.Other);
            }
        }

        /// <summary>The primary synchronisation signal d_u(0..61) for N_ID^(2) (TS 36.211 cl. 6.11.1.1, u = 25, 29, 34).</summary>
        public static Complex[] Pss(int nid2)
        {
            int u = nid2 switch { 0 => 25, 1 => 29, _ => 34 };
            var d = new Complex[62];
            for (int n = 0; n < 62; n++)
                d[n] = n < 31 ? Complex.FromPolarCoordinates(1, -Math.PI * u * n * (n + 1) / 63.0)
                              : Complex.FromPolarCoordinates(1, -Math.PI * u * (n + 1) * (n + 2) / 63.0);
            return d;
        }

        /// <summary>The secondary synchronisation signal d(0..61) of subframe 0 or 5 (TS 36.211 cl. 6.11.2.1).</summary>
        public static Complex[] Sss(int cellId, bool subframe5)
        {
            int n1 = cellId / 3, n2 = cellId % 3;
            int qp = n1 / 30, q = (n1 + qp * (qp + 1) / 2) / 30, mp = n1 + q * (q + 1) / 2, m0 = mp % 31, m1 = (m0 + mp / 31 + 1) % 31;
            int[] M(Func<int[], int, int> step)
            {
                var x = new int[31];
                x[4] = 1;
                for (int i = 0; i + 5 < 31; i++) x[i + 5] = step(x, i);
                var r = new int[31];
                for (int i = 0; i < 31; i++) r[i] = 1 - 2 * x[i];
                return r;
            }
            var st = M((x, i) => (x[i + 2] + x[i]) & 1);
            var ct = M((x, i) => (x[i + 3] + x[i]) & 1);
            var zt = M((x, i) => (x[i + 4] + x[i + 2] + x[i + 1] + x[i]) & 1);
            var d = new Complex[62];
            for (int n = 0; n < 31; n++)
            {
                int s0 = st[(n + m0) % 31], s1 = st[(n + m1) % 31];
                int c0 = ct[(n + n2) % 31], c1 = ct[(n + n2 + 3) % 31];
                int z0 = zt[(n + m0 % 8) % 31], z1 = zt[(n + m1 % 8) % 31];
                d[2 * n] = subframe5 ? s1 * c0 : s0 * c0;
                d[2 * n + 1] = subframe5 ? s0 * c1 * z1 : s1 * c1 * z0;
            }
            return d;
        }

        // ── NB-IoT uplink (TS 36.211 V19.3.0 cl. 10.1) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// NB-IoT NPUSCH format 1 on one 180 kHz carrier at 1.92 MS/s, <paramref name="slots"/> slots: 3.75 kHz single-tone
        /// (FFT 512, CP 16, 7 symbols and a 144-sample guard per 2 ms slot, DM-RS in symbol 4) or 15 kHz (FFT 128, the LTE CP,
        /// DM-RS in symbol 3) with 1, 3, 6 or 12 tones from <paramref name="firstTone"/>. Single-tone: pi/2-BPSK or pi/4-QPSK
        /// with the phase rotation of cl. 10.1.5 and the DM-RS of cl. 10.1.4.1.1 (c_init 35, w = row 0 of table 10.1.4.1.1-1);
        /// multi-tone: SC-FDMA QPSK with the DM-RS of cl. 10.1.4.1.2 (alpha = 0). Subcarrier k sits at (k + 1/2) df.
        /// </summary>
        public static OfdmGrid NbIotUplink(double scsHz, int tones, int firstTone, bool qpsk, int slots, int seed, int cellId = 0)
        {
            bool narrow = Math.Abs(scsHz - 3.75e3) < 1;
            if (!narrow && Math.Abs(scsHz - 15e3) > 1) throw new ArgumentException("NB-IoT uplink subcarrier spacing is 3.75 or 15 kHz");
            int nsc = narrow ? 48 : 12, n = narrow ? 512 : 128, dmrsL = narrow ? 4 : 3;
            if (narrow && tones != 1) throw new ArgumentException("3.75 kHz NB-IoT is single-tone");
            if (tones is not (1 or 3 or 6 or 12) || firstTone < 0 || firstTone + tones > nsc || firstTone % tones != 0)
                throw new ArgumentException($"NB-IoT: {tones} tones from tone {firstTone} is not an allocation of the {nsc} subcarriers");
            int symbols = 7 * slots;
            var cp = new int[symbols];
            var gap = new int[symbols];
            for (int s = 0; s < symbols; s++)
            {
                cp[s] = narrow ? 16 : s % 7 == 0 ? 10 : 9;
                if (narrow && s % 7 == 6) gap[s] = 144;                       // 2 ms slot = 7 x 528 + 144 samples
            }
            var x = new Complex[symbols][];
            var role = new ReRole[symbols][];
            var pre = new Complex[]?[symbols];
            var preStart = new int[symbols];
            var rng = new Random(seed);
            var c = Gold(35, slots);
            Complex[]? mtDmrs = tones switch
            {
                3 => PhiBase(Ofdm3gppTables.NbPhi3[cellId % 12]),
                6 => PhiBase(Ofdm3gppTables.NbPhi6[cellId % 14]),
                12 => LowPaprLte(12, cellId % 30),
                _ => null,
            };
            double rho = qpsk ? Math.PI / 4 : Math.PI / 2, phi = 0;
            int k = firstTone - nsc / 2;                                       // single tone: k in -24..23 or -6..5
            for (int s = 0; s < symbols; s++)
            {
                var row = x[s] = new Complex[nsc];
                var r = role[s] = new ReRole[nsc];
                bool dmrs = s % 7 == dmrsL;
                if (tones == 1)
                {
                    // cl. 10.1.5: phi(l) = phi(l-1) + 2 pi (k + 1/2)(N + N_CP,l)/N, plus rho (l mod 2)
                    if (s > 0) phi += 2 * Math.PI * (k + 0.5) * (n + cp[s]) / n;
                    Complex a;
                    if (dmrs) a = new Complex(1, 1) / Math.Sqrt(2) * (1 - 2 * c[s / 7]);
                    else if (qpsk) a = new Complex(1 - 2 * rng.Next(2), 1 - 2 * rng.Next(2)) / Math.Sqrt(2);
                    else a = new Complex(1, 1) / Math.Sqrt(2) * (1 - 2 * rng.Next(2));
                    row[firstTone] = a * Complex.FromPolarCoordinates(1, rho * (s % 2) + phi);
                    r[firstTone] = dmrs ? ReRole.Pilot : ReRole.Data;
                    continue;
                }
                if (dmrs) { for (int i = 0; i < tones; i++) { row[firstTone + i] = mtDmrs![i]; r[firstTone + i] = ReRole.Pilot; } continue; }
                var d = new Complex[tones];
                for (int i = 0; i < tones; i++) d[i] = new Complex(1 - 2 * rng.Next(2), 1 - 2 * rng.Next(2)) / Math.Sqrt(2);
                var y = Spread(d);
                for (int i = 0; i < tones; i++) { row[firstTone + i] = y[i]; r[firstTone + i] = ReRole.Data; }
                pre[s] = d; preStart[s] = firstTone;
            }
            var bins = new int[nsc];
            for (int i = 0; i < nsc; i++) bins[i] = ((i - nsc / 2) % n + n) % n;
            var on = new bool[symbols];
            Array.Fill(on, true);
            string mod = tones > 1 ? "QPSK" : qpsk ? "pi/4-QPSK" : "pi/2-BPSK";
            return new OfdmGrid
            {
                Label = $"NB-IoT UL {scsHz / 1e3:0.##} kHz {tones}-tone {mod}", Nfft = n, SampleRateHz = 1.92e6, Cp = cp, Gap = gap,
                Bins = bins, X = x, Role = role, On = on, Shift = 0.5, Pre = tones > 1 ? pre : null, PreStart = tones > 1 ? preStart : null,
            };
        }
    }
}
