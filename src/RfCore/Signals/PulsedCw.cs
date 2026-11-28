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
//
//  Radar options, each off by default (a shape without them generates
//  today's record):
//    Code       an intra-pulse phase code (RadarCodes): N chips of PW/N
//               tile the 50 %-to-50 % width; ChipSmoothing moves the
//               phase to the next chip's along a raised cosine over that
//               fraction of a chip, so |envelope| never dips.
//    Fm         the law the Chirp bandwidth is swept with: linear, or a
//               nonlinear FM (tangent, sine) that lingers near the centre
//               frequency, a spectral taper that lowers range sidelobes.
//    Gaussian   a Gaussian envelope of half-amplitude width PW (DME).
//    Train      several pulses in one record: staggered or jittered PRIs,
//               stepped frequencies, a burst then a dwell, pulse pairs.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>A pCW carrier's timing with every default applied (seconds), and the analysis window fractions.</summary>
    /// <summary>The law a pulse's chirp bandwidth is swept with.</summary>
    public enum PulseFm { Linear, Tangent, Sine }

    /// <summary>A pCW carrier's timing with every default applied (seconds), and the analysis window fractions.</summary>
    public sealed record PulseShape(double Pri, double Width, double Rise, double Fall, double Delay,
                                    double MeasureStart, double MeasureEnd, double ChirpHz = 0)
    {
        /// <summary>An intra-pulse phase code, one phase (rad) per chip; null = none. See <see cref="RadarCodes"/>.</summary>
        public double[]? Code { get; init; }
        /// <summary>Fraction of a chip (0 to 1) over which the phase moves to the next chip's; 0 = an abrupt step.</summary>
        public double ChipSmoothing { get; init; }
        /// <summary>The sweep law of <see cref="ChirpHz"/>: linear (the default), tangent or sine NLFM.</summary>
        public PulseFm Fm { get; init; }
        /// <summary>The NLFM's shape: β (rad, 0 &lt; β &lt; π/2) of the tangent law, k (0 ≤ k &lt; 1) of the sine law;
        /// null = <see cref="PulsedCw.DefaultTangentBeta"/> / <see cref="PulsedCw.DefaultSineK"/>.</summary>
        public double? FmParam { get; init; }
        /// <summary>A Gaussian envelope of half-amplitude width <see cref="Width"/> instead of the flat top and
        /// raised-cosine edges: it leaves 0 at Delay, peaks at Delay + 2 PW, and is cut at Delay + 4 PW
        /// (where it is 1.5e-5); Rise and Fall are not used.</summary>
        public bool Gaussian { get; init; }
        /// <summary>Several pulses in one record; null = one pulse in one PRI.</summary>
        public PulseTrain? Train { get; init; }

        /// <summary>The pulse centre, midway between the 50 % points: the chirp is at the carrier here.</summary>
        public double Centre => Gaussian ? Delay + 2 * Width : Delay + RiseSpan / 2 + Width / 2;
        /// <summary>The rising edge's 50 % point, where the code's first chip starts.</summary>
        public double Lead => Centre - Width / 2;

        /// <summary>The chirp's phase (rad) at record time <paramref name="t"/>: π B/PW (t − Tc)², so the frequency is
        /// B (t − Tc) / PW, from −B/2 to +B/2 over the width.</summary>
        public double ChirpPhase(double t) => Math.PI * ChirpHz / Width * (t - Centre) * (t - Centre);

        /// <summary>The NLFM's shape parameter with its default applied.</summary>
        public double FmShape => FmParam ?? (Fm == PulseFm.Tangent ? PulsedCw.DefaultTangentBeta : PulsedCw.DefaultSineK);

        /// <summary>
        /// The instantaneous frequency (Hz, from the carrier) at time <paramref name="t"/> from the slot's start, with
        /// τ = (t − Tc)/PW in [−1/2, 1/2] and held at ±B/2 beyond:
        ///   linear   f = B τ
        ///   tangent  f = (B/2) tan(2βτ) / tan β     (Collins and Atkins 1999, the tangent member of their family)
        ///   sine     f = B (τ − k sin(2πτ) / (2π)) (slope B(1 − k cos 2πτ)/PW: slow at the centre, fast at the ends)
        /// </summary>
        public double FrequencyAt(double t)
        {
            if (ChirpHz == 0) return 0;
            double tau = Math.Clamp((t - Centre) / Width, -0.5, 0.5), b = ChirpHz, k = FmShape;
            return Fm switch
            {
                PulseFm.Tangent => b / 2 * Math.Tan(2 * k * tau) / Math.Tan(k),
                PulseFm.Sine => b * (tau - k * Math.Sin(2 * Math.PI * tau) / (2 * Math.PI)),
                _ => b * tau,
            };
        }

        /// <summary>The phase (rad) at time <paramref name="t"/> from the slot's start: the sweep's, 2π ∫ f from the
        /// centre (zero there), plus the code's.</summary>
        public double PhaseAt(double t)
        {
            double p = 0;
            if (ChirpHz != 0)
            {
                if (Fm == PulseFm.Linear) p = ChirpPhase(t);
                else
                {
                    double T = Width, b = ChirpHz, k = FmShape, d = t - Centre, tau = Math.Clamp(d / T, -0.5, 0.5), u = tau * T;
                    p = Fm == PulseFm.Tangent
                        ? -Math.PI * b * T / (2 * k * Math.Tan(k)) * Math.Log(Math.Cos(2 * k * tau))
                        : Math.PI * b * u * u / T - k * b * T / (2 * Math.PI) * (1 - Math.Cos(2 * Math.PI * tau));
                    p += 2 * Math.PI * FrequencyAt(t) * (d - u);     // held at the band edge beyond the 50 % points
                }
            }
            if (Code is { Length: > 0 } c) p += CodePhase(c, t);
            return p;
        }

        private double CodePhase(double[] c, double t)
        {
            int n = c.Length;
            double pos = (t - Lead) / (Width / n);
            int i = Math.Clamp((int)Math.Floor(pos), 0, n - 1);
            double frac = pos - i, w = ChipSmoothing;
            if (w <= 0) return c[i];
            int from; double u;
            if (frac > 1 - w / 2 && i < n - 1) { from = i; u = (frac - (1 - w / 2)) / w; }
            else if (frac < w / 2 && i > 0) { from = i - 1; u = (frac + w / 2) / w; }
            else return c[i];
            double step = Math.IEEERemainder(c[from + 1] - c[from], 2 * Math.PI);   // the shorter way round, (−π, π]
            if (step == -Math.PI) step = Math.PI;
            return c[from] + step * 0.5 * (1 - Math.Cos(Math.PI * u));
        }

        /// <summary>Full duration of the rising edge (0 to 1).</summary>
        public double RiseSpan => Rise / PulsedCw.TenNinetyPerSpan;
        /// <summary>Full duration of the falling edge (1 to 0).</summary>
        public double FallSpan => Fall / PulsedCw.TenNinetyPerSpan;
        /// <summary>Where the flat top starts and ends, from the start of the record (the peak, for a Gaussian).</summary>
        public double TopStart => Gaussian ? Centre : Delay + RiseSpan;
        public double TopEnd => Gaussian ? Centre : Delay + RiseSpan / 2 + Width - FallSpan / 2;
        /// <summary>Where the falling edge reaches 0.</summary>
        public double End => Gaussian ? Delay + 4 * Width : Delay + RiseSpan / 2 + Width + FallSpan / 2;
        public double Prf => 1 / Pri;
        /// <summary>Width over PRI (the 50 % to 50 % duty).</summary>
        public double Duty => Width / Pri;

        /// <summary>The power duty of one pulse in one PRI: mean of |envelope|² over it. The top contributes its length,
        /// each raised-cosine edge of full span T contributes ∫ sin⁴ = 3T/8; a Gaussian PW √(π / (8 ln 2)).</summary>
        public double PowerDuty => Gaussian ? Width * Math.Sqrt(Math.PI / (8 * Math.Log(2))) / Pri
                                            : (TopEnd - TopStart + 0.375 * (RiseSpan + FallSpan)) / Pri;

        /// <summary>The envelope at record time <paramref name="t"/> (s, reduced modulo the PRI).</summary>
        public double At(double t)
        {
            t -= Math.Floor(t / Pri) * Pri;
            return Envelope(t);
        }

        /// <summary>The envelope at time <paramref name="t"/> from the slot's start, not reduced.</summary>
        public double Envelope(double t)
        {
            if (t <= Delay || t >= End) return 0;
            if (Gaussian) { double x = (t - Centre) / Width; return Math.Exp(-4 * Math.Log(2) * x * x); }
            if (t < TopStart) return 0.5 * (1 - Math.Cos(Math.PI * (t - Delay) / RiseSpan));
            if (t <= TopEnd) return 1;
            return 0.5 * (1 + Math.Cos(Math.PI * (t - TopEnd) / FallSpan));
        }
    }

    /// <summary>
    /// Several pulses in one periodic record. Slot i lasts PRI[i mod n] of <see cref="Pris"/> (null = the shape's PRI),
    /// times (1 + <see cref="Jitter"/> u) with u uniform in [−1, 1] from <see cref="Seed"/>, rounded to whole samples; it
    /// holds the shape's pulse at its Delay, and a second one <see cref="PairSpacing"/> later when that is set. Slot i's
    /// pulse sits at f0 + k Δf, k = i mod <see cref="Steps"/>, f0 = −(Steps − 1) Δf / 2 so the steps are centred on the
    /// carrier; its phase runs from the record's start, coherent from pulse to pulse. After <see cref="Pulses"/> slots
    /// comes <see cref="Dwell"/> of off-time, then the record repeats.
    /// </summary>
    public sealed record PulseTrain
    {
        public double[]? Pris { get; init; }
        /// <summary>± fraction of each PRI (0 to 1).</summary>
        public double Jitter { get; init; }
        public int Seed { get; init; } = 1;
        /// <summary>Pulses (slots) before the dwell; null = the least common multiple of the PRI count and the steps.</summary>
        public int? Pulses { get; init; }
        /// <summary>Off-time after the burst (s).</summary>
        public double Dwell { get; init; }
        /// <summary>Δf (Hz) between stepped pulses, and how many frequencies.</summary>
        public double StepHz { get; init; }
        public int Steps { get; init; } = 1;
        /// <summary>A pulse pair: the second pulse this long (s) after the first, peak to peak (0 = single pulses).</summary>
        public double PairSpacing { get; init; }

        public int PriCount => Pris is { Length: > 0 } p ? p.Length : 1;
        public double PriOf(PulseShape s, int i) => Pris is { Length: > 0 } p ? p[i % p.Length] : s.Pri;
        public int PulseCount
        {
            get
            {
            if (Pulses is int n) return n;
            int a = PriCount, b = Math.Max(Steps, 1);
            static int Gcd(int x, int y) => y == 0 ? x : Gcd(y, x % y);
            return a / Gcd(a, b) * b;
            }
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
        /// <summary>The NLFM shapes when none is stated (no optimum is claimed).</summary>
        public const double DefaultTangentBeta = 1.2, DefaultSineK = 0.5;

        /// <summary>A Gaussian's 10-90 % time over its half-amplitude width: (√ln 10 − √ln(10/9)) / √ln 2 / 2.</summary>
        public static readonly double GaussianTenNinetyPerWidth = (Math.Sqrt(Math.Log(10)) - Math.Sqrt(Math.Log(10.0 / 9))) / (2 * Math.Sqrt(Math.Log(2)));

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
        public static PulseShape Validate(CarrierSpec c) => Check(Resolve(c));

        /// <summary>Refuses a shape that cannot be generated, with a sentence naming the field; returns it.</summary>
        public static PulseShape Check(PulseShape s)
        {
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
            CheckRadar(s);
            NativeRateHz(s);
            return s;
        }

        /// <summary>
        /// The carrier's bandwidth, from its faster edge: twice the 99 %-power band of that edge (it sits on both sides
        /// of the carrier), over 0.9 — the occupied fraction every FILE carrier is measured with, so the envelope rate,
        /// the preview span and the bands follow from the edges exactly as they follow from a file's measured band. A chirp
        /// adds its sweep to the edges' band.
        /// </summary>
        /// A phase code adds its chips' main lobe (2 / chip), stepped frequencies their span.
        public static double BandwidthHz(PulseShape s) => (2 * OccupiedHalfBandHz(s) + Math.Abs(s.ChirpHz) + CodeBandHz(s) + StepSpanHz(s)) / 0.9;

        private static double CodeBandHz(PulseShape s) => s.Code is { Length: > 0 } c ? 2 * c.Length / s.Width : 0;
        private static double StepSpanHz(PulseShape s) => s.Train is { } t ? Math.Abs(t.StepHz) * (Math.Max(t.Steps, 1) - 1) : 0;

        /// <summary>The 99 %-power band of the faster edge, one side (Hz); a Gaussian's, 2.576 σ_f = 0.6826 / PW.</summary>
        public static double OccupiedHalfBandHz(PulseShape s)
            => s.Gaussian ? 2.5758 * Math.Sqrt(8 * Math.Log(2)) / (2 * Math.Sqrt(2) * Math.PI * s.Width)
                          : EdgeBand99TimesSpan / (Math.Min(s.Rise, s.Fall) / TenNinetyPerSpan);

        /// <summary>The faster edge's 10-90 % time.</summary>
        private static double EdgeTime(PulseShape s) => s.Gaussian ? GaussianTenNinetyPerWidth * s.Width : Math.Min(s.Rise, s.Fall);

        /// <summary>The native rate: 1.92 MHz x 2^k, the smallest k that puts at least <see cref="SamplesPerEdge"/> samples
        /// in the faster edge's 10-90 % time, at least twice the bandwidth (which only a chirp reaches: a plain pulse's is
        /// 1.55 / tr) and a whole number of samples in a PRI. A PRI no such rate divides is refused.</summary>
        public static double NativeRateHz(PulseShape s)
        {
            double need = Math.Max(SamplesPerEdge / EdgeTime(s), 2 * BandwidthHz(s));
            double r = SignalRate.LadderBaseHz;
            while (r < need) r *= 2;
            // at most 2^8 above the edge's own need, and whole to an absolute 1e-6 of a sample: a tolerance relative to
            // the count would let a large enough rate pass any PRI
            for (int k = 0; k <= 8; k++, r *= 2)
            {
                if (Whole(s.Pri * r) && (s.Train is not { } t || TrainWhole(s, t, r))) return r;
            }
            if (s.Train is not null)
                throw new FormatException("pCW carrier: a train's PRIs and Dwell must be whole numbers of samples at a 1.92 MHz x 2^k rate");
            throw new FormatException($"pCW carrier: PRI {FormatTime(s.Pri)} is not a whole number of samples at any 1.92 MHz x 2^k rate; choose a PRI that is (100 us, 1 ms, 250 us ...)");
        }

        private static bool Whole(double n) => Math.Abs(n - Math.Round(n)) <= 1e-6;

        private static bool TrainWhole(PulseShape s, PulseTrain t, double r)
            => (t.Pris ?? []).All(p => Whole(p * r)) && Whole(t.Dwell * r);

        /// <summary>The record (one PRI, or a train's slots and dwell) at <paramref name="rateHz"/>: exact 0 and 1 off and
        /// on the top (in magnitude, with a chirp or a code).</summary>
        public static Complex[] Generate(PulseShape s, double rateHz)
        {
            if (s.Train is { } train) return GenerateTrain(s, train, rateHz);
            int n = (int)Math.Round(s.Pri * rateHz);
            var x = new Complex[n];
            if (s.ChirpHz == 0 && s.Code is null)
                for (int i = 0; i < n; i++) x[i] = new Complex(s.At(i / rateHz), 0);
            else if (s.Code is null && s.Fm == PulseFm.Linear)
                for (int i = 0; i < n; i++) x[i] = Complex.FromPolarCoordinates(s.At(i / rateHz), s.ChirpPhase(i / rateHz));
            else
                for (int i = 0; i < n; i++)
                {
                    double t = i / rateHz, a = s.At(t);
                    x[i] = a == 0 ? Complex.Zero : Complex.FromPolarCoordinates(a, s.PhaseAt(t - Math.Floor(t / s.Pri) * s.Pri));
                }
            return x;
        }

        /// <summary>One pulse alone, from its Delay to its End at <paramref name="rateHz"/>: what a matched filter is
        /// matched to (<see cref="PulseCompression"/>).</summary>
        public static Complex[] OnePulse(PulseShape s, double rateHz)
        {
            int i0 = (int)Math.Floor(s.Delay * rateHz), i1 = (int)Math.Ceiling(s.End * rateHz);
            var x = new Complex[i1 - i0 + 1];
            for (int i = 0; i < x.Length; i++)
            {
                double t = (i0 + i) / rateHz, a = s.Envelope(t);
                x[i] = a == 0 ? Complex.Zero : Complex.FromPolarCoordinates(a, s.PhaseAt(t));
            }
            return x;
        }

        /// <summary>A train's slots: each one's start (s, on the native sample grid), frequency offset (Hz), and the
        /// record length (the slots and the dwell).</summary>
        public static (double[] Starts, double[] OffsetsHz, double Record) Layout(PulseShape s)
        {
            if (s.Train is not { } t) return ([0], [0], s.Pri);
            double r = NativeRateHz(s);
            int m = t.PulseCount, steps = Math.Max(t.Steps, 1);
            var rng = new Random(t.Seed);
            var starts = new double[m];
            var off = new double[m];
            long at = 0;                                                     // whole samples at the native rate
            for (int i = 0; i < m; i++)
            {
                starts[i] = at / r;
                off[i] = ((i % steps) - (steps - 1) / 2.0) * t.StepHz;
                double pri = t.PriOf(s, i) * (t.Jitter > 0 ? 1 + t.Jitter * (2 * rng.NextDouble() - 1) : 1);
                at += (long)Math.Round(pri * r);
            }
            at += (long)Math.Round(t.Dwell * r);
            return (starts, off, at / r);
        }

        /// <summary>The record length (s): the PRI, or a train's slots and dwell.</summary>
        public static double RecordSeconds(PulseShape s) => s.Train is null ? s.Pri : Layout(s).Record;

        private static Complex[] GenerateTrain(PulseShape s, PulseTrain t, double rateHz)
        {
            var (starts, offsets, record) = Layout(s);
            var x = new Complex[(int)Math.Round(record * rateHz)];
            double[] subs = t.PairSpacing > 0 ? [0, t.PairSpacing] : [0];
            for (int p = 0; p < starts.Length; p++)
                foreach (double o in subs)
                {
                    double t0 = starts[p] + o;
                    int i0 = (int)Math.Ceiling((t0 + s.Delay) * rateHz), i1 = (int)Math.Floor((t0 + s.End) * rateHz);
                    for (int i = Math.Max(i0, 0); i <= i1 && i < x.Length; i++)
                    {
                        double tr = i / rateHz, a = s.Envelope(tr - t0);
                        if (a == 0) continue;
                        x[i] += Complex.FromPolarCoordinates(a, s.PhaseAt(tr - t0) + 2 * Math.PI * offsets[p] * tr);
                    }
                }
            return x;
        }

        private static void CheckRadar(PulseShape s)
        {
            if (s.Code is { } c && (c.Length == 0 || c.Any(v => !double.IsFinite(v))))
                throw new FormatException("pCW carrier: Code must hold at least one finite chip phase");
            if (!(s.ChipSmoothing >= 0 && s.ChipSmoothing <= 1))
                throw new FormatException($"pCW carrier: ChipSmoothing is a fraction of a chip, 0 to 1 (got {s.ChipSmoothing.ToString(CultureInfo.InvariantCulture)})");
            if (s.Fm == PulseFm.Tangent && !(s.FmShape > 0 && s.FmShape < Math.PI / 2))
                throw new FormatException($"pCW carrier: the tangent NLFM's beta must lie between 0 and pi/2 rad (got {s.FmShape.ToString(CultureInfo.InvariantCulture)})");
            if (s.Fm == PulseFm.Sine && !(s.FmShape >= 0 && s.FmShape < 1))
                throw new FormatException($"pCW carrier: the sine NLFM's k must lie in 0 <= k < 1 (got {s.FmShape.ToString(CultureInfo.InvariantCulture)})");
            if (s.Train is not { } t) return;
            if (t.Pris is { } ps && (ps.Length == 0 || ps.Any(p => !(p > 0) || !double.IsFinite(p))))
                throw new FormatException("pCW carrier: the train's PRIs must be positive times");
            if (!(t.Jitter >= 0 && t.Jitter < 1))
                throw new FormatException($"pCW carrier: Jitter is a fraction of the PRI, 0 <= Jitter < 1 (got {t.Jitter.ToString(CultureInfo.InvariantCulture)})");
            if (t.Steps < 1 || t.PulseCount < 1 || !double.IsFinite(t.StepHz))
                throw new FormatException("pCW carrier: a train needs at least one pulse and one frequency step");
            if (!(t.Dwell >= 0) || !(t.PairSpacing >= 0))
                throw new FormatException("pCW carrier: Dwell and the pair spacing are times of 0 or more");
            double shortest = Enumerable.Range(0, t.PriCount).Min(i => t.PriOf(s, i)) * (1 - t.Jitter);
            if (s.End + t.PairSpacing > shortest * (1 + 1e-12))
                throw new FormatException($"pCW carrier: the pulse{(t.PairSpacing > 0 ? " pair" : "")} ends at {FormatTime(s.End + t.PairSpacing)}, past the shortest PRI ({FormatTime(shortest)})");
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
