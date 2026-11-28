// ================================================================
//  PulsedCw.cs  —  the pulsed-CW (pCW) carrier: a generated pulse train
//
//  One record is one pulse repetition interval (PRI), which repeats.
//  The envelope is real and non-negative: exactly 0 in the off-time,
//  exactly 1 on the flat top, raised-cosine edges.  Normalised to unit
//  power ON THE TOP, not to unit mean power, so a source whose only
//  carrier is pCW states its pulse-top power in Pavg (brief ENV-PCW D1).
//
//  Timing, in signal time from the start of the record:
//    Delay                      the rising edge leaves 0
//    Delay + Tr/2               the rising edge's 50 % point
//    Delay + Tr/2 + PW          the falling edge's 50 % point
//    Delay + Tr/2 + PW + Tf/2   the falling edge reaches 0
//  where Tr, Tf are the edges' full raised-cosine durations; a
//  raised-cosine edge of full duration T rises 10-90 % in 0.5903 T.
//
//  An optional linear-FM chirp of bandwidth B (signed: up or down)
//  sweeps the frequency over the width, B/2 below the carrier at the
//  rising edge's 50 % point to B/2 above it at the falling edge's:
//  phase pi B/PW (t - Tc)^2 about the pulse centre Tc.  |envelope| is
//  unchanged, so the gated CCDF and the pulse measurements are too.
// ================================================================

using System;
using System.Globalization;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>A pCW carrier's timing with every default applied (seconds), and the analysis window fractions.</summary>
    public sealed record PulseShape(double Pri, double Width, double Rise, double Fall, double Delay,
                                    double MeasureStart, double MeasureEnd, double ChirpHz = 0)
    {
        /// <summary>The pulse centre, midway between the 50 % points: the chirp is at the carrier here.</summary>
        public double Centre => Delay + RiseSpan / 2 + Width / 2;

        /// <summary>The chirp's phase (rad) at record time <paramref name="t"/>: π B/PW (t − Tc)², so the frequency is
        /// B (t − Tc) / PW, from −B/2 to +B/2 over the width.</summary>
        public double ChirpPhase(double t) => Math.PI * ChirpHz / Width * (t - Centre) * (t - Centre);

        /// <summary>Full duration of the rising edge (0 to 1).</summary>
        public double RiseSpan => Rise / PulsedCw.TenNinetyPerSpan;
        /// <summary>Full duration of the falling edge (1 to 0).</summary>
        public double FallSpan => Fall / PulsedCw.TenNinetyPerSpan;
        /// <summary>Where the flat top starts and ends, from the start of the record.</summary>
        public double TopStart => Delay + RiseSpan;
        public double TopEnd => Delay + RiseSpan / 2 + Width - FallSpan / 2;
        /// <summary>Where the falling edge reaches 0.</summary>
        public double End => Delay + RiseSpan / 2 + Width + FallSpan / 2;
        public double Prf => 1 / Pri;
        /// <summary>Width over PRI (the 50 % to 50 % duty).</summary>
        public double Duty => Width / Pri;

        /// <summary>The power duty: mean of |envelope|² over the record. The top contributes its length, each
        /// raised-cosine edge of full span T contributes ∫ sin⁴ = 3T/8.</summary>
        public double PowerDuty => (TopEnd - TopStart + 0.375 * (RiseSpan + FallSpan)) / Pri;

        /// <summary>The envelope at record time <paramref name="t"/> (s, reduced modulo the PRI).</summary>
        public double At(double t)
        {
            t -= Math.Floor(t / Pri) * Pri;
            if (t <= Delay || t >= End) return 0;
            if (t < TopStart) return 0.5 * (1 - Math.Cos(Math.PI * (t - Delay) / RiseSpan));
            if (t <= TopEnd) return 1;
            return 0.5 * (1 + Math.Cos(Math.PI * (t - TopEnd) / FallSpan));
        }
    }

    public static class PulsedCw
    {
        /// <summary>10-90 % time of a raised-cosine edge over its full duration: (acos(-0.8) - acos(0.8)) / π.</summary>
        public static readonly double TenNinetyPerSpan = (Math.Acos(-0.8) - Math.Acos(0.8)) / Math.PI;

        /// <summary>
        /// 99 % of the power of a raised-cosine edge's derivative (a half-sine of full duration T) lies below 1.1818 / T
        /// (integrated numerically: |P(f)| ∝ cos(πfT) / (1 - 4f²T²)). In 10-90 % time that is 0.6977 / tr.
        /// </summary>
        public const double EdgeBand99TimesSpan = 1.1818;

        /// <summary>Samples the faster edge's 10-90 % time must hold at the native rate.</summary>
        public const int SamplesPerEdge = 10;

        public const double DefaultPri = 100e-6, DefaultWidth = 10e-6, DefaultEdge = 100e-9;
        public const double DefaultMeasureStart = 0.1, DefaultMeasureEnd = 0.9;

        /// <summary><paramref name="c"/>'s timing with the defaults applied. The default Delay centres the pulse in the
        /// PRI (equal off-time either side), so the first pulse starts cold and the last ends clear of the wrap.</summary>
        public static PulseShape Resolve(CarrierSpec c)
        {
            double pri = c.PriSeconds ?? DefaultPri, pw = c.PulseWidthSeconds ?? DefaultWidth;
            double rise = c.RiseSeconds ?? DefaultEdge, fall = c.FallSeconds ?? DefaultEdge;
            double occupied = pw + 0.5 * (rise + fall) / TenNinetyPerSpan;
            double delay = c.DelaySeconds ?? 0.5 * (pri - occupied);
            return new PulseShape(pri, pw, rise, fall, delay,
                                  c.MeasureStart ?? DefaultMeasureStart, c.MeasureEnd ?? DefaultMeasureEnd, c.ChirpHz ?? 0);
        }

        /// <summary>Refuses a pulse that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static PulseShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            static string T(double v) => FormatTime(v);
            void Positive(string field, double v)
            {
                if (!(v > 0) || !double.IsFinite(v)) throw new FormatException($"pCW carrier: {field} must be a positive time (got {T(v)})");
            }
            Positive("PRI", s.Pri);
            Positive("PW", s.Width);
            Positive("Rise", s.Rise);
            Positive("Fall", s.Fall);
            if (s.Width >= s.Pri)
                throw new FormatException($"pCW carrier: PW ({T(s.Width)}) must be shorter than PRI ({T(s.Pri)})");
            if (s.Rise > s.Width / 2)
                throw new FormatException($"pCW carrier: Rise ({T(s.Rise)}) is longer than half of PW ({T(s.Width / 2)})");
            if (s.Fall > s.Width / 2)
                throw new FormatException($"pCW carrier: Fall ({T(s.Fall)}) is longer than half of PW ({T(s.Width / 2)})");
            if (s.End > s.Pri * (1 + 1e-12))
                throw new FormatException($"pCW carrier: Delay ({T(s.Delay)}) + PW ({T(s.Width)}) and half of each edge end the pulse at {T(s.End)}, past the PRI ({T(s.Pri)})");
            if (!(s.Delay > 0))
                throw new FormatException($"pCW carrier: Delay must be greater than 0 (got {T(s.Delay)}), so the run starts in the off-time, at the quiescent point");
            if (!(s.MeasureStart >= 0 && s.MeasureStart < s.MeasureEnd && s.MeasureEnd <= 1))
                throw new FormatException($"pCW carrier: MeasureStart and MeasureEnd are fractions of the flat top, 0 <= MeasureStart < MeasureEnd <= 1 (got {s.MeasureStart.ToString(CultureInfo.InvariantCulture)} and {s.MeasureEnd.ToString(CultureInfo.InvariantCulture)})");
            if (!double.IsFinite(s.ChirpHz))
                throw new FormatException("pCW carrier: Chirp must be a finite frequency");
            NativeRateHz(s);
            return s;
        }

        /// <summary>
        /// The carrier's bandwidth, from its faster edge: twice the 99 %-power band of that edge (it sits on both sides
        /// of the carrier), over 0.9 — the occupied fraction every FILE carrier is measured with, so the envelope rate,
        /// the preview span and the bands follow from the edges exactly as they follow from a file's measured band. A chirp
        /// adds its sweep to the edges' band.
        /// </summary>
        public static double BandwidthHz(PulseShape s) => (2 * OccupiedHalfBandHz(s) + Math.Abs(s.ChirpHz)) / 0.9;

        /// <summary>The 99 %-power band of the faster edge, one side (Hz).</summary>
        public static double OccupiedHalfBandHz(PulseShape s) => EdgeBand99TimesSpan / (Math.Min(s.Rise, s.Fall) / TenNinetyPerSpan);

        /// <summary>The native rate: 1.92 MHz x 2^k, the smallest k that puts at least <see cref="SamplesPerEdge"/> samples
        /// in the faster edge's 10-90 % time, at least twice the bandwidth (which only a chirp reaches: a plain pulse's is
        /// 1.55 / tr) and a whole number of samples in a PRI. A PRI no such rate divides is refused.</summary>
        public static double NativeRateHz(PulseShape s)
        {
            double need = Math.Max(SamplesPerEdge / Math.Min(s.Rise, s.Fall), 2 * BandwidthHz(s));
            double r = SignalRate.LadderBaseHz;
            while (r < need) r *= 2;
            // at most 2^8 above the edge's own need, and whole to an absolute 1e-6 of a sample: a tolerance relative to
            // the count would let a large enough rate pass any PRI
            for (int k = 0; k <= 8; k++, r *= 2)
            {
                double n = s.Pri * r;
                if (Math.Abs(n - Math.Round(n)) <= 1e-6) return r;
            }
            throw new FormatException($"pCW carrier: PRI {FormatTime(s.Pri)} is not a whole number of samples at any 1.92 MHz x 2^k rate; choose a PRI that is (100 us, 1 ms, 250 us ...)");
        }

        /// <summary>One PRI of the envelope at <paramref name="rateHz"/>: exact 0 and 1 off and on the top (in magnitude,
        /// with a chirp).</summary>
        public static Complex[] Generate(PulseShape s, double rateHz)
        {
            int n = (int)Math.Round(s.Pri * rateHz);
            var x = new Complex[n];
            if (s.ChirpHz == 0)
                for (int i = 0; i < n; i++) x[i] = new Complex(s.At(i / rateHz), 0);
            else
                for (int i = 0; i < n; i++) x[i] = Complex.FromPolarCoordinates(s.At(i / rateHz), s.ChirpPhase(i / rateHz));
            return x;
        }

        /// <summary>A time in the unit a reader would say it in: 1 ms, 50 µs, 500 ns.</summary>
        public static string FormatTime(double s)
        {
            var inv = CultureInfo.InvariantCulture;
            double a = Math.Abs(s);
            return a >= 1 ? string.Format(inv, "{0:0.###} s", s)
                 : a >= 1e-3 ? string.Format(inv, "{0:0.###} ms", s * 1e3)
                 : a >= 1e-6 ? string.Format(inv, "{0:0.###} µs", s * 1e6)
                 : string.Format(inv, "{0:0.###} ns", s * 1e9);
        }
    }
}
