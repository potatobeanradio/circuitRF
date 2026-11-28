// ================================================================
//  Fsk.cs  —  the FSK carrier: continuous-phase 2/4/8-level FSK, GFSK,
//              GMSK, C4FM, RRC-shaped 4FSK, LREC / LRC CPM and bursts
//
//  Each bit holds the instantaneous frequency at +Dev (a 1) or -Dev
//  (a 0) for one bit period; the phase is the running integral of it,
//  so it never jumps (CPFSK). A Gaussian filter of bandwidth-time
//  product BT on the frequency pulse makes it GFSK, and GMSK when the
//  modulation index h = 2 Dev / Rb is 0.5. The envelope is constant,
//  so the record is unit power everywhere and its PAPR is 0 dB.
//
//  The data is a PN sequence (PN9, PN15: ITU-T O.150 polynomials,
//  all-ones start) or a fixed bit pattern, and the record is ONE
//  period of it: 511 bits for PN9, the pattern's length otherwise.
//
//  Closing the record. The phase gathers pi h per 1 and loses it per
//  0, so over a record it gathers pi h (ones - zeros) — 0.9 pi for
//  PN9 at h = 0.9 — and a record repeated as it stands would jump by
//  that much at every seam. The residual (wrapped to +-pi) is taken
//  out as a linear phase ramp across the record: the whole carrier
//  moves by at most half a record bin, 1 / (2 x record) — 98 Hz for
//  PN9 at 100 kbps — which is the same snapping to the record's
//  frequency grid every carrier offset already gets.
//
//  M levels. log2 M bits (first bit most significant) pick a level,
//  an odd integer up to +-(M-1), from the mapping; the frequency is
//  level x Dev / (M-1), so Dev is the outer symbol's deviation and
//  h = 2 Dev / ((M-1) Rs). The data repeats until it fills whole
//  symbols (PN9 twice for 4FSK).
//
//  Frequency pulses. Rectangular (NRZ, with the optional Gaussian
//  filter) as before; otherwise each symbol is an impulse through
//  G(f), centred on the symbol: raised cosine, root raised cosine
//  (DMR, dPMR), C4FM's raised cosine with the inverse-sinc shaping
//  filter (P25), or an L-symbol REC / RC pulse (partial-response CPM).
//
//  Bursts. Ramp up, preamble, payload with the sync word at its place,
//  tail, ramp down, then the carrier off for the rest of the guard;
//  the ramps are raised-cosine and unmodulated. A burst may switch to
//  a payload of another modulation after a guard (Bluetooth EDR): the
//  payload comes through a function and joins the FSK phase. A record
//  that goes off needs no closing ramp: its seam is at zero amplitude.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>The frequency pulse: NRZ (rectangular; Gaussian with BT &gt; 0), raised cosine, root raised cosine, C4FM
    /// (raised cosine with the inverse-sinc shaping filter), or an L-symbol rectangular / raised-cosine CPM pulse.</summary>
    public enum FskPulse { Nrz, RaisedCosine, RootRaisedCosine, C4fm, Lrec, Lrc }

    /// <summary>A burst's second part in another modulation (Bluetooth EDR's DPSK payload): its modulation name, symbol
    /// rate (sym/s), symbols, roll-off and the guard (s) before it, during which the carrier is unmodulated.</summary>
    public sealed record FskSwitch(string Modulation, double SymbolRate, int Symbols, double RollOff = 0.4, double GuardSeconds = 5e-6);

    /// <summary>A burst: the <paramref name="Preamble"/> pattern repeated to <paramref name="PreambleBits"/> (0 = once),
    /// then the payload (<paramref name="PayloadBits"/> of the data, null = one period) with the <paramref name="Sync"/>
    /// word after its first <paramref name="SyncAt"/> bits, then <paramref name="Tail"/>; raised-cosine ramps of
    /// <paramref name="RampSymbols"/> either side, and <paramref name="GuardSymbols"/> (the ramps included) outside the bits.</summary>
    public sealed record FskBurst(string Preamble = "", int PreambleBits = 0, string Sync = "", int SyncAt = 0, int? PayloadBits = null,
                                  string Tail = "", double RampSymbols = 0, double GuardSymbols = 0, FskSwitch? Then = null);

    /// <summary>An FSK carrier's settings with every default applied: bit (chip) rate (b/s), peak deviation of the outer
    /// level (Hz), the Gaussian filter's BT (0 = none: rectangular CPFSK) and the data name as written (PN9, PN15 or a
    /// pattern of 0s and 1s); levels, mapping, pulse and burst default to binary NRZ FSK, continuous.</summary>
    public sealed record FskShape(double BitRate, double Deviation, double Bt, string Data)
    {
        /// <summary>2, 4 or 8.</summary>
        public int Levels { get; init; } = 2;
        /// <summary>The level (odd, up to +-(Levels-1)) of each symbol value; null = 0 -> -1, 1 -> +1 for 2 levels,
        /// 00 +1, 01 +3, 10 -1, 11 -3 for 4, Gray for 8.</summary>
        public int[]? Mapping { get; init; }
        public FskPulse Pulse { get; init; }
        /// <summary>Roll-off of the raised-cosine, root-raised-cosine and C4FM pulses.</summary>
        public double RollOff { get; init; } = 0.2;
        /// <summary>L, symbols, of an LREC / LRC pulse.</summary>
        public int PulseLength { get; init; } = 1;
        /// <summary>Each bit sent as two chips (1 -> 10, 0 -> 01); <see cref="BitRate"/> is then the chip rate.</summary>
        public bool Manchester { get; init; }
        public FskBurst? Burst { get; init; }

        public int BitsPerSymbol => Levels switch { 4 => 2, 8 => 3, _ => 1 };
        public double SymbolRate => BitRate / BitsPerSymbol;

        /// <summary>Modulation index: 2 Dev / ((M-1) Rs), the spacing of adjacent levels over the symbol rate.</summary>
        public double ModulationIndex => 2 * Deviation / ((Levels - 1) * SymbolRate);

        /// <summary>What the settings make: MSK / GMSK at h = 0.5, GFSK with a filter, FSK without; 4FSK, C4FM, LREC CPM.</summary>
        public string Kind => Pulse switch
        {
            FskPulse.C4fm => "C4FM",
            FskPulse.Lrec => $"{PulseLength}REC CPM",
            FskPulse.Lrc => $"{PulseLength}RC CPM",
            _ when Levels > 2 => $"{Levels}{(Bt > 0 && Pulse == FskPulse.Nrz ? "G" : "")}FSK",
            _ => Math.Abs(ModulationIndex - 0.5) < 1e-9 ? (Bt > 0 ? "GMSK" : "MSK") : Bt > 0 ? "GFSK" : "FSK",
        };

        /// <summary>Bits (chips) the record sends by FSK.</summary>
        public int Bits => Burst is null && Levels == 2 && !Manchester ? Fsk.DataLength(Data) : Fsk.RecordBits(this).Length;
        public double RecordSeconds => Burst is null ? Bits / BitRate : Fsk.Layout(this, Fsk.NativeRateHz(this)).Total / Fsk.NativeRateHz(this);
    }

    /// <summary>A record's samples in order: ramp up, the FSK symbols, the switch's guard and payload, ramp down, off.</summary>
    public sealed record FskLayout(int SamplesPerSymbol, int Ramp, int Fsk, int SwitchGuard, int Payload, int Off)
    {
        public int Total => 2 * Ramp + Fsk + SwitchGuard + Payload + Off;
        public int PayloadStart => Ramp + Fsk + SwitchGuard;
    }

    public static class Fsk
    {
        public const double DefaultBitRate = 100e3, DefaultDeviation = 45e3;
        public const string DefaultData = "PN9";

        /// <summary>Fewest samples per bit; more when the deviation needs them (<see cref="NativeRateHz"/>).</summary>
        public const int MinSamplesPerBit = 32;

        /// <summary>Most samples a record may hold (2^23, 128 MB of I/Q): PN15 at a low bit rate and a wide deviation
        /// would otherwise ask for gigabytes without saying so.</summary>
        public const int MaxSamples = 1 << 23;

        /// <summary>The carrier's shape: its preset's (burst and switch included) or binary NRZ FSK's, each stated field over it.</summary>
        public static FskShape Resolve(CarrierSpec c)
        {
            var b = c.Preset is { } name
                ? (FskPresets.Find(name) ?? throw new FormatException($"FSK carrier: unknown preset '{name}' ({string.Join(", ", FskPresets.All.Select(FskPresets.Key))})")).Shape
                : new FskShape(DefaultBitRate, DefaultDeviation, 0, DefaultData);
            return b with
            {
                BitRate = c.BitRateBps ?? b.BitRate, Deviation = c.DeviationHz ?? b.Deviation, Bt = c.GaussianBt ?? b.Bt,
                Data = (c.Data ?? b.Data).ToUpperInvariant(), Levels = c.Levels ?? b.Levels, Mapping = ParseMapping(c.Mapping) ?? b.Mapping,
                Pulse = c.FskPulse ?? b.Pulse, RollOff = c.RollOff ?? b.RollOff, PulseLength = c.PulseLength ?? b.PulseLength,
                Manchester = c.Manchester ?? b.Manchester,
            };
        }

        private static int[]? ParseMapping(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var p = text.Split(',', StringSplitOptions.TrimEntries);
            var v = new int[p.Length];
            for (int i = 0; i < p.Length; i++)
                if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                    throw new FormatException($"FSK carrier: Mapping is the odd level of each symbol, comma-separated (got '{text}')");
            return v;
        }

        /// <summary>A pulse as a carrier's <c>Pulse=</c> names it.</summary>
        public static string PulseName(FskPulse p) => p switch
        {
            FskPulse.Nrz => "NRZ", FskPulse.RaisedCosine => "RC", FskPulse.RootRaisedCosine => "RRC", FskPulse.C4fm => "C4FM",
            FskPulse.Lrec => "LREC", _ => "LRC",
        };

        public static FskPulse ParsePulse(string v)
        {
            foreach (var p in Enum.GetValues<FskPulse>())
                if (PulseName(p).Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            throw new FormatException($"FSK carrier: unknown pulse '{v}' ({string.Join(", ", Enum.GetValues<FskPulse>().Select(PulseName))})");
        }

        /// <summary>Refuses an FSK carrier that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static FskShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            var inv = CultureInfo.InvariantCulture;
            if (!(s.BitRate > 0) || !double.IsFinite(s.BitRate))
                throw new FormatException($"FSK carrier: Rb must be a positive bit rate (got {s.BitRate.ToString(inv)} b/s)");
            if (!(s.Deviation > 0) || !double.IsFinite(s.Deviation))
                throw new FormatException($"FSK carrier: Dev must be a positive frequency deviation (got {s.Deviation.ToString(inv)} Hz)");
            if (!(s.Bt >= 0) || !double.IsFinite(s.Bt))
                throw new FormatException($"FSK carrier: BT must be positive, or 0 for no filter (got {s.Bt.ToString(inv)})");
            if (s.Data is not ("PN9" or "PN15") && (s.Data.Length == 0 || s.Data.Any(ch => ch is not ('0' or '1'))))
                throw new FormatException($"FSK carrier: Data must be PN9, PN15 or a pattern of 0s and 1s (got '{c.Data}')");
            Check(s);
            double rate = NativeRateHz(s);
            double n = s.Burst is null && s.Levels == 2 && !s.Manchester ? s.Bits * (rate / s.BitRate) : Layout(s, rate).Total;
            if (n > MaxSamples)
                throw new FormatException(string.Format(inv,
                    "FSK carrier: Data {0} is {1:N0} bits, {2:N0} samples at {3:0.###} MS/s, over the {4:N0} a record may hold; choose PN9, a shorter pattern or a higher bit rate",
                    s.Data.Length > 12 ? s.Data[..12] + "…" : s.Data, s.Bits, n, rate / 1e6, MaxSamples));
            if (s.Burst?.Then is { } t && Math.Abs(rate / t.SymbolRate - Math.Round(rate / t.SymbolRate)) > 1e-9 * rate / t.SymbolRate)
                throw new FormatException(string.Format(inv, "FSK carrier: the switched payload's symbol rate must divide the record's rate ({0:0.###} MS/s)", rate / 1e6));
            return s;
        }

        /// <summary>Refuses levels, a mapping, a pulse or a burst that cannot be generated.</summary>
        public static void Check(FskShape s)
        {
            static bool Bits01(string p) => p.All(ch => ch is '0' or '1');
            int m = s.Levels;
            string? why =
                m is not (2 or 4 or 8) ? $"Levels must be 2, 4 or 8 (got {m})"
              : s.Mapping is { } map && (map.Length != m || map.Distinct().Count() != m || map.Any(v => (v & 1) == 0 || Math.Abs(v) > m - 1))
                    ? $"the mapping must give each of the {m} symbols a different odd level up to ±{m - 1}"
              : s.Pulse is FskPulse.RaisedCosine or FskPulse.RootRaisedCosine or FskPulse.C4fm && !(s.RollOff > 0 && s.RollOff <= 1)
                    ? "the roll-off must be over 0 and at most 1"
              : s.Pulse is FskPulse.Lrec or FskPulse.Lrc && s.PulseLength < 1 ? "the pulse length L must be at least one symbol"
              : s.Burst is not { } b ? null
              : !Bits01(b.Preamble + b.Sync + b.Tail) ? "the preamble, sync word and tail must be 0s and 1s"
              : b.PreambleBits < 0 || b.PayloadBits < 0 || b.SyncAt < 0 || b.SyncAt > (b.PayloadBits ?? DataLength(s.Data))
                    ? "the preamble and payload lengths must be positive and the sync word inside the payload"
              : !(b.RampSymbols >= 0) || !(b.GuardSymbols >= 2 * b.RampSymbols) ? "the guard must hold both ramps"
              : b.Then is { } t && !(t.SymbolRate > 0 && t.Symbols > 0 && t.GuardSeconds >= 0) ? "the switched payload needs a symbol rate and symbols"
              : null;
            if (why is not null) throw new FormatException("FSK carrier: " + why);
        }

        /// <summary>The bits (chips) the record sends by FSK: the data, or the burst's preamble, payload, sync word and tail;
        /// Manchester-coded if asked; filled to whole symbols (the data repeated, a burst padded with 0s).</summary>
        public static byte[] RecordBits(FskShape s)
        {
            var data = DataBits(s.Data);
            static IEnumerable<byte> Of(string p) => p.Select(ch => (byte)(ch - '0'));
            List<byte> bits;
            if (s.Burst is not { } b) bits = [.. data];
            else
            {
                var pay = Enumerable.Range(0, b.PayloadBits ?? data.Length).Select(i => data[i % data.Length]).ToList();
                pay.InsertRange(b.SyncAt, Of(b.Sync));
                bits = [.. Enumerable.Range(0, b.Preamble.Length == 0 ? 0 : Math.Max(b.PreambleBits, b.Preamble.Length))
                                      .Select(i => (byte)(b.Preamble[i % b.Preamble.Length] - '0')), .. pay, .. Of(b.Tail)];
            }
            if (s.Manchester) bits = [.. bits.SelectMany(v => v == 1 ? new byte[] { 1, 0 } : [0, 1])];
            var once = bits.ToArray();
            while (bits.Count % s.BitsPerSymbol != 0) { if (s.Burst is null) bits.AddRange(once); else bits.Add(0); }
            return [.. bits];
        }

        /// <summary>Where each part of the record sits at <paramref name="rateHz"/>.</summary>
        public static FskLayout Layout(FskShape s, double rateHz)
        {
            int sps = (int)Math.Round(rateHz / s.SymbolRate), fsk = RecordBits(s).Length / s.BitsPerSymbol * sps;
            if (s.Burst is not { } b) return new(sps, 0, fsk, 0, 0, 0);
            int ramp = (int)Math.Round(b.RampSymbols * sps);
            var t = b.Then;
            return new(sps, ramp, fsk, t is null ? 0 : (int)Math.Round(t.GuardSeconds * rateHz),
                       t is null ? 0 : (int)Math.Round(t.Symbols * rateHz / t.SymbolRate), (int)Math.Round(b.GuardSymbols * sps) - 2 * ramp);
        }

        /// <summary>The sample each FSK symbol's frequency is read from, as the quality measurement reads it: the
        /// frequency between it and two samples on is the symbol centre's.</summary>
        public static int[] SymbolCentres(FskShape s, double rateHz)
        {
            var l = Layout(s, rateHz);
            return [.. Enumerable.Range(0, l.Fsk / l.SamplesPerSymbol).Select(j => l.Ramp + j * l.SamplesPerSymbol + l.SamplesPerSymbol / 2 - 1)];
        }

        /// <summary>The level of each symbol value when no mapping is given.</summary>
        public static int[] DefaultMapping(int levels) => levels switch
        {
            4 => [1, 3, -1, -3],
            8 => [.. Enumerable.Range(0, 8).Select(v => 2 * Enumerable.Range(0, 8).First(i => (i ^ (i >> 1)) == v) - 7)],
            _ => [-1, 1],
        };

        /// <summary>The frequency pulse's response at x = f / Rs, unity at DC.</summary>
        public static double PulseResponse(FskShape s, double x)
        {
            double a = s.RollOff, ax = Math.Abs(x), lx = s.PulseLength * x;
            static double Sinc(double u) => u == 0 ? 1 : Math.Sin(Math.PI * u) / (Math.PI * u);
            double rc = ax <= (1 - a) / 2 ? 1 : ax > (1 + a) / 2 ? 0 : 0.5 * (1 + Math.Cos(Math.PI / a * (ax - (1 - a) / 2)));
            return s.Pulse switch
            {
                FskPulse.RaisedCosine => rc,
                FskPulse.RootRaisedCosine => Math.Sqrt(rc),
                FskPulse.C4fm => rc / Sinc(x),                                      // rc is 0 before the sinc's first zero
                FskPulse.Lrec => Sinc(lx),
                _ => Math.Abs(Math.Abs(lx) - 1) < 1e-12 ? 0.5 : Sinc(lx) / (1 - lx * lx),
            };
        }

        /// <summary>Carson's bandwidth, 2 (Dev + Rs): the carrier's channel width, from which the preview
        /// span, the envelope rate and the ACLR bands follow.</summary>
        public static double BandwidthHz(FskShape s) => 2 * (s.Deviation + s.SymbolRate);

        /// <summary>Rs x 2^k: at least <see cref="MinSamplesPerBit"/> samples per symbol and eight times the bandwidth, so the
        /// slowly falling sidelobes of rectangular CPFSK are well down before the native Nyquist edge.</summary>
        public static double NativeRateHz(FskShape s)
        {
            double sps = MinSamplesPerBit;
            while (sps * s.SymbolRate < 8 * BandwidthHz(s)) sps *= 2;
            return sps * s.SymbolRate;
        }

        public static int DataLength(string data) => data switch
        {
            "PN9" => 511,
            "PN15" => 32767,
            _ => data.Length,
        };

        /// <summary>One period of the data as bits.</summary>
        public static byte[] DataBits(string data) => data switch
        {
            "PN9" => Prbs(9, 5),
            "PN15" => Prbs(15, 14),
            "PN23" => Prbs(23, 18),
            _ => data.Select(ch => (byte)(ch - '0')).ToArray(),
        };

        /// <summary>One period of the maximal-length sequence of x^<paramref name="order"/> + x^<paramref name="tap"/> + 1
        /// (ITU-T O.150), from the all-ones state.</summary>
        public static byte[] Prbs(int order, int tap)
        {
            int n = (1 << order) - 1, state = n;
            var bits = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int b = ((state >> (order - 1)) ^ (state >> (tap - 1))) & 1;
                bits[i] = (byte)b;
                state = ((state << 1) | b) & n;
            }
            return bits;
        }

        /// <summary>
        /// The frequency (Hz) the record's phase integrates, at <paramref name="rateHz"/> (a whole number of samples per
        /// symbol): 0 in ramps, guards and a switched payload. A Gaussian filter, or a pulse other than NRZ, is applied in
        /// the frequency domain, H(f) = exp(-ln2 / 2 (f / BT Rs)^2) for the filter, so the record stays exactly periodic
        /// with no truncated taps.
        /// </summary>
        public static double[] FrequencyTrack(FskShape s, double rateHz)
        {
            var bits = RecordBits(s);
            var l = Layout(s, rateHz);
            int sps = l.SamplesPerSymbol, k = s.BitsPerSymbol, n = l.Total;
            var map = s.Mapping ?? DefaultMapping(s.Levels);
            double step = s.Deviation / (s.Levels - 1);
            bool nrz = s.Pulse == FskPulse.Nrz;
            var f = new double[n];
            for (int j = 0; j < bits.Length / k; j++)
            {
                int v = 0;
                for (int q = 0; q < k; q++) v = (v << 1) | bits[j * k + q];
                int i0 = l.Ramp + j * sps;
                if (nrz) for (int i = i0; i < i0 + sps; i++) f[i] = map[v] * step;
                else f[i0] = map[v] * step * sps;                                   // an impulse of the symbol's area
            }
            if (nrz && s.Bt > 0)
            {
                var spec = Fft.Forward(f.Select(v => new Complex(v, 0)).ToArray());
                double b = s.Bt * s.SymbolRate;
                for (int q = 0; q < n; q++)
                {
                    double fk = (q <= n / 2 ? q : q - n) * rateHz / n;
                    spec[q] *= Math.Exp(-Math.Log(2) / 2 * (fk / b) * (fk / b));
                }
                var back = Fft.Inverse(spec);
                for (int i = 0; i < n; i++) f[i] = back[i].Real;
            }
            else if (!nrz)
            {
                var spec = Fft.Forward(f.Select(v => new Complex(v, 0)).ToArray());
                double centre = (sps - 1) / 2.0 / rateHz;                          // the impulse moves to the symbol's centre
                for (int q = 0; q < n; q++)
                {
                    double fk = (q <= n / 2 ? q : q - n) * rateHz / n;
                    spec[q] *= PulseResponse(s, fk / s.SymbolRate) * Complex.FromPolarCoordinates(1, -2 * Math.PI * fk * centre);
                }
                var back = Fft.Inverse(spec);
                for (int i = 0; i < n; i++) f[i] = back[i].Real;
            }
            return f;
        }

        /// <summary>
        /// One record at <paramref name="rateHz"/>, unit magnitude while the carrier is on, and the instantaneous
        /// frequency (Hz) it was integrated from. A burst that switches modulation takes its payload from
        /// <paramref name="payload"/> (the switch, the data name, the rate), which returns exactly the payload's samples
        /// at unit mean power; they are turned to the FSK phase where they start.
        /// </summary>
        public static (Complex[] Samples, double[] FrequencyHz) Generate(FskShape s, double rateHz,
                                                                        Func<FskSwitch, string, double, Complex[]>? payload = null)
        {
            var f = FrequencyTrack(s, rateHz);
            var l = Layout(s, rateHz);
            int n = f.Length;
            // phase before each sample; the residual over the record (wrapped to +-pi) is ramped out so the seam is smooth,
            // unless the carrier goes off, which puts the seam at zero amplitude
            double total = 2 * Math.PI * f.Sum() / rateHz;
            double residual = l.Off > 0 ? 0 : total - 2 * Math.PI * Math.Round(total / (2 * Math.PI));
            var x = new Complex[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                x[i] = Complex.FromPolarCoordinates(1.0, phase - residual * i / n);
                phase += 2 * Math.PI * f[i] / rateHz;
            }
            if (s.Burst is not { } b) return (x, f);
            if (b.Then is { } t)
            {
                var p = (payload ?? throw new InvalidOperationException($"FSK carrier: no generator for the {t.Modulation} payload"))(t, s.Data, rateHz);
                if (p.Length != l.Payload)
                    throw new InvalidOperationException($"FSK carrier: the {t.Modulation} payload is {p.Length} samples, not {l.Payload}");
                int p0 = l.PayloadStart;
                var turn = x[p0];
                for (int j = 0; j < p.Length; j++) x[p0 + j] = p[j] * turn;
                for (int j = 0; j < p.Length; j++)
                    f[p0 + j] = (x[(p0 + j + 1) % n] * Complex.Conjugate(x[p0 + j])).Phase * rateHz / (2 * Math.PI);
            }
            int down = l.PayloadStart + l.Payload;
            for (int i = 0; i < l.Ramp; i++)
            {
                double g = 0.5 * (1 - Math.Cos(Math.PI * (i + 0.5) / l.Ramp));
                x[i] *= g;
                x[down + l.Ramp - 1 - i] *= g;
            }
            for (int i = down + l.Ramp; i < n; i++) x[i] = 0;
            return (x, f);
        }

        /// <summary>A bit rate as the card shows it: 100 kb/s, 32.768 kb/s, 1.2 Mb/s.</summary>
        public static string FormatBitRate(double bps)
        {
            var inv = CultureInfo.InvariantCulture;
            return bps >= 1e6 ? string.Format(inv, "{0:0.######} Mb/s", bps / 1e6)
                 : bps >= 1e3 ? string.Format(inv, "{0:0.######} kb/s", bps / 1e3)
                 : string.Format(inv, "{0:0.######} b/s", bps);
        }

        /// <summary>A frequency as a label reads it: 45 kHz, 1.2 MHz.</summary>
        public static string FormatHz(double hz)
        {
            var inv = CultureInfo.InvariantCulture;
            return Math.Abs(hz) >= 1e6 ? string.Format(inv, "{0:0.######} MHz", hz / 1e6)
                 : Math.Abs(hz) >= 1e3 ? string.Format(inv, "{0:0.######} kHz", hz / 1e3)
                 : string.Format(inv, "{0:0.######} Hz", hz);
        }
    }
}
