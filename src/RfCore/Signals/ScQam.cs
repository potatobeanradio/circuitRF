// ================================================================
//  ScQam.cs  —  the single-carrier PSK / QAM / APSK carrier (brief PCH-5)
//
//  Symbols at Rs, shaped by a root-raised-cosine filter of roll-off α,
//  so the matched filter at the receiver makes the raised cosine and
//  every symbol instant is free of its neighbours.
//
//  The shaping is applied in the frequency domain over the whole
//  periodic record (the filter's span is the record): the spectrum is
//  exactly zero beyond Rs(1 + α)/2, and the matched-filtered record has
//  no intersymbol interference at all, to round-off. Span=n truncates
//  the impulse response to n symbols (n/2 either side, rectangular),
//  as a transmitter's FIR does; the receiver's matched filter is then
//  the same truncated filter, and the eye keeps the ISI it leaves.
//
//  Constellations, unit mean power, the label of a point being the
//  bits it carries, first bit first:
//    BPSK          0 -> +1, 1 -> -1
//    QPSK          Gray: first bit I, second Q, (±1 ± j)/√2
//    8PSK          Gray round the circle: the point at kπ/4 carries
//                  k ^ (k >> 1)
//    16/64/256QAM  per-axis Gray, the first half of the bits I, the
//                  second Q (the OFDM carriers' mapping)
//    16APSK        4 + 12 points on two rings, radii 1 : Rings
//                  (default 2.85), phase offsets Phases (default 45°,
//                  15°); the natural ring-wise mapping: labels 0-3 the
//                  inner ring counter-clockwise from its offset, 4-15
//                  the outer ring the same way
//    32APSK        4 + 12 + 16, radii 1 : Rings (default 2.84, 5.27),
//                  Phases (default 45°, 15°, 0°), the same mapping
//  APSK is normalised to the constellation's mean power (every point
//  equally likely), which is what its EVM is read against.
//
//  The record. The data (PN9, PN15, PN23 or a fixed pattern) is read
//  k bits a symbol, first bit first, cyclically; the record holds one
//  whole data period — lcm(period, k)/k symbols, k data periods when
//  k shares no factor with the period — capped at 65,536 data symbols
//  (PN23's period would be 8.4 million), or Symbols= of them. An
//  optional preamble of Preamble= known symbols starts the record, and
//  PilotBlock=L/P puts L known symbols after every P data symbols
//  (none after the last). Known symbols are QPSK, (±1 ± j)/√2, read
//  from PN9 two bits a symbol in order through the record. The record
//  is whole symbols and periodic, as every carrier's is.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;

namespace RfCore.Signals
{
    /// <summary>An SC carrier that cannot be generated: a FormatException, as every carrier's refusal is (the netlist
    /// reader and the Signal Designer catch that), carrying its coded <see cref="CircuitRF.Diagnostics.Diagnostic"/>.</summary>
    public sealed class ScCarrierRefusal : FormatException
    {
        public Diagnostic Diagnostic { get; }
        public ScCarrierRefusal(Diagnostic diagnostic) : base(diagnostic.Render()) { Diagnostic = diagnostic; }
    }

    /// <summary>The SC carrier's coded refusals, each naming the field to change.</summary>
    public static class ScDiagnostics
    {
        private static Diagnostic E(string id, string template, params (string, object?)[] args)
            => Diagnostic.Create("signal.sc." + id, DiagnosticSeverity.Error, template, args);

        private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        public static Diagnostic Modulation(string value)
            => E("modulation", "SC carrier: unknown modulation '{value}' ({choices})", ("value", value), ("choices", string.Join(", ", ScQam.ModNames)));
        public static Diagnostic NotAList(string key, string text)
            => E("list", "SC carrier: {key}={text} is not a list of numbers separated by commas", ("key", key), ("text", text));
        public static Diagnostic PilotBlock(string text)
            => E("pilot-block", "SC carrier: PilotBlock={text} is not L/P (L known symbols after every P data symbols, both whole numbers from 1)", ("text", text));
        public static Diagnostic SymbolRate(double value)
            => E("symbol-rate", "SC carrier: Rs must be a positive symbol rate (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic RollOff(double value)
            => E("roll-off", "SC carrier: RollOff must be 0 to 1 (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic Span(int value)
            => E("span", "SC carrier: Span must be 2 symbols or more (got {value}); leave it out to shape over the whole record", ("value", value));
        public static Diagnostic Data(string? value)
            => E("data", "SC carrier: Data must be PN9, PN15, PN23 or a pattern of 0s and 1s (got '{value}')", ("value", value));
        public static Diagnostic Symbols(int value)
            => E("symbols", "SC carrier: Symbols must be 1 or more (got {value})", ("value", value));
        public static Diagnostic Preamble(int value)
            => E("preamble", "SC carrier: Preamble must be 0 or more symbols (got {value})", ("value", value));
        public static Diagnostic RingCount(string mod, int rings, int got)
            => E("rings-count", "SC carrier: {mod} has {rings} rings, so Rings takes {need} radius ratios to the inner ring, separated by commas (got {got})",
                 ("mod", mod), ("rings", rings), ("need", rings - 1), ("got", got));
        public static Diagnostic RingOrder(string? text)
            => E("rings-order", "SC carrier: Rings must grow outwards from 1 (got {text})", ("text", text));
        public static Diagnostic PhaseCount(string mod, int rings, int got)
            => E("phases-count", "SC carrier: {mod} has {rings} rings, so Phases takes {rings} offsets in degrees (got {got})",
                 ("mod", mod), ("rings", rings), ("got", got));
        public static Diagnostic NotApsk(string mod)
            => E("rings-not-apsk", "SC carrier: Rings and Phases belong to 16APSK and 32APSK, not {mod}", ("mod", mod));
        public static Diagnostic TooLong(int symbols, long samples, int max)
            => E("too-long", "SC carrier: {symbolsText} symbols make {samplesText} samples, over the {maxText} a record may hold; lower Symbols",
                 ("symbols", symbols), ("samples", samples), ("max", max),
                 ("symbolsText", symbols.ToString("N0", CultureInfo.InvariantCulture)),
                 ("samplesText", samples.ToString("N0", CultureInfo.InvariantCulture)),
                 ("maxText", max.ToString("N0", CultureInfo.InvariantCulture)));
    }

    public enum ScModulation { Bpsk, Qpsk, Psk8, Qam16, Qam64, Qam256, Apsk16, Apsk32 }

    /// <summary>A single-carrier carrier's settings with every default applied.</summary>
    /// <param name="SymbolRate">Rs, symbols/s.</param>
    /// <param name="Span">The shaping filter's span in symbols; null = the whole record (exact).</param>
    /// <param name="Rings">APSK: each outer ring's radius over the inner ring's.</param>
    /// <param name="PhasesDeg">APSK: each ring's phase offset, degrees, inner first.</param>
    /// <param name="DataSymbols">Data symbols in the record.</param>
    public sealed record ScShape(double SymbolRate, ScModulation Mod, double RollOff, int? Span, double[] Rings, double[] PhasesDeg,
                                 string Data, int Preamble, int PilotLength, int PilotEvery, int DataSymbols)
    {
        public int BitsPerSymbol => ScQam.BitsPerSymbol(Mod);
        /// <summary>Pilot blocks in the record: one after every <see cref="PilotEvery"/> data symbols but the last.</summary>
        public int PilotBlocks => PilotLength > 0 && PilotEvery > 0 ? (DataSymbols - 1) / PilotEvery : 0;
        public int KnownSymbols => Preamble + PilotBlocks * PilotLength;
        public int RecordSymbols => DataSymbols + KnownSymbols;
        public double RecordSeconds => RecordSymbols / SymbolRate;
    }

    /// <summary>The record's symbols: the ideal point at each, whether it is known (preamble or pilot), and a data symbol's
    /// label (-1 for a known one).</summary>
    public sealed record ScSymbols(Complex[] Points, bool[] Known, int[] Labels);

    public static class ScQam
    {
        public const double DefaultSymbolRate = 1e6;
        public const ScModulation DefaultModulation = ScModulation.Qpsk;
        public const double DefaultRollOff = 0.35;
        public const string DefaultData = "PN9";
        /// <summary>Most data symbols a record holds unless Symbols= says otherwise.</summary>
        public const int DefaultMaxSymbols = 1 << 16;

        /// <summary>Samples per symbol at the native rate: the band ends at Rs(1 + α)/2 ≤ Rs, a quarter of the native
        /// Nyquist, so a compressed amplifier's regrowth has room in the native record too.</summary>
        public const int SamplesPerSymbol = 8;

        public static readonly double[] DefaultRings16 = [2.85];
        public static readonly double[] DefaultRings32 = [2.84, 5.27];
        public static readonly double[] DefaultPhases16 = [45, 15];
        public static readonly double[] DefaultPhases32 = [45, 15, 0];

        public static int BitsPerSymbol(ScModulation m) => m switch
        {
            ScModulation.Bpsk => 1,
            ScModulation.Qpsk => 2,
            ScModulation.Psk8 => 3,
            ScModulation.Qam16 or ScModulation.Apsk16 => 4,
            ScModulation.Apsk32 => 5,
            ScModulation.Qam64 => 6,
            _ => 8,
        };

        public static bool IsApsk(ScModulation m) => m is ScModulation.Apsk16 or ScModulation.Apsk32;

        public static int[] RingSizes(ScModulation m) => m == ScModulation.Apsk16 ? [4, 12] : [4, 12, 16];

        public static string ModName(ScModulation m) => m switch
        {
            ScModulation.Bpsk => "BPSK",
            ScModulation.Qpsk => "QPSK",
            ScModulation.Psk8 => "8PSK",
            ScModulation.Qam16 => "16QAM",
            ScModulation.Qam64 => "64QAM",
            ScModulation.Qam256 => "256QAM",
            ScModulation.Apsk16 => "16APSK",
            _ => "32APSK",
        };

        public static readonly string[] ModNames = [.. Enum.GetValues<ScModulation>().Select(ModName)];

        public static ScModulation ParseMod(string v)
        {
            foreach (var m in Enum.GetValues<ScModulation>())
                if (ModName(m).Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return m;
            throw new ScCarrierRefusal(ScDiagnostics.Modulation(v));
        }

        /// <summary>A comma-separated list of numbers (<c>Rings=</c>, <c>Phases=</c>), or null when empty.</summary>
        public static double[]? ParseList(string? text, string key)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    throw new ScCarrierRefusal(ScDiagnostics.NotAList(key, text));
            return v;
        }

        /// <summary><c>PilotBlock=L/P</c>: L known symbols after every P data symbols; null when not that.</summary>
        public static (int Length, int Every)? ParsePilotBlock(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var p = text.Split('/', StringSplitOptions.TrimEntries);
            if (p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l)
                && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) && l >= 1 && e >= 1)
                return (l, e);
            throw new ScCarrierRefusal(ScDiagnostics.PilotBlock(text));
        }

        public static ScShape Resolve(CarrierSpec c)
        {
            var mod = c.ScModulation ?? DefaultModulation;
            string data = (c.Data ?? DefaultData).ToUpperInvariant();
            int k = BitsPerSymbol(mod);
            var pilot = ParsePilotBlock(c.PilotBlock);
            int nd = c.Symbols ?? (int)Math.Min(DefaultMaxSymbols, PeriodSymbols(data, k));
            return new ScShape(c.SymbolRateHz ?? DefaultSymbolRate, mod, c.RollOff ?? DefaultRollOff, c.Span,
                               ParseList(c.Rings, "Rings") ?? (mod == ScModulation.Apsk32 ? DefaultRings32 : IsApsk(mod) ? DefaultRings16 : []),
                               ParseList(c.RingPhases, "Phases") ?? (mod == ScModulation.Apsk32 ? DefaultPhases32 : IsApsk(mod) ? DefaultPhases16 : []),
                               data, c.Preamble ?? 0, pilot?.Length ?? 0, pilot?.Every ?? 0, nd);
        }

        /// <summary>Symbols in one whole data period: lcm(period, k) / k.</summary>
        public static long PeriodSymbols(string data, int k)
        {
            long p = DataLength(data);
            long g = Gcd(p, k);
            return p / g;
        }

        private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }

        /// <summary>The data's period in bits.</summary>
        public static int DataLength(string data) => data switch
        {
            "PN9" => 511,
            "PN15" => 32767,
            "PN23" => 8388607,
            _ => data.Length,
        };

        /// <summary>Refuses a single-carrier carrier that cannot be generated, with a sentence naming the field; returns
        /// the shape.</summary>
        public static ScShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            if (!(s.SymbolRate > 0) || !double.IsFinite(s.SymbolRate)) throw new ScCarrierRefusal(ScDiagnostics.SymbolRate(s.SymbolRate));
            if (!(s.RollOff >= 0 && s.RollOff <= 1)) throw new ScCarrierRefusal(ScDiagnostics.RollOff(s.RollOff));
            if (s.Span is int sp && sp < 2) throw new ScCarrierRefusal(ScDiagnostics.Span(sp));
            if (s.Data is not ("PN9" or "PN15" or "PN23") && (s.Data.Length == 0 || s.Data.Any(ch => ch is not ('0' or '1'))))
                throw new ScCarrierRefusal(ScDiagnostics.Data(c.Data));
            if (s.DataSymbols < 1) throw new ScCarrierRefusal(ScDiagnostics.Symbols(s.DataSymbols));
            if (s.Preamble < 0) throw new ScCarrierRefusal(ScDiagnostics.Preamble(s.Preamble));
            if (IsApsk(s.Mod))
            {
                int rings = RingSizes(s.Mod).Length;
                if (s.Rings.Length != rings - 1) throw new ScCarrierRefusal(ScDiagnostics.RingCount(ModName(s.Mod), rings, s.Rings.Length));
                for (int i = 0; i < s.Rings.Length; i++)
                    if (!(s.Rings[i] > (i == 0 ? 1 : s.Rings[i - 1])) || !double.IsFinite(s.Rings[i]))
                        throw new ScCarrierRefusal(ScDiagnostics.RingOrder(c.Rings));
                if (s.PhasesDeg.Length != rings) throw new ScCarrierRefusal(ScDiagnostics.PhaseCount(ModName(s.Mod), rings, s.PhasesDeg.Length));
            }
            else if (c.Rings is not null || c.RingPhases is not null)
                throw new ScCarrierRefusal(ScDiagnostics.NotApsk(ModName(s.Mod)));
            long n = (long)s.RecordSymbols * SamplesPerSymbol;
            if (n > Fsk.MaxSamples) throw new ScCarrierRefusal(ScDiagnostics.TooLong(s.RecordSymbols, n, Fsk.MaxSamples));
            return s;
        }

        /// <summary>The channel bandwidth: Rs(1 + α), where the shaped spectrum ends.</summary>
        public static double BandwidthHz(ScShape s) => s.SymbolRate * (1 + s.RollOff);

        public static double NativeRateHz(ScShape s) => SamplesPerSymbol * s.SymbolRate;

        // ── the constellation ─────────────────────────────────────────────────────────────────────────────

        private static int Gray(int i) => i ^ (i >> 1);

        /// <summary>The points by label, unit mean power (the file header).</summary>
        public static Complex[] Constellation(ScShape s)
        {
            int k = s.BitsPerSymbol, m = 1 << k;
            var p = new Complex[m];
            switch (s.Mod)
            {
                case ScModulation.Bpsk:
                    p[0] = 1; p[1] = -1;
                    return p;
                case ScModulation.Qpsk:
                    for (int l = 0; l < 4; l++) p[l] = new Complex(1 - 2 * (l >> 1), 1 - 2 * (l & 1)) / Math.Sqrt(2);
                    return p;
                case ScModulation.Psk8:
                    for (int i = 0; i < 8; i++) p[Gray(i)] = Complex.FromPolarCoordinates(1, i * Math.PI / 4);
                    return p;
                case ScModulation.Qam16 or ScModulation.Qam64 or ScModulation.Qam256:
                {
                    int h = k / 2, levels = 1 << h;
                    double norm = 1.0 / Math.Sqrt(2.0 * (levels * levels - 1) / 3.0);
                    for (int i = 0; i < levels; i++)
                        for (int q = 0; q < levels; q++)
                            p[(Gray(i) << h) | Gray(q)] = new Complex((2 * i - levels + 1) * norm, (2 * q - levels + 1) * norm);
                    return p;
                }
                default:
                {
                    var sizes = RingSizes(s.Mod);
                    int l = 0;
                    for (int r = 0; r < sizes.Length; r++)
                    {
                        double radius = r == 0 ? 1 : s.Rings[r - 1], ph = s.PhasesDeg[r] * Math.PI / 180;
                        for (int j = 0; j < sizes[r]; j++) p[l++] = Complex.FromPolarCoordinates(radius, ph + 2 * Math.PI * j / sizes[r]);
                    }
                    double mean = p.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary), a = 1 / Math.Sqrt(mean);
                    for (int i = 0; i < m; i++) p[i] *= a;
                    return p;
                }
            }
        }

        /// <summary>The label of the point of <paramref name="constellation"/> nearest <paramref name="v"/>.</summary>
        public static int Decide(Complex[] constellation, Complex v)
        {
            int best = 0; double bd = double.MaxValue;
            for (int l = 0; l < constellation.Length; l++)
            {
                double dr = v.Real - constellation[l].Real, di = v.Imaginary - constellation[l].Imaginary, d = dr * dr + di * di;
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        /// <summary>A known symbol's point: QPSK from PN9, two bits per known symbol, in order through the record.</summary>
        private static Complex KnownPoint(byte[] pn9, int j)
        {
            int b0 = pn9[2 * j % pn9.Length], b1 = pn9[(2 * j + 1) % pn9.Length];
            return new Complex(1 - 2 * b0, 1 - 2 * b1) / Math.Sqrt(2);
        }

        /// <summary>The record's symbols, in order (the file header).</summary>
        public static ScSymbols Symbols(ScShape s)
        {
            var bits = Fsk.DataBits(s.Data);
            var pn9 = Fsk.Prbs(9, 5);
            var cons = Constellation(s);
            int k = s.BitsPerSymbol, n = s.RecordSymbols;
            var pts = new Complex[n]; var known = new bool[n]; var labels = new int[n];
            int at = 0, kn = 0;
            void Known(int count)
            {
                for (int i = 0; i < count; i++) { pts[at] = KnownPoint(pn9, kn++); known[at] = true; labels[at] = -1; at++; }
            }
            Known(s.Preamble);
            long bit = 0;
            for (int d = 0; d < s.DataSymbols; d++)
            {
                if (d > 0 && s.PilotLength > 0 && d % s.PilotEvery == 0) Known(s.PilotLength);
                int l = 0;
                for (int j = 0; j < k; j++) l = (l << 1) | bits[(int)(bit++ % bits.Length)];
                pts[at] = cons[l]; labels[at] = l; at++;
            }
            return new ScSymbols(pts, known, labels);
        }

        // ── shaping ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The root-raised-cosine response at <paramref name="fHz"/>, 1 in the flat part — the whole-record filter's.</summary>
        public static double RootRaisedCosine(double fHz, double rs, double alpha)
        {
            double f = Math.Abs(fHz), lo = (1 - alpha) * rs / 2, hi = (1 + alpha) * rs / 2;
            if (f <= lo) return 1;
            if (f >= hi) return 0;
            return Math.Cos(Math.PI / (2 * alpha * rs) * (f - lo));
        }

        /// <summary>The root-raised-cosine impulse response at <paramref name="t"/> symbols (peak 1 − α + 4α/π).</summary>
        public static double ImpulseResponse(double t, double alpha)
        {
            if (Math.Abs(t) < 1e-12) return 1 - alpha + 4 * alpha / Math.PI;
            if (alpha > 0 && Math.Abs(Math.Abs(t) - 1 / (4 * alpha)) < 1e-9)
                return alpha / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * alpha)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * alpha)));
            double x = 4 * alpha * t;
            return (Math.Sin(Math.PI * t * (1 - alpha)) + x * Math.Cos(Math.PI * t * (1 + alpha))) / (Math.PI * t * (1 - x * x));
        }

        /// <summary>
        /// The shaping (and matched) filter's response at <paramref name="fHz"/>: the root raised cosine for a whole-record
        /// filter; for Span=n the transform of the n-symbol truncated impulse response at the native rate, scaled to 1 at DC
        /// as the whole-record filter is.
        /// </summary>
        public static Complex Response(ScShape s, double fHz)
        {
            if (s.Span is not int span) return RootRaisedCosine(fHz, s.SymbolRate, s.RollOff);
            const int sps = SamplesPerSymbol;
            int half = span * sps / 2;
            double fs = NativeRateHz(s), dc = 0;
            Complex acc = 0;
            for (int n = -half; n <= half; n++)
            {
                double h = ImpulseResponse((double)n / sps, s.RollOff);
                dc += h;
                acc += h * Complex.FromPolarCoordinates(1, -2 * Math.PI * fHz * n / fs);
            }
            return acc / dc;
        }

        /// <summary><paramref name="x"/> (periodic, at <paramref name="rateHz"/>) through the filter, in the frequency
        /// domain — conjugated for the matched filter.</summary>
        public static Complex[] Filter(ScShape s, Complex[] x, double rateHz, bool matched)
        {
            int n = x.Length;
            var spec = Fft.Forward(x);
            const int sps = SamplesPerSymbol;
            if (s.Span is int span && Math.Abs(rateHz / NativeRateHz(s) - 1) < 1e-12 && span * sps < n)
            {
                // the truncated taps laid circularly at the native rate: their transform is Response at every bin
                var taps = new Complex[n];
                double dc = 0;
                for (int t = -span * sps / 2; t <= span * sps / 2; t++)
                {
                    double h = ImpulseResponse((double)t / sps, s.RollOff);
                    taps[(t + n) % n] += h;
                    dc += h;
                }
                var hs = Fft.Forward(taps);
                for (int b = 0; b < n; b++) spec[b] *= (matched ? Complex.Conjugate(hs[b]) : hs[b]) / dc;
                return Fft.Inverse(spec);
            }
            for (int b = 0; b < n; b++)
            {
                var h = Response(s, (b <= n / 2 ? b : b - n) * rateHz / n);
                spec[b] *= matched ? Complex.Conjugate(h) : h;
            }
            return Fft.Inverse(spec);
        }

        /// <summary>One record at the native rate (<see cref="SamplesPerSymbol"/> per symbol), unit mean power: symbol k
        /// is an impulse at sample k·sps through the shaping filter, so its matched-filtered peak is on that sample.</summary>
        public static Complex[] Generate(ScShape s)
        {
            var a = Symbols(s).Points;
            const int sps = SamplesPerSymbol;
            var z = new Complex[a.Length * sps];
            for (int k = 0; k < a.Length; k++) z[k * sps] = a[k];
            var x = Filter(s, z, NativeRateHz(s), matched: false);
            OfdmGenerator.Normalise(x);
            return x;
        }

        // ── the semi-analytic BER ─────────────────────────────────────────────────────────────────────────

        private static double Q(double x) => 0.5 * RfCore.Statistics.Normal.Erfc(x / Math.Sqrt(2));

        /// <summary>
        /// The BER at a symbol SNR γ = Es/N0 (linear) in additive white Gaussian noise: BPSK Q(√(2γ)); QPSK and square QAM
        /// as <see cref="SignalDecisions.QamBer"/>; 8PSK exact (<see cref="Psk8Ber"/>); APSK by the union bound over every
        /// pair of points, (1/(M k)) Σ_i Σ_j d_H(i, j) Q(|s_i − s_j| √(γ/2)), tight at the SNRs it is read at.
        /// </summary>
        public static double Ber(ScShape s, double gamma)
        {
            switch (s.Mod)
            {
                case ScModulation.Bpsk: return Q(Math.Sqrt(2 * gamma));
                case ScModulation.Qpsk: return SignalDecisions.QamBer(QamOrder.Qpsk, gamma);
                case ScModulation.Qam16: return SignalDecisions.QamBer(QamOrder.Qam16, gamma);
                case ScModulation.Qam64: return SignalDecisions.QamBer(QamOrder.Qam64, gamma);
                case ScModulation.Qam256: return SignalDecisions.QamBer(QamOrder.Qam256, gamma);
                case ScModulation.Psk8: return Psk8Ber(gamma);
            }
            var c = Constellation(s);
            int m = c.Length, k = s.BitsPerSymbol;
            double sum = 0, r = Math.Sqrt(gamma / 2);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    if (i != j) sum += System.Numerics.BitOperations.PopCount((uint)(i ^ j)) * Q((c[i] - c[j]).Magnitude * r);
            return Math.Min(0.5, sum / (m * k));
        }

        /// <summary>
        /// Gray-coded 8PSK's BER at γ = Es/N0, exact: the received phase's density about the sent point,
        /// p(θ) = e^(−γ)/2π + √γ/(2√π) cos θ e^(−γ sin²θ) (1 + erf(√γ cos θ)), integrated over each other point's
        /// decision sector (Simpson, 400 panels a sector), each weighted by the bits it differs in, over 3.
        /// </summary>
        public static double Psk8Ber(double gamma)
        {
            double sg = Math.Sqrt(gamma);
            double P(double th)
            {
                double c = Math.Cos(th), s = Math.Sin(th);
                return Math.Exp(-gamma) / (2 * Math.PI)
                       + sg / (2 * Math.Sqrt(Math.PI)) * c * Math.Exp(-gamma * s * s) * RfCore.Statistics.Normal.Erfc(-sg * c);
            }
            double ber = 0;
            for (int j = 1; j < 8; j++)
            {
                double a = j * Math.PI / 4 - Math.PI / 8, b = a + Math.PI / 4;
                const int panels = 400;
                double h = (b - a) / panels, sum = P(a) + P(b);
                for (int i = 1; i < panels; i++) sum += P(a + i * h) * (i % 2 == 1 ? 4 : 2);
                ber += System.Numerics.BitOperations.PopCount((uint)(j ^ (j >> 1))) * sum * h / 3;
            }
            return ber / 3;
        }
    }
}
