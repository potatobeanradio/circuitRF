// ================================================================
//  RadarPresets.cs  —  radar waveforms and the regulators' radar test
//  signals as pulse shapes and FMCW ramps
//
//  DFS test radars, one representative member of each type, chosen
//  inside the ranges the tables give (a test lab draws its own):
//    ETSI EN 301 893 V2.1.1 (2017-05), Annex D: Table D.3 (reference
//      signal) and Table D.4 (radar test signals 1-6; #4 chirped
//      ±2.5 MHz; #5 and #6 pulse-by-pulse staggered over 2 or 3 PRFs,
//      PPB per PRF). Read from the published text.
//    FCC 47 CFR 15.407(h) and KDB 905462 D02 (UNII DFS compliance
//      procedures, v02): Table 5 (short-pulse Types 0-4), Table 6 (long
//      pulse Type 5), Table 7 (hopping Type 6). The KDB itself could not
//      be fetched: the rows were checked against public reproductions of
//      it only, so they are marked unverified.
//  DME: ICAO Annex 10 Vol. I, 3.5.4.1.3 / 3.5.5.1.3 (pulse duration
//  3.5 ± 0.5 µs between the half-amplitude points, rise <= 3 µs, decay
//  2.5 µs nominal) and the pulse-code table (X 12/12 µs, Y 36/30 µs
//  interrogation/reply), read from public reproductions of the 8th ed.
//  text, not the edition itself (unverified). The Annex states no pulse
//  shape; the Gaussian is the usual model: of half-amplitude width
//  3.5 µs it rises 10-90 % in 2.51 µs, the nominal decay time.
//
//  Every PRI is rounded to whole samples at 30.72 MHz (1.92 MHz x 16),
//  so 1428 µs becomes 1427.995 µs: within 5 ns of the table.
// ================================================================

using System;
using System.Linq;

namespace RfCore.Signals
{
    /// <summary>One radar waveform: its menu, name, the clause it comes from, and the pulse shape or the FMCW ramp.</summary>
    public sealed record RadarPreset(string Family, string Name, string Reference, PulseShape? Pulse = null, Fmcw? Ramp = null);

    public static class RadarPresets
    {
        private const double Us = 1e-6, MHz = 1e6;

        /// <summary><paramref name="seconds"/> rounded to whole samples at 30.72 MHz.</summary>
        public static double Grid(double seconds) => Math.Round(seconds * 30.72e6) / 30.72e6;

        private static PulseShape Pulse(double pri, double pw, double edge = 0.1 * Us, double chirp = 0)
            => PulsedCw.Resolve(new CarrierSpec
            {
                Standard = CarrierStandard.Pcw, PriSeconds = Grid(pri), PulseWidthSeconds = pw,
                RiseSeconds = edge, FallSeconds = edge, ChirpHz = chirp == 0 ? null : chirp,
            });

        private static PulseShape Coded(RadarCode kind, int n, double chip, double pri, double edge = 0.1 * Us)
        {
            var code = RadarCodes.Phases(kind, n);
            return Pulse(pri, code.Length * chip, edge) with { Code = code };
        }

        /// <summary>A burst of <paramref name="pulses"/> at the PRFs (pps) given, cycled pulse by pulse.</summary>
        private static PulseShape Burst(double pw, int pulses, double edge, double chirp, params double[] prf)
        {
            double[] pris = [.. prf.Select(f => Grid(1 / f))];
            return Pulse(pris.Min(), pw, edge, chirp) with { Train = new PulseTrain { Pris = pris, Pulses = pulses } };
        }

        // FCC Type 1: pulses = roundup((1/360) x (19e6 / PRI in µs)), KDB 905462 D02 Table 5
        private static int FccType1Pulses(double priUs) => (int)Math.Ceiling(19e6 / priUs / 360);

        private static PulseShape Dme(double spacing)
        {
            const double w = 3.5 * Us;
            double pri = Grid(1 / 2700.0);                    // the 2700 pulse pairs/s of a loaded beacon (unverified)
            return Pulse(pri, w) with { Gaussian = true, Delay = (pri - 4 * w - spacing) / 2, Train = new PulseTrain { PairSpacing = spacing } };
        }

        private const string Etsi = "ETSI EN 301 893 V2.1.1 Annex D";
        private const string Fcc = "FCC 47 CFR 15.407, KDB 905462 D02 (unverified)";
        private const string Icao = "ICAO Annex 10 Vol. I, 3.5 (unverified)";
        private const double Dfs = 0.25 * Us;              // DFS tables state no edge; 0.25 µs fits their 0.5 µs pulses

        public static readonly RadarPreset[] All =
        [
            new("Codes", "Barker 13", "Barker 1953", Coded(RadarCode.Barker, 13, 1 * Us, 100 * Us)),
            new("Codes", "Barker 7", "Barker 1953", Coded(RadarCode.Barker, 7, 1 * Us, 100 * Us)),
            new("Codes", "Frank 16", "Frank 1963", Coded(RadarCode.Frank, 4, 1 * Us, 100 * Us)),
            new("Codes", "P1 16", "Lewis and Kretschmer 1981", Coded(RadarCode.P1, 4, 1 * Us, 100 * Us)),
            new("Codes", "P2 16", "Lewis and Kretschmer 1981", Coded(RadarCode.P2, 4, 1 * Us, 100 * Us)),
            new("Codes", "P3 64", "Lewis and Kretschmer 1982", Coded(RadarCode.P3, 64, 0.1 * Us, 100 * Us, 0.05 * Us)),
            new("Codes", "P4 64", "Lewis and Kretschmer 1982", Coded(RadarCode.P4, 64, 0.1 * Us, 100 * Us, 0.05 * Us)),
            new("Codes", "Zadoff-Chu 63", "Chu 1972", Coded(RadarCode.ZadoffChu, 63, 0.1 * Us, 100 * Us, 0.05 * Us)),
            new("Codes", "m-sequence 127", "Golomb 2014", Coded(RadarCode.Mls, 7, 0.1 * Us, 100 * Us, 0.05 * Us)),

            new("FM", "Tangent NLFM 10 MHz", "Collins and Atkins 1999", Pulse(500 * Us, 50 * Us, chirp: 10 * MHz) with { Fm = PulseFm.Tangent }),
            new("FM", "Sine NLFM 10 MHz", "sine-weighted sweep", Pulse(500 * Us, 50 * Us, chirp: 10 * MHz) with { Fm = PulseFm.Sine }),
            new("FM", "Stepped frequency 8 x 1 MHz", "stepped-frequency train",
                Pulse(100 * Us, 1 * Us) with { Train = new PulseTrain { Steps = 8, StepHz = 1 * MHz } }),
            new("FM", "FMCW sawtooth 250 MHz", "FMCW", Ramp: new Fmcw(250 * MHz, 50 * Us, 12.5 * Us)),
            new("FM", "FMCW triangle 250 MHz", "FMCW", Ramp: new Fmcw(250 * MHz, 50 * Us, Triangle: true)),

            new("PRI", "Stagger 100/125/150 µs", "staggered PRI",
                Pulse(100 * Us, 10 * Us) with { Train = new PulseTrain { Pris = [100 * Us, 125 * Us, 150 * Us] } }),
            new("PRI", "Jitter ±5 %", "jittered PRI",
                Pulse(100 * Us, 10 * Us) with { Train = new PulseTrain { Jitter = 0.05, Pulses = 16 } }),
            new("PRI", "Burst 8 + 1 ms dwell", "burst and dwell",
                Pulse(100 * Us, 10 * Us) with { Train = new PulseTrain { Pulses = 8, Dwell = 1000 * Us } }),

            new("DFS ETSI", "Reference", Etsi + " Table D.3", Burst(1 * Us, 18, Dfs, 0, 700)),
            new("DFS ETSI", "Signal 1", Etsi + " Table D.4", Burst(1 * Us, 10, Dfs, 0, 700)),
            new("DFS ETSI", "Signal 2", Etsi + " Table D.4", Burst(10 * Us, 15, Dfs, 0, 1000)),
            new("DFS ETSI", "Signal 3", Etsi + " Table D.4", Burst(10 * Us, 25, Dfs, 0, 3000)),
            new("DFS ETSI", "Signal 4", Etsi + " Table D.4", Burst(25 * Us, 20, Dfs, 5 * MHz, 3000)),
            new("DFS ETSI", "Signal 5", Etsi + " Table D.4", Burst(1 * Us, 30, Dfs, 0, 300, 330, 360)),
            new("DFS ETSI", "Signal 6", Etsi + " Table D.4", Burst(1 * Us, 45, Dfs, 0, 400, 600, 800)),

            new("DFS FCC", "Type 0", Fcc + " Table 5", Burst(1 * Us, 18, Dfs, 0, 1 / 1428e-6)),
            new("DFS FCC", "Type 1", Fcc + " Table 5", Burst(1 * Us, FccType1Pulses(3066), Dfs, 0, 1 / 3066e-6)),
            new("DFS FCC", "Type 2", Fcc + " Table 5", Burst(3 * Us, 26, Dfs, 0, 1 / 190e-6)),
            new("DFS FCC", "Type 3", Fcc + " Table 5", Burst(8 * Us, 17, Dfs, 0, 1 / 350e-6)),
            new("DFS FCC", "Type 4", Fcc + " Table 5", Burst(15 * Us, 14, Dfs, 0, 1 / 350e-6)),
            new("DFS FCC", "Type 5", Fcc + " Table 6", Burst(75 * Us, 2, Dfs, 10 * MHz, 1 / 1500e-6)),
            new("DFS FCC", "Type 6 (one hop)", Fcc + " Table 7", Burst(1 * Us, 9, Dfs, 0, 1 / 333e-6)),

            new("DME", "DME X", Icao, Dme(12 * Us)),
            new("DME", "DME Y interrogation", Icao, Dme(36 * Us)),
            new("DME", "DME Y reply", Icao, Dme(30 * Us)),
        ];
    }
}
