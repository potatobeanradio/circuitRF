// ================================================================
//  PhaseNoiseMask.cs  —  a source's phase noise, as a mask, and the
//  phase record it produces
//
//  The mask is single-sided L(f) in dBc/Hz at offsets from the carrier,
//  "1kHz:-80, 10kHz:-100, 100kHz:-120".  Between points it is a straight
//  line in dB against log-frequency.  Below the first point the first
//  segment's slope continues; beyond the last point it is the floor,
//  which is the last point's level unless stated.
//
//  The phase is a real Gaussian process with single-sided density
//  S_phi(f) = 2 * 10^(L(f)/10), drawn over one periodic record of N
//  samples at fs: bin n (frequency n*fs/N) gets a complex Gaussian of
//  mean square 10^(L/10) * fs / N, its mirror the conjugate, and the
//  inverse transform is the record.  Two bins +-n then carry
//  S_phi(f_n) * fs/N of mean-square phase, which is the density times
//  the bin width.  DC and the Nyquist bin carry none.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CircuitRF.Diagnostics;

namespace RfCore.Signals
{
    public sealed class PhaseNoiseMask
    {
        /// <summary>The stated points, offset (Hz) ascending, level (dBc/Hz).</summary>
        public IReadOnlyList<(double OffsetHz, double Dbc)> Points { get; }

        /// <summary>The level beyond the last point (dBc/Hz).</summary>
        public double FloorDbc { get; }

        private PhaseNoiseMask(List<(double, double)> points, double floor)
        {
            Points = points;
            FloorDbc = floor;
        }

        /// <summary>
        /// Reads <paramref name="text"/> — <c>offset:level</c> pairs separated by commas or semicolons, the offset a number
        /// with an optional k, M or G and an optional Hz, the level a number in dBc/Hz — with <paramref name="floorDbc"/>
        /// beyond the last point (NaN: the last point's level). Throws <see cref="FormatException"/> naming what is wrong.
        /// </summary>
        public static PhaseNoiseMask Parse(string text, double floorDbc = double.NaN)
        {
            var pts = new List<(double, double)>();
            foreach (var raw in (text ?? "").Trim().Trim('"').Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                if (item.Length == 0) continue;
                int colon = item.IndexOf(':');
                if (colon <= 0)
                    throw Refusal("phase-noise.point", "PhaseNoise point '{item}' is not offset:level (for example 10kHz:-100)", ("item", item));
                double f = ParseOffset(item[..colon].Trim(), item);
                string lv = item[(colon + 1)..].Trim();
                foreach (var unit in new[] { "dBc/Hz", "dBc" })
                    if (lv.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) { lv = lv[..^unit.Length].Trim(); break; }
                if (!double.TryParse(lv, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) || !double.IsFinite(l))
                    throw Refusal("phase-noise.level", "PhaseNoise point '{item}': the level is not a number of dBc/Hz", ("item", item));
                if (pts.Count > 0 && f <= pts[^1].Item1)
                    throw Refusal("phase-noise.order", "PhaseNoise offsets must rise from point to point; '{item}' does not", ("item", item));
                pts.Add((f, l));
            }
            if (pts.Count == 0) throw Refusal("phase-noise.empty", "PhaseNoise states no points (for example \"10kHz:-100, 1MHz:-140\")");
            return new PhaseNoiseMask(pts, double.IsNaN(floorDbc) ? pts[^1].Item2 : floorDbc);
        }

        /// <summary>A mask that does not read, as the coded diagnostic it is, thrown as a <see cref="FormatException"/>.</summary>
        private static FormatException Refusal(string id, string template, params (string Name, object? Value)[] args)
            => new(Diagnostic.Create(id, DiagnosticSeverity.Error, template, args).Render());

        private static double ParseOffset(string s, string item)
        {
            string t = s;
            if (t.EndsWith("Hz", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
            double scale = 1;
            if (t.Length > 0)
                switch (t[^1])
                {
                    case 'k': case 'K': scale = 1e3; t = t[..^1]; break;
                    case 'M': scale = 1e6; t = t[..^1]; break;
                    case 'G': case 'g': scale = 1e9; t = t[..^1]; break;
                }
            if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0) || double.IsInfinity(v))
                throw Refusal("phase-noise.offset", "PhaseNoise point '{item}': the offset '{offset}' is not a positive frequency",
                              ("item", item), ("offset", s));
            return v * scale;
        }

        /// <summary>
        /// The text <see cref="Parse"/> reads, from <paramref name="points"/> (offset Hz, level dBc/Hz): offsets with a k, M or
        /// G and Hz, levels to 0.01 dB. A point that is not finite, or whose offset does not rise past the one before, is left
        /// out. Brief NZ-10: an oscillator's computed PN, pasted into a source's mask.
        /// </summary>
        public static string Format(IEnumerable<(double OffsetHz, double Dbc)> points)
        {
            var parts = new List<string>();
            double last = 0;
            foreach (var (f, l) in points)
            {
                if (!(f > last) || !double.IsFinite(f) || !double.IsFinite(l)) continue;
                last = f;
                (double v, string u) = f >= 1e9 ? (f / 1e9, "GHz") : f >= 1e6 ? (f / 1e6, "MHz") : f >= 1e3 ? (f / 1e3, "kHz") : (f, "Hz");
                parts.Add(v.ToString("G6", CultureInfo.InvariantCulture) + u + ":" + l.ToString("F2", CultureInfo.InvariantCulture));
            }
            return string.Join(", ", parts);
        }

        /// <summary>The whole key as a source takes it: <c>PhaseNoise="…"</c>.</summary>
        public static string Assignment(IEnumerable<(double OffsetHz, double Dbc)> points) => "PhaseNoise=\"" + Format(points) + "\"";

        /// <summary>L at <paramref name="offsetHz"/> &gt; 0, dBc/Hz.</summary>
        public double At(double offsetHz)
        {
            var p = Points;
            if (offsetHz > p[^1].OffsetHz) return FloorDbc;
            if (p.Count == 1) return p[0].Dbc;
            int i = 0;
            if (offsetHz >= p[0].OffsetHz)
                while (i < p.Count - 2 && offsetHz > p[i + 1].OffsetHz) i++;
            // Within segment i, or its continuation below the first point.
            double x0 = Math.Log10(p[i].OffsetHz), x1 = Math.Log10(p[i + 1].OffsetHz);
            double t = (Math.Log10(offsetHz) - x0) / (x1 - x0);
            return p[i].Dbc + t * (p[i + 1].Dbc - p[i].Dbc);
        }

        /// <summary>The first stated offset above <paramref name="hz"/>, or NaN when none is.</summary>
        public double FirstOffsetAbove(double hz)
        {
            foreach (var (f, _) in Points) if (f > hz) return f;
            return double.NaN;
        }

        /// <summary>
        /// One periodic record of the phase (rad), <paramref name="n"/> samples at <paramref name="fsHz"/>, drawn from the
        /// stream <paramref name="key"/> (<see cref="NoiseDraw"/>): bin m takes draw m.
        /// </summary>
        public double[] Phase(int n, double fsHz, ulong key)
        {
            var a = new Complex[n];
            for (int m = 1; 2 * m < n; m++)
            {
                double amp = Math.Sqrt(Math.Pow(10, At(m * fsHz / n) / 10) * fsHz / n);
                var z = amp * NoiseDraw.Complex(key, m);
                a[m] = z;
                a[n - m] = Complex.Conjugate(z);
            }
            Fft.Transform(a, inverse: true);                          // unnormalised: phi[t] = sum_m a[m] e^{+j2pi mt/n}
            var phi = new double[n];
            for (int t = 0; t < n; t++) phi[t] = a[t].Real;
            return phi;
        }
    }
}
