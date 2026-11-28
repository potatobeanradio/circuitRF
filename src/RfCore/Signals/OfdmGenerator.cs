// ================================================================
//  OfdmGenerator.cs  —  LTE / NR downlink-structured OFDM carriers
//
//  Produces one carrier as a PERIODIC complex baseband record at the
//  carrier's native rate (SCS x FFT size), fully loaded with random
//  QAM on every resource element (the statistics of a full-load test
//  model; no control channels are coded — ACLR, PAPR, CCDF and
//  amplifier distortion depend on the loading and the numerology, not
//  on the channel coding).
//
//  Numerology:
//    LTE   15 kHz, N_RB {6,15,25,50,75,100} for {1.4,3,5,10,15,20} MHz,
//          FFT {128..2048}, CP 160/144 x N/2048 (7 symbols / 0.5 ms),
//          DC subcarrier unused.
//    NR    SCS 15/30/60/120 kHz, N_RB from the transmission-bandwidth
//          tables (TS 38.101-1 for FR1; TS 38.101-2 for FR2: 50-400 MHz;
//          FR2-2 at 480 and 960 kHz: 400-2000 MHz),
//          FFT = smallest power of two >= N_sc/0.85 (min 128),
//          CP 144 x N/2048, long CP (144+16*2^mu) x N/2048 on the first
//          symbol of every 0.5 ms; DC subcarrier used.
//  Window: raised-cosine WOLA over `windowFraction` of the normal CP,
//  cyclic over the record so the loop seam is as smooth as any symbol
//  boundary.  Optional spectral shaping: a per-bin magnitude ceiling
//  outside the occupied band (in-band bins untouched).
//  Uplink (DFT-s-OFDM, SC-FDMA), RB allocation, the 3GPP test models
//  and TDD gating: the OfdmOptions overload, through Ofdm3gpp's grid.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Signals
{
    public sealed record CarrierNumerology(int NRb, int NSc, int Nfft, double ScsHz, int SymbolsPerHalfMs,
                                           int CpNormal, int CpLong, bool DcUsed)
    {
        public double SampleRateHz => ScsHz * Nfft;
        public double OccupiedBwHz => NSc * ScsHz;
        public int SamplesPerHalfMs => SymbolsPerHalfMs * (Nfft + CpNormal) + (CpLong - CpNormal);
    }

    public static class OfdmGenerator
    {
        private static readonly Dictionary<double, (int nrb, int nfft)> LteTable = new()
        {
            [1.4e6] = (6, 128), [3e6] = (15, 256), [5e6] = (25, 512),
            [10e6] = (50, 1024), [15e6] = (75, 1536), [20e6] = (100, 2048),
        };

        // Maximum transmission bandwidth configuration N_RB, by SCS then channel BW (MHz): FR1 (TS 38.101-1 Table 5.3.2-1)
        // and FR2 (TS 38.101-2 Table 5.3.2-1). FR2 allows 60 kHz at 50, 100 and 200 MHz and 120 kHz at 50 to 400 MHz;
        // at 60 kHz, 50 and 100 MHz are FR1's too and keep FR1's N_RB (65, 135; FR2's are 66, 132), and 200 MHz is FR2's.
        private static readonly Dictionary<int, Dictionary<int, int>> NrTable = new()
        {
            [15] = new() { [5] = 25, [10] = 52, [15] = 79, [20] = 106, [25] = 133, [30] = 160, [40] = 216, [50] = 270 },
            [30] = new() { [5] = 11, [10] = 24, [15] = 38, [20] = 51, [25] = 65, [30] = 78, [40] = 106, [50] = 133,
                           [60] = 162, [70] = 189, [80] = 217, [90] = 245, [100] = 273 },
            [60] = new() { [10] = 11, [15] = 18, [20] = 24, [25] = 31, [30] = 38, [40] = 51, [50] = 65, [60] = 79,
                           [70] = 93, [80] = 107, [90] = 121, [100] = 135, [200] = 264 },
            [120] = new() { [50] = 32, [100] = 66, [200] = 132, [400] = 264 },
            // FR2-2 (TS 38.101-2 V19.5.0 Table 5.3.2-1; 480 and 960 kHz are optional SCS there)
            [480] = new() { [400] = 66, [800] = 124, [1600] = 248 },
            [960] = new() { [400] = 33, [800] = 62, [1600] = 124, [2000] = 148 },
        };

        public static IReadOnlyCollection<double> LteBandwidths => LteTable.Keys;

        public static IEnumerable<int> NrBandwidthsMHz(double scsHz)
            => NrTable.TryGetValue((int)Math.Round(scsHz / 1e3), out var t) ? t.Keys : Array.Empty<int>();

        public static CarrierNumerology Numerology(CarrierSpec c)
        {
            if (c.Standard == CarrierStandard.Lte)
            {
                foreach (var kv in LteTable)
                    if (Math.Abs(kv.Key - c.BandwidthHz) < 1e3)
                    {
                        int n = kv.Value.nfft;
                        return new CarrierNumerology(kv.Value.nrb, 12 * kv.Value.nrb, n, 15e3, 7,
                                                     144 * n / 2048, 160 * n / 2048, DcUsed: false);
                    }
                throw new ArgumentException($"LTE channel bandwidth {c.BandwidthHz / 1e6:0.###} MHz is not one of 1.4, 3, 5, 10, 15, 20 MHz");
            }
            if (c.Standard == CarrierStandard.Nr)
            {
                int scsK = (int)Math.Round(c.ScsHz / 1e3);
                if (!NrTable.TryGetValue(scsK, out var t))
                    throw new ArgumentException($"NR subcarrier spacing {scsK} kHz is not one of 15, 30, 60, 120, 480, 960 kHz");
                int bw = (int)Math.Round(c.BandwidthHz / 1e6);
                if (!t.TryGetValue(bw, out int nrb))
                    throw new ArgumentException($"NR {bw} MHz is not defined at {scsK} kHz");
                int nsc = 12 * nrb;
                int n = 128;
                while (n < nsc / 0.85) n <<= 1;
                int mu = (int)Math.Round(Math.Log2(scsK / 15.0));
                return new CarrierNumerology(nrb, nsc, n, c.ScsHz, 7 << mu, 144 * n / 2048,
                                             (144 + (16 << mu)) * n / 2048, DcUsed: true);
            }
            throw new ArgumentException("numerology is defined only for LTE and NR carriers");
        }

        /// <summary>
        /// One carrier as a periodic record of <paramref name="halfMsCount"/> x 0.5 ms at the native rate,
        /// normalised to unit mean power.
        /// </summary>
        public static Complex[] Generate(CarrierSpec c, int halfMsCount, double windowFraction, double? shapingFloorDbc)
        {
            var nm = Numerology(c);
            int n = nm.Nfft, nsc = nm.NSc;
            int symbols = nm.SymbolsPerHalfMs * halfMsCount;
            int total = nm.SamplesPerHalfMs * halfMsCount;
            var rec = new Complex[total];
            var rng = new Random(c.Seed);
            int bitsPerAxis = (int)c.Modulation / 2;
            int levels = 1 << bitsPerAxis;
            double qamNorm = 1.0 / Math.Sqrt(2.0 * (levels * levels - 1) / 3.0);
            var ramp = Ramp(Math.Max(0, (int)Math.Round(windowFraction * nm.CpNormal)));

            var bins = new Complex[n];
            var row = new Complex[nsc];
            int pos = 0;
            for (int s = 0; s < symbols; s++)
            {
                Array.Clear(bins);
                DrawRow(rng, levels, qamNorm, row);
                for (int i = 0; i < nsc; i++) bins[BinOf(nm, i)] = row[i];
                Fft.Transform(bins, inverse: true);                               // unnormalised: sum over bins
                double sc = 1.0 / Math.Sqrt(n);
                int cp = (s % nm.SymbolsPerHalfMs == 0) ? nm.CpLong : nm.CpNormal;
                AddSymbol(rec, pos, bins, cp, ramp, sc);
                pos += cp + n;
            }
            Normalise(rec);
            if (shapingFloorDbc is double floor)
            {
                Shape(rec, nm.SampleRateHz, nm.OccupiedBwHz + 2 * nm.ScsHz, c.BandwidthHz, floor);
                Normalise(rec);
            }
            return rec;
        }

        /// <summary>
        /// The carrier with the uplink, allocation, test-model and TDD options of <paramref name="o"/> (see
        /// <see cref="Ofdm3gpp"/>). With every option unset this is <see cref="Generate(CarrierSpec, int, double, double?)"/>,
        /// sample for sample.
        /// </summary>
        public static Complex[] Generate(CarrierSpec c, int halfMsCount, double windowFraction, double? shapingFloorDbc, OfdmOptions? o)
        {
            if (o is null || o.IsDefault) return Generate(c, halfMsCount, windowFraction, shapingFloorDbc);
            var nm = Numerology(c);
            var rec = Ofdm3gpp.Grid(c, o, halfMsCount).Synthesize(windowFraction);
            if (shapingFloorDbc is double floor)
            {
                Shape(rec, nm.SampleRateHz, nm.OccupiedBwHz + 2 * nm.ScsHz, c.BandwidthHz, floor);
                Normalise(rec);
            }
            return rec;
        }

        /// <summary>The raised-cosine WOLA ramp of <paramref name="wlen"/> samples, rising.</summary>
        internal static double[] Ramp(int wlen)
        {
            var ramp = new double[wlen];
            for (int i = 0; i < wlen; i++) ramp[i] = 0.5 * (1 - Math.Cos(Math.PI * (i + 0.5) / wlen));
            return ramp;
        }

        /// <summary>
        /// Adds one symbol to the periodic record at <paramref name="pos"/>: the IFFT output <paramref name="bins"/> with
        /// its cyclic prefix and a cyclic suffix for the window tail, scaled by <paramref name="sc"/> and the ramp. A
        /// <paramref name="shift"/> (subcarriers) moves every subcarrier by that much, its phase referred to the end of the
        /// cyclic prefix (the LTE uplink's e^(j2π(k+1/2)Δf(t-N_CP T_s)), TS 36.211 cl. 5.6).
        /// </summary>
        internal static void AddSymbol(Complex[] rec, int pos, Complex[] bins, int cp, double[] ramp, double sc, double shift = 0)
        {
            int n = bins.Length, wlen = ramp.Length, len = cp + n, total = rec.Length;
            for (int i = 0; i < len + wlen; i++)
            {
                int src = ((i - cp) % n + n) % n;
                double w = 1.0;
                if (i < wlen) w = ramp[i];
                else if (i >= len) w = ramp[wlen - 1 - (i - len)];
                if (shift == 0) rec[(pos + i) % total] += bins[src] * (sc * w);
                else rec[(pos + i) % total] += bins[src] * (sc * w) * Complex.FromPolarCoordinates(1, 2 * Math.PI * shift * (i - cp) / n);
            }
        }

        /// <summary>FFT bin of subcarrier <paramref name="i"/> (0 = lowest occupied), DC skipped when unused.</summary>
        public static int BinOf(CarrierNumerology nm, int i)
        {
            int nsc = nm.NSc, n = nm.Nfft;
            int k = nm.DcUsed ? i - nsc / 2 : i < nsc / 2 ? i - nsc / 2 : i - nsc / 2 + 1;
            return (k % n + n) % n;
        }

        /// <summary>One symbol's QAM points, lowest subcarrier first, from the generator's own random stream.</summary>
        private static void DrawRow(Random rng, int levels, double qamNorm, Complex[] row)
        {
            for (int i = 0; i < row.Length; i++)
            {
                double re = (2 * rng.Next(levels) - (levels - 1)) * qamNorm;
                double im = (2 * rng.Next(levels) - (levels - 1)) * qamNorm;
                row[i] = new Complex(re, im);
            }
        }

        /// <summary>
        /// The QAM points <see cref="Generate"/> puts on every resource element of a <paramref name="halfMsCount"/> x
        /// 0.5 ms record (unit mean power), one row per OFDM symbol — the reference an EVM is measured against.
        /// </summary>
        public static Complex[][] IdealSymbols(CarrierSpec c, int halfMsCount)
        {
            var nm = Numerology(c);
            var rng = new Random(c.Seed);
            int levels = 1 << ((int)c.Modulation / 2);
            double qamNorm = 1.0 / Math.Sqrt(2.0 * (levels * levels - 1) / 3.0);
            var rows = new Complex[nm.SymbolsPerHalfMs * halfMsCount][];
            for (int s = 0; s < rows.Length; s++) DrawRow(rng, levels, qamNorm, rows[s] = new Complex[nm.NSc]);
            return rows;
        }

        public static void Normalise(Complex[] x)
        {
            double p = 0;
            foreach (var v in x) p += v.Real * v.Real + v.Imaginary * v.Imaginary;
            p /= x.Length;
            if (p <= 0) return;
            double s = 1.0 / Math.Sqrt(p);
            for (int i = 0; i < x.Length; i++) x[i] *= s;
        }

        /// <summary>
        /// Per-bin magnitude ceiling outside the occupied band: untouched inside +-occ/2, a raised-cosine (in dB)
        /// fall from the in-band peak to <paramref name="floorDbc"/> between occ/2 and BW/2, the floor beyond.
        /// </summary>
        public static void Shape(Complex[] x, double fs, double occBw, double chBw, double floorDbc)
        {
            int n = x.Length;
            var X = Fft.Forward(x);
            double df = fs / n, mean = 0, peak = 0; int cnt = 0;
            for (int k = 0; k < n; k++)
            {
                double f = (k < (n + 1) / 2 ? k : k - n) * df;
                if (Math.Abs(f) <= occBw / 2)
                {
                    double p = X[k].Real * X[k].Real + X[k].Imaginary * X[k].Imaginary;
                    mean += p; cnt++; peak = Math.Max(peak, p);
                }
            }
            if (cnt == 0) return;
            mean /= cnt;
            double floorMag = Math.Sqrt(mean) * Math.Pow(10, floorDbc / 20);
            double peakMag = Math.Sqrt(peak);
            double edge = occBw / 2, outer = Math.Max(chBw / 2, edge + df);
            for (int k = 0; k < n; k++)
            {
                double f = Math.Abs((k < (n + 1) / 2 ? k : k - n) * df);
                if (f <= edge) continue;
                double ceilDb;
                if (f >= outer) ceilDb = 20 * Math.Log10(floorMag);
                else
                {
                    double u = (f - edge) / (outer - edge);
                    double a = 0.5 * (1 + Math.Cos(Math.PI * u));
                    ceilDb = 20 * Math.Log10(floorMag) + a * (20 * Math.Log10(peakMag) - 20 * Math.Log10(floorMag));
                }
                double ceil = Math.Pow(10, ceilDb / 20), m = X[k].Magnitude;
                if (m > ceil) X[k] *= ceil / m;
            }
            var y = Fft.Inverse(X);
            Array.Copy(y, x, n);
        }

        /// <summary>
        /// Least oversampling at which <see cref="Clip"/> cancels peaks. It works at the next power of two at or above
        /// this, so every FFT in its loop is radix-2: an LTE / NR record is 2^k x 15 samples, and Bluestein at that
        /// length made one NR 100 clip take over a minute. Resampling a band-limited periodic record to any length is
        /// exact, so the length need not be a multiple of the record's.
        /// </summary>
        public const int ClipOversample = 4;

        /// <summary>
        /// Oversampling at which <see cref="Clip"/> judges its target PAPR. 8x, not 4x: at 4x the peaks of a wide
        /// carrier still fall between samples, and NR 100 judged at 4x read 9.17 dB at the Designer's 8x display rate
        /// for a 9 dB target. Cancelling at 8x as well cost ~20x the time (two minutes for NR 100) for nothing the
        /// steered threshold does not already give.
        /// </summary>
        public const int PaprOversample = 8;

        /// <summary>
        /// Reduces the record to a target PEAK PAPR measured at <see cref="PaprOversample"/>x oversampling (the waveform
        /// a DAC reconstructs and the amplifier sees) by PEAK CANCELLATION, the reduction with the least in-band
        /// distortion for a given PAPR, and so the lowest EVM.
        /// <para>Each pass finds every local maximum above the threshold and subtracts the occupied-band kernel (a
        /// band-limited pulse, peak 1) scaled to exactly that peak's excess. Every correction is in-band, so ACLR is
        /// untouched, and only the peaks are touched. Clip-and-filter instead cut every sample over the threshold and
        /// then kept only the in-band share of the cut. That cancels a fraction of each excess, so it had to clip
        /// lower and more often to arrive, and all of that extra cut became EVM: 2.44 % against 2.01 % at 8 dB for
        /// NR 20 64QAM, and 4.9 % against 3.8 % at 7 dB.</para>
        /// <para>The kernel's sidelobes can raise a neighbour over the line, so passes repeat until nothing is over.
        /// The target is a CEILING: an outer loop moves the threshold until the 8x peak is at or below the target and
        /// within 0.05 dB of it, and only a result under the target is ever kept, so the 0.01 % PAPR, which sits at
        /// or below the peak, meets it too. The record is band-limited and periodic, so resampling up and back down
        /// is exact.</para>
        /// </summary>
        public static void Clip(Complex[] x, double fs, double occBw, double targetPaprDb)
        {
            int n = x.Length, m = Pow2AtLeast(n * ClipOversample), mj = Pow2AtLeast(n * PaprOversample);
            double fo = fs * m / n;
            var orig = Fft.ResamplePeriodic(x, m);
            var inBand = new bool[m];
            int nIn = 0;
            for (int k = 0; k < m; k++)
                if (inBand[k] = Math.Abs((k < (m + 1) / 2 ? k : k - m) * fo / m) <= occBw / 2) nIn++;
            double k0 = (double)nIn / m;                      // the in-band kernel's peak for a unit impulse
            double rms = Math.Sqrt(MeanPower(orig));

            double thr = targetPaprDb, bestMiss = double.MaxValue;
            Complex[]? best = null, under = null;
            double underMiss = double.MaxValue;
            for (int outer = 0; outer < 12; outer++)
            {
                var cur = (Complex[])orig.Clone();
                double lim = rms * Math.Pow(10, thr / 20);
                for (int pass = 0; pass < 16; pass++)
                {
                    var imp = new Complex[m];
                    int found = 0;
                    for (int i = 0; i < m; i++)
                    {
                        double a = cur[i].Magnitude;
                        if (a <= lim) continue;
                        if (a < cur[(i - 1 + m) % m].Magnitude || a < cur[(i + 1) % m].Magnitude) continue;
                        imp[i] = cur[i] * ((a - lim) / a / k0);
                        found++;
                    }
                    if (found == 0) break;
                    var f = Fft.Forward(imp);
                    for (int k = 0; k < m; k++) if (!inBand[k]) f[k] = Complex.Zero;
                    var c = Fft.Inverse(f);
                    for (int i = 0; i < m; i++) cur[i] -= c[i];
                }
                double miss = SignalMeasure.PaprDb(Fft.ResamplePeriodic(cur, mj), 0.0) - targetPaprDb;
                if (Math.Abs(miss) < bestMiss) { bestMiss = Math.Abs(miss); best = cur; }
                if (miss <= 1e-6 && -miss < underMiss) { underMiss = -miss; under = cur; }
                if (miss <= 1e-6 && miss > -0.05) break;
                // aim a little under the line, so the next pass lands inside the window rather than on its edge
                thr -= miss + 0.02;
            }
            var y = Fft.ResamplePeriodic(under ?? best!, n);
            Array.Copy(y, x, n);
            Normalise(x);
        }

        private static int Pow2AtLeast(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        public static double MeanPower(Complex[] x)
        {
            double p = 0;
            foreach (var v in x) p += v.Real * v.Real + v.Imaginary * v.Imaginary;
            return p / x.Length;
        }
    }
}
