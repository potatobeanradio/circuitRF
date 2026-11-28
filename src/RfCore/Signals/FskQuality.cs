// ================================================================
//  FskQuality.cs  —  the FSK quality numbers of a record
//
//  The frequency at each FSK symbol's centre is read from the phase
//  step across it and fitted to the ideal frequency there,
//  f_rx = a f_ideal + b, by least squares: the deviation is a Dev, its
//  error a - 1, the carrier frequency error b, and the FSK error the
//  rest, RMS and peak, over Dev. Ramps, guards and a switched payload
//  are not read. The record must be the carrier at its native rate,
//  aligned to the generator's (as SignalQuality delivers it).
// ================================================================

using System;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>Measured outer deviation (Hz), its error (%), the carrier frequency error (Hz), the FSK error RMS and peak
    /// (% of the deviation) and the symbols read.</summary>
    public sealed record FskQualityResult(double DeviationHz, double DeviationErrorPercent, double FrequencyErrorHz,
                                          double FskErrorRmsPercent, double FskErrorPeakPercent, int Symbols);

    public static class FskQuality
    {
        public static FskQualityResult Measure(FskShape s, Complex[] x, double rateHz)
        {
            var ideal = Fsk.FrequencyTrack(s, rateHz);
            var at = Fsk.SymbolCentres(s, rateHz);
            int m = x.Length;
            double k2 = rateHz / (4 * Math.PI);
            var fr = at.Select(i => (x[(i + 2) % m] * Complex.Conjugate(x[i])).Phase * k2).ToArray();
            var fs = at.Select(i => (ideal[i] + ideal[(i + 1) % m]) / 2).ToArray();
            int n = fr.Length;
            double mx = fs.Average(), my = fr.Average(), sxx = 0, sxy = 0;
            for (int q = 0; q < n; q++) { sxx += (fs[q] - mx) * (fs[q] - mx); sxy += (fs[q] - mx) * (fr[q] - my); }
            double a = sxx > 0 ? sxy / sxx : 1, b = my - a * mx;
            var e = fr.Select((v, q) => Math.Abs(v - a * fs[q] - b)).ToArray();
            return new(a * s.Deviation, 100 * (a - 1), b, 100 * Math.Sqrt(e.Average(v => v * v)) / s.Deviation,
                       100 * e.Max() / s.Deviation, n);
        }
    }
}
