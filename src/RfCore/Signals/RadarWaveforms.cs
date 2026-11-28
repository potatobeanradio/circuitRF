// ================================================================
//  RadarWaveforms.cs  —  radar phase codes, FMCW ramps and the
//  matched-filter (pulse-compression) readout
//
//  Codes are chip phases (rad) for PulseShape.Code. Sources:
//    Barker      R. H. Barker, "Group synchronizing of binary digital
//                systems", Communication Theory, Butterworth, 1953 (no DOI)
//    Frank       R. L. Frank, IEEE Trans. IT 9(1):43-45, 1963,
//                https://doi.org/10.1109/TIT.1963.1057798
//    P1, P2      B. L. Lewis, F. F. Kretschmer, IEEE Trans. AES 17(3):
//                364-372, 1981, https://doi.org/10.1109/TAES.1981.309063
//    P3, P4      B. L. Lewis, F. F. Kretschmer, IEEE Trans. AES 18(5):
//                637-641, 1982, https://doi.org/10.1109/TAES.1982.309276
//    Zadoff-Chu  D. C. Chu, IEEE Trans. IT 18(4):531-532, 1972,
//                https://doi.org/10.1109/TIT.1972.1054840
//    m-sequence  S. W. Golomb, Shift Register Sequences, 3rd ed., World
//                Scientific, 2014, https://doi.org/10.1142/9361
//  The tables of all of them, and the PSL / ISL readout: N. Levanon,
//  E. Mozeson, Radar Signals, Wiley, 2004,
//  https://doi.org/10.1002/0471663085
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public enum RadarCode { Barker, Frank, P1, P2, P3, P4, ZadoffChu, Mls }

    public static class RadarCodes
    {
        /// <summary>The chip phases of <paramref name="kind"/>: <paramref name="n"/> is the length of a Barker, P3, P4 or
        /// Zadoff-Chu code, M of a Frank, P1 or P2 code (M² chips), the degree of an m-sequence (2^n − 1 chips);
        /// <paramref name="root"/> is the Zadoff-Chu root, coprime with n.</summary>
        public static double[] Phases(RadarCode kind, int n, int root = 1) => kind switch
        {
            RadarCode.Barker => [.. Barker(n).Select(b => b > 0 ? 0 : Math.PI)],
            RadarCode.Frank => Frank(n),
            RadarCode.P1 => P1(n),
            RadarCode.P2 => P2(n),
            RadarCode.P3 => P3(n),
            RadarCode.P4 => P4(n),
            RadarCode.ZadoffChu => ZadoffChu(n, root),
            RadarCode.Mls => [.. Mls(n).Select(b => b == 0 ? 0 : Math.PI)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>The Barker code of length 2, 3, 4, 5, 7, 11 or 13 as ±1: every aperiodic sidelobe 0 or ±1.</summary>
        public static int[] Barker(int n) => n switch
        {
            2 => [1, -1],
            3 => [1, 1, -1],
            4 => [1, 1, -1, 1],
            5 => [1, 1, 1, -1, 1],
            7 => [1, 1, 1, -1, -1, 1, -1],
            11 => [1, 1, 1, -1, -1, -1, 1, -1, -1, 1, -1],
            13 => [1, 1, 1, 1, 1, -1, -1, 1, 1, -1, 1, -1, 1],
            _ => throw new FormatException($"radar code: there is no Barker code of length {n} (2, 3, 4, 5, 7, 11 or 13)"),
        };

        // The matrix codes are listed group by group: chip (j−1)M + (i−1) is element i of group j, i, j = 1..M.
        private static double[] Matrix(int m, Func<int, int, double> phase)
        {
            if (m < 2) throw new FormatException($"radar code: M must be 2 or more (got {m})");
            var c = new double[m * m];
            for (int j = 1; j <= m; j++)
                for (int i = 1; i <= m; i++) c[(j - 1) * m + i - 1] = phase(i, j);
            return c;
        }

        /// <summary>Frank: φ(i, j) = 2π (i − 1)(j − 1) / M.</summary>
        public static double[] Frank(int m) => Matrix(m, (i, j) => 2 * Math.PI * (i - 1) * (j - 1) / m);

        /// <summary>P1: φ(i, j) = −(π / M) [M − (2j − 1)] [(j − 1) M + (i − 1)].</summary>
        public static double[] P1(int m) => Matrix(m, (i, j) => -Math.PI / m * (m - (2 * j - 1)) * ((j - 1) * m + (i - 1)));

        /// <summary>P2 (M even): φ(i, j) = [(π/2)(M − 1)/M − (π/M)(i − 1)] (M + 1 − 2j); a palindrome.</summary>
        public static double[] P2(int m)
        {
            if (m % 2 != 0) throw new FormatException($"radar code: a P2 code needs an even M (got {m})");
            return Matrix(m, (i, j) => (Math.PI / 2 * (m - 1) / m - Math.PI / m * (i - 1)) * (m + 1 - 2 * j));
        }

        /// <summary>P3: φ(i) = π (i − 1)² / N, i = 1..N.</summary>
        public static double[] P3(int n) => [.. Enumerable.Range(0, Length(n)).Select(i => Math.PI * i * (double)i / n)];

        /// <summary>P4: φ(i) = π (i − 1)² / N − π (i − 1), i = 1..N.</summary>
        public static double[] P4(int n) => [.. Enumerable.Range(0, Length(n)).Select(i => Math.PI * i * (double)i / n - Math.PI * i)];

        /// <summary>Zadoff-Chu of length N and root u (coprime with N): φ(n) = −π u n (n + 1) / N for odd N, −π u n² / N
        /// for even N, n = 0..N−1 (the sign the 3GPP form uses). Constant amplitude, zero periodic autocorrelation.</summary>
        public static double[] ZadoffChu(int n, int u)
        {
            static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
            if (Length(n) < 2 || u < 1 || Gcd(u, n) != 1)
                throw new FormatException($"radar code: a Zadoff-Chu root must be coprime with the length (got N={n}, u={u})");
            return [.. Enumerable.Range(0, n).Select(k => -Math.PI * u * k * (k + (n % 2)) / (double)n)];
        }

        private static int Length(int n) => n >= 1 ? n : throw new FormatException($"radar code: the length must be 1 or more (got {n})");

        // Primitive feedback polynomials, the exponents of x^m + ... + 1 (Golomb, and any table of primitive
        // polynomials); the test proves every one gives the full period 2^m − 1.
        private static readonly int[][] MlsTaps =
        [
            [], [], [2, 1], [3, 2], [4, 3], [5, 3], [6, 5], [7, 6], [8, 6, 5, 4], [9, 5], [10, 7], [11, 9],
            [12, 11, 10, 4], [13, 12, 11, 8], [14, 13, 12, 2], [15, 14], [16, 15, 13, 4],
        ];

        /// <summary>One period (2^m − 1 bits) of the maximal-length sequence of degree <paramref name="m"/> (2 to 16), from
        /// the all-ones state of a Fibonacci shift register.</summary>
        public static byte[] Mls(int m)
        {
            if (m < 2 || m >= MlsTaps.Length)
                throw new FormatException($"radar code: an m-sequence degree is 2 to {MlsTaps.Length - 1} (got {m})");
            int n = (1 << m) - 1, state = n;
            var taps = MlsTaps[m];
            var bits = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int b = 0;
                foreach (int t in taps) b ^= state >> (t - 1);
                bits[i] = (byte)(b & 1);
                state = ((state << 1) | (b & 1)) & n;
            }
            return bits;
        }
    }

    /// <summary>
    /// A continuous FMCW ramp, constant envelope with no off-time; one period is the record. Sawtooth: up from −B/2 to
    /// +B/2 over <see cref="Ramp"/>, back down linearly over <see cref="Idle"/> (the reset; 0 = a jump); triangle: up over
    /// Ramp, down over Ramp, no idle (its reset is the down-ramp). Each segment's mean frequency is the carrier, so the
    /// phase ends each segment where it began and the record wraps with no phase step.
    /// </summary>
    public sealed record Fmcw(double BandwidthHz, double Ramp, double Idle = 0, bool Triangle = false)
    {
        public double Period => Triangle ? 2 * Ramp : Ramp + Idle;

        /// <summary>The frequency (Hz, from the carrier) at time <paramref name="t"/> into the period.</summary>
        public double FrequencyAt(double t)
        {
            double b = BandwidthHz;
            if (t < Ramp) return -b / 2 + b * t / Ramp;
            double u = t - Ramp, back = Triangle ? Ramp : Idle;
            return b / 2 - b * u / back;
        }

        /// <summary>The phase (rad) at time <paramref name="t"/> into the period: 2π ∫ f, zero at each segment's ends.</summary>
        public double PhaseAt(double t)
        {
            double b = BandwidthHz;
            if (t < Ramp) return 2 * Math.PI * (-b / 2 * t + b * t * t / (2 * Ramp));
            double u = t - Ramp, back = Triangle ? Ramp : Idle;
            return 2 * Math.PI * (b / 2 * u - b * u * u / (2 * back));
        }

        /// <summary>The sweep over 0.9, as every carrier's occupied fraction.</summary>
        public double OccupiedBandwidthHz => Math.Abs(BandwidthHz) / 0.9;

        /// <summary>1.92 MHz x 2^k, the smallest k at or above twice the bandwidth with a whole number of samples in a
        /// period (and in the ramp).</summary>
        public double NativeRateHz()
        {
            Check();
            double r = SignalRate.LadderBaseHz;
            while (r < 2 * OccupiedBandwidthHz) r *= 2;
            for (int k = 0; k <= 8; k++, r *= 2)
                if (Whole(Ramp * r) && Whole(Idle * r)) return r;
            throw new FormatException($"FMCW: Ramp {PulsedCw.FormatTime(Ramp)} and Idle {PulsedCw.FormatTime(Idle)} are not whole numbers of samples at any 1.92 MHz x 2^k rate");
        }

        private static bool Whole(double n) => Math.Abs(n - Math.Round(n)) <= 1e-6;

        public Fmcw Check()
        {
            var inv = CultureInfo.InvariantCulture;
            if (!(BandwidthHz != 0 && double.IsFinite(BandwidthHz))) throw new FormatException("FMCW: the bandwidth must be a non-zero frequency");
            if (!(Ramp > 0 && double.IsFinite(Ramp))) throw new FormatException($"FMCW: Ramp must be a positive time (got {Ramp.ToString(inv)} s)");
            if (!(Idle >= 0 && double.IsFinite(Idle))) throw new FormatException($"FMCW: Idle must be 0 or a positive time (got {Idle.ToString(inv)} s)");
            if (Triangle && Idle > 0) throw new FormatException("FMCW: a triangle has no idle time: its reset is the down-ramp");
            return this;
        }

        /// <summary>One period at <paramref name="rateHz"/>, unit magnitude throughout.</summary>
        public Complex[] Generate(double rateHz)
        {
            var x = new Complex[(int)Math.Round(Period * rateHz)];
            for (int i = 0; i < x.Length; i++) x[i] = Complex.FromPolarCoordinates(1, PhaseAt(i / rateHz));
            return x;
        }
    }

    /// <summary>The matched filter's output for one pulse, as a radar designer reads it.</summary>
    /// <param name="MainlobeSeconds">−3 dB width of the compressed peak.</param>
    /// <param name="NullToNullSeconds">width between the first minima either side of the peak.</param>
    /// <param name="PslDb">peak sidelobe level: the largest |output| beyond the first minima, over the peak.</param>
    /// <param name="IslDb">integrated sidelobe level: the energy beyond the first minima (both sides) over the peak's.</param>
    public sealed record CompressionResult(double MainlobeSeconds, double NullToNullSeconds, double PslDb, double IslDb);

    public static class PulseCompression
    {
        /// <summary>The aperiodic autocorrelation |r(k)|, k = 0..N−1, of <paramref name="pulse"/>: the matched filter's
        /// output, symmetric in k (through a zero-padded FFT).</summary>
        public static double[] Autocorrelation(ReadOnlySpan<Complex> pulse)
        {
            int n = pulse.Length, m = 1;
            while (m < 2 * n) m <<= 1;
            var a = new Complex[m];
            pulse.CopyTo(a);
            Fft.Transform(a, inverse: false);
            for (int i = 0; i < m; i++) a[i] = new Complex(a[i].Magnitude * a[i].Magnitude, 0);
            Fft.Transform(a, inverse: true);
            return [.. a.Take(n).Select(v => v.Magnitude / m)];
        }

        /// <summary>The mainlobe width, PSL and ISL of the matched-filter output of <paramref name="pulse"/>, sampled at
        /// <paramref name="rateHz"/> (one sample per chip of a bare code gives the textbook values: Barker 13 PSL
        /// 20 log10(1/13) = −22.3 dB, ISL 10 log10(12/169) = −11.5 dB).</summary>
        public static CompressionResult Measure(ReadOnlySpan<Complex> pulse, double rateHz)
        {
            var r = Autocorrelation(pulse);
            double peak = r[0];
            int k1 = 1;
            while (k1 < r.Length - 1 && r[k1 + 1] < r[k1]) k1++;           // the first minimum
            int k3 = 0;
            double half = peak / Math.Sqrt(2);
            while (k3 + 1 < r.Length && r[k3 + 1] >= half) k3++;
            double w3 = k3 + 1 < r.Length ? k3 + (r[k3] - half) / (r[k3] - r[k3 + 1]) : k3;
            double psl = 0, isl = 0;
            for (int k = k1; k < r.Length; k++) { psl = Math.Max(psl, r[k]); isl += 2 * r[k] * r[k]; }
            return new(2 * w3 / rateHz, 2 * k1 / rateHz, 20 * Math.Log10(psl / peak), 10 * Math.Log10(isl / (peak * peak)));
        }

        /// <summary>The bare code's readout, one sample per chip.</summary>
        public static CompressionResult Measure(double[] code)
            => Measure([.. code.Select(p => Complex.FromPolarCoordinates(1, p))], 1);
    }
}
