// ================================================================
//  OfdmGrid.cs  —  one OFDM record as a resource grid, its synthesis,
//                  and its pilot-aided EVM
//
//  Every OFDM signal past the plain LTE / NR downlink (uplink DFT-s-
//  OFDM and SC-FDMA, the 3GPP test models, TDD gating, and the
//  numerology presets of OfdmPresets) is built as a grid first: per
//  symbol its cyclic prefix, the value on every used subcarrier, and
//  what that value is (data, pilot or another signal).  The grid is
//  synthesised the way OfdmGenerator.Generate synthesises LTE / NR
//  (IFFT, cyclic prefix, raised-cosine WOLA over the record seen as
//  periodic), with two additions: a frequency shift of half a
//  subcarrier (the LTE and NB-IoT uplink, TS 36.211 cl. 5.6 and
//  10.1.5) and silence after a symbol (the NB-IoT 3.75 kHz guard).
//
//  EVM (Measure): FFT over each transmitted symbol's useful part, a
//  per-subcarrier channel from the pilots (received over sent,
//  averaged over the record, linear between pilot-bearing
//  subcarriers), equalised data against the sent points.  A DFT-
//  spread symbol is equalised, then taken back through the inverse
//  DFT and compared with the constellation points before the DFT
//  (TS 38.211 cl. 6.3.1.4, TS 36.211 cl. 5.3.3).  A grid with no
//  pilots falls back to one complex gain fitted on the data.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>What a resource element carries.</summary>
    public enum ReRole : byte { Empty = 0, Data = 1, Pilot = 2, Other = 3 }

    /// <summary>An OFDM record as a resource grid, symbol by symbol. Columns are the used subcarriers, lowest first.</summary>
    public sealed class OfdmGrid
    {
        public required string Label { get; init; }
        public required int Nfft { get; init; }
        public required double SampleRateHz { get; init; }
        /// <summary>Cyclic prefix of every symbol in the record (samples).</summary>
        public required int[] Cp { get; init; }
        /// <summary>FFT bin of every column.</summary>
        public required int[] Bins { get; init; }
        /// <summary>Value on the air, [symbol][column] (0 where empty).</summary>
        public required Complex[][] X { get; init; }
        public required ReRole[][] Role { get; init; }
        /// <summary>Silence after each symbol (samples); null = none.</summary>
        public int[]? Gap { get; init; }
        /// <summary>Frequency shift of every subcarrier, in subcarriers (0.5 for the LTE / NB-IoT uplink).</summary>
        public double Shift { get; init; }
        /// <summary>DFT-spread symbols: the constellation points before the DFT ([symbol] null when not spread), and the first
        /// column of the spread block.</summary>
        public Complex[]?[]? Pre { get; init; }
        public int[]? PreStart { get; init; }
        /// <summary>The symbol is transmitted (false = gated off: TDD, a null symbol).</summary>
        public required bool[] On { get; init; }

        public int Symbols => Cp.Length;
        public double ScsHz => SampleRateHz / Nfft;
        public int Length
        {
            get
            {
                long n = 0;
                for (int s = 0; s < Cp.Length; s++) n += Cp[s] + Nfft + (Gap?[s] ?? 0);
                return (int)n;
            }
        }

        /// <summary>The fraction of the record's time that is transmitted.</summary>
        public double Duty
        {
            get
            {
                double on = 0;
                for (int s = 0; s < Cp.Length; s++) if (On[s]) on += Cp[s] + Nfft;
                return on / Length;
            }
        }

        /// <summary>Frequency of a column (Hz from the carrier centre).</summary>
        public double ColumnHz(int col) => ((Bins[col] < Nfft / 2 ? Bins[col] : Bins[col] - Nfft) + Shift) * ScsHz;

        /// <summary>Sample index of each symbol's start (its cyclic prefix) in the record.</summary>
        public int[] SymbolStarts()
        {
            var at = new int[Cp.Length];
            for (int s = 0, p = 0; s < Cp.Length; s++) { at[s] = p; p += Cp[s] + Nfft + (Gap?[s] ?? 0); }
            return at;
        }

        /// <summary>The record, periodic, normalised to unit mean power over its whole length (gated time included). The
        /// WOLA window spans <paramref name="windowFraction"/> of the shortest cyclic prefix.</summary>
        public Complex[] Synthesize(double windowFraction)
        {
            int n = Nfft, minCp = int.MaxValue;
            foreach (int c in Cp) if (c > 0) minCp = Math.Min(minCp, c);
            if (minCp == int.MaxValue) minCp = 0;
            var ramp = OfdmGenerator.Ramp(Math.Max(0, (int)Math.Round(windowFraction * minCp)));
            var rec = new Complex[Length];
            var bins = new Complex[n];
            double sc = 1.0 / Math.Sqrt(n);
            var starts = SymbolStarts();
            for (int s = 0; s < Cp.Length; s++)
            {
                if (!On[s]) continue;
                Array.Clear(bins);
                var row = X[s];
                for (int c = 0; c < row.Length; c++) bins[Bins[c]] += row[c];
                Fft.Transform(bins, inverse: true);
                OfdmGenerator.AddSymbol(rec, starts[s], bins, Cp[s], ramp, sc, Shift);
            }
            OfdmGenerator.Normalise(rec);
            return rec;
        }

        /// <summary>The received value on every resource element of every transmitted symbol, [symbol][column] (null where
        /// gated off): FFT over the useful part right after the cyclic prefix, the half-subcarrier shift taken out.</summary>
        public Complex[]?[] Demodulate(ReadOnlySpan<Complex> rec)
        {
            if (rec.Length != Length) throw new ArgumentException($"{Label}: the record holds {rec.Length} samples, not the {Length} its grid gives");
            int n = Nfft, total = rec.Length;
            var starts = SymbolStarts();
            var buf = new Complex[n];
            var rot = new Complex[n];
            for (int u = 0; u < n; u++) rot[u] = Shift == 0 ? Complex.One : Complex.FromPolarCoordinates(1, -2 * Math.PI * Shift * u / n);
            var y = new Complex[]?[Cp.Length];
            for (int s = 0; s < Cp.Length; s++)
            {
                if (!On[s]) continue;
                int p0 = starts[s] + Cp[s];
                for (int u = 0; u < n; u++) buf[u] = rec[(p0 + u) % total] * rot[u];
                Fft.Transform(buf, inverse: false);
                var row = y[s] = new Complex[Bins.Length];
                for (int c = 0; c < row.Length; c++) row[c] = buf[Bins[c]];
            }
            return y;
        }
    }

    /// <summary>EVM of an <see cref="OfdmGrid"/> record: rms error over the data, against the rms of the sent data.</summary>
    public sealed record OfdmEvmResult(double RmsPercent, int Points, Complex[] Received, Complex[] Ideal, bool PilotAided)
    {
        public double RmsDb => 20 * Math.Log10(Math.Max(RmsPercent, 1e-12) / 100);
    }

    public static class OfdmEvm
    {
        /// <summary>Pilot-aided EVM of <paramref name="rec"/> (any scale) against the grid it was synthesised from.</summary>
        public static OfdmEvmResult Measure(OfdmGrid g, ReadOnlySpan<Complex> rec)
        {
            var y = g.Demodulate(rec);
            int cols = g.Bins.Length;
            var num = new Complex[cols];
            var den = new double[cols];
            bool any = false;
            for (int s = 0; s < g.Symbols; s++)
            {
                if (y[s] is not { } row) continue;
                for (int c = 0; c < cols; c++)
                    if (g.Role[s][c] == ReRole.Pilot)
                    {
                        var p = g.X[s][c];
                        num[c] += row[c] * Complex.Conjugate(p);
                        den[c] += p.Real * p.Real + p.Imaginary * p.Imaginary;
                        any = true;
                    }
            }
            var h = new Complex[cols];
            if (any) Interpolate(num, den, h);
            else
            {
                // no pilots: one gain, fitted on the data (data-aided, as SignalEvm does for LTE / NR)
                Complex nu = 0; double de = 0;
                for (int s = 0; s < g.Symbols; s++)
                {
                    if (y[s] is not { } row) continue;
                    for (int c = 0; c < cols; c++)
                        if (g.Role[s][c] == ReRole.Data) { var x = g.X[s][c]; nu += row[c] * Complex.Conjugate(x); de += x.Real * x.Real + x.Imaginary * x.Imaginary; }
                }
                Array.Fill(h, de > 0 ? nu / de : Complex.One);
            }

            var rx = new List<Complex>();
            var id = new List<Complex>();
            for (int s = 0; s < g.Symbols; s++)
            {
                if (y[s] is not { } row) continue;
                var pre = g.Pre?[s];
                if (pre is not null)
                {
                    int m = pre.Length, k0 = g.PreStart![s];
                    var blk = new Complex[m];
                    for (int i = 0; i < m; i++) blk[i] = row[k0 + i] / h[k0 + i];
                    var x = Fft.Inverse(blk);
                    double sq = Math.Sqrt(m);
                    for (int i = 0; i < m; i++) { rx.Add(x[i] * sq); id.Add(pre[i]); }
                    continue;
                }
                for (int c = 0; c < cols; c++)
                    if (g.Role[s][c] == ReRole.Data) { rx.Add(row[c] / h[c]); id.Add(g.X[s][c]); }
            }
            double err = 0, refp = 0;
            for (int i = 0; i < rx.Count; i++)
            {
                var e = rx[i] - id[i];
                err += e.Real * e.Real + e.Imaginary * e.Imaginary;
                refp += id[i].Real * id[i].Real + id[i].Imaginary * id[i].Imaginary;
            }
            double rms = refp > 0 ? 100 * Math.Sqrt(err / refp) : double.NaN;
            return new OfdmEvmResult(rms, rx.Count, [.. rx], [.. id], any);
        }

        /// <summary>h = num/den where a column has pilots; linear between pilot-bearing columns, held flat beyond the ends.</summary>
        private static void Interpolate(Complex[] num, double[] den, Complex[] h)
        {
            int n = h.Length, prev = -1;
            for (int c = 0; c < n; c++)
            {
                if (den[c] <= 0) continue;
                h[c] = num[c] / den[c];
                if (prev < 0) for (int j = 0; j < c; j++) h[j] = h[c];
                else for (int j = prev + 1; j < c; j++) h[j] = h[prev] + (h[c] - h[prev]) * ((double)(j - prev) / (c - prev));
                prev = c;
            }
            for (int j = prev + 1; j < n; j++) h[j] = h[prev];
        }
    }
}
